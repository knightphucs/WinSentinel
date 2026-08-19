using System.ComponentModel;
using System.Runtime.InteropServices;
using TaskServiceMonitor.Management.Native;
using static TaskServiceMonitor.Management.Native.AdvApi32;

namespace TaskServiceMonitor.Management;

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string State,
    string StartType,
    string? ImagePath,
    string? Account);

/// <summary>
/// Bản đầy đủ cho modal chi tiết — thêm nhóm field mà <c>services.msc</c> hiện ở tab
/// General/Recovery/Dependencies.
///
/// Chia làm hai loại chi phí rất khác nhau:
/// - <see cref="Dependencies"/>, <see cref="LoadOrderGroup"/>, <see cref="ErrorControl"/>,
///   <see cref="ProcessId"/>, <see cref="CanStop"/> vốn ĐÃ nằm trong struct mà
///   <c>List()</c> marshal rồi vứt đi — không tốn thêm lời gọi nào.
/// - <see cref="Description"/>, <see cref="DelayedAutoStart"/>,
///   <see cref="RecoveryActions"/> cần <c>QueryServiceConfig2</c>, mỗi thứ một lời gọi
///   riêng → chỉ đọc ở endpoint chi tiết, không nhét vào danh sách.
/// </summary>
public sealed record ServiceDetail(
    string Name,
    string DisplayName,
    string State,
    string StartType,
    string? ImagePath,
    string? Account,

    string? Description,
    string ServiceTypeText,
    string ErrorControl,
    string? LoadOrderGroup,
    IReadOnlyList<string> Dependencies,
    bool DelayedAutoStart,

    /// <summary>Null khi service đang dừng.</summary>
    uint? ProcessId,
    bool CanStop,
    bool CanPauseContinue,

    /// <summary>
    /// Hành động khi service chết. Đáng chú ý về bảo mật: malware đặt recovery để tự
    /// sống lại sau khi bị kill.
    /// </summary>
    IReadOnlyList<string> RecoveryActions);

/// <summary>
/// Đọc Windows Service qua P/Invoke <c>advapi32.dll</c> — cùng bộ hàm mà
/// <c>services.msc</c> dùng để liệt kê/xem cấu hình.
///
/// CHỈ ĐỌC: theo yêu cầu của mentor, app không tự thao tác (tạo/sửa/xoá/start/stop)
/// Task/Service nữa — chỉ giám sát log/event và đối chiếu trạng thái hiện tại.
/// </summary>
public sealed class ServiceManager(ILogger<ServiceManager> logger)
{
    public IReadOnlyList<ServiceInfo> List()
    {
        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Khong mo duoc Service Control Manager.");
        }

        var buffer = nint.Zero;
        try
        {
            // Goi lan dau voi buffer rong chi de Windows bao can bao nhieu byte.
            uint resume = 0;
            EnumServicesStatusExW(scm, 0, SERVICE_WIN32, SERVICE_STATE_ALL,
                nint.Zero, 0, out var needed, out _, ref resume, null);

            var lastError = Marshal.GetLastWin32Error();
            if (lastError is not (ERROR_MORE_DATA or ERROR_INSUFFICIENT_BUFFER))
            {
                throw new Win32Exception(lastError, "Khong liet ke duoc danh sach service.");
            }

            buffer = Marshal.AllocHGlobal((int)needed);
            resume = 0;

            if (!EnumServicesStatusExW(scm, 0, SERVICE_WIN32, SERVICE_STATE_ALL,
                    buffer, needed, out _, out var count, ref resume, null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Khong liet ke duoc danh sach service.");
            }

            var result = new List<ServiceInfo>((int)count);
            var entrySize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();

            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + i * entrySize);

                // Ten nam ngay trong cung buffer, doc theo con tro.
                var name = Marshal.PtrToStringUni(entry.lpServiceName) ?? "";
                var displayName = Marshal.PtrToStringUni(entry.lpDisplayName) ?? name;

                var config = TryReadConfig(scm, name);

                result.Add(new ServiceInfo(
                    name,
                    displayName,
                    DescribeState(entry.ServiceStatusProcess.dwCurrentState),
                    config.StartType,
                    config.ImagePath,
                    config.Account));
            }

            return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Toàn bộ nội dung <c>QUERY_SERVICE_CONFIG</c>. Trước đây hàm này marshal cả struct
    /// rồi chỉ trả 3 trong 9 field — bốn field dưới đây là "miễn phí", không tốn thêm
    /// lời gọi nào.
    /// </summary>
    private sealed record ServiceConfigData(
        string StartType,
        string? ImagePath,
        string? Account,
        string ServiceTypeText,
        string ErrorControl,
        string? LoadOrderGroup,
        IReadOnlyList<string> Dependencies)
    {
        public static ServiceConfigData Unknown { get; } =
            new("unknown", null, null, "unknown", "unknown", null, []);
    }

