using System.Runtime.InteropServices;

namespace TaskServiceMonitor.Management;

public sealed record TaskInfo(
    string Name,
    string Path,
    bool Enabled,
    string State,
    DateTime? LastRunTime,
    DateTime? NextRunTime,
    string? Command,
    string? ActionType);

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
/// Đọc Scheduled Task qua COM API <c>Schedule.Service</c> — chính là API mà Task
/// Scheduler dùng. Windows không expose Task Scheduler qua DLL phẳng như service,
/// nên đây là đường tương đương WinAPI cho nhóm task.
///
/// CHỈ ĐỌC: theo yêu cầu của mentor, app không tự thao tác (tạo/sửa/xoá/bật/tắt/chạy)
/// Task/Service nữa — chỉ giám sát log/event và đối chiếu trạng thái hiện tại.
///
/// Dùng late binding (<c>dynamic</c>) để khỏi phải nhúng type library.
/// </summary>
public sealed class TaskManager(ILogger<TaskManager> logger)
{
    /// <summary>Cờ cho GetTasks: 1 = lấy cả task đang ẩn.</summary>
    private const int IncludeHiddenTasks = 1;

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
            actionType);
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

    // Trước đây có CreateOrUpdate/CreateViaObjectModel/Delete/SetEnabled/RunNow/
    // Validate/BuildTaskXml ở đây, đăng ký task qua COM RegisterTask/
    // RegisterTaskDefinition/DeleteTask. Mentor xác nhận app chỉ cần MONITORING
    // (đọc log + đối chiếu trạng thái hiện tại), không cần tự thao tác Task — đã gỡ
    // bỏ cùng lớp rào SafeNameGuard/InputPolicy và model TaskDefinitionRequest vốn
    // chỉ tồn tại để bảo vệ/dựng các thao tác ghi đó.

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
