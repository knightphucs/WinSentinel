using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Detection;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Api;

/// <summary>
/// API cho tầng cảnh báo (bước 11). Cùng khuôn với <see cref="EventEndpoints"/>:
/// static extension class, Minimal API, không controller.
/// </summary>
public static class AlertEndpoints
{
    private const int DefaultTake = 100;
    private const int MaxTake = 500;

    /// <summary>
    /// Các mức từ <paramref name="minimum"/> trở lên, dạng danh sách để dịch thành
    /// <c>Severity IN ('Medium','High')</c>.
    ///
    /// BẮT BUỘC làm theo kiểu này, KHÔNG được viết <c>a.Severity &gt;= minimum</c>:
    /// cột <c>Severity</c> lưu thành CHUỖI (<c>HasConversion&lt;string&gt;</c>), nên
    /// EF sẽ dịch phép so sánh đó thành so chuỗi theo bảng chữ cái — mà
    /// <c>'High' &gt;= 'Medium'</c> là FALSE. Lọc "Medium trở lên" sẽ âm thầm nuốt mất
    /// đúng nhóm High, tức là giấu đi những cảnh báo nghiêm trọng nhất.
    /// </summary>
    internal static RiskLevel[] SeverityAtLeast(RiskLevel minimum) =>
        [.. Enum.GetValues<RiskLevel>().Where(level => level >= minimum)];

    public static void MapAlertEndpoints(this WebApplication app)
    {
        app.MapGet("/api/alerts", GetAlerts);
        app.MapGet("/api/alerts/summary", GetSummary);
        app.MapGet("/api/alerts/rules", GetRules);
        app.MapPost("/api/alerts/{id:guid}/acknowledge", Acknowledge);
        app.MapPost("/api/alerts/acknowledge-all", AcknowledgeAll);
    }

    /// <summary>
    /// GET /api/alerts?severity=&amp;host=&amp;ruleId=&amp;objectName=&amp;acknowledged=&amp;from=&amp;to=&amp;take=100
    /// — mới nhất trước.
    ///
    /// <c>from</c>/<c>to</c> lọc theo <see cref="Alert.EventTime"/> (lúc hành vi xảy ra)
    /// chứ KHÔNG phải <see cref="Alert.DetectedAt"/> (lúc app chấm rule). Hai mốc này
    /// lệch hẳn nhau khi đọc bù sau restart hoặc khi chạy <c>--rebuild-alerts</c>: cùng
    /// một hành vi lúc 2 giờ sáng có thể mang <c>DetectedAt</c> là 9 giờ sáng hôm sau.
    /// Người dùng lọc "hôm qua" là đang hỏi về lúc hành vi xảy ra.
    /// </summary>
    private static async Task<IResult> GetAlerts(
        MonitorDbContext db,
        string? severity,
        string? host,
        string? ruleId,
        string? objectName,
        bool? acknowledged,
        string? from,
        string? to,
        int? take,
        CancellationToken ct)
    {
        if (!TimeRangeFilter.TryParse(from, out var fromUtc))
        {
            return TimeRangeFilter.Invalid("from", from!);
        }

        if (!TimeRangeFilter.TryParse(to, out var toUtc))
        {
            return TimeRangeFilter.Invalid("to", to!);
        }

        var query = db.Alerts.AsNoTracking();

        if (fromUtc is DateTime since)
        {
            query = query.Where(a => a.EventTime >= since);
        }

        if (toUtc is DateTime until)
        {
            query = query.Where(a => a.EventTime <= until);
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (!Enum.TryParse<RiskLevel>(severity, ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new
                {
                    error = $"Gia tri 'severity' khong hop le: '{severity}'.",
                    validValues = Enum.GetNames<RiskLevel>()
                });
            }

            // Loc "tu muc nay tro len" chu khong phai dung bang: tab Canh bao mac dinh
            // mo o Medium va nguoi dung mong doi thay ca High.
            var atLeast = SeverityAtLeast(parsed);
            query = query.Where(a => atLeast.Contains(a.Severity));
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            query = query.Where(a => a.Hostname == host);
        }

        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            query = query.Where(a => a.RuleId == ruleId);
        }

        if (!string.IsNullOrWhiteSpace(objectName))
        {
            query = query.Where(a => a.ObjectName == objectName);
        }

        if (acknowledged is bool ack)
        {
            query = query.Where(a => a.Acknowledged == ack);
        }