    /// <summary>
    /// EnumServicesStatusEx không trả ImagePath/StartType — phải hỏi riêng từng service
    /// bằng QueryServiceConfig. Service nào không đủ quyền đọc thì bỏ qua, không làm
    /// hỏng cả danh sách.
    /// </summary>
    private ServiceConfigData TryReadConfig(SafeServiceHandle scm, string name)
    {
        var buffer = nint.Zero;
        try
        {
            using var service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);
            if (service.IsInvalid)
            {
                return ServiceConfigData.Unknown;
            }

            QueryServiceConfigW(service, nint.Zero, 0, out var needed);
            buffer = Marshal.AllocHGlobal((int)needed);

            if (!QueryServiceConfigW(service, buffer, needed, out _))
            {
                return ServiceConfigData.Unknown;
            }

            var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer);
            return new ServiceConfigData(
                DescribeStartType(config.dwStartType),
                Marshal.PtrToStringUni(config.lpBinaryPathName),
                Marshal.PtrToStringUni(config.lpServiceStartName),
                DescribeServiceType(config.dwServiceType),
                DescribeErrorControl(config.dwErrorControl),
                Marshal.PtrToStringUni(config.lpLoadOrderGroup),
                ReadMultiSz(config.lpDependencies));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Khong doc duoc cau hinh service {Name}", name);
            return ServiceConfigData.Unknown;
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Chi tiết một service. Ngoài phần "miễn phí" ở <see cref="TryReadConfig"/>, gọi
    /// thêm 3 lần <c>QueryServiceConfig2</c> — vì vậy CHỈ dùng cho modal, không dùng
    /// trong <see cref="List"/>.
    /// </summary>
    public ServiceDetail Detail(string name)
    {
        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Khong mo duoc Service Control Manager.");
        }

