using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;

namespace TaskServiceMonitor.Management;

public sealed record TaskInfo(
    string Name,
    string Path,
    bool Enabled,
    string State,
    DateTime? LastRunTime,
    DateTime? NextRunTime,
    string? Command,
    string? ActionType,
    bool IsWritable);

public sealed record TaskTriggerInfo(string Type, bool Enabled, string? StartBoundary, string? Summary);

public sealed record TaskActionInfo(string Type, string? Path, string? Arguments, string? WorkingDirectory);

/// <summary>
/// Bản đầy đủ cho modal chi tiết — đọc thêm <c>Definition.RegistrationInfo</c>,
/// <c>.Principal</c>, <c>.Triggers</c>, <c>.Settings</c>.
///
/// CỐ Ý tách khỏi <see cref="TaskInfo"/>: mỗi field ở đây là thêm vài lời gọi COM,
/// mà máy này có hàng trăm task — trả hết trong danh sách sẽ làm tab Tasks chậm hẳn.
/// Modal chỉ mở một task nên trả tiền đúng lúc cần.
/// </summary>
public sealed record TaskDetail(
    string Name,
    string Path,
    bool Enabled,
    string State,
    DateTime? LastRunTime,
    DateTime? NextRunTime,
    bool IsWritable,

    string? Author,
    string? Description,
    string? RegistrationDate,

    // Principal / User / Run Level deu nam trong cung mot object Definition.Principal.
    string? UserId,
    string? GroupId,
    string? LogonType,
    string? RunLevel,

    bool Hidden,
    string? LastTaskResult,
    int? NumberOfMissedRuns,

    IReadOnlyList<TaskTriggerInfo> Triggers,
    IReadOnlyList<TaskActionInfo> Actions);

/// <summary>
/// Đọc và thao tác Scheduled Task qua COM API <c>Schedule.Service</c> — chính là API
/// mà Task Scheduler dùng. Windows không expose Task Scheduler qua DLL phẳng như
/// service, nên đây là đường tương đương WinAPI cho nhóm task.
///
/// Dùng late binding (<c>dynamic</c>) để khỏi phải nhúng type library.
/// </summary>
public sealed class TaskManager(SafeNameGuard guard, InputPolicy policy, ILogger<TaskManager> logger)
{
    /// <summary>Cờ cho GetTasks: 1 = lấy cả task đang ẩn.</summary>
    private const int IncludeHiddenTasks = 1;

    // Co cho RegisterTask
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;

