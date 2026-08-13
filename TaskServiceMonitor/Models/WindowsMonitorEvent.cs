namespace TaskServiceMonitor.Models;

/// <summary>Loại đối tượng mà event nói về.</summary>
public enum MonitoredObjectType
{
    Unknown = 0,
    ScheduledTask = 1,
    Service = 2
}

/// <summary>Mức rủi ro do <c>RiskScorer</c> chấm, theo rule-based.</summary>
public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// Model chuẩn hoá cho mọi event đang theo dõi. Mục đích là gom các cấu trúc XML
/// rất khác nhau (Security dùng field có tên, SCM dùng param1..4, 4697 dùng mã số
/// còn 7045 dùng chữ) về một hình dạng duy nhất cho DB và dashboard dùng chung.
/// </summary>
public sealed record WindowsMonitorEvent
{
    // ------------------------------------------------ Field theo spec mentor giao

    /// <summary>Khoá chính do app sinh (event log không có ID toàn cục).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public required int EventId { get; init; }

    /// <summary>Máy sinh ra event, lấy từ <c>System/Computer</c>.</summary>
    public required string Hostname { get; init; }

    public required DateTime TimeCreated { get; init; }

    /// <summary>
    /// Tài khoản gây ra hành động: <c>SubjectUserName</c> nếu có (nhóm Security),
    /// nếu không thì SID ở <c>System/Security/@UserID</c> (nhóm SCM).
    /// </summary>
    public string? ActorAccount { get; init; }

    public required MonitoredObjectType ObjectType { get; init; }

    /// <summary>Tên task (<c>\WinSentinelTest</c>) hoặc tên ngắn của service (<c>BITS</c>).</summary>
    public string? ObjectName { get; init; }

    /// <summary>Mô tả ngắn hành động, ví dụ "Task created", "Service installed".</summary>
    public required string ActionDescription { get; init; }

    /// <summary>
    /// Mức rủi ro do <c>RiskScorer</c> gán sau khi parse, trước khi lưu DB.
    /// Mặc định <c>Low</c> để event chưa qua chấm điểm không bị hiểu nhầm là nguy hiểm.
    /// </summary>
    public RiskLevel RiskLevel { get; init; } = RiskLevel.Low;

    public required string RawXml { get; init; }

    // ------------------------------------------------ Field bổ sung
    // Giữ thêm để bước 5 (RiskScorer) và dashboard có dữ liệu chấm điểm rủi ro.

    public required string Channel { get; init; }
    public required string ProviderName { get; init; }
    public long? RecordId { get; init; }

    /// <summary>SID của tài khoản gây ra hành động.</summary>
    public string? ActorSid { get; init; }

    /// <summary>Tên hiển thị của service, chỉ 7040 mới có.</summary>
    public string? DisplayName { get; init; }

    // --- Chi tiết riêng cho service ---
    public string? ImagePath { get; init; }
    public string? ServiceType { get; init; }
    public string? StartType { get; init; }

    /// <summary>Start type cũ, chỉ có ở 7040 (param2).</summary>
    public string? PreviousStartType { get; init; }

    public string? ServiceAccount { get; init; }

    // --- Chi tiết riêng cho scheduled task, moi ra từ XML lồng trong TaskContent ---

    /// <summary>
    /// Kiểu hành động của task: "Exec" (chạy file), "ComHandler" (gọi COM object),
    /// "SendEmail", "ShowMessage". Task dùng ComHandler KHÔNG có Command — đây là
    /// trường hợp thật, không phải parse lỗi.
    /// </summary>
    public string? TaskActionType { get; init; }

    /// <summary>CLSID của COM object, chỉ có khi <see cref="TaskActionType"/> là "ComHandler".</summary>
    public string? TaskComHandlerClassId { get; init; }

    public string? TaskCommand { get; init; }
    public string? TaskArguments { get; init; }
    public string? TaskRunAsUser { get; init; }
    public string? TaskRunLevel { get; init; }

    /// <summary>Nguyên văn XML định nghĩa task (đã unescape).</summary>
    public string? TaskContentXml { get; init; }

    /// <summary>
    /// false = Event ID này chưa có nhánh xử lý riêng (mới chỉ có dữ liệu thô trong
    /// <see cref="Data"/>). Dùng để biết chỗ nào cần bổ sung khi gặp event lạ.
    /// </summary>
    public bool IsRecognized { get; init; }

    /// <summary>Toàn bộ field trong &lt;EventData&gt;, giữ lại để không mất thông tin.</summary>
    public required IReadOnlyDictionary<string, string> Data { get; init; }
}
