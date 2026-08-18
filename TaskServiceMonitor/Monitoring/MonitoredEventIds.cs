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
        7034, // service crashed unexpectedly

        // --- Nhom "service dung bat thuong", them o buoc 11 ---
        // Mentor yeu cau bat "Service Crash / Dung dot ngot", nhung 7034 CHUA BAO GIO
        // phat tren may dev (da xac minh: SCM o day chi ghi 7023/7026/7030/7031/7040/
        // 7043/7045). Chi dung dung 7034/7036 thi muc do cua mentor se vinh vien trong.
        // 7031 la ID may dev THUC SU co phat - xem docs/hanh-vi-mapping.md muc 3.2.
        7031, // terminated unexpectedly (kem so lan loi + hanh dong khoi phuc)
        7024, // terminated voi ma loi rieng cua service
        7000, // khong khoi dong duoc
        7009  // qua thoi gian cho khi ket noi toi service
    ];

    /// <summary>
    /// Registry — channel <c>Security</c>. Đây là đường DUY NHẤT có log Windows thật
    /// cho hai hành vi mentor nêu mà SCM <b>không phát event nào</b>: đổi
    /// <c>binPath</c> và đổi tài khoản khởi chạy service (event 7040 chỉ báo start
    /// type). Xem docs/hanh-vi-mapping.md mục 3.1.
    ///
    /// Cần bật thêm audit policy <c>"Registry"</c> VÀ đặt SACL trên khoá
    /// <c>HKLM\SYSTEM\CurrentControlSet\Services</c>. Khối lượng event được khống chế
    /// bằng chính phạm vi SACL, không phải bằng lọc phía app.
    /// </summary>
    public static readonly int[] RegistryEventIds =
    [
        4657 // registry value modified
    ];

    /// <summary>
    /// Task — channel <c>Microsoft-Windows-TaskScheduler/Operational</c>. Channel
    /// này MẶC ĐỊNH TẮT trên Windows (khác System/Security), phải bật tay bằng
    /// <c>wevtutil sl "Microsoft-Windows-TaskScheduler/Operational" /e:true</c>
    /// (quyền Administrator) trước khi subscribe. CHƯA có nhánh parse riêng cho các
    /// ID này — rơi vào <see cref="WindowsEventParser"/>.EnrichUnknown cho tới khi
    /// có mẫu XML thật (xem CLAUDE.md, quy tắc "không đoán cấu trúc").
    /// </summary>
    public static readonly int[] TaskSchedulerOperationalEventIds =
    [
        106, // task registered
        140, // task updated
        141, // task deleted
        200, // task action started
        201  // task action completed
    ];

    /// <summary>Hợp của các nhóm trên, dùng để dựng XPath filter.</summary>
    public static readonly int[] All =
        [.. TaskEventIds, .. ServiceEventIds, .. RegistryEventIds, .. TaskSchedulerOperationalEventIds];

    /// <summary>
    /// Event ID cần theo dõi CHO TỪNG CHANNEL. Trước đây mọi channel subscribe dùng
    /// chung một filter dựng từ <see cref="All"/> — vô hại với 10 ID gốc (số hiệu
    /// đặc thù, khó trùng chéo giữa Security/System), nhưng ID của channel khác (ví
    /// dụ Microsoft-Windows-TaskScheduler/Operational: 106/140/141/200/201) là số
    /// nhỏ, chung chung — phải khai báo riêng theo channel, đúng ghi chú trong
    /// CLAUDE.md ("phải khai báo riêng trong subscription query vì không nằm trong
    /// Security/System").
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int[]> ByChannel =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Security"] = [4657, 4697, 4698, 4699, 4700, 4701, 4702],
            ["System"] = [7045, 7040, 7036, 7034, 7031, 7024, 7000, 7009],
            ["Microsoft-Windows-TaskScheduler/Operational"] = TaskSchedulerOperationalEventIds,
        };

    /// <summary>
    /// Dựng XPath filter cho <c>EventLogQuery</c> của MỘT channel cụ thể, dạng:
    /// <c>*[System[(EventID=4698 or EventID=4699 or ...)]]</c>. Channel chưa khai
    /// báo trong <see cref="ByChannel"/> rơi về lọc theo <see cref="All"/> — tránh
    /// subscribe "không lọc gì cả" một cách âm thầm cho channel lạ.
    ///
    /// <paramref name="afterRecordId"/>: khi có giá trị, thêm điều kiện
    /// <c>(EventRecordID>N)</c> để chỉ lấy event MỚI HƠN record cuối cùng đã lưu -
    /// đây là cơ chế "resume sau restart" (xem EventWatcherService), dùng filter
    /// numeric của XPath thay cho EventBookmark (EventBookmark không serialize
    /// được an toàn trên .NET 8 vì BinaryFormatter đã bị gỡ bỏ mặc định).
    /// BẮT BUỘC bọc nhóm EventID trong ngoặc riêng: XPath "and" ưu tiên cao hơn
    /// "or", nối thẳng "...EventID=X or EventID=Y and EventRecordID>N" sẽ chỉ AND
    /// với điều kiện cuối cùng, không phải cả nhóm OR.
    /// </summary>
    public static string BuildXPathFilter(string channel, long? afterRecordId = null)
    {
        var ids = ByChannel.TryGetValue(channel, out var channelIds) ? channelIds : All;
        var conditions = string.Join(" or ", ids.Select(id => $"EventID={id}"));

        return afterRecordId is long cursor
            ? $"*[System[({conditions}) and (EventRecordID>{cursor})]]"
            : $"*[System[({conditions})]]";
    }

    /// <summary>Event ID này thuộc nhóm Scheduled Task hay Service.</summary>
    public static MonitoredObjectType GetObjectType(int eventId)
    {
        if (TaskEventIds.Contains(eventId)) return MonitoredObjectType.ScheduledTask;
        if (ServiceEventIds.Contains(eventId)) return MonitoredObjectType.Service;
        if (TaskSchedulerOperationalEventIds.Contains(eventId)) return MonitoredObjectType.ScheduledTask;

        // 4657 la event registry CHUNG CHUNG, khong rieng cho service. Xep vao nhom
        // Service vi theo huong dan cai dat (docs/hanh-vi-mapping.md muc 3.1) SACL chi
        // duoc dat tren dung khoa HKLM\SYSTEM\CurrentControlSet\Services. Dat SACL rong
        // hon thi nhan nay khong con chinh xac - rule trong RuleCatalog van loc lai theo
        // '\Services\' nen khong sinh canh bao sai, chi rieng nhan ObjectType la rong tay.
        if (RegistryEventIds.Contains(eventId)) return MonitoredObjectType.Service;

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
        7031 => "Service terminated unexpectedly",
        7024 => "Service terminated with error",
        7000 => "Service failed to start",
        7009 => "Service start timed out",
        4657 => "Registry value modified",

        // Text tam thoi - chua co mau XML that de xac nhan y nghia chinh xac.
        // Se sua lai (neu can) o Phase 4 sau khi doc mau capture duoc.
        106 => "Task registered (Operational)",
        140 => "Task updated (Operational)",
        141 => "Task deleted (Operational)",
        200 => "Task action started",
        201 => "Task action completed",

        _ => $"Unknown event {eventId}"
    };
}
