using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Detection;

/// <summary>Một dấu hiệu app đề nghị đưa vào blacklist.</summary>
internal sealed record LearnCandidate(
    BlacklistKind Kind,
    string Value,
    RiskLevel Severity,
    string Reason,
    string RuleId,
    string? ObjectName);

/// <summary>
/// Quyết định <b>dấu hiệu nào đáng đưa vào blacklist</b> sau khi một rule khớp.
/// HÀM THUẦN, tách hẳn khỏi phần ghi DB để test được đầy đủ các trường hợp nguy hiểm.
///
/// ⚠️ ĐÂY LÀ CHỖ DỄ TỰ BẮN VÀO CHÂN NHẤT CỦA CẢ TÍNH NĂNG. Học sai một lần là dấu
/// hiệu đó nằm lại trong DB và bắn cảnh báo High mãi mãi. Vì vậy điều kiện học được
/// giữ RẤT HẸP và mọi nới lỏng phải đo lại bằng <c>--rebuild-alerts</c> trước.
///
/// Bốn rào, bỏ bất kỳ cái nào cũng đủ làm ngập tab Cảnh báo:
///
/// <list type="number">
///   <item>Chỉ học từ hit mức <b>High</b>. Medium là "đáng xem", chưa đủ chắc để đóng dấu.</item>
///   <item>Chỉ học <b>đường dẫn cụ thể</b> (<see cref="BlacklistKind.ExecutablePath"/>),
///   không học tên file trần và không học chuỗi con. Học <c>rundll32.exe</c> thì mọi
///   task Windows hợp lệ dùng nó đều thành High.</item>
///   <item><b>KHÔNG BAO GIỜ</b> học đường dẫn nằm trong thư mục hệ thống chuẩn. Đây là
///   rào quan trọng nhất: nếu không, một dương tính giả trên
///   <c>C:\Windows\System32\rundll32.exe</c> sẽ đóng dấu vĩnh viễn một binary của
///   Windows.</item>
///   <item>Phải là đường dẫn <b>có thư mục</b>, không phải tên trần — tên trần không
///   định danh được file nào cụ thể.</item>
/// </list>
/// </summary>
internal static class BlacklistLearner
{
    /// <summary>
    /// Rule nào được phép dạy blacklist. CỐ Ý không phải mọi rule High:
    /// <c>SUSPICIOUS_RAW_CONTENT</c> là lưới an toàn quét cả XML thô nên giá trị nó
    /// bắt được không gắn với một file cụ thể nào; <c>TASK_CREATE_THEN_DELETE</c> nói
    /// về mẫu thời gian chứ không về một đường dẫn.
    ///
    /// <b>⚠️ CỐ Ý KHÔNG CÓ <c>TASK_WRITABLE_DIR</c> — quyết định lấy từ dữ liệu thật,
    /// đừng thêm lại mà không đo lại.</b>
    ///
    /// Lần chạy <c>--rebuild-alerts</c> đầu tiên trên 1.807 event thật: rule đó dạy
    /// blacklist 2 đường dẫn, <b>cả hai đều là dương tính giả</b>, và chúng chiếm
    /// 17/19 cảnh báo <c>BLACKLIST_HIT</c>:
    /// <code>
    /// %localappdata%\microsoft\onedrive\onedrivestandaloneupdater.exe   (10 hit)
    /// ...\onedrive\26.139.0720.0007\onedrivelauncher.exe                 ( 7 hit)
    /// </code>
    /// Cả hai là OneDrive của Microsoft. Lý do: <c>%LOCALAPPDATA%</c> chính là nơi
    /// phần mềm per-user hợp lệ cài đặt (OneDrive, Teams, Chrome, VS Code) — danh sách
    /// này không bao giờ liệt kê hết được.
    ///
    /// Vị trí là tín hiệu đủ để CẢNH BÁO (rule vẫn chạy, vẫn hiện ở tab Cảnh báo)
    /// nhưng không đủ để KẾT ÁN VĨNH VIỄN một binary. Muốn đóng dấu thì cần bằng chứng
    /// về HÀNH VI: gọi LOLBin, dùng PowerShell mã hoá, hoặc cài hẳn một service.
    ///
    /// <c>SERVICE_NONSTANDARD_PATH</c> thì GIỮ LẠI dù cũng là tín hiệu vị trí: một
    /// <b>service</b> chạy từ AppData bất thường hơn hẳn một <b>task</b>, vì service
    /// là phạm vi toàn máy và phải có quyền admin mới cài được — phần mềm per-user
    /// không cài service vào AppData. Dòng đúng duy nhất trong lần đo trên chính là từ
    /// rule này.
    /// </summary>
    internal static readonly string[] TeachingRules =
    [
        RuleCatalog.TaskLolBin,
        RuleCatalog.TaskEncodedPs,
        RuleCatalog.ServiceNonStandardPath,
        RuleCatalog.ServiceSuspiciousCommand
    ];

