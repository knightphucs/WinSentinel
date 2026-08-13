using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TaskServiceMonitor.Management.Native;

/// <summary>
/// Khai báo P/Invoke tới <c>advapi32.dll</c> — đúng bộ hàm mà <c>services.msc</c> và
/// <c>sc.exe</c> dùng để nói chuyện với Service Control Manager.
///
/// File này CỐ Ý chỉ chứa khai báo, không chứa logic nghiệp vụ. Phần bọc an toàn nằm
/// ở <see cref="TaskServiceMonitor.Management.ServiceManager"/>.
/// </summary>
internal static class AdvApi32
{
    private const string Dll = "advapi32.dll";

    // ------------------------------------------------------------ Quyen truy cap

    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    internal const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_CHANGE_CONFIG = 0x0002;
    internal const uint SERVICE_QUERY_STATUS = 0x0004;
    internal const uint SERVICE_START = 0x0010;
    internal const uint SERVICE_STOP = 0x0020;
    internal const uint DELETE = 0x00010000;

    // ------------------------------------------------------------ Loai / trang thai

    internal const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    internal const uint SERVICE_WIN32 = 0x00000030;
    internal const uint SERVICE_STATE_ALL = 0x00000003;

    internal const uint SERVICE_BOOT_START = 0;
    internal const uint SERVICE_SYSTEM_START = 1;
    internal const uint SERVICE_AUTO_START = 2;
    internal const uint SERVICE_DEMAND_START = 3;
    internal const uint SERVICE_DISABLED = 4;

    internal const uint SERVICE_ERROR_NORMAL = 0x00000001;
    internal const uint SERVICE_CONTROL_STOP = 0x00000001;

    /// <summary>Giá trị báo "giữ nguyên tham số này" cho <c>ChangeServiceConfig</c>.</summary>
    internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_MORE_DATA = 234;

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
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

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeServiceHandle CreateServiceW(
        SafeServiceHandle hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        nint lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteService(SafeServiceHandle hService);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfigW(
        SafeServiceHandle hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        nint lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceW(
        SafeServiceHandle hService, uint dwNumServiceArgs, nint lpServiceArgVectors);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(
        SafeServiceHandle hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);
}

/// <summary>
/// Handle của SCM/service. Dùng SafeHandle để .NET tự đóng kể cả khi có exception —
/// quên <c>CloseServiceHandle</c> là rò handle, tích lại lâu ngày sẽ hết quota.
/// </summary>
internal sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() => AdvApi32.CloseServiceHandle(handle);
}