    private static readonly XNamespace TaskNs =
        "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static dynamic ConnectService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException(
                       "Khong tim thay COM 'Schedule.Service' - Task Scheduler khong kha dung.");

        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    public IReadOnlyList<TaskInfo> List()
    {
        dynamic service = ConnectService();
        var result = new List<TaskInfo>();

        try
        {
            CollectFolder(service.GetFolder("\\"), result);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }

        return result.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void CollectFolder(dynamic folder, List<TaskInfo> result)
    {
        foreach (dynamic task in folder.GetTasks(IncludeHiddenTasks))
        {
            try
            {
                result.Add(Describe(task));
            }
            catch (Exception ex)
            {
                // Mot task doc loi khong duoc lam hong ca danh sach.
                logger.LogDebug(ex, "Bo qua mot task khong doc duoc");
            }
        }

        foreach (dynamic sub in folder.GetFolders(0))
        {
            CollectFolder(sub, result);
        }
    }

    private TaskInfo Describe(dynamic task)
    {
        string name = task.Name;
        string path = task.Path;
        string? command = null;
        string? actionType = null;

        try
        {
            // Cot "Lenh" trong danh sach chi hien action DAU TIEN cho gon; ban day du
            // (moi action) nam o TaskDetail. Task he thong hay dung ComHandler thay vi
            // Exec - binh thuong, khong phai loi (xem WindowsEventParser).
            foreach (dynamic action in task.Definition.Actions)
            {
                actionType = DescribeActionType((int)action.Type);
                if (actionType == "Exec")
                {
                    command = action.Path;
                }

                break;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Khong doc duoc action cua task {Path}", path);
        }

        return new TaskInfo(
            name,
            path,
            task.Enabled,
            DescribeState((int)task.State),
            NormalizeDate(task.LastRunTime),
            NormalizeDate(task.NextRunTime),
            command,
            actionType,
            guard.IsWritable(path));
    }

    /// <summary>
    /// Bản chi tiết cho modal. Mỗi nhóm field bọc try/catch RIÊNG: task hệ thống có
    /// thể chặn đọc một phần <c>Definition</c>, một nhóm lỗi không được làm rỗng cả
    /// modal (cùng tinh thần <c>EventRecordDescriber.Try</c>).
    /// </summary>
    public TaskDetail Detail(string path)
    {
        dynamic service = ConnectService();
        try
        {
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(path.StartsWith('\\') ? path : "\\" + path);

            string? author = null, description = null, registrationDate = null;
            string? userId = null, groupId = null, logonType = null, runLevel = null;
            var hidden = false;
            var triggers = new List<TaskTriggerInfo>();
            var actions = new List<TaskActionInfo>();

            try
            {
                dynamic reg = task.Definition.RegistrationInfo;
                author = Blank(reg.Author);
                description = Blank(reg.Description);
                registrationDate = Blank(reg.Date);
            }
            catch (Exception ex) { logger.LogDebug(ex, "Khong doc duoc RegistrationInfo cua {Path}", path); }

            try
            {
                dynamic principal = task.Definition.Principal;
                userId = Blank(principal.UserId);
                groupId = Blank(principal.GroupId);
                logonType = DescribeLogonType((int)principal.LogonType);
                runLevel = DescribeRunLevel((int)principal.RunLevel);
            }
            catch (Exception ex) { logger.LogDebug(ex, "Khong doc duoc Principal cua {Path}", path); }

            try { hidden = (bool)task.Definition.Settings.Hidden; }
            catch (Exception ex) { logger.LogDebug(ex, "Khong doc duoc Settings cua {Path}", path); }

            try
            {
                foreach (dynamic trigger in task.Definition.Triggers)
                {
                    triggers.Add(new TaskTriggerInfo(
                        DescribeTriggerType((int)trigger.Type),
                        (bool)trigger.Enabled,
                        Blank(trigger.StartBoundary),
                        Blank(trigger.Id)));
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "Khong doc duoc Triggers cua {Path}", path); }

            try
            {
                foreach (dynamic action in task.Definition.Actions)
                {
                    var type = DescribeActionType((int)action.Type);

                    // Chi action kieu Exec moi co Path/Arguments/WorkingDirectory; hoi
                    // cac property do tren ComHandler se nem COMException.
                    actions.Add(type == "Exec"
                        ? new TaskActionInfo(type, Blank(action.Path), Blank(action.Arguments), Blank(action.WorkingDirectory))
                        : new TaskActionInfo(type, TryComClassId(action), null, null));
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "Khong doc duoc Actions cua {Path}", path); }

            return new TaskDetail(
                task.Name, task.Path, task.Enabled, DescribeState((int)task.State),
                NormalizeDate(task.LastRunTime), NormalizeDate(task.NextRunTime),
                guard.IsWritable((string)task.Path),
                author, description, registrationDate,
                userId, groupId, logonType, runLevel,
                hidden,
                TryLastTaskResult(task), TryMissedRuns(task),
                triggers, actions);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    private static string? TryComClassId(dynamic action)
    {
        try { return Blank(action.ClassId); }
        catch { return null; }
    }

    /// <summary>Mã kết quả lần chạy cuối. 0 = thành công; hiện kèm hex vì tài liệu Microsoft tra theo hex.</summary>
    private static string? TryLastTaskResult(dynamic task)
    {
        try
        {
            int code = task.LastTaskResult;
            return code == 0 ? "0 (thành công)" : $"{code} (0x{code:X8})";
        }
        catch { return null; }
    }

    private static int? TryMissedRuns(dynamic task)
    {
        try { return (int)task.NumberOfMissedRuns; }
        catch { return null; }
    }

    private static string? Blank(dynamic value)
    {
        string? text = value as string;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>XML định nghĩa task — cùng định dạng với field TaskContent của event 4698.</summary>
    public string GetXml(string path)
    {
        dynamic service = ConnectService();
        try
        {
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(path.StartsWith('\\') ? path : "\\" + path);
            return task.Xml;
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>
    /// Tạo <b>hoặc cập nhật</b> task (cờ <c>TASK_CREATE_OR_UPDATE</c>): tên chưa có thì
    /// tạo mới và sinh event <b>4698</b>, tên đã có thì ghi đè và sinh event <b>4702</b>.
    ///
    /// Dựng đúng XML schema mà <c>WindowsEventParser</c> đã biết đọc, rồi đăng ký qua COM.
    /// Vòng khép kín: XML ghi ra chính là XML đọc lại được từ event 4698/4702.
    /// </summary>
    /// <summary>
    /// Kiểm tra mọi giá trị người dùng nhập, rồi trả về bản đã chuẩn hoá (đường dẫn
    /// tuyệt đối, thời gian đúng định dạng). Tách riêng để cả hai đường tạo task
    /// (XML và object model) dùng chung — không thể có đường nào lách được whitelist.
    /// </summary>
    private TaskDefinitionRequest Validate(TaskDefinitionRequest req)
    {
        // BA lop kiem tra, moi lop mot cau hoi khac nhau:
        //   guard      - duoc phep GHI len ten nay khong?
        //   policy ten - ten co dung dinh dang khong?
        //   policy exe - thu SE CHAY co nam trong whitelist khong?
        // Truoc buoc 10 chi co lop dau, nen tao duoc task chay bat cu file gi.
        guard.EnsureWritable(req.Name);
        policy.EnsureValidName(req.Name);

        if (req.Actions.Count == 0)
        {
            throw new ArgumentException("Task phai co it nhat mot action.");
        }

        var actions = req.Actions.Select(a =>
        {
            policy.EnsureValidArguments(a.Arguments);
            return a with { Command = policy.EnsureAllowedExecutable(a.Command, "command") };
        }).ToList();

        var triggers = req.Triggers.Count > 0
            ? req.Triggers
            // Khong co trigger nao = task chi chay khi bam tay. Van hop le, nhung them
            // mot TimeTrigger hen gio xa de van sinh ra log dung nhu truoc day.
            : [new TriggerRequest("Time", InputPolicy.ParseStartBoundary(null))];

        triggers = triggers
            .Select(t => t with { StartBoundary = t.Type is "Time" or "Daily"
                ? InputPolicy.ParseStartBoundary(t.StartBoundary)
                : t.StartBoundary })
            .ToList();

        return req with { Actions = actions, Triggers = triggers };
    }

    public void CreateOrUpdate(TaskDefinitionRequest request)
    {
        var req = Validate(request);
        var xml = BuildTaskXml(req);

        dynamic service = ConnectService();
        try
        {
            dynamic folder = service.GetFolder("\\");
            folder.RegisterTask(
                req.Name,
                xml,
                TaskCreateOrUpdate,
                Type.Missing,           // chay bang tai khoan hien tai
                Type.Missing,
                TaskLogonInteractiveToken,
                Type.Missing);

            logger.LogInformation(
                "Da tao task {Name} ({Actions} action, {Triggers} trigger, RunLevel={RunLevel})",
                req.Name, req.Actions.Count, req.Triggers.Count, req.RunLevel);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>
    /// Đường TƯƠNG ĐƯƠNG dùng object model của COM thay vì XML:
    /// <c>TaskService.NewTask()</c> → gán <c>RegistrationInfo/Triggers/Actions/
    /// Principal/Settings</c> → <c>TaskFolder.RegisterTaskDefinition()</c>, đúng như
    /// tài liệu TaskService mô tả.
    ///
    /// KHÔNG phải đường mặc định — <see cref="CreateOrUpdate"/> (dựng XML) mới là.
    /// Lý do: XML dựng ra chính là XML đọc lại được từ event 4698/4702, giữ vòng khép
    /// kín với <c>WindowsEventParser</c>; và nó là hàm thuần nên test được không cần
    /// Windows. Giữ hàm này để đối chiếu hai cách làm.
    ///
    /// Đi qua CÙNG <see cref="Validate"/> — không được có đường nào lách whitelist.
    /// </summary>
    public void CreateViaObjectModel(TaskDefinitionRequest request)
    {
        var req = Validate(request);

        dynamic service = ConnectService();
        try
        {
            dynamic definition = service.NewTask(0);

            definition.RegistrationInfo.Description =
                string.IsNullOrWhiteSpace(req.Description) ? "Tao boi TaskServiceMonitor" : req.Description;
            if (!string.IsNullOrWhiteSpace(req.Author))
            {
                definition.RegistrationInfo.Author = req.Author;
            }

            foreach (var t in req.Triggers)
            {
                // TASK_TRIGGER_TYPE2: 0=Event, 1=Time, 2=Daily, 8=Boot, 9=Logon, 7=Registration.
                var typeCode = t.Type switch
                {
                    "Time" => 1, "Daily" => 2, "Registration" => 7, "Boot" => 8, "Logon" => 9, _ => 1
                };

                dynamic trigger = definition.Triggers.Create(typeCode);
                trigger.Enabled = t.Enabled;
                if (!string.IsNullOrWhiteSpace(t.StartBoundary))
                {
                    trigger.StartBoundary = t.StartBoundary;
                }
            }

            foreach (var a in req.Actions)
            {
                dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
                action.Path = a.Command;
                if (!string.IsNullOrWhiteSpace(a.Arguments)) action.Arguments = a.Arguments;
                if (!string.IsNullOrWhiteSpace(a.WorkingDirectory)) action.WorkingDirectory = a.WorkingDirectory;
            }

            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = req.RunLevel == "HighestAvailable" ? 1 : 0;

            definition.Settings.Enabled = true;
            definition.Settings.Hidden = req.Hidden;
            definition.Settings.AllowDemandStart = req.AllowStartOnDemand;
            definition.Settings.DisallowStartIfOnBatteries = req.StopIfGoingOnBatteries;

            dynamic folder = service.GetFolder("\\");
            folder.RegisterTaskDefinition(
                req.Name, definition, TaskCreateOrUpdate,
                Type.Missing, Type.Missing, TaskLogonInteractiveToken, Type.Missing);

            logger.LogInformation("Da tao task {Name} qua object model (NewTask)", req.Name);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    public void Delete(string name)
    {
        guard.EnsureWritable(name);

        dynamic service = ConnectService();
        try
        {
            dynamic folder = service.GetFolder("\\");
            folder.DeleteTask(name.TrimStart('\\'), 0);
            logger.LogInformation("Da xoa task {Name}", name);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>
    /// Bật/tắt task. Sinh event <b>4700</b> (enabled) hoặc <b>4701</b> (disabled) —
    /// hai Event ID mà app theo dõi nhưng trước đây chưa tự sinh được.
    /// </summary>
    public void SetEnabled(string name, bool enabled)
    {
        guard.EnsureWritable(name);

        dynamic service = ConnectService();
        try
        {
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(name.TrimStart('\\'));
            task.Enabled = enabled;

            logger.LogInformation("Da {Action} task {Name}", enabled ? "bat" : "tat", name);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>Chạy task ngay lập tức, không chờ tới lịch.</summary>
    public void RunNow(string name)
    {
        guard.EnsureWritable(name);

        dynamic service = ConnectService();
        try
        {
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(name.TrimStart('\\'));

            // Khong dung Type.Missing: pParams cua IRegisteredTask::Run la VARIANT
            // thuong (khong danh dau optional khi goi qua dynamic/late-binding),
            // Task Scheduler tu choi voi loi "Value does not fall within the
            // expected range." Chuoi rong tuong duong "khong co tham so".
            task.Run(string.Empty);

            logger.LogInformation("Da chay task {Name}", name);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>
    /// Model → XML. HÀM THUẦN, không đụng COM, nên test được bằng unit test thường.
    ///
    /// Giữ cách dựng XML (thay vì object model của COM) là CÓ CHỦ ĐÍCH: XML sinh ra ở
    /// đây chính là XML đọc lại được từ event 4698/4702, tức vòng khép kín với
    /// <c>WindowsEventParser</c>. Xem <see cref="CreateViaObjectModel"/> cho đường
    /// tương đương dùng <c>TaskService.NewTask()</c>.
    /// </summary>
    internal static string BuildTaskXml(TaskDefinitionRequest req)
    {
        var registration = new XElement(TaskNs + "RegistrationInfo",
            new XElement(TaskNs + "Description",
                string.IsNullOrWhiteSpace(req.Description) ? "Tao boi TaskServiceMonitor" : req.Description));

        if (!string.IsNullOrWhiteSpace(req.Author))
        {
            registration.Add(new XElement(TaskNs + "Author", req.Author));
        }

        var triggers = new XElement(TaskNs + "Triggers",
            req.Triggers.Select(BuildTrigger).Where(t => t is not null));

        // Principal: UserId va GroupId LOAI TRU nhau - khai ca hai thi Task Scheduler
        // tu choi ca task.
        var principal = new XElement(TaskNs + "Principal", new XAttribute("id", "Author"));

        if (!string.IsNullOrWhiteSpace(req.GroupId))
        {
            principal.Add(new XElement(TaskNs + "GroupId", req.GroupId));
        }
        else if (!string.IsNullOrWhiteSpace(req.UserId))
        {
            principal.Add(new XElement(TaskNs + "UserId", req.UserId));
        }

        principal.Add(
            new XElement(TaskNs + "LogonType", req.LogonType),
            new XElement(TaskNs + "RunLevel", req.RunLevel));

        var settings = new XElement(TaskNs + "Settings",
            new XElement(TaskNs + "Enabled", "true"),
            new XElement(TaskNs + "Hidden", Bool(req.Hidden)),
            new XElement(TaskNs + "AllowStartOnDemand", Bool(req.AllowStartOnDemand)),
            new XElement(TaskNs + "DisallowStartIfOnBatteries", Bool(req.StopIfGoingOnBatteries)),
            new XElement(TaskNs + "MultipleInstancesPolicy", req.MultipleInstancesPolicy));

        if (!string.IsNullOrWhiteSpace(req.ExecutionTimeLimit))
        {
            settings.Add(new XElement(TaskNs + "ExecutionTimeLimit", req.ExecutionTimeLimit));
        }

        var actions = new XElement(TaskNs + "Actions",
            new XAttribute("Context", "Author"),
            req.Actions.Select(BuildAction).Where(a => a is not null));

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(TaskNs + "Task",
                new XAttribute("version", "1.2"),
                registration, triggers,
                new XElement(TaskNs + "Principals", principal),
                settings, actions));

        return doc.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static XElement? BuildTrigger(TriggerRequest trigger)
    {
        // Ten phan tu XML KHAC ten hien thi: "Time" -> <TimeTrigger>, "Logon" ->
        // <LogonTrigger>... Sai ten thi Task Scheduler tu choi ca task.
        var element = trigger.Type switch
        {
            "Time" => new XElement(TaskNs + "TimeTrigger"),
            "Daily" => new XElement(TaskNs + "CalendarTrigger"),
            "Logon" => new XElement(TaskNs + "LogonTrigger"),
            "Boot" => new XElement(TaskNs + "BootTrigger"),
            "Registration" => new XElement(TaskNs + "RegistrationTrigger"),
            _ => null
        };

        if (element is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(trigger.StartBoundary))
        {
            element.Add(new XElement(TaskNs + "StartBoundary", trigger.StartBoundary));
        }

        element.Add(new XElement(TaskNs + "Enabled", Bool(trigger.Enabled)));

        if (trigger.Type == "Daily")
        {
            // CalendarTrigger BAT BUOC co lich con, thieu la XML khong hop le.
            element.Add(new XElement(TaskNs + "ScheduleByDay",
                new XElement(TaskNs + "DaysInterval", trigger.DaysInterval is > 0 ? trigger.DaysInterval : 1)));
        }

        if (trigger.Type == "Logon" && !string.IsNullOrWhiteSpace(trigger.UserId))
        {
            element.Add(new XElement(TaskNs + "UserId", trigger.UserId));
        }

        return element;
    }

    private static XElement? BuildAction(ActionRequest action)
    {
        if (string.IsNullOrWhiteSpace(action.Command))
        {
            return null;
        }

        var exec = new XElement(TaskNs + "Exec",
            new XElement(TaskNs + "Command", action.Command));

        if (!string.IsNullOrWhiteSpace(action.Arguments))
        {
            exec.Add(new XElement(TaskNs + "Arguments", action.Arguments));
        }

        if (!string.IsNullOrWhiteSpace(action.WorkingDirectory))
        {
            exec.Add(new XElement(TaskNs + "WorkingDirectory", action.WorkingDirectory));
        }

        return exec;
    }

    private static DateTime? NormalizeDate(dynamic value)
    {
        try
        {
            DateTime dt = value;

            // "Chua bao gio chay" co hai sentinel tuy phien ban/duong goi COM:
            // 1899-12-30 (OLE Automation date 0) hoac 1999-11-30 (sentinel cu cua
            // Task Scheduler 1.0 API). Ca hai deu khong phai ngay chay that.
            var isNeverRun = dt.Year < 1900
                || (dt.Year == 1999 && dt.Month == 11 && dt.Day == 30);

            return isNeverRun ? null : dt;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeState(int state) => state switch
    {
        0 => "unknown",
        1 => "disabled",
        2 => "queued",
        3 => "ready",
        4 => "running",
        _ => "unknown"
    };

    /// <summary>
    /// TASK_ACTION_TYPE. Trước đây chỉ phân biệt "0 hay không phải 0" nên SendEmail và
    /// ShowMessage đều bị gán nhầm nhãn "ComHandler".
    /// </summary>
    internal static string DescribeActionType(int type) => type switch
    {
        0 => "Exec",
        5 => "ComHandler",
        6 => "SendEmail",
        7 => "ShowMessage",
        _ => $"Unknown ({type})"
    };

    /// <summary>TASK_RUNLEVEL_TYPE — "HighestAvailable" nghĩa là task chạy quyền Administrator.</summary>
    internal static string DescribeRunLevel(int runLevel) => runLevel switch
    {
        0 => "LeastPrivilege",
        1 => "HighestAvailable",
        _ => $"Unknown ({runLevel})"
    };

    internal static string DescribeLogonType(int logonType) => logonType switch
    {
        0 => "None",
        1 => "Password",
        2 => "S4U",
        3 => "InteractiveToken",
        4 => "Group",
        5 => "ServiceAccount",
        6 => "InteractiveTokenOrPassword",
        _ => $"Unknown ({logonType})"
    };

    internal static string DescribeTriggerType(int type) => type switch
    {
        0 => "Event",
        1 => "Time",
        2 => "Daily",
        3 => "Weekly",
        4 => "Monthly",
        5 => "MonthlyDOW",
        6 => "Idle",
        7 => "Registration",
        8 => "Boot",
        9 => "Logon",
        11 => "SessionStateChange",
        12 => "CustomTrigger01",
        _ => $"Unknown ({type})"
    };
}
