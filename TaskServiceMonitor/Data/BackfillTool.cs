using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Monitoring;

namespace TaskServiceMonitor.Data;

/// <summary>
/// Điền lại các cột tính được từ <c>RawXml</c> cho event đã có trong DB:
/// <c>RiskLevel</c> và nhóm field hiển thị thêm ở bước 8.
///
/// CỐ Ý chạy lại chính <see cref="WindowsEventParser"/> và <see cref="RiskScorer"/>
/// chứ không viết lại rule bằng SQL — một nguồn sự thật duy nhất.
///
/// GIỚI HẠN: <c>Description</c> của dòng cũ vẫn null sau khi chạy — nó không nằm
/// trong XML (xem <see cref="EventRecordDescriber"/>), chỉ event forward qua WEF mới
/// có sẵn trong <c>&lt;RenderingInfo&gt;</c>.
/// </summary>
public static class BackfillTool
{
    public static async Task<int> RunAsync(
        MonitorDbContext db, WindowsEventParser parser, RiskScorer scorer)
    {
        var events = await db.Events.ToListAsync();
        Console.WriteLine($"Doc {events.Count} event tu DB, dang tinh lai cac cot dan xuat...");

        var riskChanged = 0;
        var displayChanged = 0;
        var descriptionFilled = 0;
        var parseFailed = 0;

        foreach (var evt in events)
        {
            var entry = db.Entry(evt);

            // --- RiskLevel ---
            var newRisk = scorer.Score(evt);
            if (evt.RiskLevel != newRisk)
            {
                entry.Property(e => e.RiskLevel).CurrentValue = newRisk;
                riskChanged++;
            }

            // --- Nhom field hien thi ---
            Models.WindowsMonitorEvent reparsed;
            try
            {
                reparsed = parser.Parse(evt.RawXml);
            }
            catch (Exception ex)
            {
                parseFailed++;
                Console.WriteLine($"  [bo qua] event {evt.Id}: khong parse lai duoc RawXml ({ex.Message})");
                continue;
            }

            if (evt.Level != reparsed.Level ||
                evt.LevelDisplayName != reparsed.LevelDisplayName ||
                evt.TaskCategoryId != reparsed.TaskCategoryId ||
                evt.Keywords != reparsed.Keywords)
            {
                entry.Property(e => e.Level).CurrentValue = reparsed.Level;
                entry.Property(e => e.LevelDisplayName).CurrentValue = reparsed.LevelDisplayName;
                entry.Property(e => e.TaskCategoryId).CurrentValue = reparsed.TaskCategoryId;
                entry.Property(e => e.Keywords).CurrentValue = reparsed.Keywords;
                displayChanged++;
            }

            // KHONG ghi de gia tri dang co bang null: dong da co du lieu that (bat luc
            // chay realtime, hoi thang provider) phai duoc giu.
            if (evt.TaskCategoryName is null && reparsed.TaskCategoryName is not null)
            {
                entry.Property(e => e.TaskCategoryName).CurrentValue = reparsed.TaskCategoryName;
            }

            if (evt.OpcodeName is null && reparsed.OpcodeName is not null)
            {
                entry.Property(e => e.OpcodeName).CurrentValue = reparsed.OpcodeName;
            }

            if (evt.Description is null && reparsed.Description is not null)
            {
                entry.Property(e => e.Description).CurrentValue = reparsed.Description;
                descriptionFilled++;
            }
        }

        await db.SaveChangesAsync();

        var stillMissing = events.Count(e => e.Description is null);

        Console.WriteLine($"RiskLevel doi:        {riskChanged} dong");
        Console.WriteLine($"Level/Task/Keywords:  {displayChanged} dong");
        Console.WriteLine($"Description dien duoc: {descriptionFilled} dong (tu <RenderingInfo> cua WEF)");
        Console.WriteLine($"Description con thieu: {stillMissing} dong " +
                          "- BINH THUONG voi event cu doc tu may local, khong khoi phuc duoc.");

        if (parseFailed > 0)
        {
            Console.WriteLine($"Parse loi:            {parseFailed} dong (da bo qua)");
        }

        var summary = events
            .GroupBy(e => scorer.Score(e))
            .OrderByDescending(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}");

        Console.WriteLine($"Phan bo rui ro: {string.Join(", ", summary)}");
        return 0;
    }
}
