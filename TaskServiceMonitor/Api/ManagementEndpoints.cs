using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Management;
using TaskServiceMonitor.Models;
using TaskServiceMonitor.Monitoring;

namespace TaskServiceMonitor.Api;

/// <summary>
/// Dạng RÚT GỌN, giữ lại để tương thích ngược (form 4 ô cũ, script, curl demo).
/// Hộp thoại mới gửi thẳng <see cref="TaskDefinitionRequest"/>.
/// </summary>
public sealed record CreateTaskRequest(string Name, string Command, string? Arguments, string? StartBoundary);
public sealed record CreateServiceRequest(
    string Name, string BinaryPath, string? StartType, string? DisplayName,
    string? Description = null, string[]? Dependencies = null, string? Account = null);
public sealed record ChangeStartTypeRequest(string StartType);

public static class ManagementEndpoints
{
    /// <summary>
    /// GET /api/system/overview — số liệu cho ô "Overview" ở Dashboard.
    ///
    /// CỐ Ý query DB thật chứ không tính từ mảng ~200 event trên client như 3 card ở
    /// trên: ô này để trả lời "hệ thống có đang chạy đúng không", cần con số toàn cục.
    /// </summary>
    private static async Task<IResult> GetOverview(
        MonitorDbContext db, ChannelStatusRegistry registry, SafeNameGuard guard,
        CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);

        var total = await db.Events.CountAsync(ct);

