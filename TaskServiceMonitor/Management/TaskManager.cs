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

/// <summary>
/// Đọc và thao tác Scheduled Task qua COM API <c>Schedule.Service</c> — chính là API
/// mà Task Scheduler dùng. Windows không expose Task Scheduler qua DLL phẳng như
/// service, nên đây là đường tương đương WinAPI cho nhóm task.
///
/// Dùng late binding (<c>dynamic</c>) để khỏi phải nhúng type library.
/// </summary>
public sealed class TaskManager(SafeNameGuard guard, ILogger<TaskManager> logger)
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
            // Doc action dau tien. Task he thong hay dung ComHandler thay vi Exec -
            // do la binh thuong, khong phai loi (xem WindowsEventParser).
            foreach (dynamic action in task.Definition.Actions)
            {
                // TASK_ACTION_EXEC = 0, TASK_ACTION_COM_HANDLER = 5.
                if ((int)action.Type == 0)
                {
                    actionType = "Exec";
                    command = action.Path;
                }
                else
                {
                    actionType = "ComHandler";
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
    public void CreateOrUpdate(string name, string command, string? arguments, string startBoundary)
    {
        guard.EnsureWritable(name);

        var xml = BuildTaskXml(command, arguments, startBoundary);

        dynamic service = ConnectService();
        try
        {
            dynamic folder = service.GetFolder("\\");
            folder.RegisterTask(
                name,
                xml,
                TaskCreateOrUpdate,
                Type.Missing,           // chay bang tai khoan hien tai
                Type.Missing,
                TaskLogonInteractiveToken,
                Type.Missing);

            logger.LogInformation("Da tao task {Name} -> {Command}", name, command);
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
            task.Run(Type.Missing);

            logger.LogInformation("Da chay task {Name}", name);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    private static string BuildTaskXml(string command, string? arguments, string startBoundary)
    {
        var exec = new XElement(TaskNs + "Exec",
            new XElement(TaskNs + "Command", command));

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            exec.Add(new XElement(TaskNs + "Arguments", arguments));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(TaskNs + "Task",
                new XAttribute("version", "1.2"),
                new XElement(TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Description", "Tao boi TaskServiceMonitor")),
                new XElement(TaskNs + "Triggers",
                    new XElement(TaskNs + "TimeTrigger",
                        new XElement(TaskNs + "StartBoundary", startBoundary),
                        new XElement(TaskNs + "Enabled", "true"))),
                new XElement(TaskNs + "Principals",
                    new XElement(TaskNs + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNs + "LogonType", "InteractiveToken"),
                        new XElement(TaskNs + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskNs + "Settings",
                    new XElement(TaskNs + "Enabled", "true"),
                    new XElement(TaskNs + "AllowStartOnDemand", "true"),
                    new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew")),
                new XElement(TaskNs + "Actions",
                    new XAttribute("Context", "Author"),
                    exec)));

        return doc.ToString();
    }

    private static DateTime? NormalizeDate(dynamic value)
    {
        try
        {
            DateTime dt = value;
            // Task Scheduler tra ve nam 1899 cho "chua bao gio chay".
            return dt.Year < 1900 ? null : dt;
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
}
