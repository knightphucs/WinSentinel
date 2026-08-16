using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Chuyển XML thô của Windows Event Log thành <see cref="WindowsMonitorEvent"/>.
/// Viết dựa trên MẪU XML THẬT thu được ở bước 1 (thư mục samples/), không phải giả định.
/// Stateless và thuần tuý -> test được bằng cách nạp lại file mẫu.
/// </summary>
public sealed class WindowsEventParser
{
    /// <summary>
    /// Các Event ID ĐÃ có nhánh xử lý riêng, viết dựa trên mẫu XML thật.
    /// Phải khớp với danh sách nhánh trong <see cref="Parse"/> — sửa một chỗ thì sửa cả hai.
    /// 7036 và 7034 CỐ Ý không nằm ở đây: chưa lấy được mẫu thật nên không đoán cấu trúc.
    /// </summary>
    public static readonly int[] RecognizedEventIds =
        [4697, 4698, 4699, 4700, 4701, 4702, 7040, 7045, 106, 140, 141, 200, 201];

    private static readonly XNamespace EventNs = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>SID hệ thống hay gặp, đổi sang tên cho dễ đọc trên dashboard.</summary>
    private static readonly Dictionary<string, string> WellKnownSids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S-1-5-18"] = "LocalSystem",
        ["S-1-5-19"] = "LocalService",
        ["S-1-5-20"] = "NetworkService"
    };

    /// <summary>
    /// Parse trực tiếp từ <see cref="EventRecord"/> nhận được lúc chạy realtime.
    /// </summary>
    public WindowsMonitorEvent Parse(EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Parse(record.ToXml(), record);
    }

    /// <summary>
    /// Parse XML rồi bổ sung các field CHỈ lấy được từ <see cref="EventRecord"/> sống
    /// (Description, tên Task Category/Opcode/Keywords — xem
    /// <see cref="EventRecordDescriber"/>).
    ///
    /// Tồn tại overload này thay vì để bên gọi tự ghép hai bước: mọi đường đọc log
    /// (<c>AdHocLogReader</c> khi duyệt, <c>EventWatcherService</c> khi realtime) đi
    /// qua ĐÚNG một hàm, không thể có chỗ quên gọi describer rồi âm thầm mất
    /// Description ở riêng một nhánh. Nhận sẵn <paramref name="rawXml"/> để bên gọi
    /// nào đã có XML rồi (watcher cần nó cho sample writer) không phải gọi
    /// <c>ToXml()</c> lần hai — đây là lời gọi tốn kém, không phải property.
    /// </summary>
    public WindowsMonitorEvent Parse(string rawXml, EventRecord? record)
    {
        var parsed = Parse(rawXml);
        return record is null ? parsed : EventRecordDescriber.Apply(parsed, record);
    }

    public WindowsMonitorEvent Parse(string rawXml)
    {
        var root = XDocument.Parse(rawXml).Root
                   ?? throw new FormatException("XML event khong co phan tu goc.");

        var system = root.Element(EventNs + "System")
                     ?? throw new FormatException("XML event thieu phan tu <System>.");

        var eventId = ParseEventId(system);
        var data = ReadEventData(root);

        var result = new WindowsMonitorEvent
        {
            EventId = eventId,
            ObjectType = MonitoredEventIds.GetObjectType(eventId),
            ActionDescription = MonitoredEventIds.GetActionDescription(eventId),
            TimeCreated = ReadTimeCreated(system),
            Hostname = system.Element(EventNs + "Computer")?.Value ?? "unknown",
            Channel = system.Element(EventNs + "Channel")?.Value ?? "unknown",
            ProviderName = system.Element(EventNs + "Provider")?.Attribute("Name")?.Value ?? "unknown",
            RecordId = long.TryParse(system.Element(EventNs + "EventRecordID")?.Value, out var rid) ? rid : null,
            Data = data,
            RawXml = rawXml
        };

        result = ApplyDisplayFields(result, root, system);

        return eventId switch
        {
            4698 or 4699 or 4700 or 4701 or 4702 => EnrichScheduledTask(result, data),
            4697 => EnrichServiceFromSecurity(result, data),
            7045 => EnrichServiceInstalled(result, data, system),
            7040 => EnrichServiceStartTypeChanged(result, data, system),
            106 => EnrichTaskRegisteredOperational(result, data),
            140 or 141 => EnrichTaskChangedOperational(result, data),
            200 => EnrichTaskActionStarted(result, data, system),
            201 => EnrichTaskActionCompleted(result, data, system),
            _ => EnrichUnknown(result, data, system)
        };
    }

    // ---------------------------------------------------------------- Scheduled Task

    private static WindowsMonitorEvent EnrichScheduledTask(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data)
    {
        // BAY: 4702 (task updated) dung 'TaskContentNew', cac event task con lai dung
        // 'TaskContent'. Doc cung mot ten cho ca nhom se mat du lieu dung o 4702.
        var taskContent = Get(data, "TaskContent") ?? Get(data, "TaskContentNew");

        var enriched = e with
        {
            IsRecognized = true,
            ObjectName = Get(data, "TaskName"),
            ActorAccount = BuildAccountName(Get(data, "SubjectDomainName"), Get(data, "SubjectUserName")),
            ActorSid = Get(data, "SubjectUserSid"),
            TaskContentXml = taskContent
        };

        return taskContent is null ? enriched : EnrichFromTaskContent(enriched, taskContent);
    }

    /// <summary>
    /// TaskContent la MOT XML LONG TRONG XML (bi escape trong the Data). Moi ra
    /// command / arguments / user chay / run level de sau nay cham diem rui ro.
    /// </summary>
    private static WindowsMonitorEvent EnrichFromTaskContent(WindowsMonitorEvent e, string taskContentXml)
    {
        try
        {
            var task = XDocument.Parse(taskContentXml).Root;
            if (task is null)
            {
                return e;
            }

            var principal = task.Element(TaskNs + "Principals")?.Element(TaskNs + "Principal");

            // Task khong nhat thiet chay bang <Exec>. Cac task he thong (NGEN,
            // RefreshCache...) dung <ComHandler> voi mot CLSID - khong he co <Command>.
            var action = task.Element(TaskNs + "Actions")?.Elements().FirstOrDefault();
            var actionType = action?.Name.LocalName;

            var enriched = e with
            {
                TaskActionType = actionType,
                TaskRunAsUser = principal?.Element(TaskNs + "UserId")?.Value,
                TaskRunLevel = principal?.Element(TaskNs + "RunLevel")?.Value
            };

            return actionType switch
            {
                "Exec" => enriched with
                {
                    TaskCommand = action!.Element(TaskNs + "Command")?.Value,
                    TaskArguments = action.Element(TaskNs + "Arguments")?.Value
                },
                "ComHandler" => enriched with
                {
                    TaskComHandlerClassId = action!.Element(TaskNs + "ClassId")?.Value
                },
                _ => enriched
            };
        }
        catch (Exception)
        {
            // XML task hong khong duoc lam hong ca event - van giu nguyen ban tho.
            return e;
        }
    }

    // ---------------------------------------------------------------- Service

    /// <summary>
    /// 4697 (Security) mo ta cung hanh dong nhu 7045 nhung TEN FIELD KHAC va GIA TRI
    /// la MA SO (ServiceStartType='3', ServiceType='0x10') thay vi chu. Chuan hoa ve
    /// cung dang chu voi 7045 de dashboard hien thi thong nhat.
    /// </summary>
    private static WindowsMonitorEvent EnrichServiceFromSecurity(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data) => e with
        {
            IsRecognized = true,
            ObjectName = Get(data, "ServiceName"),
            ImagePath = Get(data, "ServiceFileName"),
            ServiceType = DescribeServiceType(Get(data, "ServiceType")),
            StartType = DescribeStartType(Get(data, "ServiceStartType")),
            ServiceAccount = Get(data, "ServiceAccount"),
            ActorAccount = BuildAccountName(Get(data, "SubjectDomainName"), Get(data, "SubjectUserName")),
            ActorSid = Get(data, "SubjectUserSid")
        };

    private static WindowsMonitorEvent EnrichServiceInstalled(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data, XElement system) => e with
        {
            IsRecognized = true,
            ObjectName = Get(data, "ServiceName"),
            ImagePath = Get(data, "ImagePath"),
            ServiceType = Get(data, "ServiceType"),
            StartType = Get(data, "StartType"),
            ServiceAccount = Get(data, "AccountName"),
            ActorSid = ReadSystemUserSid(system),
            ActorAccount = ResolveSid(ReadSystemUserSid(system))
        };

    /// <summary>
    /// 7040 dung param1..param4 chu khong co ten field:
    /// param1 = ten hien thi, param2 = start type CU, param3 = start type MOI, param4 = ten ngan.
    /// </summary>
    private static WindowsMonitorEvent EnrichServiceStartTypeChanged(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data, XElement system) => e with
        {
            IsRecognized = true,
            ObjectName = Get(data, "param4") ?? Get(data, "param1"),
            DisplayName = Get(data, "param1"),
            PreviousStartType = Get(data, "param2"),
            StartType = Get(data, "param3"),
            ActorSid = ReadSystemUserSid(system),
            ActorAccount = ResolveSid(ReadSystemUserSid(system))
        };

    // ---------------------------------------------------------------- TaskScheduler-Operational
    // Channel Microsoft-Windows-TaskScheduler/Operational: chi tiet hon nhom Security
    // (4698-4702) - vi du 200/201 la task THUC SU CHAY, thu ma nhom Security khong co.
    // Viet dua tren mau XML that thu duoc (samples/106_*.xml, 140_*.xml, 141_*.xml,
    // 200_*.xml, 201_*.xml).

    /// <summary>
    /// TaskName cua channel nay CO KHOANG TRANG THUA o cuoi (da xac nhan qua mau
    /// that, vi du "\WinSentinelSampleCapture "). Cat di de khop dinh dang "\Path"
    /// khong khoang trang ma COM Task Scheduler (TaskManager.cs) va channel Security
    /// dang dung - khong cat se lam lech key luc doi chieu ObjectName giua cac nguon
    /// (vi du tinh nang "sap xep theo moi nhat" o manage.js dua vao TaskManager.Path).
    /// </summary>
    private static string? TrimTaskName(string? raw) => raw?.Trim();

    /// <summary>106 - task vua duoc dang ky (tao moi). Field: TaskName, UserContext.</summary>
    private static WindowsMonitorEvent EnrichTaskRegisteredOperational(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data) => e with
        {
            IsRecognized = true,
            ObjectName = TrimTaskName(Get(data, "TaskName")),
            ActorAccount = Get(data, "UserContext")
        };

    /// <summary>
    /// 140 (task updated) va 141 (task deleted) - CUNG mot hinh dang field: TaskName,
    /// UserName. Dung chung mot ham. Khac voi nhom Security (4698-4702): the &lt;Security&gt;
    /// trong &lt;System&gt; cua nhom nay luon la LocalSystem (Task Scheduler engine thuc
    /// hien thao tac), nguoi dung that nam o field UserName trong EventData.
    /// </summary>
    private static WindowsMonitorEvent EnrichTaskChangedOperational(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data) => e with
        {
            IsRecognized = true,
            ObjectName = TrimTaskName(Get(data, "TaskName")),
            ActorAccount = Get(data, "UserName")
        };

    /// <summary>
    /// 200 - mot action cua task VUA BAT DAU CHAY. Field: TaskName, ActionName (co the
    /// rong voi action ComHandler), TaskInstanceId, EnginePID. Khong co UserContext/
    /// UserName nhu 106/140/141 - actor lay tu System/Security nhu nhom Service.
    /// </summary>
    private static WindowsMonitorEvent EnrichTaskActionStarted(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data, XElement system) => e with
        {
            IsRecognized = true,
            ObjectName = TrimTaskName(Get(data, "TaskName")),
            TaskCommand = Get(data, "ActionName"),
            TaskInstanceId = Get(data, "TaskInstanceId"),
            ActorSid = ReadSystemUserSid(system),
            ActorAccount = ResolveSid(ReadSystemUserSid(system))
        };

    /// <summary>
    /// 201 - mot action cua task VUA CHAY XONG. Field giong 200 nhung them ResultCode -
    /// dung TaskInstanceId de doi chieu voi dong 200 tuong ung (cung mot lan chay).
    /// </summary>
    private static WindowsMonitorEvent EnrichTaskActionCompleted(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data, XElement system) => e with
        {
            IsRecognized = true,
            ObjectName = TrimTaskName(Get(data, "TaskName")),
            TaskCommand = Get(data, "ActionName"),
            TaskInstanceId = Get(data, "TaskInstanceId"),
            TaskActionResultCode = Get(data, "ResultCode"),
            ActorSid = ReadSystemUserSid(system),
            ActorAccount = ResolveSid(ReadSystemUserSid(system))
        };

    /// <summary>
    /// Nhanh du phong cho Event ID chua co xu ly rieng (vi du 7036/7034 - chua lay
    /// duoc mau that nen KHONG doan cau truc). Van tra ve event dung dinh dang,
    /// chi la khong co field chi tiet; du lieu tho nam nguyen trong Data.
    /// </summary>
    private static WindowsMonitorEvent EnrichUnknown(
        WindowsMonitorEvent e, IReadOnlyDictionary<string, string> data, XElement system) => e with
        {
            IsRecognized = false,
            // Doan ten doi tuong theo cac ten field pho bien, khong co thi thoi.
            ObjectName = Get(data, "ServiceName") ?? Get(data, "TaskName") ?? Get(data, "param1"),
            ActorSid = ReadSystemUserSid(system),
            ActorAccount = ResolveSid(ReadSystemUserSid(system))
        };

    // ---------------------------------------------------------------- Helpers

    private static int ParseEventId(XElement system)
    {
        var raw = system.Element(EventNs + "EventID")?.Value;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new FormatException($"Khong doc duoc EventID tu '{raw}'.");
    }

    /// <summary>
    /// Luôn trả về UTC (Kind = Utc). Event log ghi SystemTime theo UTC; giữ nguyên UTC
    /// khi lưu DB rồi mới đổi sang giờ máy lúc hiển thị — nhiều máy nguồn có thể khác
    /// múi giờ nên không được lưu giờ local.
    /// </summary>
    private static DateTime ReadTimeCreated(XElement system)
    {
        var raw = system.Element(EventNs + "TimeCreated")?.Attribute("SystemTime")?.Value;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value)
            ? value.UtcDateTime
            : DateTime.UtcNow;
    }

    // ---------------------------------------------------------------- Field hien thi kieu Event Viewer

    /// <summary>
    /// Hai nguồn khác hẳn nhau:
    /// 1. <c>&lt;System&gt;</c> — Level/Task/Keywords luôn có sẵn nhưng chỉ là SỐ.
    /// 2. <c>&lt;RenderingInfo&gt;</c> — CHỈ có ở event forward qua WEF format mặc định
    ///    "Rendered Text", mang sẵn cả Message lẫn tên đã dịch nên collector đọc được
    ///    mà không cần message DLL của máy nguồn. Đổi sang <c>wecutil /cf:Events</c> sẽ
    ///    CẮT MẤT khối này — xem docs/wef-mapping.md.
    /// Còn lại phải lấy từ EventRecord sống (<see cref="EventRecordDescriber"/>).
    ///
    /// ⚠️ Nhánh RenderingInfo CHƯA verify bằng mẫu thật (máy dev chưa bật WEF);
    /// fixture test là file tự soạn <c>renderinginfo_synthetic.xml</c>.
    /// </summary>
    private static WindowsMonitorEvent ApplyDisplayFields(
        WindowsMonitorEvent e, XElement root, XElement system)
    {
        var rendering = root.Element(EventNs + "RenderingInfo");
        var level = ParseIntOrNull(system.Element(EventNs + "Level")?.Value);

        return e with
        {
            Description = Clean(rendering?.Element(EventNs + "Message")?.Value),
            Level = level,
            // Ten da dich tu may nguon uu tien hon bang cung ben duoi.
            LevelDisplayName = Clean(rendering?.Element(EventNs + "Level")?.Value)
                               ?? DescribeLevel(level),
            TaskCategoryId = ParseIntOrNull(system.Element(EventNs + "Task")?.Value),
            TaskCategoryName = Clean(rendering?.Element(EventNs + "Task")?.Value),
            OpcodeName = Clean(rendering?.Element(EventNs + "Opcode")?.Value),
            Keywords = ReadKeywords(rendering, system)
        };
    }

    /// <summary>
    /// Bảng HẰNG SỐ của Windows (khác Task vốn là metadata riêng từng provider) nên
    /// map thẳng từ XML được. 0 = LogAlways, Event Viewer vẫn hiện "Information".
    /// </summary>
    private static string? DescribeLevel(int? level) => level switch
    {
        null => null,
        0 => "Information",
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        5 => "Verbose",
        _ => $"Level {level}"
    };

    /// <summary>Tên Keyword đã dịch nếu có, không thì giá trị hex thô.</summary>
    private static string? ReadKeywords(XElement? rendering, XElement system)
    {
        var named = rendering?.Element(EventNs + "Keywords")?
            .Elements(EventNs + "Keyword")
            .Select(k => k.Value.Trim())
            .Where(v => v.Length > 0)
            .ToArray();

        return named is { Length: > 0 }
            ? string.Join(", ", named)
            : Clean(system.Element(EventNs + "Keywords")?.Value);
    }

    private static int? ParseIntOrNull(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string? Clean(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Doc &lt;EventData&gt; thanh dictionary. Field khong co thuoc tinh Name (mot so
    /// provider khong dat ten) duoc danh so theo vi tri: Data0, Data1...
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadEventData(XElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var container = root.Element(EventNs + "EventData") ?? root.Element(EventNs + "UserData");
        if (container is null)
        {
            return result;
        }

        var index = 0;
        foreach (var element in container.Descendants().Where(d => d.Name == EventNs + "Data"))
        {
            var name = element.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Data{index}";
            }

            result[name] = element.Value;
            index++;
        }

        return result;
    }

    private static string? ReadSystemUserSid(XElement system)
        => system.Element(EventNs + "Security")?.Attribute("UserID")?.Value;

    private static string? Get(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? BuildAccountName(string? domain, string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return null;
        return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
    }

    private static string? ResolveSid(string? sid)
        => sid is not null && WellKnownSids.TryGetValue(sid, out var name) ? name : sid;

    /// <summary>Doi ma start type cua 4697 (0..4) sang chu giong 7045.</summary>
    private static string? DescribeStartType(string? raw) => raw switch
    {
        null => null,
        "0" => "boot start",
        "1" => "system start",
        "2" => "auto start",
        "3" => "demand start",
        "4" => "disabled",
        _ => raw
    };

    /// <summary>Doi ma service type cua 4697 (hex) sang chu giong 7045.</summary>
    private static string? DescribeServiceType(string? raw)
    {
        if (raw is null) return null;

        var text = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? raw[2..]
            : raw;

        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return raw;
        }

        return value switch
        {
            0x1 => "kernel mode driver",
            0x2 => "file system driver",
            0x10 => "user mode service",
            0x20 => "shared process service",
            0x110 => "interactive user mode service",
            _ => raw
        };
    }
}

