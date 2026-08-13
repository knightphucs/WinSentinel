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
    string? ImagePath,
    string? StartType,
    string? PreviousStartType,
    string? TaskActionType,
    string? TaskCommand)
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
            e.ImagePath,
            e.StartType,
            e.PreviousStartType,
            e.TaskActionType,
            e.TaskCommand);

    // Phai khai bao SAU Projection: static field initializer chay theo thu tu trong file.
    private static readonly Func<WindowsMonitorEvent, EventSummaryDto> CompiledProjection =
        Projection.Compile();

    public static EventSummaryDto From(WindowsMonitorEvent e) => CompiledProjection(e);
}

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
    bool IsRecognized,
    IReadOnlyDictionary<string, string> Data,
    string RawXml);