    /// <summary>
    /// Dấu hiệu nên học từ một hit, hoặc <c>null</c> nếu không có gì đáng học.
    ///
    /// Trả về <b>một</b> ứng viên chứ không phải danh sách: học nhiều thứ từ một hit
    /// làm blacklist phình nhanh và khó rà.
    /// </summary>
    internal static LearnCandidate? FromHit(
        WindowsMonitorEvent evt, string ruleId, RiskLevel severity, string evidence)
    {
        // Rào 1: chỉ mức High.
        if (severity != RiskLevel.High)
        {
            return null;
        }

        // Rào 2: chỉ rule biết nói về một file cụ thể.
        if (!TeachingRules.Contains(ruleId))
        {
            return null;
        }

        // Đường dẫn cần học nằm ở lệnh của task, hoặc ImagePath của service.
        var candidate = LearnableFrom(evt.TaskCommand) ?? LearnableFrom(evt.ImagePath);

        if (candidate is null)
        {
            return null;
        }

        return new LearnCandidate(
            BlacklistKind.ExecutablePath,
            BlacklistMatcher.Normalize(candidate),
            RiskLevel.High,
            $"Tự học từ rule {ruleId}: {evidence}",
            ruleId,
            evt.ObjectName);
    }

    /// <summary>
    /// Đường dẫn học được từ một chuỗi lệnh thô, hoặc <c>null</c>.
    ///
    /// 🪤 BẪY ĐÃ DÍNH: phải xét rào TRÊN CHUỖI GỐC TRƯỚC, rồi mới bóc exe.
    /// <c>ExtractExecutable</c> cắt tại dấu cách đầu tiên khi không có nháy, nên
    /// <c>C:\Program Files\App\svc.exe</c> biến thành <c>C:\Program</c> — chuỗi đó
    /// không còn khớp tiền tố <c>C:\Program Files\</c> nên <b>lọt rào</b> thư mục hệ
    /// thống, và app đóng dấu xấu một giá trị rác (<c>c:\program</c>) cho một binary
    /// hoàn toàn hợp lệ.
    ///
    /// Vì vậy kiểm tra HAI LẦN: bản gốc (bắt được Program Files) và bản đã bóc (bắt
    /// được trường hợp chuỗi mang tham số phía sau).
    /// </summary>
    private static string? LearnableFrom(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !IsLearnablePath(raw))
        {
            return null;
        }

        var extracted = ExecutablePathParser.ExtractAndNormalize(raw);

        return IsLearnablePath(extracted) ? extracted : null;
    }

    /// <summary>
    /// Đường dẫn này có đáng đóng dấu không. Đây là rào 3 và 4 — xem phần tóm tắt ở
    /// đầu class để biết vì sao từng điều kiện tồn tại.
    /// </summary>
    internal static bool IsLearnablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim();

        // Rào 4: phải là đường dẫn thật, không phải tên trần ('notepad.exe').
        if (!normalized.Contains('\\'))
        {
            return false;
        }

        // Rào 3 — QUAN TRỌNG NHẤT. Binary trong System32/Program Files là của Windows
        // hoặc của phần mềm đã cài đàng hoàng. Một dương tính giả ở đây (ví dụ
        // rundll32 bị chấm nhầm) mà được học thì mọi task hệ thống dùng nó sẽ thành
        // High vĩnh viễn - đúng thảm hoạ mà ContextualLolBins sinh ra để tránh.
        return !SuspiciousIndicators.IsInStandardSystemDirectory(normalized);
    }
}
