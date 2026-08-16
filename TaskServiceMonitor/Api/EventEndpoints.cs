using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Api;

public static class EventEndpoints
{
    private const int DefaultTake = 50;
    private const int MaxTake = 500;

    public static void MapEventEndpoints(this WebApplication app)
    {
        app.MapGet("/api/events", GetEvents);
        app.MapGet("/api/events/latest-by-object", GetLatestByObject);
        app.MapGet("/api/events/latest", GetLatestForObject);
        app.MapGet("/api/events/summary", GetSummary);
        app.MapGet("/api/events/{id:guid}", GetEventById);
    }

    /// <summary>Khoa gop nhom: giong bo (Event ID, Source, Log) cua Event Viewer that.</summary>
    private readonly record struct SummarySourceKey(RiskLevel RiskLevel, int EventId, string Source, string Log);

    /// <summary>
    /// GET /api/events/summary — dem event theo RiskLevel, cat theo 1 gio / 24 gio /
    /// 7 ngay, kem breakdown theo (Event ID, Source, Log) de frontend xo ra khi bam
    /// "+" tren tung dong - giong nut "+" tren "Summary of Administrative Events"
    /// that trong Event Viewer. Cho trang Overview/Summary o Dashboard. Client chi
    /// giu toi da 200 event trong bo nho nen KHONG du chinh xac cho khung "7 ngay" -
    /// phai query DB that.
    ///
    /// BAY: 3 truy van nay chay TUAN TU (await tung cai), KHONG duoc Task.WhenAll -
    /// mot DbContext scoped khong an toan khi chay nhieu operation dong thoi tren
    /// CUNG mot instance (se nem InvalidOperationException).
    /// </summary>
    private static async Task<IResult> GetSummary(MonitorDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var lastHour = await CountByRiskAndSource(db, now.AddHours(-1), ct);
        var last24h = await CountByRiskAndSource(db, now.AddHours(-24), ct);
        var last7d = await CountByRiskAndSource(db, now.AddDays(-7), ct);

        // Luon tra du 3 dong High/Medium/Low, ke ca dong nao dang rong - de frontend
        // khong phai tu doan dong nao thieu.
        var summary = Enum.GetValues<RiskLevel>()
            .Select(risk => BuildSummaryRow(risk, lastHour, last24h, last7d))
            .ToList();

        return Results.Ok(summary);
    }

    private static SummaryRowDto BuildSummaryRow(
        RiskLevel risk,
        Dictionary<SummarySourceKey, int> lastHour,
        Dictionary<SummarySourceKey, int> last24h,
        Dictionary<SummarySourceKey, int> last7d)
    {
        // Cua so 7 ngay RONG NHAT nen chac chan da co du moi key tung xuat hien -
        // dung lam danh sach nen, roi tra nguoc lai 2 cua so con lai cho tung key.
        var keys = last7d.Keys.Where(k => k.RiskLevel == risk)
            .Union(last24h.Keys.Where(k => k.RiskLevel == risk))
            .Union(lastHour.Keys.Where(k => k.RiskLevel == risk));

        var breakdown = keys
            .Select(k => new SummaryBreakdownRowDto(
                k.EventId, k.Source, k.Log,
                lastHour.GetValueOrDefault(k),
                last24h.GetValueOrDefault(k),
                last7d.GetValueOrDefault(k)))
            .OrderByDescending(b => b.Last7d)
            .ThenBy(b => b.EventId)
            .ToList();

        // Tong dong cha = cong lai cac dong con - khong query rieng mot lan nua.
        return new SummaryRowDto(
            risk.ToString(),
            breakdown.Sum(b => b.LastHour),
            breakdown.Sum(b => b.Last24h),
            breakdown.Sum(b => b.Last7d),
            breakdown);
    }

    private static async Task<Dictionary<SummarySourceKey, int>> CountByRiskAndSource(
        MonitorDbContext db, DateTime since, CancellationToken ct)
    {
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.TimeCreated >= since)
            .GroupBy(e => new { e.RiskLevel, e.EventId, e.ProviderName, e.Channel })
            .Select(g => new
            {
                g.Key.RiskLevel, g.Key.EventId, g.Key.ProviderName, g.Key.Channel, Count = g.Count()
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => new SummarySourceKey(r.RiskLevel, r.EventId, r.ProviderName, r.Channel),
            r => r.Count);
    }

