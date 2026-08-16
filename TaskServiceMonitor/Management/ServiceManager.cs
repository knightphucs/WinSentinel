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
    bool IsWritable,

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
/// Đọc và thao tác Windows Service qua P/Invoke <c>advapi32.dll</c> — cùng bộ hàm mà
/// <c>services.msc</c> dùng.
///
/// Mọi thao tác GHI đều đi qua <see cref="SafeNameGuard"/> trước. Đọc thì không giới hạn.
/// </summary>
public sealed class ServiceManager(SafeNameGuard guard, InputPolicy policy, ILogger<ServiceManager> logger)
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
                    config.Account,
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
            guard.IsWritable(name),

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

    // ---------------------------------------------------------------- Thao tac ghi

    public void Create(
        string name, string binaryPath, string startType, string? displayName,
        string? description = null, IReadOnlyList<string>? dependencies = null,
        string? account = null)
    {
        guard.EnsureWritable(name);
        policy.EnsureValidName(name);

        // QUAN TRONG NHAT trong ca file: service tao ra chay LocalSystem (khi account
        // null), va start duoc ngay qua API. Khong chan o day thi mot o text tren
        // trinh duyet thanh thuc thi quyen SYSTEM.
        var resolvedPath = policy.EnsureAllowedExecutable(binaryPath, "binaryPath");

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            // Chan service ten 'WinSentinelX' nhung hien thi la 'Windows Update'.
            policy.EnsureValidName(displayName, "displayName");
        }

        var resolvedAccount = InputPolicy.EnsureAllowedServiceAccount(account);
        var dependencyBlock = policy.BuildDependencyMultiSz(dependencies);

        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        ThrowIfInvalid(scm, "Khong mo duoc Service Control Manager (can quyen Administrator).");

        using var service = CreateServiceW(
            scm, name, displayName ?? name,
            // Can them SERVICE_CHANGE_CONFIG de doi Description ngay sau khi tao.
            SERVICE_QUERY_STATUS | SERVICE_CHANGE_CONFIG,
            SERVICE_WIN32_OWN_PROCESS,
            ParseStartType(startType),
            SERVICE_ERROR_NORMAL,
            resolvedPath,
            null, nint.Zero,
            dependencyBlock,
            resolvedAccount,
            null); // Khong nhan mat khau - xem InputPolicy.AllowedServiceAccounts.

        ThrowIfInvalid(service, $"Khong tao duoc service '{name}'.");

        if (!string.IsNullOrWhiteSpace(description))
        {
            SetDescription(service, description);
        }

        logger.LogInformation(
            "Da tao service {Name} -> {Path} (account={Account}, {Deps} dependency)",
            name, resolvedPath, resolvedAccount ?? "LocalSystem", dependencies?.Count ?? 0);
    }

    /// <summary>
    /// Description KHÔNG đặt được qua <c>CreateServiceW</c> — phải gọi riêng
    /// <c>ChangeServiceConfig2</c> sau khi service đã tồn tại.
    /// </summary>
    private void SetDescription(SafeServiceHandle service, string description)
    {
        var text = nint.Zero;
        var info = nint.Zero;

        try
        {
            text = Marshal.StringToHGlobalUni(description);
            var payload = new SERVICE_DESCRIPTION { lpDescription = text };

            info = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_DESCRIPTION>());
            Marshal.StructureToPtr(payload, info, false);

            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, info))
            {
                // Service DA tao xong roi - mo ta chi la trang tri, khong dang de
                // huy ca thao tac.
                logger.LogWarning("Khong dat duoc mo ta cho service: {Error}",
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }
        finally
        {
            if (info != nint.Zero) Marshal.FreeHGlobal(info);
            if (text != nint.Zero) Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>Cho toi da bao lau de service tu dung han truoc khi xoa.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    public void Delete(string name)
    {
        guard.EnsureWritable(name);

        using var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        ThrowIfInvalid(scm, "Khong mo duoc Service Control Manager.");

        // Can them SERVICE_STOP + SERVICE_QUERY_STATUS: DeleteService tren mot
        // service dang chay chi "danh dau xoa", KHONG xoa ngay - service van con
        // trong EnumServicesStatusEx toi khi thuc su dung, khien dashboard tuong
        // nhu xoa khong thanh cong. Phai dung han truoc.
        using var service = OpenServiceW(scm, name,
            DELETE | SERVICE_STOP | SERVICE_QUERY_STATUS);
        ThrowIfInvalid(service, $"Khong mo duoc service '{name}'.");

        if (QueryStatus(service) != SERVICE_STOPPED)
        {
            var status = new SERVICE_STATUS();
            if (!ControlService(service, SERVICE_CONTROL_STOP, ref status))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_SERVICE_NOT_ACTIVE)
                {
                    throw new Win32Exception(error, $"Khong dung duoc service '{name}' truoc khi xoa.");
                }
            }

            var deadline = DateTime.UtcNow + StopTimeout;
            while (QueryStatus(service) != SERVICE_STOPPED && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
        }

        if (!DeleteService(service))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Khong xoa duoc service '{name}'.");
        }

        logger.LogInformation("Da xoa service {Name}", name);
    }

    /// <summary>Doc trang thai hien tai (dwCurrentState) qua QueryServiceStatusEx.</summary>
    private static uint QueryStatus(SafeServiceHandle service)
    {
        var size = (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, buffer, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Khong doc duoc trang thai service.");
            }

            return Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buffer).dwCurrentState;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
