namespace TaskServiceMonitor.Models;

/// <summary>
/// Một cảnh báo do <c>RuleCatalog</c> sinh ra khi một event (hoặc một thay đổi cấu
/// hình service phát hiện bằng poll) khớp với rule.
///
/// Vì sao tách khỏi <see cref="WindowsMonitorEvent"/> thay vì thêm cột:
/// một event có thể khớp NHIỀU rule cùng lúc (task vừa chạy từ %TEMP%, vừa dùng
/// PowerShell mã hoá, vừa chạy quyền SYSTEM = 3 cảnh báo). Cột <c>RiskLevel</c> trên
/// event chỉ trả lời "nguy hiểm tới mức nào", còn bảng này trả lời "nguy hiểm VÌ
/// HÀNH VI GÌ" — thứ mentor cần đọc.
///
/// <see cref="Severity"/> DÙNG LẠI <see cref="RiskLevel"/> chứ không tạo enum thứ
/// hai: CSS (<c>.risk--High/Medium/Low</c>), bộ lọc và biểu đồ trên dashboard đã bám
/// theo nó rồi, thêm một bộ từ vựng nữa là hai chỗ phải đồng bộ mãi mãi.
/// </summary>
public sealed record Alert
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Event đã kích hoạt cảnh báo này. <c>null</c> khi cảnh báo do
    /// <c>ServiceConfigWatcher</c> sinh ra bằng cách so snapshot — trường hợp đó
    /// không có event Windows nào tương ứng (xem docs/hanh-vi-mapping.md mục 3.1).
    /// </summary>
    public Guid? SourceEventId { get; init; }

    /// <summary>Mã rule cố định, ví dụ <c>TASK_WRITABLE_DIR</c>. Dùng để lọc và thống kê.</summary>
    public required string RuleId { get; init; }

    /// <summary>
    /// Tên tiếng Việt của hành vi, chép lại lúc sinh cảnh báo. CỐ Ý lưu trùng với
    /// catalog: đổi tên rule về sau không được làm sai lệch cảnh báo lịch sử.
    /// </summary>
    public required string RuleName { get; init; }

    public required RiskLevel Severity { get; init; }

    public required MonitoredObjectType ObjectType { get; init; }

    /// <summary>Lúc app phát hiện. Luôn UTC.</summary>
    public required DateTime DetectedAt { get; init; }

    /// <summary>
    /// Lúc hành vi thực sự xảy ra (<c>TimeCreated</c> của event gốc). Luôn UTC.
    /// Khác <see cref="DetectedAt"/> khi đọc bù sau restart hoặc khi chạy backfill.
    /// </summary>
    public required DateTime EventTime { get; init; }

    public required string Hostname { get; init; }

    /// <summary>Tên task (<c>\WinSentinelTest</c>) hoặc tên ngắn service (<c>BITS</c>).</summary>
    public string? ObjectName { get; init; }

    /// <summary>Event ID gốc. <c>null</c> với cảnh báo sinh từ poll cấu hình.</summary>
    public int? EventId { get; init; }

    /// <summary>
    /// Câu bằng chứng trích thẳng từ dữ liệu event, ví dụ
    /// <c>"Lệnh: C:\Users\Public\a.cmd (khớp '\Users\Public')"</c>.
    /// Đây là thứ làm cảnh báo đọc được mà không phải mở raw XML.
    /// </summary>
    public required string Evidence { get; init; }

    /// <summary>Gợi ý việc cần làm. Không bắt buộc.</summary>
    public string? Recommendation { get; init; }

    public bool Acknowledged { get; init; }

    public DateTime? AcknowledgedAt { get; init; }
}