    /// <summary>
    /// GET /api/events/latest-by-object?type=ScheduledTask — thoi diem event gan nhat
    /// cho tung ObjectName. Windows khong co field "ngay tao" cho task/service, nen
    /// tab Tasks/Services dung endpoint nay de sap "moi nhat" dua vao lich su event
    /// app da bat duoc (4698-4702 cho task, 4697/7040/7045 cho service).
    /// </summary>
    private static async Task<IResult> GetLatestByObject(
        MonitorDbContext db, string type, CancellationToken ct)
    {
        if (!Enum.TryParse<MonitoredObjectType>(type, ignoreCase: true, out var objectType))
        {
            return Results.BadRequest(new
            {
                error = $"Gia tri 'type' khong hop le: '{type}'.",
                validValues = Enum.GetNames<MonitoredObjectType>()
            });
        }

        var items = await db.Events.AsNoTracking()
            .Where(e => e.ObjectType == objectType && e.ObjectName != null)
            .GroupBy(e => e.ObjectName)
            .Select(g => new { objectName = g.Key!, latest = g.Max(e => e.TimeCreated) })
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    /// <summary>
    /// GET /api/events/latest?type=ScheduledTask&amp;objectName=\Foo — event gần nhất của
    /// ĐÚNG MỘT đối tượng, cho modal chi tiết Task/Service.
    ///
    /// Tách khỏi <c>latest-by-object</c> (vốn chỉ trả về thời điểm, không có Event ID):
    /// gộp thêm Event ID vào truy vấn gom nhóm đó cần lấy "cả dòng có TimeCreated lớn
    /// nhất trong mỗi nhóm" — EF Core không dịch được gọn. Modal chỉ mở một đối tượng
    /// nên hỏi riêng một dòng vừa đơn giản vừa rẻ.
    /// </summary>
    private static async Task<IResult> GetLatestForObject(
        MonitorDbContext db, string type, string objectName, CancellationToken ct)
    {
        if (!Enum.TryParse<MonitoredObjectType>(type, ignoreCase: true, out var objectType))
        {
            return Results.BadRequest(new
            {
                error = $"Gia tri 'type' khong hop le: '{type}'.",
                validValues = Enum.GetNames<MonitoredObjectType>()
            });
        }

        var latest = await db.Events.AsNoTracking()
            .Where(e => e.ObjectType == objectType && e.ObjectName == objectName)
            .OrderByDescending(e => e.TimeCreated)
            .Select(EventSummaryDto.Projection)
            .FirstOrDefaultAsync(ct);

        return latest is null ? Results.NoContent() : Results.Ok(latest);
    }

    /// <summary>
    /// GET /api/events?host=&amp;type=&amp;take=50 — mới nhất trước.
    /// </summary>
    private static async Task<IResult> GetEvents(
        MonitorDbContext db,
        string? host,
        string? type,
        string? risk,
        int? take,
        CancellationToken ct)
    {
        MonitoredObjectType? objectType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<MonitoredObjectType>(type, ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new
                {
                    error = $"Gia tri 'type' khong hop le: '{type}'.",
                    validValues = Enum.GetNames<MonitoredObjectType>()
                });
            }

            objectType = parsed;
        }

        RiskLevel? riskLevel = null;
        if (!string.IsNullOrWhiteSpace(risk))
        {
            if (!Enum.TryParse<RiskLevel>(risk, ignoreCase: true, out var parsedRisk))
            {
                return Results.BadRequest(new
                {
                    error = $"Gia tri 'risk' khong hop le: '{risk}'.",
                    validValues = Enum.GetNames<RiskLevel>()
                });
            }

            riskLevel = parsedRisk;
        }

        var limit = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var query = db.Events.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(host))
        {
            query = query.Where(e => e.Hostname == host);
        }

        if (objectType is not null)
        {
            query = query.Where(e => e.ObjectType == objectType);
        }

        if (riskLevel is not null)
        {
            query = query.Where(e => e.RiskLevel == riskLevel);
        }

        // Select truoc khi ToList de RawXml khong bi keo ve tu DB.
        // Dung chung Projection voi payload SignalR de hai ben khong lech nhau.
        var items = await query
            .OrderByDescending(e => e.TimeCreated)
            .ThenByDescending(e => e.RecordId)
            .Take(limit)
            .Select(EventSummaryDto.Projection)
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<IResult> GetEventById(
        MonitorDbContext db,
        Guid id,
        CancellationToken ct)
    {
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null)
        {
            return Results.NotFound(new { error = $"Khong tim thay event {id}." });
        }

        return Results.Ok(new EventDetailDto(
            e.Id, e.EventId, e.Hostname, e.TimeCreated, e.ObjectType, e.ObjectName,
            e.DisplayName, e.ActorAccount, e.ActorSid, e.ActionDescription, e.RiskLevel,
            e.Channel, e.ProviderName, e.RecordId, e.ImagePath, e.ServiceType, e.StartType,
            e.PreviousStartType, e.ServiceAccount, e.TaskActionType, e.TaskComHandlerClassId,
            e.TaskCommand, e.TaskArguments, e.TaskRunAsUser, e.TaskRunLevel,
            e.TaskInstanceId, e.TaskActionResultCode,
            e.Description, e.Level, e.LevelDisplayName, e.TaskCategoryId,
            e.TaskCategoryName, e.OpcodeName, e.Keywords,
            e.IsRecognized, e.Data, e.RawXml));
    }
}
