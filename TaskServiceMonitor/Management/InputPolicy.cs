using System.Text.RegularExpressions;
using TaskServiceMonitor.Detection;

namespace TaskServiceMonitor.Management;

/// <summary>Cấu hình đọc từ section "Management" trong appsettings.json.</summary>
public sealed record ManagementOptions
{
    public const string SectionName = "Management";

    public string WritablePrefix { get; init; } = "WinSentinel";

    /// <summary>
    /// Thư mục được phép chứa file thực thi. Để rỗng trong config = dùng
    /// <see cref="InputPolicy.DefaultDirectories"/>.
    /// </summary>
    /// <remarks>
    /// CỐ Ý để mặc định rỗng, KHÔNG đặt sẵn giá trị — ConfigurationBinder của .NET khi
    /// bind mảng sẽ NỐI THÊM chứ không ghi đè (bẫy đã dính một lần với
    /// <c>EventLogOptions.Channels</c>).
    /// </remarks>
    public string[] AllowedExecutableDirectories { get; init; } = [];

    /// <summary>Tên file thực thi được phép. Rỗng = dùng <see cref="InputPolicy.DefaultExecutables"/>.</summary>
    public string[] AllowedExecutables { get; init; } = [];

    public int MaxNameLength { get; init; } = 64;
    public int MaxArgumentsLength { get; init; } = 256;
}

/// <summary>
/// Whitelist cho các GIÁ TRỊ mà người dùng nhập — bổ sung cho
/// <see cref="SafeNameGuard"/> vốn chỉ xét TÊN đối tượng.
///
/// Vì sao cần: trước bước 10, <c>SafeNameGuard</c> chặn 9 chỗ nhưng tất cả đều nhận
/// tên. Còn <c>BinaryPath</c> của service đi thẳng vào <c>CreateServiceW</c> với
/// account <c>null</c> — tức service tạo ra chạy <b>LocalSystem</b> và start được
/// ngay qua API. Một ô text trên trình duyệt thành thực thi quyền SYSTEM.
///
/// ⚠️ GIỚI HẠN — ĐỌC KỸ, ĐỪNG NÓI QUÁ VỀ MỨC BẢO VỆ:
/// Lớp này chặn ĐƯỜNG DẪN, không phải hành vi. Một khi <c>cmd.exe</c> nằm trong danh
/// sách cho phép thì <c>cmd.exe /c &lt;bất cứ gì&gt;</c> vẫn chạy được. Nó thu hẹp bề
/// mặt tấn công (không đăng ký được binary lạ, không trỏ ra UNC, không thoát thư mục)
/// chứ KHÔNG phải sandbox. Muốn chặn hành vi thì phải lọc cả tham số, hoặc bỏ hẳn
/// <c>cmd.exe</c>/<c>powershell.exe</c> khỏi whitelist.
/// </summary>
public sealed class InputPolicy
{
    public static readonly string[] DefaultDirectories = [@"C:\Windows\System32"];

    /// <summary>
    /// CỐ Ý không có <c>powershell.exe</c>: nó nhận <c>-EncodedCommand</c>, đúng thứ
    /// mà <c>RiskScorer</c> của dự án chấm là High.
    /// </summary>
    public static readonly string[] DefaultExecutables =
        ["cmd.exe", "notepad.exe", "calc.exe", "snmptrap.exe"];

    // Ten task/service: chi chu, so, gach duoi, gach ngang, cham.
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly string[] _directories;
    private readonly HashSet<string> _executables;
    private readonly int _maxNameLength;
    private readonly int _maxArgumentsLength;

