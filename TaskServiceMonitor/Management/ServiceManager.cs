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
    string? Account,
    bool IsWritable);

/// <summary>
/// Đọc và thao tác Windows Service qua P/Invoke <c>advapi32.dll</c> — cùng bộ hàm mà
/// <c>services.msc</c> dùng.
///
/// Mọi thao tác GHI đều đi qua <see cref="SafeNameGuard"/> trước. Đọc thì không giới hạn.
/// </summary>
public sealed class ServiceManager(SafeNameGuard guard, ILogger<ServiceManager> logger)
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

                var (startType, imagePath, account) = TryReadConfig(scm, name);

                result.Add(new ServiceInfo(
                    name,
                    displayName,
                    DescribeState(entry.ServiceStatusProcess.dwCurrentState),
                    startType,
                    imagePath,
                    account,
                    guard.IsWritable(name)));
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
    /// EnumServicesStatusEx không trả ImagePath/StartType — phải hỏi riêng từng service
    /// bằng QueryServiceConfig. Service nào không đủ quyền đọc thì bỏ qua, không làm
    /// hỏng cả danh sách.
    /// </summary>
    private (string StartType, string? ImagePath, string? Account) TryReadConfig(
        SafeServiceHandle scm, string name)
    {
        var buffer = nint.Zero;
        try
        {
            using var service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);
            if (service.IsInvalid)
            {
                return ("unknown", null, null);
            }

            QueryServiceConfigW(service, nint.Zero, 0, out var needed);
            buffer = Marshal.AllocHGlobal((int)needed);

            if (!QueryServiceConfigW(service, buffer, needed, out _))
            {
                return ("unknown", null, null);
            }

            var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer);
            return (
                DescribeStartType(config.dwStartType),
                Marshal.PtrToStringUni(config.lpBinaryPathName),
                Marshal.PtrToStringUni(config.lpServiceStartName));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Khong doc duoc cau hinh service {Name}", name);
            return ("unknown", null, null);
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // ---------------------------------------------------------------- Thao tac ghi

    public void Create(string name, string binaryPath, string startType, string? displayName)
    {
        guard.EnsureWritable(name);

        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        ThrowIfInvalid(scm, "Khong mo duoc Service Control Manager (can quyen Administrator).");

        using var service = CreateServiceW(
            scm, name, displayName ?? name,
            SERVICE_QUERY_STATUS,
            SERVICE_WIN32_OWN_PROCESS,
            ParseStartType(startType),
            SERVICE_ERROR_NORMAL,
            binaryPath,
            null, nint.Zero, null, null, null);

        ThrowIfInvalid(service, $"Khong tao duoc service '{name}'.");
        logger.LogInformation("Da tao service {Name} -> {Path}", name, binaryPath);
    }

    public void Delete(string name)
    {
        guard.EnsureWritable(name);

        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        ThrowIfInvalid(scm, "Khong mo duoc Service Control Manager.");

        using var service = OpenServiceW(scm, name, DELETE);
        ThrowIfInvalid(service, $"Khong mo duoc service '{name}'.");

        if (!DeleteService(service))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Khong xoa duoc service '{name}'.");
        }

        logger.LogInformation("Da xoa service {Name}", name);
    }

    public void Start(string name)
    {
        guard.EnsureWritable(name);

        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        ThrowIfInvalid(scm, "Khong mo duoc Service Control Manager.");

        using var service = OpenServiceW(scm, name, SERVICE_START);
        ThrowIfInvalid(service, $"Khong mo duoc service '{name}'.");

        if (!StartServiceW(service, 0, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Khong start duoc service '{name}'.");
        }
    }

    public void Stop(string name)
    {
        guard.EnsureWritable(name);

        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        ThrowIfInvalid(scm, "Khong mo duoc Service Control Manager.");

        using var service = OpenServiceW(scm, name, SERVICE_STOP);
        ThrowIfInvalid(service, $"Khong mo duoc service '{name}'.");

        var status = new SERVICE_STATUS();
        if (!ControlService(service, SERVICE_CONTROL_STOP, ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Khong stop duoc service '{name}'.");
        }
    }

    public void ChangeStartType(string name, string startType)
    {
        guard.EnsureWritable(name);

        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        ThrowIfInvalid(scm, "Khong mo duoc Service Control Manager.");

        using var service = OpenServiceW(scm, name, SERVICE_CHANGE_CONFIG);
        ThrowIfInvalid(service, $"Khong mo duoc service '{name}'.");

        // SERVICE_NO_CHANGE cho moi tham so khac = chi doi dung start type.
        if (!ChangeServiceConfigW(service, SERVICE_NO_CHANGE, ParseStartType(startType),
                SERVICE_NO_CHANGE, null, null, nint.Zero, null, null, null, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Khong doi duoc start type cua '{name}'.");
        }

        logger.LogInformation("Da doi start type cua {Name} thanh {StartType}", name, startType);
    }

    // ---------------------------------------------------------------- Helper

    private static void ThrowIfInvalid(SafeServiceHandle handle, string message)
    {
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), message);
        }
    }

    /// <summary>Dùng đúng bộ chữ mà event 7040/7045 dùng, để dashboard hiển thị thống nhất.</summary>
    private static string DescribeStartType(uint value) => value switch
    {
        SERVICE_BOOT_START => "boot start",
        SERVICE_SYSTEM_START => "system start",
        SERVICE_AUTO_START => "auto start",
        SERVICE_DEMAND_START => "demand start",
        SERVICE_DISABLED => "disabled",
        _ => "unknown"
    };

    private static uint ParseStartType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "boot start" or "boot" => SERVICE_BOOT_START,
        "system start" or "system" => SERVICE_SYSTEM_START,
        "auto start" or "auto" => SERVICE_AUTO_START,
        "demand start" or "demand" or "manual" => SERVICE_DEMAND_START,
        "disabled" => SERVICE_DISABLED,
        _ => throw new ArgumentException(
            $"Start type khong hop le: '{value}'. Dung mot trong: " +
            "boot start, system start, auto start, demand start, disabled.")
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
