using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Api;

/// <summary>
/// Dòng kết quả cho tính năng "duyệt log bất kỳ" (<see cref="LogBrowseEndpoints"/>).
/// Khác <see cref="EventSummaryDto"/>: CÓ kèm <c>RawXml</c> ngay trong payload, vì
/// kết quả này không lưu DB nên không có <c>GET /api/events/{id}</c> tương ứng để
/// gọi lại lấy XML sau — phải trả kèm luôn từ đầu.
/// </summary>
public sealed record LogBrowseEventDto(
    Guid Id,
    int EventId,
    string Hostname,
    DateTime TimeCreated,
    string Channel,
    string ProviderName,
    string ActionDescription,
    MonitoredObjectType ObjectType,
    string? ObjectName,
    string? ActorAccount,
    RiskLevel RiskLevel,
    bool IsRecognized,
    IReadOnlyDictionary<string, string> Data,
    string RawXml,

    // Nhom hien thi kieu Event Viewer (buoc 8). Description la cot moc: no la thu
    // duy nhat KHONG suy ra duoc tu RawXml (xem WindowsMonitorEvent.Description).
    string? Description,
    int? Level,
    string? LevelDisplayName,
    int? TaskCategoryId,
    string? TaskCategoryName,
    string? OpcodeName,
    string? Keywords,

    /// <summary>Cần cho "lưu event đang chọn" — XPath lọc theo EventRecordID.</summary>
    long? RecordId,

    // Nhom enrichment cua parser. Co mat de bang o che do "Toan bo channel" dien duoc
    // DUNG bo cot voi che do "App da bat" - neu thieu, doi che do se thay cot trong.
    string? ImagePath,
    string? StartType,
    string? PreviousStartType,
    string? TaskActionType,
    string? TaskCommand,
    string? TaskInstanceId,
    string? TaskActionResultCode)
{
    public static LogBrowseEventDto From(WindowsMonitorEvent e) => new(
        e.Id, e.EventId, e.Hostname, e.TimeCreated, e.Channel, e.ProviderName,
        e.ActionDescription, e.ObjectType, e.ObjectName, e.ActorAccount, e.RiskLevel,
        e.IsRecognized, e.Data, e.RawXml,
        e.Description, e.Level, e.LevelDisplayName, e.TaskCategoryId,
        e.TaskCategoryName, e.OpcodeName, e.Keywords,
        e.RecordId,
        e.ImagePath, e.StartType, e.PreviousStartType,
        e.TaskActionType, e.TaskCommand, e.TaskInstanceId, e.TaskActionResultCode);
}
