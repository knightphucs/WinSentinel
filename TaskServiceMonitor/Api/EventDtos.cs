using System.Linq.Expressions;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Api;

/// <summary>
/// Dòng tóm tắt cho danh sách và cho payload SignalR. CỐ Ý không kèm <c>RawXml</c>:
/// mỗi RawXml 1-4KB, nhân với mọi event và mọi client là quá tốn. Muốn xem raw thì
/// gọi <c>GET /api/events/{id}</c>.
/// </summary>
public sealed record EventSummaryDto(
    Guid Id,
    int EventId,
    string Hostname,
    DateTime TimeCreated,
    MonitoredObjectType ObjectType,
    string? ObjectName,
    string? ActorAccount,
    string ActionDescription,
    RiskLevel RiskLevel,
    string Channel,
    string ProviderName,

    /// <summary>Cần cho "lưu event đang chọn" — XPath lọc theo EventRecordID.</summary>
    long? RecordId,

    string? ImagePath,
    string? StartType,
    string? PreviousStartType,
    string? TaskActionType,
    string? TaskCommand,
    string? TaskInstanceId,
    string? TaskActionResultCode,

    // Cot kieu Event Viewer (buoc 8). Description CO tinh trong summary vi bang log
    // hien no ngay tren dong, khong doi bam vao xem chi tiet. Van KHONG kem RawXml.
    string? Description,
    int? Level,
    string? LevelDisplayName,
    string? TaskCategoryName)
{
    /// <summary>
    /// MỘT định nghĩa mapping duy nhất, dùng cho cả hai đường:
    /// EF Core dịch thẳng expression này thành SQL (nên RawXml không bị kéo về),
    /// còn <see cref="From"/> chạy bản đã compile cho object đang nằm trong bộ nhớ.
    /// Nhờ vậy danh sách API và payload SignalR không bao giờ lệch nhau.
    /// </summary>
    public static readonly Expression<Func<WindowsMonitorEvent, EventSummaryDto>> Projection =
        e => new EventSummaryDto(
            e.Id,
            e.EventId,
            e.Hostname,
            e.TimeCreated,
            e.ObjectType,
            e.ObjectName,
            e.ActorAccount,
            e.ActionDescription,
            e.RiskLevel,
            e.Channel,
            e.ProviderName,
            e.RecordId,
            e.ImagePath,
            e.StartType,
            e.PreviousStartType,
            e.TaskActionType,
            e.TaskCommand,
            e.TaskInstanceId,
            e.TaskActionResultCode,
            e.Description,
            e.Level,
            e.LevelDisplayName,
            e.TaskCategoryName);

    // Phai khai bao SAU Projection: static field initializer chay theo thu tu trong file.
    private static readonly Func<WindowsMonitorEvent, EventSummaryDto> CompiledProjection =
        Projection.Compile();

    public static EventSummaryDto From(WindowsMonitorEvent e) => CompiledProjection(e);
}

/// <summary>
/// Một dòng của bảng Overview/Summary (kiểu Event Viewer): số event theo mức
/// rủi ro, cắt theo 3 khung giờ. <c>RiskLevel</c> để dạng chuỗi (không dùng enum
/// trực tiếp) để luôn khớp <c>JsonStringEnumConverter</c> đã đăng ký chung.
/// <c>Breakdown</c> là danh sách xổ ra khi bấm "+" trên dòng này, giống nút "+"
/// trên mỗi hàng của "Summary of Administrative Events" thật trong Event Viewer.
/// </summary>
public sealed record SummaryRowDto(
    string RiskLevel, int LastHour, int Last24h, int Last7d,
    IReadOnlyList<SummaryBreakdownRowDto> Breakdown);

/// <summary>Một dòng con khi xổ "+" - tương đương Event ID / Source / Log của Event Viewer.</summary>
public sealed record SummaryBreakdownRowDto(
    int EventId, string Source, string Log, int LastHour, int Last24h, int Last7d);

/// <summary>Bản đầy đủ cho màn hình chi tiết — có kèm <c>RawXml</c>.</summary>
public sealed record EventDetailDto(
    Guid Id,
    int EventId,
    string Hostname,
    DateTime TimeCreated,
    MonitoredObjectType ObjectType,
    string? ObjectName,
    string? DisplayName,
    string? ActorAccount,
    string? ActorSid,
    string ActionDescription,
    RiskLevel RiskLevel,
    string Channel,
    string ProviderName,
    long? RecordId,
    string? ImagePath,
    string? ServiceType,
    string? StartType,
    string? PreviousStartType,
    string? ServiceAccount,
    string? TaskActionType,
    string? TaskComHandlerClassId,
    string? TaskCommand,
    string? TaskArguments,
    string? TaskRunAsUser,
    string? TaskRunLevel,
    string? TaskInstanceId,
    string? TaskActionResultCode,

    // Nhom hien thi kieu Event Viewer (buoc 8) - day du hon ban summary.
    string? Description,
    int? Level,
    string? LevelDisplayName,
    int? TaskCategoryId,
    string? TaskCategoryName,
    string? OpcodeName,
    string? Keywords,

    bool IsRecognized,
    IReadOnlyDictionary<string, string> Data,
    string RawXml);