        using var service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS);
        if (service.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Khong mo duoc service '{name}'.");
        }

        var config = TryReadConfig(scm, name);
        var status = QueryStatusProcess(service);

        return new ServiceDetail(
            name,
            TryReadDisplayName(scm, name) ?? name,
            status is { } s ? DescribeState(s.dwCurrentState) : "unknown",
            config.StartType,
            config.ImagePath,
            config.Account,

            TryReadDescription(service),
            config.ServiceTypeText,
            config.ErrorControl,
            config.LoadOrderGroup,
            config.Dependencies,
            TryReadDelayedAutoStart(service),

            // dwProcessId = 0 nghia la service khong chay, khong phai PID 0.
            status is { dwProcessId: > 0 } p ? p.dwProcessId : null,
            status is { } st && (st.dwControlsAccepted & SERVICE_ACCEPT_STOP) != 0,
            status is { } sp && (sp.dwControlsAccepted & SERVICE_ACCEPT_PAUSE_CONTINUE) != 0,

            TryReadRecoveryActions(service));
    }

    private string? TryReadDisplayName(SafeServiceHandle scm, string name)
    {
        var buffer = nint.Zero;
        try
        {
            using var service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);
            if (service.IsInvalid) return null;

            QueryServiceConfigW(service, nint.Zero, 0, out var needed);
            buffer = Marshal.AllocHGlobal((int)needed);
            if (!QueryServiceConfigW(service, buffer, needed, out _)) return null;

            return Marshal.PtrToStringUni(
                Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer).lpDisplayName);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static SERVICE_STATUS_PROCESS? QueryStatusProcess(SafeServiceHandle service)
    {
        var buffer = nint.Zero;
        try
        {
            QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, nint.Zero, 0, out var needed);
            buffer = Marshal.AllocHGlobal((int)needed);

            return QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, buffer, needed, out _)
                ? Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buffer)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Gọi <c>QueryServiceConfig2</c> hai lần (đo rồi đọc) rồi map buffer.</summary>
    private T? ReadConfig2<T>(SafeServiceHandle service, int level, Func<nint, T?> map, string what)
    {
        var buffer = nint.Zero;
        try
        {
            QueryServiceConfig2W(service, level, nint.Zero, 0, out var needed);
            if (needed == 0) return default;

            buffer = Marshal.AllocHGlobal((int)needed);
            return QueryServiceConfig2W(service, level, buffer, needed, out _)
                ? map(buffer)
                : default;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Khong doc duoc {What} cua service", what);
            return default;
        }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private string? TryReadDescription(SafeServiceHandle service) =>
        ReadConfig2(service, SERVICE_CONFIG_DESCRIPTION,
            ptr => Marshal.PtrToStringUni(Marshal.PtrToStructure<SERVICE_DESCRIPTION>(ptr).lpDescription),
            "description");

    private bool TryReadDelayedAutoStart(SafeServiceHandle service) =>
        ReadConfig2(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO,
            ptr => (bool?)Marshal.PtrToStructure<SERVICE_DELAYED_AUTO_START_INFO>(ptr).fDelayedAutostart,
            "delayed auto start") ?? false;

    private IReadOnlyList<string> TryReadRecoveryActions(SafeServiceHandle service) =>
        ReadConfig2(service, SERVICE_CONFIG_FAILURE_ACTIONS, ptr =>
        {
            var fa = Marshal.PtrToStructure<SERVICE_FAILURE_ACTIONS>(ptr);
            if (fa.cActions == 0 || fa.lpsaActions == nint.Zero)
            {
                return (IReadOnlyList<string>)[];
            }

            // lpsaActions tro toi MANG SC_ACTION - phai tu buoc con tro theo sizeof.
            var size = Marshal.SizeOf<SC_ACTION>();
            var actions = new List<string>((int)fa.cActions);

            for (var i = 0; i < fa.cActions; i++)
            {
                var action = Marshal.PtrToStructure<SC_ACTION>(fa.lpsaActions + i * size);
                actions.Add(DescribeRecoveryAction(action.Type, action.Delay));
            }

            return actions;
        }, "recovery actions") ?? [];

    // ---------------------------------------------------------------- Helper
    //
    // Trước đây có Create/SetDescription/Delete/Start/Stop/ChangeStartType ở đây,
    // gọi CreateServiceW/ChangeServiceConfig2W/DeleteService/StartServiceW/
    // ControlService/ChangeServiceConfigW của advapi32. Mentor xác nhận app chỉ cần
    // MONITORING (đọc log + đối chiếu trạng thái hiện tại), không cần tự thao tác
    // Service — đã gỡ bỏ cùng lớp rào SafeNameGuard/InputPolicy vốn chỉ tồn tại để
    // bảo vệ các thao tác ghi đó.

    /// <summary>
    /// Đọc chuỗi kiểu MULTI_SZ: nhiều chuỗi nối nhau, mỗi chuỗi kết thúc bằng một ký
    /// tự null, cả khối kết thúc bằng null thứ hai.
    ///
    /// BẪY: <c>Marshal.PtrToStringUni(ptr)</c> chỉ trả về chuỗi ĐẦU TIÊN rồi dừng ở
    /// null đầu tiên — dùng nó cho <c>lpDependencies</c> sẽ âm thầm mất hết dependency
    /// còn lại.
    /// </summary>
    internal static IReadOnlyList<string> ReadMultiSz(nint ptr)
    {
        if (ptr == nint.Zero)
        {
            return [];
        }

        var result = new List<string>();
        var offset = 0;

        while (true)
        {
            var item = Marshal.PtrToStringUni(ptr + offset);
            if (string.IsNullOrEmpty(item))
            {
                break; // Chuoi rong = null thu hai = het khoi.
            }

            result.Add(item);

            // +1 cho ky tu null ket thuc chuoi vua doc; moi ky tu UTF-16 la 2 byte.
            offset += (item.Length + 1) * 2;
        }

        return result;
    }

    internal static string DescribeServiceType(uint serviceType) => serviceType switch
    {
        0x1 => "kernel driver",
        0x2 => "file system driver",
        0x10 => "own process",
        0x20 => "shared process",
        0x110 => "own process (interactive)",
        0x120 => "shared process (interactive)",
        _ => $"0x{serviceType:X}"
    };

    internal static string DescribeErrorControl(uint errorControl) => errorControl switch
    {
        0 => "ignore",
        1 => "normal",
        2 => "severe",
        3 => "critical",
        _ => $"unknown ({errorControl})"
    };

    /// <summary>Một dòng recovery kiểu tab "Recovery" của <c>services.msc</c>.</summary>
    internal static string DescribeRecoveryAction(uint type, uint delayMs)
    {
        var delay = delayMs >= 1000
            ? $" sau {delayMs / 1000}s"
            : delayMs > 0 ? $" sau {delayMs}ms" : "";

        return type switch
        {
            SC_ACTION_NONE => "Không làm gì",
            SC_ACTION_RESTART => $"Khởi động lại service{delay}",
            SC_ACTION_REBOOT => $"Khởi động lại máy{delay}",
            SC_ACTION_RUN_COMMAND => $"Chạy lệnh{delay}",
            _ => $"Không rõ ({type})"
        };
    }

    private static string DescribeStartType(uint value) => value switch
    {
        SERVICE_BOOT_START => "boot start",
        SERVICE_SYSTEM_START => "system start",
        SERVICE_AUTO_START => "auto start",
        SERVICE_DEMAND_START => "demand start",
        SERVICE_DISABLED => "disabled",
        _ => "unknown"
    };

    private static string DescribeState(uint value) => value switch
    {
        1 => "stopped",
        2 => "start pending",
        3 => "stop pending",
        4 => "running",
        5 => "continue pending",
        6 => "pause pending",
        7 => "paused",
        _ => "unknown"
    };
}
