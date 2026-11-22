using System.Runtime.InteropServices;

namespace EasyMouse.Platform.Windows.Native;

/// <summary>Service Control Manager calls for the broker service: reading its state (any user) and switching it on or off (administrator).</summary>
internal static unsafe partial class Scm
{
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_STOP = 0x0020;
    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    public const uint SERVICE_AUTO_START = 2;
    public const uint SERVICE_DEMAND_START = 3;
    public const uint SERVICE_DISABLED = 4;
    public const uint SERVICE_CONTROL_STOP = 1;
    public const uint SERVICE_STOPPED = 1;
    public const uint SERVICE_START_PENDING = 2;
    public const uint SERVICE_STOP_PENDING = 3;
    public const uint SERVICE_RUNNING = 4;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QUERY_SERVICE_CONFIGW
    {
        public uint dwServiceType, dwStartType, dwErrorControl;
        public char* lpBinaryPathName, lpLoadOrderGroup;
        public uint dwTagId;
        public char* lpDependencies, lpServiceStartName, lpDisplayName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    public static partial nint OpenSCManager(char* machine, char* database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true)]
    public static partial nint OpenService(nint scm, [MarshalAs(UnmanagedType.LPWStr)] string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceConfig(nint service, byte* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(nint service, out SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl,
        char* binaryPath, char* loadOrderGroup, uint* tagId, char* dependencies, char* startName, char* password, char* displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartService(nint service, uint argc, char** argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ControlService(nint service, uint control, out SERVICE_STATUS status);
}
