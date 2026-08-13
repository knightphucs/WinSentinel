using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Nơi khai báo tập trung DUY NHẤT danh sách Event ID đang theo dõi.
/// Mọi nơi khác (XPath filter, parser ở bước 2, risk scorer ở bước 5) phải tham
/// chiếu class này thay vì hardcode lại số Event ID.
/// </summary>
public static class MonitoredEventIds
{
    /// <summary>
    /// Scheduled Task — channel <c>Security</c>.
    /// Cần bật Audit Policy "Other Object Access Events" thì mới sinh ra được.
    /// </summary>
    public static readonly int[] TaskEventIds =
    [
        4698, // task created
        4699, // task deleted
        4700, // task enabled
        4701, // task disabled
        4702  // task updated
    ];

    /// <summary>
    /// Service — 4697 nằm ở channel <c>Security</c> (cần Audit Policy),
    /// còn 7045/7040/7036/7034 nằm ở channel <c>System</c> (mặc định đã bật).
    /// </summary>
    public static readonly int[] ServiceEventIds =
    [
        4697, // service installed (Security)
        7045, // service installed (System)
        7040, // start type changed
        7036, // state changed
        7034  // service crashed unexpectedly
    ];

    /// <summary>Hợp của hai nhóm trên, dùng để dựng XPath filter.</summary>
    public static readonly int[] All = [.. TaskEventIds, .. ServiceEventIds];

    /// <summary>
    /// Dựng XPath filter cho <c>EventLogQuery</c>, dạng:
    /// <c>*[System[(EventID=4698 or EventID=4699 or ...)]]</c>
    /// </summary>
    public static string BuildXPathFilter()
    {
        var conditions = string.Join(" or ", All.Select(id => $"EventID={id}"));
        return $"*[System[({conditions})]]";
    }

    /// <summary>Event ID này thuộc nhóm Scheduled Task hay Service.</summary>
    public static MonitoredObjectType GetObjectType(int eventId)
    {
        if (TaskEventIds.Contains(eventId)) return MonitoredObjectType.ScheduledTask;
        if (ServiceEventIds.Contains(eventId)) return MonitoredObjectType.Service;
        return MonitoredObjectType.Unknown;
    }

    /// <summary>Mô tả ngắn hành động của Event ID, dùng để hiển thị trên dashboard.</summary>
    public static string GetActionDescription(int eventId) => eventId switch
    {
        4698 => "Task created",
        4699 => "Task deleted",
        4700 => "Task enabled",
        4701 => "Task disabled",
        4702 => "Task updated",
        4697 => "Service installed (Security)",
        7045 => "Service installed",
        7040 => "Service start type changed",
        7036 => "Service state changed",
        7034 => "Service crashed",
        _ => $"Unknown event {eventId}"
    };
}
