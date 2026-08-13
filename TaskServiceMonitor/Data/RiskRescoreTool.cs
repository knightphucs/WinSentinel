using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Monitoring;

namespace TaskServiceMonitor.Data;

/// <summary>
/// Chấm lại <c>RiskLevel</c> cho toàn bộ event đã có trong DB.
///
/// Cần vì các event lưu trước khi có RiskScorer đều mang giá trị mặc định <c>Low</c>.
/// CỐ Ý dùng lại chính class <see cref="RiskScorer"/> chứ không viết lại rule bằng
/// SQL — một nguồn sự thật duy nhất, backfill không bao giờ lệch với lúc chạy thật.
/// </summary>
public static class RiskRescoreTool
{
    public static async Task<int> RunAsync(MonitorDbContext db, RiskScorer scorer)
    {
        var events = await db.Events.ToListAsync();
        Console.WriteLine($"Doc {events.Count} event tu DB, dang cham lai diem rui ro...");

        var changed = 0;
        foreach (var evt in events)
        {
            var newLevel = scorer.Score(evt);
            if (evt.RiskLevel == newLevel)
            {
                continue;
            }

            // Entity dang duoc theo doi -> ghi thang vao entry roi SaveChanges mot lan.
            db.Entry(evt).Property(e => e.RiskLevel).CurrentValue = newLevel;
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync();
        }

        var summary = events
            .GroupBy(e => scorer.Score(e))
            .OrderByDescending(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}");

        Console.WriteLine($"Da cap nhat {changed} dong. Phan bo: {string.Join(", ", summary)}");
        return 0;
    }
}