        var limit = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var items = await query
            .OrderByDescending(a => a.DetectedAt)
            .Take(limit)
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    /// <summary>
    /// GET /api/alerts/summary — số cảnh báo theo mức và theo rule, cắt theo
    /// 1 giờ / 24 giờ / 7 ngày, kèm số chưa xử lý cho badge trên tab.
    /// </summary>
    /// <remarks>
    /// BẪY giống <c>EventEndpoints.GetSummary</c>: các truy vấn chạy TUẦN TỰ, KHÔNG
    /// được <c>Task.WhenAll</c> — một <c>DbContext</c> scoped không an toàn khi chạy
    /// nhiều operation đồng thời trên cùng instance.
    /// </remarks>
    private static async Task<IResult> GetSummary(MonitorDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var bySeverity = await db.Alerts.AsNoTracking()
            .GroupBy(a => a.Severity)
            .Select(g => new
            {
                Severity = g.Key,
                LastHour = g.Count(a => a.DetectedAt >= now.AddHours(-1)),
                Last24h = g.Count(a => a.DetectedAt >= now.AddHours(-24)),
                Last7d = g.Count(a => a.DetectedAt >= now.AddDays(-7)),
                Total = g.Count()
            })
            .ToListAsync(ct);

        var byRule = await db.Alerts.AsNoTracking()
            .GroupBy(a => new { a.RuleId, a.RuleName, a.Severity })
            .Select(g => new
            {
                g.Key.RuleId,
                g.Key.RuleName,
                g.Key.Severity,
                Last24h = g.Count(a => a.DetectedAt >= now.AddHours(-24)),
                Last7d = g.Count(a => a.DetectedAt >= now.AddDays(-7)),
                Total = g.Count()
            })
            .ToListAsync(ct);

        var unacknowledged = await db.Alerts.AsNoTracking()
            .CountAsync(a => !a.Acknowledged, ct);

        var unacknowledgedHigh = await db.Alerts.AsNoTracking()
            .CountAsync(a => !a.Acknowledged && a.Severity == RiskLevel.High, ct);

        // Luon tra du 3 muc, ke ca muc dang rong - frontend khong phai tu doan.
        var severityRows = Enum.GetValues<RiskLevel>()
            .Select(level =>
            {
                var row = bySeverity.FirstOrDefault(r => r.Severity == level);

                return new
                {
                    severity = level.ToString(),
                    lastHour = row?.LastHour ?? 0,
                    last24h = row?.Last24h ?? 0,
                    last7d = row?.Last7d ?? 0,
                    total = row?.Total ?? 0
                };
            })
            .ToList();

        return Results.Ok(new
        {
            bySeverity = severityRows,
            byRule = byRule.OrderByDescending(r => r.Total).ToList(),
            unacknowledged,
            unacknowledgedHigh
        });
    }

    /// <summary>
    /// GET /api/alerts/rules — trả nguyên danh mục rule.
    ///
    /// Nhờ endpoint này, bảng phân rã "hành vi → Event ID" của mentor hiện được ngay
    /// trong giao diện chứ không chỉ nằm trong docs/hanh-vi-mapping.md — đọc app là
    /// biết app đang tìm những gì.
    /// </summary>
    private static IResult GetRules() => Results.Ok(RuleCatalog.Describe());

    /// <summary>POST /api/alerts/{id}/acknowledge — đánh dấu đã xử lý.</summary>
    private static async Task<IResult> Acknowledge(
        MonitorDbContext db, Guid id, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (alert is null)
        {
            return Results.NotFound();
        }

        if (alert.Acknowledged)
        {
            return Results.Ok(alert);
        }

        // Alert la record immutable -> khong gan truc tiep duoc, phai di qua
        // ChangeTracker bang SetValues.
        var updated = alert with { Acknowledged = true, AcknowledgedAt = DateTime.UtcNow };

        db.Entry(alert).CurrentValues.SetValues(updated);
        await db.SaveChangesAsync(ct);

        return Results.Ok(updated);
    }

    /// <summary>
    /// POST /api/alerts/acknowledge-all?severity=&amp;ruleId=&amp;from=&amp;to= — đánh dấu hàng loạt.
    /// Dùng <c>ExecuteUpdateAsync</c> để không phải nạp hàng nghìn dòng vào bộ nhớ chỉ
    /// để đổi một cột.
    ///
    /// PHẢI nhận cùng bộ tham số lọc như <c>GetAlerts</c>, kể cả <c>from</c>/<c>to</c>:
    /// nút "Đánh dấu đã xử lý hết" nằm ngay cạnh bộ lọc nên người dùng hiểu là "hết
    /// những gì đang thấy". Thiếu một tham số ở đây là âm thầm đánh dấu cả những cảnh
    /// báo ngoài màn hình — thao tác không hoàn tác được.
    /// </summary>
    private static async Task<IResult> AcknowledgeAll(
        MonitorDbContext db, string? severity, string? ruleId,
        string? from, string? to, CancellationToken ct)
    {
        if (!TimeRangeFilter.TryParse(from, out var fromUtc))
        {
            return TimeRangeFilter.Invalid("from", from!);
        }

        if (!TimeRangeFilter.TryParse(to, out var toUtc))
        {
            return TimeRangeFilter.Invalid("to", to!);
        }

        var query = db.Alerts.Where(a => !a.Acknowledged);

        if (fromUtc is DateTime since)
        {
            query = query.Where(a => a.EventTime >= since);
        }

        if (toUtc is DateTime until)
        {
            query = query.Where(a => a.EventTime <= until);
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (!Enum.TryParse<RiskLevel>(severity, ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new
                {
                    error = $"Gia tri 'severity' khong hop le: '{severity}'.",
                    validValues = Enum.GetNames<RiskLevel>()
                });
            }

            var atLeast = SeverityAtLeast(parsed);
            query = query.Where(a => atLeast.Contains(a.Severity));
        }

        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            query = query.Where(a => a.RuleId == ruleId);
        }

        var now = DateTime.UtcNow;

        var affected = await query.ExecuteUpdateAsync(
            s => s.SetProperty(a => a.Acknowledged, true)
                  .SetProperty(a => a.AcknowledgedAt, now),
            ct);

        return Results.Ok(new { acknowledged = affected });
    }
}
