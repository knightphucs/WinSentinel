using Microsoft.EntityFrameworkCore;
using TaskServiceMonitor.Configuration;
using TaskServiceMonitor.Data;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Detection;

/// <summary>
/// Rule cần nhìn NHIỀU event chứ không chỉ một, nên phải truy vấn DB — vì vậy tách
/// khỏi <see cref="RuleCatalog"/> (vốn là hàm thuần, test được không cần DB).
///
/// Đây chính là phần "phân tích tương quan hành vi" mentor nêu: nhìn chuỗi event
/// theo <c>ObjectName</c> + cửa sổ thời gian, thay vì chấm điểm từng event rời rạc.
///
/// Đăng ký <b>scoped</b> vì phụ thuộc <see cref="MonitorDbContext"/>.
/// </summary>
internal sealed class CorrelationRules(MonitorDbContext db, AlertingOptions options)
{
    /// <summary>Event ID nghĩa là "task vừa được tạo".</summary>
    private static readonly int[] TaskCreateEventIds = [4698, 106];

    /// <summary>Event ID nghĩa là "task vừa bị xoá".</summary>
    private static readonly int[] TaskDeleteEventIds = [4699, 141];

    /// <summary>Event ID nghĩa là "định nghĩa task vừa bị ghi đè".</summary>
    private static readonly int[] TaskUpdateEventIds = [4702, 140];

    /// <summary>Event ID nghĩa là "service vừa được cài hoặc vừa bị đổi cấu hình".</summary>
    private static readonly int[] ServiceChangeEventIds = [7045, 4697, 7040, 4657];

    internal async Task<IReadOnlyList<(string RuleId, string RuleName, RuleHit Hit)>> EvaluateAsync(
        WindowsMonitorEvent evt, CancellationToken ct = default)
    {
        List<(string, string, RuleHit)> hits = [];

        if (string.IsNullOrWhiteSpace(evt.ObjectName))
        {
            // Không có tên đối tượng thì không đối chiếu được với event nào khác.
            return hits;
        }

        var commandChanged = await EvaluateCommandChangedAsync(evt, ct);
        if (commandChanged is not null)
        {
            hits.Add((RuleCatalog.TaskCommandChanged,
                "Lệnh của task bị đổi so với lần ghi nhận trước", commandChanged));
        }

        var createThenDelete = await EvaluateCreateThenDeleteAsync(evt, ct);
        if (createThenDelete is not null)
        {
            hits.Add((RuleCatalog.TaskCreateThenDelete,
                "Task vừa tạo đã bị xoá ngay", createThenDelete));
        }

        return hits;
    }

