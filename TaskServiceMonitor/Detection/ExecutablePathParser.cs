namespace TaskServiceMonitor.Detection;

/// <summary>
/// Bóc phần đường dẫn file thực thi ra khỏi một dòng lệnh thô, và chuẩn hoá về
/// dạng so sánh được.
///
/// Vì sao cần một lớp riêng thay vì <c>Contains("\\Temp\\")</c>: giá trị thật lấy
/// từ event log KHÔNG phải đường dẫn thuần. Ba dạng gặp trên máy thật:
/// <code>
/// "C:\Program Files\App\svc.exe" -k netsvcs   ← có dấu nháy + tham số
/// \??\C:\Windows\System32\drivers\x.sys        ← tiền tố NT namespace
/// \SystemRoot\System32\drivers\y.sys           ← tương đối theo SystemRoot
/// </code>
/// Ngoài ra task lưu <b>nguyên văn</b> biến môi trường (<c>%TEMP%\a.exe</c>) chứ
/// không giãn sẵn, nên phải so được cả hai dạng.
///
/// Thuần hàm, không đụng registry/WinAPI ⇒ test được không cần Windows.
/// </summary>
internal static class ExecutablePathParser
{
    /// <summary>Tiền tố NT namespace cần cắt bỏ trước khi so sánh.</summary>
    private const string NtObjectPrefix = @"\??\";

    /// <summary>
    /// <c>\SystemRoot</c> tương đương <c>C:\Windows</c>. Driver hệ thống dùng dạng
    /// này rất nhiều; không quy đổi thì chúng bị tính nhầm là "ngoài thư mục hệ thống".
    /// </summary>
    private const string SystemRootPrefix = @"\SystemRoot";

    /// <summary>
    /// Bóc exe khỏi dòng lệnh. Trả về chuỗi đã cắt tham số nhưng CHƯA giãn biến
    /// môi trường — bên gọi tự quyết định có giãn hay không (xem <see cref="Normalize"/>).
    /// </summary>
    /// <remarks>
    /// Cùng thuật toán với <c>InputPolicy.ExtractExecutablePath</c>; lớp đó gọi
    /// sang đây để hai bên không bao giờ lệch nhau.
    /// </remarks>
    internal static string ExtractExecutable(string raw)
    {
        var text = raw.Trim();

        if (text.Length == 0)
        {
            return text;
        }

        // Có dấu nháy thì phần trong nháy là đường dẫn, kể cả khi chứa khoảng trắng.
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            return closing > 1 ? text[1..closing] : text[1..];
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// Chuẩn hoá đường dẫn về dạng so sánh được: cắt tiền tố NT, quy đổi
    /// <c>\SystemRoot</c>, đổi <c>/</c> thành <c>\</c>, bỏ dấu nháy thừa.
    ///
    /// CỐ Ý KHÔNG gọi <c>Path.GetFullPath</c>: đường dẫn ở đây đến từ event log của
    /// máy khác, file có thể không tồn tại trên máy đang chạy app, và
    /// <c>GetFullPath</c> sẽ ghép nhầm với thư mục làm việc hiện tại cho đường dẫn
    /// tương đối. Đây là khác biệt căn bản so với <c>InputPolicy</c> — lớp đó xét
    /// đường dẫn SẮP chạy trên chính máy này nên bắt buộc phải chuẩn hoá tuyệt đối.
    /// </summary>
    internal static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var text = path.Trim().Trim('"').Replace('/', '\\');

        if (text.StartsWith(NtObjectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[NtObjectPrefix.Length..];
        }

        if (text.StartsWith(SystemRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = @"C:\Windows" + text[SystemRootPrefix.Length..];
        }

        return text;
    }

    /// <summary>
    /// Bóc exe rồi chuẩn hoá — dạng dùng nhiều nhất, gộp lại để chỗ gọi không quên
    /// mất một bước.
    /// </summary>
    internal static string ExtractAndNormalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? string.Empty : Normalize(ExtractExecutable(raw));

    /// <summary>
    /// Bản đã giãn biến môi trường THEO MÁY ĐANG CHẠY, dùng bổ sung chứ không thay
    /// thế bản gốc.
    ///
    /// ⚠️ Kết quả chỉ đúng khi event đến từ chính máy này. Với event forward qua WEF
    /// thì <c>%TEMP%</c> của máy nguồn khác máy collector — vì vậy rule luôn so
    /// <b>cả hai</b> bản (gốc + đã giãn), không bao giờ chỉ dựa vào bản giãn.
    /// </summary>
    internal static string ExpandLocally(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.Contains('%'))
        {
            return path;
        }

        try
        {
            return Environment.ExpandEnvironmentVariables(path);
        }
        catch (Exception)
        {
            // Chuỗi dị dạng thì giữ nguyên bản gốc - không được để rule nổ vì một
            // giá trị lạ trong log.
            return path;
        }
    }

    /// <summary>Tên file (kèm đuôi) của một dòng lệnh, chữ thường. Rỗng nếu không bóc được.</summary>
    internal static string FileName(string? raw)
    {
        var normalized = ExtractAndNormalize(raw);

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var slash = normalized.LastIndexOf('\\');
        var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        return name.ToLowerInvariant();
    }
}
