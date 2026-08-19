using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Detection;

/// <summary>Một dòng blacklist khớp, kèm chỗ nó khớp để viết câu bằng chứng.</summary>
internal sealed record BlacklistMatch(BlacklistEntry Entry, string MatchedIn, string MatchedValue);

/// <summary>
/// So một event với danh sách blacklist. HÀM THUẦN — nhận sẵn danh sách, không tự đi
/// hỏi DB, nên test được không cần Postgres và chạy được trong hot path.
///
/// Việc nạp/cache danh sách là của <see cref="BlacklistRegistry"/>.
/// </summary>
internal static class BlacklistMatcher
{
    /// <summary>
    /// Chuẩn hoá giá trị trước khi lưu VÀ trước khi so — hai bên phải dùng chung đúng
    /// một hàm, nếu không dòng blacklist ghi bằng chữ hoa sẽ không bao giờ khớp.
    /// </summary>
    internal static string Normalize(string? value) =>
        value?.Trim().Trim('"').ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Mọi dòng blacklist khớp với event này. Trả danh sách chứ không phải dòng đầu
    /// tiên: một event có thể dính nhiều dấu hiệu, và người xem cần thấy đủ.
    /// </summary>
    internal static IReadOnlyList<BlacklistMatch> Match(
        WindowsMonitorEvent evt, IReadOnlyList<BlacklistEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        // Bóc sẵn một lần, dùng lại cho mọi dòng blacklist - danh sách có thể dài.
        var taskExe = ExecutablePathParser.ExtractAndNormalize(evt.TaskCommand);
        var serviceExe = ExecutablePathParser.ExtractAndNormalize(evt.ImagePath);

        var taskFile = ExecutablePathParser.FileName(evt.TaskCommand);
        var serviceFile = ExecutablePathParser.FileName(evt.ImagePath);

        List<BlacklistMatch> matches = [];

        foreach (var entry in entries)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            var match = entry.Kind switch
            {
                BlacklistKind.ExecutablePath => MatchPath(entry, taskExe, serviceExe),
                BlacklistKind.FileName => MatchFileName(entry, taskFile, serviceFile),
                BlacklistKind.CommandFragment => MatchFragment(entry, evt),
                BlacklistKind.Account => MatchAccount(entry, evt),
                _ => null
            };

            if (match is not null)
            {
                matches.Add(match);
            }
        }

        return matches;
    }

    /// <summary>
    /// Đường dẫn: so BẰNG NHAU sau khi chuẩn hoá, không phải chuỗi con. Dùng chuỗi con
    /// ở đây thì một dòng <c>c:\a.exe</c> sẽ khớp luôn <c>c:\a.exe.backup</c>.
    /// </summary>
    private static BlacklistMatch? MatchPath(BlacklistEntry entry, string taskExe, string serviceExe)
    {
        if (taskExe.Length > 0 && Normalize(taskExe) == entry.Value)
        {
            return new BlacklistMatch(entry, "lệnh của task", taskExe);
        }

        return serviceExe.Length > 0 && Normalize(serviceExe) == entry.Value
            ? new BlacklistMatch(entry, "ImagePath của service", serviceExe)
            : null;
    }

    private static BlacklistMatch? MatchFileName(BlacklistEntry entry, string taskFile, string serviceFile)
    {
        if (taskFile.Length > 0 && Normalize(taskFile) == entry.Value)
        {
            return new BlacklistMatch(entry, "tên file lệnh của task", taskFile);
        }

        return serviceFile.Length > 0 && Normalize(serviceFile) == entry.Value
            ? new BlacklistMatch(entry, "tên file ImagePath của service", serviceFile)
            : null;
    }

    /// <summary>
    /// Chuỗi con — dạng duy nhất dùng <c>Contains</c>. Quét cả lệnh, tham số và
    /// ImagePath vì dấu hiệu kiểu <c>-enc</c> có thể nằm ở bất kỳ chỗ nào.
    /// </summary>
    private static BlacklistMatch? MatchFragment(BlacklistEntry entry, WindowsMonitorEvent evt)
    {
        (string? Value, string Where)[] haystacks =
        [
            (evt.TaskCommand, "lệnh của task"),
            (evt.TaskArguments, "tham số của task"),
            (evt.ImagePath, "ImagePath của service")
        ];

        foreach (var (value, where) in haystacks)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                value.Contains(entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                return new BlacklistMatch(entry, where, value);
            }
        }

        return null;
    }

    private static BlacklistMatch? MatchAccount(BlacklistEntry entry, WindowsMonitorEvent evt)
    {
        (string? Value, string Where)[] accounts =
        [
            (evt.ServiceAccount, "tài khoản chạy service"),
            (evt.TaskRunAsUser, "principal chạy task")
        ];

        foreach (var (value, where) in accounts)
        {
            if (!string.IsNullOrWhiteSpace(value) && Normalize(value) == entry.Value)
            {
                return new BlacklistMatch(entry, where, value);
            }
        }

        return null;
    }
}