    /// <summary>
    /// Event 4702 CHỈ mang bản mới (<c>TaskContentNew</c>), tự nó không cho biết đã đổi
    /// từ gì sang gì. Rule này lấp đúng chỗ đó bằng cách so với bản ghi gần nhất cùng
    /// tên task trong DB.
    /// </summary>
    private async Task<RuleHit?> EvaluateCommandChangedAsync(
        WindowsMonitorEvent evt, CancellationToken ct)
    {
        if (!TaskUpdateEventIds.Contains(evt.EventId) || string.IsNullOrWhiteSpace(evt.TaskCommand))
        {
            return null;
        }

        // Bản ghi gần nhất TRƯỚC event này, cùng máy + cùng tên task, có lệnh đọc được.
        // Loại chính nó ra: AlertEvaluator chạy SAU khi event đã được lưu.
        var previous = await db.Events
            .Where(x => x.Id != evt.Id
                && x.Hostname == evt.Hostname
                && x.ObjectName == evt.ObjectName
                && x.ObjectType == MonitoredObjectType.ScheduledTask
                && x.TaskCommand != null
                && x.TimeCreated <= evt.TimeCreated)
            .OrderByDescending(x => x.TimeCreated)
            .Select(x => new { x.TaskCommand, x.TaskArguments })
            .FirstOrDefaultAsync(ct);

        if (previous?.TaskCommand is null)
        {
            return null;
        }

        var commandChanged = !string.Equals(
            previous.TaskCommand, evt.TaskCommand, StringComparison.OrdinalIgnoreCase);

        var argumentsChanged = !string.Equals(
            previous.TaskArguments ?? string.Empty,
            evt.TaskArguments ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        if (!commandChanged && !argumentsChanged)
        {
            return null;
        }

        // Lệnh mới trỏ vào chỗ đáng ngờ thì đây không còn là "một thay đổi cấu hình"
        // bình thường nữa.
        var newLooksSuspicious =
            SuspiciousIndicators.MatchWritableDirectory(evt.TaskCommand) is not null ||
            SuspiciousIndicators.MatchWritableDirectoryInText(evt.TaskArguments) is not null ||
            SuspiciousIndicators.MatchLivingOffTheLandBinary(evt.TaskCommand) is not null ||
            SuspiciousIndicators.MatchSuspiciousCommandFragment(evt.TaskCommand, evt.TaskArguments) is not null;

        var before = Describe(previous.TaskCommand, previous.TaskArguments);
        var after = Describe(evt.TaskCommand, evt.TaskArguments);

        return new RuleHit
        {
            Severity = newLooksSuspicious ? RiskLevel.High : RiskLevel.Medium,
            Evidence = $"Task '{evt.ObjectName}' đổi lệnh: {before} → {after}",
            Recommendation = newLooksSuspicious
                ? "Lệnh mới khớp dấu hiệu đáng ngờ — xem ngay định nghĩa task."
                : "Đối chiếu với thay đổi có kế hoạch."
        };
    }

    /// <summary>
    /// Task được tạo rồi bị xoá trong thời gian ngắn — chạy một lần rồi dọn dấu vết.
    /// Chấm điểm từng event rời rạc không bao giờ thấy được hành vi này: cả "tạo" lẫn
    /// "xoá" tách riêng đều là thao tác bình thường.
    /// </summary>
    private async Task<RuleHit?> EvaluateCreateThenDeleteAsync(
        WindowsMonitorEvent evt, CancellationToken ct)
    {
        if (!TaskDeleteEventIds.Contains(evt.EventId))
        {
            return null;
        }

        var window = TimeSpan.FromMinutes(Math.Max(1, options.CorrelationWindowMinutes));
        var since = evt.TimeCreated - window;

        var created = await db.Events
            .Where(x => x.Id != evt.Id
                && x.Hostname == evt.Hostname
                && x.ObjectName == evt.ObjectName
                && TaskCreateEventIds.Contains(x.EventId)
                && x.TimeCreated >= since
                && x.TimeCreated <= evt.TimeCreated)
            .OrderByDescending(x => x.TimeCreated)
            .Select(x => new { x.TimeCreated, x.TaskCommand, x.ActorAccount })
            .FirstOrDefaultAsync(ct);

        if (created is null)
        {
            return null;
        }

        // Task nay da bi xoa bao nhieu lan TRUOC day? Tao roi xoa MOT lan la dau hieu
        // don dau vet; lam di lam lai hang nghin lan la thoi quen cua phan mem.
        //
        // Khong co buoc nay thi rule sinh 4.419 canh bao High tren du lieu that, trong
        // do 4.415 la cua dung hai task driver am thanh Nahimic. Loc theo MAU LAP chu
        // khong theo ten hang - them mot phan mem "hay quan" nua thi khong phai sua code.
        var previousDeletions = await db.Events
            .CountAsync(x => x.Id != evt.Id
                && x.Hostname == evt.Hostname
                && x.ObjectName == evt.ObjectName
                && TaskDeleteEventIds.Contains(x.EventId)
                && x.TimeCreated < evt.TimeCreated, ct);

        if (previousDeletions >= Math.Max(1, options.CreateDeleteRepeatThreshold))
        {
            return null;
        }

        var lifetime = evt.TimeCreated - created.TimeCreated;

        return new RuleHit
        {
            Severity = RiskLevel.High,
            Evidence =
                $"Task '{evt.ObjectName}' tồn tại {FormatDuration(lifetime)} rồi bị xoá " +
                $"(tạo bởi {created.ActorAccount ?? "(không rõ)"}" +
                (created.TaskCommand is null ? "" : $", lệnh: {created.TaskCommand}") + ")",
            Recommendation = "Task sống rất ngắn — kiểm tra event 200/201 xem nó đã kịp chạy chưa."
        };
    }

    /// <summary>
    /// Service vừa bị cài hoặc đổi cấu hình ngay trước lúc crash không? Dùng để nâng
    /// <c>SERVICE_CRASH</c> từ Medium lên High — service crash ngay sau khi binary bị
    /// thay là dấu hiệu thay thế binary hỏng, không phải lỗi ngẫu nhiên.
    /// </summary>
    internal async Task<DateTime?> FindRecentServiceChangeAsync(
        WindowsMonitorEvent evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.ObjectName))
        {
            return null;
        }

        var since = evt.TimeCreated - TimeSpan.FromHours(Math.Max(1, options.ServiceChangeLookbackHours));

        var changed = await db.Events
            .Where(x => x.Id != evt.Id
                && x.Hostname == evt.Hostname
                && x.ObjectName == evt.ObjectName
                && ServiceChangeEventIds.Contains(x.EventId)
                && x.TimeCreated >= since
                && x.TimeCreated <= evt.TimeCreated)
            .OrderByDescending(x => x.TimeCreated)
            .Select(x => (DateTime?)x.TimeCreated)
            .FirstOrDefaultAsync(ct);

        return changed;
    }

    private static string Describe(string? command, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? command ?? "(không có)" : $"{command} {arguments}";

    private static string FormatDuration(TimeSpan value) =>
        value.TotalMinutes < 1
            ? $"{Math.Max(0, (int)value.TotalSeconds)} giây"
            : $"{(int)value.TotalMinutes} phút";
}
