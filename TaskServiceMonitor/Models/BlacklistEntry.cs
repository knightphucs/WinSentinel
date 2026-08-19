namespace TaskServiceMonitor.Models;

/// <summary>
/// Loại giá trị mà một dòng blacklist so khớp. Quyết định dòng đó được đem so với
/// field nào của event — xem <c>BlacklistMatcher</c>.
/// </summary>
public enum BlacklistKind
{
    /// <summary>
    /// Đường dẫn file thực thi đầy đủ (<c>C:\Users\Public\a.exe</c>). So sau khi đã
    /// bóc khỏi tham số và chuẩn hoá — xem <c>ExecutablePathParser</c>.
    /// </summary>
    ExecutablePath,

    /// <summary>Chỉ tên file (<c>evil.exe</c>), bất kể nằm ở thư mục nào.</summary>
    FileName,

    /// <summary>
    /// Chuỗi con xuất hiện trong lệnh hoặc tham số (<c>-enc</c>, <c>DownloadString</c>).
    /// Đây là dạng linh hoạt nhất và cũng dễ gây dương tính giả nhất.
    /// </summary>
    CommandFragment,

    /// <summary>Tài khoản chạy service hoặc principal chạy task.</summary>
    Account
}

/// <summary>
/// Dòng blacklist này từ đâu ra. CỐ Ý phân biệt rõ vì <b>tự học có thể học nhầm</b> —
/// người xem phải biết dòng nào do máy tự thêm để còn rà lại.
/// </summary>
public enum BlacklistSource
{
    /// <summary>Người dùng nhập tay qua UI/API.</summary>
    Manual,

    /// <summary>
    /// App tự thêm sau khi một rule mức High khớp trên một giá trị cụ thể.
    /// Xem <c>BlacklistLearner</c> để biết điều kiện học (rất hẹp, có chủ đích).
    /// </summary>
    AutoLearned
}

/// <summary>
/// Một dấu hiệu đã bị đánh dấu là xấu. Gặp lại lần sau là cảnh báo <b>High ngay</b>,
/// không cần rule nào khác phải khớp.
///
/// <b>Vì sao cần, khi đã có <c>SuspiciousIndicators</c>?</b> Hai lớp trả lời hai câu
/// hỏi khác nhau và CỐ Ý không gộp:
///
/// <list type="table">
///   <item>
///     <term><c>SuspiciousIndicators</c></term>
///     <description>Dấu hiệu <b>tổng quát</b> (<c>%TEMP%</c>, <c>mshta.exe</c>,
///     <c>-enc</c>). Hardcode trong code, có unit test chạy trên 14 fixture thật,
///     đổi là phải build lại. Đây vẫn là nguồn sự thật cho "cái gì nói chung là
///     đáng ngờ".</description>
///   </item>
///   <item>
///     <term><c>BlacklistEntry</c> (lớp này)</term>
///     <description>Giá trị <b>cụ thể đã thực sự gặp trên máy này</b>
///     (<c>C:\Users\Public\svchost.exe</c>). Nằm trong DB, sửa được lúc đang chạy,
///     đếm được số lần khớp, tự học được.</description>
///   </item>
/// </list>
///
/// Không seed blacklist bằng nội dung của <c>SuspiciousIndicators</c> — làm vậy là
/// tạo ra hai nguồn sự thật cho cùng một thứ, sớm muộn hai bên lệch nhau.
/// </summary>
public sealed record BlacklistEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required BlacklistKind Kind { get; init; }

    /// <summary>
    /// Giá trị đem so, đã chuẩn hoá về chữ thường lúc ghi (xem
    /// <c>BlacklistNormalizer.Normalize</c>). Chuẩn hoá lúc GHI chứ không phải lúc so,
    /// để unique index chặn được trùng lặp chỉ khác hoa/thường.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Mức cảnh báo khi khớp. Mặc định High — mục đích của blacklist là "gặp lại là
    /// báo động ngay", nhưng vẫn cho hạ xuống Medium với dấu hiệu chưa chắc chắn.
    /// </summary>
    public required RiskLevel Severity { get; init; }

    public required BlacklistSource Source { get; init; }

    /// <summary>
    /// Tắt tạm mà không xoá. Cần vì dòng tự học có thể là dương tính giả — tắt đi để
    /// theo dõi thêm vẫn tốt hơn xoá mất dấu vết.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>Vì sao giá trị này bị đánh dấu. Với dòng tự học thì đây là câu bằng chứng gốc.</summary>
    public string? Reason { get; init; }

    /// <summary>Rule nào đã dẫn tới việc học dòng này. Null với dòng nhập tay.</summary>
    public string? LearnedFromRuleId { get; init; }

    /// <summary>Task/service nào đã làm lộ ra dấu hiệu này. Null với dòng nhập tay.</summary>
    public string? LearnedFromObjectName { get; init; }

    /// <summary>
    /// Số lần dòng này khớp. Đây là con số dùng để rà dương tính giả: một dòng khớp
    /// hàng nghìn lần gần như chắc chắn là học nhầm một binary hợp lệ.
    /// </summary>
    public int HitCount { get; init; }

    public required DateTime CreatedAt { get; init; }

    public DateTime? LastHitAt { get; init; }
}
