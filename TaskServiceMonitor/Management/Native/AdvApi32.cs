using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TaskServiceMonitor.Management.Native;

/// <summary>
/// Khai báo P/Invoke tới <c>advapi32.dll</c> — đúng bộ hàm ĐỌC mà <c>services.msc</c>
/// dùng để nói chuyện với Service Control Manager (liệt kê, đọc cấu hình/trạng thái).
///
/// Mentor xác nhận app chỉ cần MONITORING (đọc log + đối chiếu trạng thái hiện tại),
/// không cần tự thao tác Task/Service — nên các hàm GHI
/// (<c>CreateServiceW</c>/<c>DeleteService</c>/<c>ChangeServiceConfigW</c>/
/// <c>StartServiceW</c>/<c>ControlService</c>...) đã bị bỏ khỏi file này.
///
/// File này CỐ Ý chỉ chứa khai báo, không chứa logic nghiệp vụ. Phần bọc an toàn nằm
/// ở <see cref="TaskServiceMonitor.Management.ServiceManager"/>.
/// </summary>
internal static class AdvApi32
{
    private const string Dll = "advapi32.dll";

    // ------------------------------------------------------------ Quyen truy cap

    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_QUERY_STATUS = 0x0004;

    // ------------------------------------------------------------ Loai / trang thai

    internal const uint SERVICE_WIN32 = 0x00000030;
    internal const uint SERVICE_STATE_ALL = 0x00000003;

    internal const uint SERVICE_BOOT_START = 0;
    internal const uint SERVICE_SYSTEM_START = 1;
    internal const uint SERVICE_AUTO_START = 2;
    internal const uint SERVICE_DEMAND_START = 3;
    internal const uint SERVICE_DISABLED = 4;

    /// <summary>Level cho QueryServiceStatusEx: trả về SERVICE_STATUS_PROCESS.</summary>
    internal const int SC_STATUS_PROCESS_INFO = 0;

    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_MORE_DATA = 234;

    /// <summary>Level cho <c>QueryServiceConfig2</c> — mỗi level trả về một struct khác nhau.</summary>
    internal const int SERVICE_CONFIG_DESCRIPTION = 1;
    internal const int SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    internal const int SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    /// <summary>Bit trong <c>dwControlsAccepted</c> — service tự khai nó nhận lệnh gì.</summary>
    internal const uint SERVICE_ACCEPT_STOP = 0x00000001;
    internal const uint SERVICE_ACCEPT_PAUSE_CONTINUE = 0x00000002;

    /// <summary>SC_ACTION_TYPE cho recovery action.</summary>
    internal const uint SC_ACTION_NONE = 0;
    internal const uint SC_ACTION_RESTART = 1;
    internal const uint SC_ACTION_REBOOT = 2;
    internal const uint SC_ACTION_RUN_COMMAND = 3;

    // ------------------------------------------------------------ Struct

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    /// <summary>
    /// Hai truong dau la con tro toi chuoi nam TRONG cung buffer ma
    /// <c>EnumServicesStatusEx</c> tra ve, khong phai chuoi roi rac.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ENUM_SERVICE_STATUS_PROCESS
    {
        public nint lpServiceName;
        public nint lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public nint lpBinaryPathName;
        public nint lpLoadOrderGroup;
        public uint dwTagId;
        public nint lpDependencies;
        public nint lpServiceStartName;
        public nint lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_DESCRIPTION
    {
        public nint lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fDelayedAutostart;
    }

    /// <summary>
    /// <c>lpsaActions</c> trỏ tới MẢNG <see cref="SC_ACTION"/> dài <c>cActions</c> phần
    /// tử — phải tự duyệt bằng con trỏ, không marshal thẳng thành mảng được.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public nint lpRebootMsg;
        public nint lpCommand;
        public uint cActions;
        public nint lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SC_ACTION
    {
        public uint Type;
        public uint Delay;
    }

    // ------------------------------------------------------------ Ham

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeServiceHandle OpenSCManagerW(
        string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeServiceHandle OpenServiceW(
        SafeServiceHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(nint hSCObject);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusExW(
        SafeServiceHandle hSCManager,
        int InfoLevel,
        uint dwServiceType,
        uint dwServiceState,
        nint lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfigW(
        SafeServiceHandle hService, nint lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

    /// <summary>
    /// Cấu hình "mở rộng" — Description / DelayedAutoStart / FailureActions, mỗi thứ
    /// một <c>dwInfoLevel</c> riêng và trả về một struct khác nhau vào cùng buffer.
    /// Gọi hai lần như <c>QueryServiceConfigW</c>: lần đầu lấy kích thước.
    /// </summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig2W(
        SafeServiceHandle hService, int dwInfoLevel, nint lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(
        SafeServiceHandle hService, int InfoLevel, nint lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);
}

/// <summary>
/// Handle của SCM/service. Dùng SafeHandle để .NET tự đóng kể cả khi có exception —
/// quên <c>CloseServiceHandle</c> là rò handle, tích lại lâu ngày sẽ hết quota.
/// </summary>
internal sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() => AdvApi32.CloseServiceHandle(handle);
}
