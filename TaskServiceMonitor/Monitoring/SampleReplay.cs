using TaskServiceMonitor.Models;

namespace TaskServiceMonitor.Monitoring;

/// <summary>
/// Công cụ dev: nạp lại toàn bộ file XML thật trong samples/ và chạy qua
/// <see cref="WindowsEventParser"/>, in kết quả ra console.
/// Mục đích: xác nhận parser đúng trên dữ liệu thật mà không cần dựng lại event
/// hay chạy quyền Administrator.
/// </summary>
public static class SampleReplay
{
    public static int Run(string[] args)
    {
        var directory = ResolveDirectory(args);
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Khong tim thay thu muc mau: {directory}");
            return 1;
        }

        var files = Directory.GetFiles(directory, "*.xml").OrderBy(f => f).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"Thu muc '{directory}' khong co file .xml nao.");
            return 1;
        }

        Console.WriteLine($"Doc {files.Length} file mau tu: {directory}");
        Console.WriteLine();

        var parser = new WindowsEventParser();
        var failures = 0;
        var unrecognised = new List<int>();
        var seenEventIds = new HashSet<int>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var e = parser.Parse(File.ReadAllText(file));
                seenEventIds.Add(e.EventId);
                if (!e.IsRecognized)
                {
                    unrecognised.Add(e.EventId);
                }

                Console.WriteLine($"[{e.EventId}] {e.ActionDescription}  ({name})");
                Console.WriteLine($"      thoi diem : {e.TimeCreated.ToLocalTime():yyyy-MM-dd HH:mm:ss}  |  may: {e.Hostname}  |  channel: {e.Channel}");
                Console.WriteLine($"      doi tuong : {e.ObjectName ?? "(khong co)"}{(e.DisplayName is null ? "" : $"   [hien thi: {e.DisplayName}]")}");
                Console.WriteLine($"      user      : {e.ActorAccount ?? "(khong co)"}   sid={e.ActorSid ?? "-"}");

                WriteIfAny("      binary    : ", e.ImagePath);
                WriteServiceLine(e);
                WriteTaskLine(e);

                if (!e.IsRecognized)
                {
                    Console.WriteLine($"      !! CHUA CO NHANH PARSE RIENG - field tho: {string.Join(", ", e.Data.Keys)}");
                }

                Console.WriteLine();
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"[LOI] {name}: {ex.Message}");
            }
        }

        PrintSummary(files.Length, failures, unrecognised, seenEventIds);
        return failures == 0 ? 0 : 1;
    }

    private static void WriteServiceLine(WindowsMonitorEvent e)
    {
        if (e.ServiceType is null && e.StartType is null && e.ServiceAccount is null)
        {
            return;
        }

        var startType = e.PreviousStartType is null
            ? e.StartType ?? "-"
            : $"{e.PreviousStartType} -> {e.StartType ?? "-"}";

        Console.WriteLine($"      service   : type={e.ServiceType ?? "-"}  startType={startType}  account={e.ServiceAccount ?? "-"}");
    }

    private static void WriteTaskLine(WindowsMonitorEvent e)
    {
        if (e.TaskContentXml is null)
        {
            return;
        }

        var action = e.TaskActionType switch
        {
            "Exec" => $"Exec: {e.TaskCommand} {e.TaskArguments ?? string.Empty}".TrimEnd(),
            "ComHandler" => $"ComHandler CLSID: {e.TaskComHandlerClassId ?? "-"}",
            null => "(khong xac dinh duoc kieu action)",
            _ => e.TaskActionType
        };

        Console.WriteLine($"      task      : {action}");
        Console.WriteLine($"                  runAs={e.TaskRunAsUser ?? "-"}  runLevel={e.TaskRunLevel ?? "-"}  (taskXml {e.TaskContentXml.Length} ky tu)");
    }

    private static void WriteIfAny(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine(label + value);
        }
    }

    private static void PrintSummary(
        int total, int failures, List<int> unrecognised, HashSet<int> seenEventIds)
    {
        Console.WriteLine("================ TONG KET ================");
        Console.WriteLine($"Tong file mau  : {total}");
        Console.WriteLine($"Parse loi      : {failures}");

        var distinctUnrecognised = unrecognised.Distinct().Order().ToArray();
        Console.WriteLine(distinctUnrecognised.Length == 0
            ? "Gap event la    : (khong co)"
            : $"Gap event la    : {string.Join(", ", distinctUnrecognised)}");

        Console.WriteLine();

        // Bao cao theo NANG LUC THAT của parser, KHONG suy ra tu mau da gap:
        // Event ID khong co mau nao van phai bi tinh la CHUA co nhanh parse.
        var recognized = WindowsEventParser.RecognizedEventIds;
        var notImplemented = MonitoredEventIds.All.Except(recognized).Order().ToArray();
        var noSample = MonitoredEventIds.All.Except(seenEventIds).Order().ToArray();

        Console.WriteLine($"Da co nhanh parse rieng ({recognized.Length}/{MonitoredEventIds.All.Length}): {string.Join(", ", recognized.Order())}");
        Console.WriteLine(notImplemented.Length == 0
            ? "Chua co nhanh parse : (khong co)"
            : $"Chua co nhanh parse : {string.Join(", ", notImplemented)}  <-- roi vao nhanh du phong");
        Console.WriteLine(noSample.Length == 0
            ? "Chua co mau that    : (khong co)"
            : $"Chua co mau that    : {string.Join(", ", noSample)}");
    }

    private static string ResolveDirectory(string[] args)
    {
        // Cho phep truyen duong dan: --parse-samples <duong-dan>
        var index = Array.IndexOf(args, "--parse-samples");
        if (index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return Path.GetFullPath(args[index + 1]);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "samples"));
    }
}