    public InputPolicy(ManagementOptions options)
    {
        // Chuan hoa thu muc NGAY luc khoi tao, kem dau '\' cuoi - de "C:\WindowsEvil"
        // khong khop tien to "C:\Windows".
        _directories = (options.AllowedExecutableDirectories.Length > 0
                ? options.AllowedExecutableDirectories
                : DefaultDirectories)
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)) + Path.DirectorySeparatorChar)
            .ToArray();

        _executables = new HashSet<string>(
            options.AllowedExecutables.Length > 0 ? options.AllowedExecutables : DefaultExecutables,
            StringComparer.OrdinalIgnoreCase);

        _maxNameLength = options.MaxNameLength;
        _maxArgumentsLength = options.MaxArgumentsLength;
    }

    public IReadOnlyList<string> AllowedDirectories => _directories;
    public IReadOnlyCollection<string> AllowedExecutables => _executables;

    /// <summary>
    /// Bóc phần đường dẫn exe ra khỏi dòng lệnh. <c>BinaryPath</c> của service là CẢ
    /// dòng lệnh (<c>"C:\x\a.exe" -flag</c>), không phải chỉ đường dẫn.
    /// </summary>
    /// <remarks>
    /// Uỷ quyền cho <see cref="ExecutablePathParser.ExtractExecutable"/> (bước 11) để
    /// lớp whitelist này và tầng phát hiện bóc đường dẫn theo đúng một cách. Trước đó
    /// hai bên có hai bản sao, sửa một bên là lệch.
    ///
    /// Chỉ dùng chung phần BÓC. Phần chuẩn hoá thì cố ý khác nhau: ở đây đường dẫn sắp
    /// được chạy trên chính máy này nên bắt buộc <c>Path.GetFullPath</c> (bước 3 bên
    /// dưới), còn tầng phát hiện xử lý đường dẫn đến từ máy khác nên không được phép
    /// ghép với thư mục làm việc hiện tại.
    /// </remarks>
    internal static string ExtractExecutablePath(string raw) =>
        ExecutablePathParser.ExtractExecutable(raw);

    /// <summary>
    /// Bắt buộc đường dẫn nằm trong whitelist. Bảy bước, THỨ TỰ QUAN TRỌNG — mỗi bước
    /// bịt một kiểu lách khác nhau.
    /// </summary>
    public string EnsureAllowedExecutable(string? raw, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new UnsafeTargetException($"Thieu '{fieldName}'.");
        }

        // 1. Bóc exe khỏi tham số.
        var candidate = ExtractExecutablePath(raw);

        // 2. Giãn %SystemRoot% - không giãn thì bước so sánh thư mục vô nghĩa.
        candidate = Environment.ExpandEnvironmentVariables(candidate);

        // 3. Chuẩn hoá: giải '..'. BẮT BUỘC trước khi so, nếu không
        //    "C:\Windows\System32\..\..\Users\x\evil.exe" sẽ lọt vì vẫn khớp tiền tố.
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UnsafeTargetException($"'{fieldName}' khong phai duong dan hop le: {raw}");
        }

        // 4. Phải là đường dẫn tuyệt đối có ổ đĩa — chặn UNC \\may-khac\share\evil.exe.
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new UnsafeTargetException(
                $"'{fieldName}' phai la duong dan tuyet doi tren o dia noi bo (khong nhan UNC): {raw}");
        }

        // 5. File phải tồn tại — chặn đăng ký đường dẫn định thả file vào sau.
        if (!File.Exists(full))
        {
            throw new UnsafeTargetException($"'{fieldName}' tro toi file khong ton tai: {full}");
        }

        // 6. Nằm trong thư mục cho phép.
        if (!_directories.Any(d => full.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnsafeTargetException(
                $"'{fieldName}' nam ngoai thu muc cho phep. Duong dan: {full}. " +
                $"Cho phep: {string.Join(", ", _directories)}. " +
                "Doi trong appsettings.json muc 'Management:AllowedExecutableDirectories'.");
        }

        // 7. Tên file nằm trong danh sách cho phép.
        var fileName = Path.GetFileName(full);
        if (!_executables.Contains(fileName))
        {
            throw new UnsafeTargetException(
                $"'{fieldName}' dung file thuc thi khong nam trong danh sach cho phep: {fileName}. " +
                $"Cho phep: {string.Join(", ", _executables)}. " +
                "Doi trong appsettings.json muc 'Management:AllowedExecutables'.");
        }

        return full;
    }

    /// <summary>Charset + độ dài cho tên task/service. Bổ sung cho <see cref="SafeNameGuard"/>.</summary>
    public void EnsureValidName(string? name, string fieldName = "name")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new UnsafeTargetException($"Thieu '{fieldName}'.");
        }

        var candidate = name.TrimStart('\\');

        if (candidate.Length > _maxNameLength)
        {
            throw new UnsafeTargetException(
                $"'{fieldName}' dai qua {_maxNameLength} ky tu.");
        }

        if (!NamePattern.IsMatch(candidate))
        {
            throw new UnsafeTargetException(
                $"'{fieldName}' chi duoc chua chu, so, '.', '_', '-'. Gia tri: {name}");
        }
    }

    /// <summary>
    /// Tài khoản chạy service. CHỈ cho 3 tài khoản dựng sẵn của Windows — đều KHÔNG
    /// cần mật khẩu.
    ///
    /// Cố ý không nhận tài khoản domain: nhận thì phải nhận cả mật khẩu, tức là thêm
    /// một đường để credential đi qua web form vào <c>CreateServiceW</c>. Ngoài phạm vi
    /// dự án và làm tăng rủi ro không cần thiết.
    /// </summary>
    public static readonly string[] AllowedServiceAccounts =
        ["LocalSystem", @"NT AUTHORITY\LocalService", @"NT AUTHORITY\NetworkService"];

    /// <summary>null = LocalSystem (mặc định của <c>CreateServiceW</c>).</summary>
    public static string? EnsureAllowedServiceAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account) ||
            account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = AllowedServiceAccounts.FirstOrDefault(
            a => a.Equals(account.Trim(), StringComparison.OrdinalIgnoreCase));

        return match ?? throw new UnsafeTargetException(
            $"Tai khoan '{account}' khong duoc phep. Chi nhan: {string.Join(", ", AllowedServiceAccounts)}.");
    }

    /// <summary>
    /// Dependencies sang dạng MULTI_SZ mà <c>CreateServiceW</c> cần: các chuỗi nối
    /// nhau, mỗi chuỗi một ký tự null, kết thúc bằng null thứ hai. Đây là dạng ngược
    /// của <c>ServiceManager.ReadMultiSz</c>.
    /// </summary>
    public string? BuildDependencyMultiSz(IReadOnlyList<string>? dependencies)
    {
        if (dependencies is null || dependencies.Count == 0)
        {
            return null;
        }

        foreach (var d in dependencies)
        {
            EnsureValidName(d, "dependency");
        }

        return string.Concat(dependencies.Select(d => d.Trim() + '\0')) + '\0';
    }

    /// <summary>Tham số dòng lệnh: giới hạn độ dài, chặn ký tự điều khiển.</summary>
    public void EnsureValidArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return;
        }

        if (arguments.Length > _maxArgumentsLength)
        {
            throw new UnsafeTargetException(
                $"Tham so dai qua {_maxArgumentsLength} ky tu.");
        }

        if (arguments.Any(char.IsControl))
        {
            throw new UnsafeTargetException("Tham so chua ky tu dieu khien.");
        }
    }

    /// <summary>
    /// Ném <see cref="ArgumentException"/> (→ 400) chứ không phải
    /// <see cref="UnsafeTargetException"/> (→ 403): định dạng sai là lỗi nhập liệu,
    /// không phải hành vi bị cấm.
    /// </summary>
    public static string ParseStartBoundary(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Khong nhap = hen gio xa, chi de sinh log chu khong nham chay that.
            return DateTime.Now.AddYears(1).ToString("yyyy-MM-ddTHH:mm:ss");
        }

        if (!DateTime.TryParse(raw, out var value))
        {
            throw new ArgumentException($"Thoi gian bat dau khong hop le: '{raw}'.");
        }

        return value.ToString("yyyy-MM-ddTHH:mm:ss");
    }
}
