namespace TaskServiceMonitor.Management;

/// <summary>Một trigger. <c>Type</c> quyết định field nào có ý nghĩa.</summary>
/// <param name="Type">Time | Daily | Logon | Boot | Registration</param>
/// <param name="DaysInterval">Chỉ dùng cho Daily.</param>
public sealed record TriggerRequest(
    string Type,
    string? StartBoundary = null,
    bool Enabled = true,
    int? DaysInterval = null,
    string? UserId = null);

/// <param name="Type">Hiện chỉ hỗ trợ ghi <c>Exec</c>; ComHandler chỉ đọc được.</param>
public sealed record ActionRequest(
    string Type,
    string? Command = null,
    string? Arguments = null,
    string? WorkingDirectory = null);

/// <summary>
/// Toàn bộ thông tin dựng nên một task — tương ứng 5 tab của hộp thoại "Create Task"
/// trong Windows, và tương ứng các nhánh của <c>ITaskDefinition</c>
/// (RegistrationInfo / Triggers / Actions / Principal / Settings).
///
/// Thay cho 4 tham số rời trước đây. Nhờ gom thành một model, <c>BuildTaskXml</c> vẫn
/// là HÀM THUẦN (model vào, XML ra) nên test được mà không cần Windows.
/// </summary>
public sealed record TaskDefinitionRequest
{
    public string Name { get; init; } = "";

    // --- General ---
    public string? Author { get; init; }
    public string? Description { get; init; }
    public bool Hidden { get; init; }

    // --- Principal ---
    public string? UserId { get; init; }
    public string? GroupId { get; init; }
    public string LogonType { get; init; } = "InteractiveToken";

    /// <summary>
    /// <c>HighestAvailable</c> = task chạy quyền Administrator. Mặc định
    /// <c>LeastPrivilege</c> — trước bước 10 giá trị này hardcode, và nó là biện pháp
    /// giảm nhẹ QUAN TRỌNG NHẤT của cả đường tạo task.
    /// </summary>
    public string RunLevel { get; init; } = "LeastPrivilege";

    // --- Settings ---
    public bool AllowStartOnDemand { get; init; } = true;
    public bool StopIfGoingOnBatteries { get; init; }
    public string MultipleInstancesPolicy { get; init; } = "IgnoreNew";
    public string? ExecutionTimeLimit { get; init; }

    public IReadOnlyList<TriggerRequest> Triggers { get; init; } = [];
    public IReadOnlyList<ActionRequest> Actions { get; init; } = [];
}