        var span = await db.Events.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Oldest = g.Min(e => e.TimeCreated),
                Newest = g.Max(e => e.TimeCreated)
            })
            .FirstOrDefaultAsync(ct);

        var topEventIds = await db.Events.AsNoTracking()
            .GroupBy(e => new { e.EventId, e.ProviderName, e.ActionDescription })
            .Select(g => new
            {
                g.Key.EventId,
                source = g.Key.ProviderName,
                action = g.Key.ActionDescription,
                count = g.Count()
            })
            .OrderByDescending(x => x.count)
            .Take(5)
            .ToListAsync(ct);

        // Gom theo (ngay, gio, muc rui ro) o TANG DB - Npgsql dich .Date/.Hour thanh
        // date_trunc/date_part. Keo het 24 gio ve roi gom trong bo nho se tai vai
        // nghin dong chi de dem.
        var raw = await db.Events.AsNoTracking()
            .Where(e => e.TimeCreated >= since)
            .GroupBy(e => new { e.TimeCreated.Date, e.TimeCreated.Hour, e.RiskLevel })
            .Select(g => new { g.Key.Date, g.Key.Hour, g.Key.RiskLevel, Count = g.Count() })
            .ToListAsync(ct);

        // Dung du 24 o theo gio UTC, o nao khong co event thi de 0 - bieu do khong
        // duoc phep "nhay coc" nhung gio vang.
        var buckets = Enumerable.Range(0, 24)
            .Select(offset =>
            {
                var slot = DateTime.UtcNow.AddHours(-23 + offset);
                var match = raw.Where(r => r.Date == slot.Date && r.Hour == slot.Hour).ToList();

                return new
                {
                    hourUtc = new DateTime(slot.Year, slot.Month, slot.Day, slot.Hour, 0, 0, DateTimeKind.Utc),
                    high = match.Where(m => m.RiskLevel == RiskLevel.High).Sum(m => m.Count),
                    medium = match.Where(m => m.RiskLevel == RiskLevel.Medium).Sum(m => m.Count),
                    low = match.Where(m => m.RiskLevel == RiskLevel.Low).Sum(m => m.Count)
                };
            })
            .ToList();

        // Gom theo may tren TOAN BO DB. Card o Dashboard dem tren ~200 event client
        // nen hai con so se lech - UI phai ghi ro cai nao la toan cuc.
        var byHost = await db.Events.AsNoTracking()
            .GroupBy(e => e.Hostname)
            .Select(g => new
            {
                hostname = g.Key,
                total = g.Count(),
                high = g.Count(e => e.RiskLevel == RiskLevel.High),
                medium = g.Count(e => e.RiskLevel == RiskLevel.Medium),
                low = g.Count(e => e.RiskLevel == RiskLevel.Low),
                lastEventUtc = g.Max(e => e.TimeCreated)
            })
            .OrderByDescending(x => x.total)
            .Take(10)
            .ToListAsync(ct);

        var riskDistribution = await db.Events.AsNoTracking()
            .GroupBy(e => e.RiskLevel)
            .Select(g => new { riskLevel = g.Key, count = g.Count() })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            isElevated = ElevationInfo.IsElevated(),
            currentUser = ElevationInfo.CurrentUserName(),
            writablePrefix = guard.WritablePrefix,
            channels = registry.All(),
            totalEvents = total,
            oldestEventUtc = span?.Oldest,
            newestEventUtc = span?.Newest,
            topEventIds,
            hourlyBuckets = buckets,
            byHost,
            riskDistribution
        });
    }

    /// <summary>
    /// GET /api/system/recovered — ĐÚNG những event nào đã được đọc bù sau khi app
    /// khởi động lại (cơ chế cursor <c>EventRecordID</c> ở bước 7).
    ///
    /// Trước đây Log Summary chỉ hiện con số "↺ khôi phục N" mà không xem được N đó
    /// gồm những gì. Endpoint này trả về chính danh sách đó.
    ///
    /// CỐ Ý không thêm cột <c>IsRecovered</c> vào bảng <c>Events</c> (không phải làm
    /// migration): mỗi channel đã có sẵn hai mốc đủ để suy ra khoảng khôi phục —
    /// <c>ResumeFromRecordId</c> (cursor = MAX(RecordId) đã lưu trong DB lúc khởi
    /// động) và <c>CatchUpTargetRecordId</c> (RecordId mới nhất đang có trong log tại
    /// đúng thời điểm đó). Mọi event nằm trong khoảng NỬA MỞ <c>(cursor, target]</c>
    /// chính là phần sinh ra trong lúc app tắt và vừa được đọc bù — không thể lẫn với
    /// dữ liệu cũ, vì theo định nghĩa cursor là số lớn nhất đã có trong DB.
    ///
    /// Hệ quả cần biết: khoảng này tính lại mỗi lần khởi động, nên trang Khôi phục
    /// nói về PHIÊN CHẠY HIỆN TẠI, không phải lịch sử mọi lần khôi phục.
    /// </summary>
    private static async Task<IResult> GetRecovered(
        MonitorDbContext db, ChannelStatusRegistry registry, int? take, CancellationToken ct)
    {
        var limit = Math.Clamp(take ?? 500, 1, 2000);

        var channelRows = new List<RecoveredChannelDto>();
        var events = new List<EventSummaryDto>();

        // Tuan tu, KHONG Task.WhenAll: mot DbContext scoped khong chay song song duoc
        // (cung bay da ghi o EventEndpoints.GetSummary).
        foreach (var status in registry.All())
        {
            // Khong co cursor = lan chay dau tien tren channel nay (chua tung luu event
            // nao) -> khong co gi de khoi phuc. Khong co target = doc duoc cursor nhung
            // GetLogInformation that bai luc khoi dong, xem ResolveCursor.
            if (status.ResumeFromRecordId is not long cursor ||
                status.CatchUpTargetRecordId is not long target ||
                target <= cursor)
            {
                channelRows.Add(new RecoveredChannelDto(
                    status.Channel, status.ResumeFromRecordId, status.CatchUpTargetRecordId,
                    status.CaughtUpCount, Recovered: 0, Shown: 0,
                    DowntimeFromUtc: null, DowntimeToUtc: null, Note: Explain(status)));
                continue;
            }

            var inRange = db.Events.AsNoTracking()
                .Where(e => e.Channel == status.Channel && e.RecordId > cursor && e.RecordId <= target);

            // Đếm RIÊNG chứ không lấy rows.Count: `take` cắt danh sách trả về, nên
            // dùng độ dài danh sách làm số tổng thì mọi channel đọc bù nhiều hơn
            // `take` sẽ báo đúng bằng `take` — sai lặng lẽ và rất khó nhận ra
            // (`take=5` từng cho ra "recovered: 5" trong khi thực tế là 5.115).
            var total = await inRange.CountAsync(ct);

            var rows = await inRange
                .OrderByDescending(e => e.RecordId)
                .Take(limit)
                .Select(EventSummaryDto.Projection)
                .ToListAsync(ct);

            // Event cuoi cung TRUOC khi mat ket noi = dau kia cua khoang thoi gian app
            // "khong nhin thay gi". Ghep voi event khoi phuc dau tien thanh cua so
            // downtime hien ngay tren trang - do moi la thu can doi chieu khi mat mang.
            var lastBeforeOutage = await db.Events.AsNoTracking()
                .Where(e => e.Channel == status.Channel && e.RecordId <= cursor)
                .MaxAsync(e => (DateTime?)e.TimeCreated, ct);

            // Moc "dau tien doc bu duoc" phai lay tu TOAN BO khoang, khong phai tu
            // trang dang tra ve - `rows` da bi `take` cat con phan MOI NHAT.
            var firstRecovered = total > 0
                ? await inRange.MinAsync(e => (DateTime?)e.TimeCreated, ct)
                : null;

            events.AddRange(rows);

            channelRows.Add(new RecoveredChannelDto(
                status.Channel, status.ResumeFromRecordId, status.CatchUpTargetRecordId,
                status.CaughtUpCount, total, rows.Count,
                lastBeforeOutage, firstRecovered, Note: null));
        }

        return Results.Ok(new
        {
            sessionStartedUtc = registry.SessionStartedUtc,
            totalRecovered = channelRows.Sum(row => row.Recovered),
            channels = channelRows,
            events = events.OrderByDescending(e => e.TimeCreated).ToList()
        });
    }

    /// <summary>
    /// Một dòng của bảng "mốc khôi phục theo channel".
    ///
    /// <paramref name="Recovered"/> là số THẬT trong khoảng khôi phục;
    /// <paramref name="Shown"/> là số dòng thực sự trả về sau khi <c>take</c> cắt bớt.
    /// Hai số này phải tách nhau — gộp lại thì trang sẽ báo thiếu mà không ai biết.
    ///
    /// <paramref name="CaughtUpCount"/> là số watcher ĐẾM ĐƯỢC trong phiên này, có thể
    /// chênh nhẹ với <paramref name="Recovered"/> (số đã kịp ghi xuống CSDL) khi đang
    /// đọc bù dở — đó là chênh lệch thật, không phải lỗi.
    /// </summary>
    private sealed record RecoveredChannelDto(
        string Channel,
        long? ResumeFromRecordId,
        long? CatchUpTargetRecordId,
        int CaughtUpCount,
        int Recovered,
        int Shown,
        DateTime? DowntimeFromUtc,
        DateTime? DowntimeToUtc,
        string? Note);

    /// <summary>Vì sao channel này không có gì để khôi phục — nói thẳng thay vì để dòng trống.</summary>
    private static string Explain(ChannelStatus status) => status switch
    {
        { Subscribed: false } => "Chưa subscribe được channel này nên không có mốc nào để đọc bù.",
        { ResumeFromRecordId: null } => "Lần đầu ghi nhận channel này — chưa có cursor cũ để so, không có gì để khôi phục.",
        { CatchUpTargetRecordId: null } => "Có cursor nhưng không đọc được thông tin log lúc khởi động (thường do thiếu quyền) nên không xác định được mốc kết thúc đọc bù.",
        _ => "Không có event mới nào sinh ra trong lúc app tắt."
    };

    public static void MapManagementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/status", (SafeNameGuard guard) => Results.Ok(new
        {
            isElevated = ElevationInfo.IsElevated(),
            currentUser = ElevationInfo.CurrentUserName(),
            writablePrefix = guard.WritablePrefix
        }));

        // Trang thai THAT cua tung channel (subscribe thanh cong hay khong, co dang
        // doc duoc event khong, bao nhieu event da nhan) - cho panel "Log Summary"
        // o Dashboard. Xem ghi chu trong ChannelStatusRegistry ve ly do can tach
        // rieng "subscribed" voi "dang thuc su nhan event".
        app.MapGet("/api/system/channels", (ChannelStatusRegistry registry) =>
            Results.Ok(registry.All()));

        app.MapGet("/api/system/overview", GetOverview);

        // Danh sach event da doc bu sau restart - phan "N" trong badge "khoi phuc N"
        // cua Log Summary, nay xem duoc tung dong.
        app.MapGet("/api/system/recovered", GetRecovered);

        // ------------------------------------------------------------ Tasks
        app.MapGet("/api/tasks", (TaskManager tasks) => Run(() => Results.Ok(tasks.List())));

        app.MapGet("/api/tasks/xml", (TaskManager tasks, string path) =>
            Run(() => Results.Text(tasks.GetXml(path), "application/xml")));

        // Endpoint RIENG cho modal chi tiet: doc them Definition.RegistrationInfo /
        // .Principal / .Triggers la nhieu loi goi COM cho MOI task - tra het trong
        // /api/tasks se lam cham ca danh sach vai tram task.
        app.MapGet("/api/tasks/detail", (TaskManager tasks, string path) =>
            Run(() => Results.Ok(tasks.Detail(path))));

        // Ten chua co -> tao moi (4698). Ten da co -> ghi de (4702).
        app.MapPost("/api/tasks", (TaskManager tasks, CreateTaskRequest req) => Run(() =>
        {
            RequireElevation();

            // Minimal API KHONG ep buoc thanh vien non-nullable cua record: body {} van
            // bind duoc voi moi field null. Khong chan o day thi null di sau vao COM/
            // Win32 roi bung ra 500 tho thay vi 400.
            RequireField(req.Name, "name");
            RequireField(req.Command, "command");

            tasks.CreateOrUpdate(new TaskDefinitionRequest
            {
                Name = req.Name,
                Actions = [new ActionRequest("Exec", req.Command, req.Arguments)],
                Triggers = [new TriggerRequest("Time", req.StartBoundary)],
            });

            return Results.Ok(new
            {
                message = $"Da ghi task '{req.Name}'. Tao moi sinh event 4698, ghi de sinh 4702."
            });
        }));

        // Dang DAY DU cho hop thoai 5 tab. Query ?api=objectmodel doi sang duong
        // TaskService.NewTask() de doi chieu hai cach lam - xem TaskManager.
        app.MapPost("/api/tasks/full",
            (TaskManager tasks, TaskDefinitionRequest req, string? api) => Run(() =>
        {
            RequireElevation();
            RequireField(req.Name, "name");

            if (string.Equals(api, "objectmodel", StringComparison.OrdinalIgnoreCase))
            {
                tasks.CreateViaObjectModel(req);
            }
            else
            {
                tasks.CreateOrUpdate(req);
            }

            return Results.Ok(new
            {
                message = $"Da ghi task '{req.Name}' ({req.Actions.Count} action, " +
                          $"{req.Triggers.Count} trigger). Tao moi sinh 4698, ghi de sinh 4702."
            });
        }));

        app.MapDelete("/api/tasks", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.Delete(name);
            return Results.Ok(new { message = $"Da xoa task '{name}' (event 4699)." });
        }));

        app.MapPost("/api/tasks/{name}/enable", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.SetEnabled(name, true);
            return Results.Ok(new { message = $"Da bat task '{name}' (event 4700)." });
        }));

        app.MapPost("/api/tasks/{name}/disable", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.SetEnabled(name, false);
            return Results.Ok(new { message = $"Da tat task '{name}' (event 4701)." });
        }));

        app.MapPost("/api/tasks/{name}/run", (TaskManager tasks, string name) => Run(() =>
        {
            RequireElevation();
            tasks.RunNow(name);
            return Results.Ok(new { message = $"Da chay task '{name}'." });
        }));

        // ------------------------------------------------------------ Services
        app.MapGet("/api/services", (ServiceManager services) => Run(() => Results.Ok(services.List())));

        // Nhu /api/tasks/detail: 3 loi goi QueryServiceConfig2 moi service (description,
        // delayed auto start, recovery actions) nen chi chay khi mo modal.
        app.MapGet("/api/services/{name}", (ServiceManager services, string name) =>
            Run(() => Results.Ok(services.Detail(name))));

        app.MapPost("/api/services", (ServiceManager services, CreateServiceRequest req) => Run(() =>
        {
            RequireElevation();
            RequireField(req.Name, "name");
            RequireField(req.BinaryPath, "binaryPath");
            services.Create(
                req.Name, req.BinaryPath, req.StartType ?? "demand start", req.DisplayName,
                req.Description, req.Dependencies, req.Account);
            return Results.Ok(new { message = $"Da tao service '{req.Name}'. Xem event 7045 o tab Dashboard." });
        }));

        app.MapDelete("/api/services", (ServiceManager services, string name) => Run(() =>
        {
            RequireElevation();
            services.Delete(name);
            return Results.Ok(new { message = $"Da xoa service '{name}'." });
        }));

        app.MapPost("/api/services/{name}/start", (ServiceManager services, string name) => Run(() =>
        {
            RequireElevation();
            services.Start(name);
            return Results.Ok(new { message = $"Da start service '{name}'." });
        }));

        app.MapPost("/api/services/{name}/stop", (ServiceManager services, string name) => Run(() =>
        {
            RequireElevation();
            services.Stop(name);
            return Results.Ok(new { message = $"Da stop service '{name}'." });
        }));

        app.MapPost("/api/services/{name}/starttype",
            (ServiceManager services, string name, ChangeStartTypeRequest req) => Run(() =>
        {
            RequireElevation();
            RequireField(req.StartType, "startType");
            services.ChangeStartType(name, req.StartType);
            return Results.Ok(new { message = $"Da doi start type cua '{name}'. Xem event 7040 o tab Dashboard." });
        }));
    }

    /// <summary>
    /// Minimal API bind body <c>{}</c> thành record với mọi field null, KHÔNG ném lỗi
    /// dù thành viên khai là non-nullable. Thiếu bước này thì null đi sâu vào COM/Win32
    /// rồi bung ra 500 thô — ví dụ <c>ParseStartType(null)</c> ném NullReferenceException.
    /// </summary>
    private static void RequireField(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Thieu tham so '{fieldName}'.");
        }
    }

    private sealed class NotElevatedException() : Exception(
        "Thao tac nay can quyen Administrator. Dong app, mo PowerShell bang " +
        "'Run as administrator' roi chay lai: dotnet run --project TaskServiceMonitor");

    private static void RequireElevation()
    {
        if (!ElevationInfo.IsElevated())
        {
            throw new NotElevatedException();
        }
    }

    /// <summary>
    /// Đổi exception thành mã HTTP có nghĩa. Quan trọng nhất:
    /// vi phạm rào an toàn → 403 chứ không phải 500.
    /// </summary>
    private static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (UnsafeTargetException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (NotElevatedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Win32Exception ex)
        {
            return Results.Json(
                new { error = $"{ex.Message} (ma loi Windows: {ex.NativeErrorCode})" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
