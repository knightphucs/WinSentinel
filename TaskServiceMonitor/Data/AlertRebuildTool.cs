using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Detection;

namespace TaskServiceMonitor.Data;

/// <summary>
/// Chạy tầng phát hiện trên toàn bộ event ĐÃ LƯU để dựng lại bảng cảnh báo.
///
/// Hai công dụng, đều quan trọng:
/// <list type="number">
///   <item>Tab Cảnh báo có dữ liệu ngay từ lịch sử đã thu, không phải chờ event mới.</item>
///   <item><b>Đây là cách đo tỉ lệ dương tính giả.</b> Chạy trên dữ liệu thật rồi đếm
///   theo từng rule — danh sách LOLBin và thư mục đáng ngờ phải chỉnh theo số này chứ
///   không chốt bằng cảm tính. <c>rundll32.exe</c> là nguồn dương tính giả điển hình.</item>
/// </list>
///
/// Chạy lại bao nhiêu lần cũng không nhân đôi: unique index <c>IX_Alerts_Dedup</c>
/// trên <c>(SourceEventId, RuleId)</c> chặn, giống cách <c>IX_Events_Dedup</c> chặn
/// event trùng.
/// </summary>
/// <remarks>
/// <c>internal</c> theo <see cref="AlertEvaluator"/> — chỉ Program.cs gọi, không nới
/// ra <c>public</c> chỉ để cho khớp kiểu truy cập.
/// </remarks>
internal static class AlertRebuildTool
{
    internal static async Task<int> RunAsync(MonitorDbContext db, AlertEvaluator evaluator)
    {
        var total = await db.Events.CountAsync();
        Console.WriteLine($"Doc {total} event tu DB, dang cham lai toan bo rule...");

        var created = 0;
        var skipped = 0;
        var processed = 0;

        Dictionary<string, int> byRule = [];
        Dictionary<string, int> bySeverity = [];

        // Doc theo lo: 12.000+ event keo het vao bo nho mot luc la khong can thiet.
        const int BatchSize = 500;

        for (var offset = 0; offset < total; offset += BatchSize)
        {
            var batch = await db.Events
                .AsNoTracking()
                .OrderBy(e => e.TimeCreated)
                .Skip(offset)
                .Take(BatchSize)
                .ToListAsync();

            foreach (var evt in batch)
            {
                var alerts = await evaluator.EvaluateAndSaveAsync(evt);

                foreach (var alert in alerts)
                {
                    created++;
                    byRule[alert.RuleId] = byRule.GetValueOrDefault(alert.RuleId) + 1;

                    var severity = alert.Severity.ToString();
                    bySeverity[severity] = bySeverity.GetValueOrDefault(severity) + 1;
                }

                processed++;
            }

            Console.WriteLine($"  ...da cham {processed}/{total} event, sinh {created} canh bao moi");
        }

        skipped = await db.Alerts.CountAsync() - created;

        Console.WriteLine();
        Console.WriteLine($"Da cham:        {processed} event");
        Console.WriteLine($"Canh bao moi:   {created}");
        Console.WriteLine($"Da co san:      {Math.Max(0, skipped)} (bo qua nho IX_Alerts_Dedup)");
        Console.WriteLine();

        Console.WriteLine("Theo muc:");
        foreach (var (severity, count) in bySeverity.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"  {severity,-8} {count}");
        }

        Console.WriteLine();
        Console.WriteLine("Theo rule - RA TAY CAC RULE HIGH TRUOC KHI CHOT DANH SACH:");
        foreach (var (ruleId, count) in byRule.OrderByDescending(x => x.Value))
        {
            Console.WriteLine($"  {ruleId,-28} {count}");
        }

        return 0;
    }
}
