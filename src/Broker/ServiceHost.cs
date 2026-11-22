using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EasyMouse.Platform.Windows.Broker;

namespace EasyMouse.Broker;

/// <summary>
/// The Windows service (SYSTEM, session 0). It cannot inject input into a user's desktop from there, so its only job is to keep one
/// <see cref="Agent"/> alive in the console session and to stop it when the service stops. It never touches the pipe or any input.
/// </summary>
internal static unsafe class ServiceHost
{
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const uint SERVICE_ACCEPT_STOP = 0x1, SERVICE_ACCEPT_SHUTDOWN = 0x4, SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
    private const uint SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_SHUTDOWN = 5, SERVICE_CONTROL_SESSIONCHANGE = 0x0E;
    private const uint START_PENDING = 2, STOP_PENDING = 3, RUNNING = 4, STOPPED = 1;
    private const uint ERROR_CALL_NOT_IMPLEMENTED = 120;

    private static nint s_handle;
    private static readonly ManualResetEventSlim s_stop = new(false);
    private static readonly AutoResetEvent s_wake = new(false);
    private static uint s_state = START_PENDING;
    private static volatile bool s_resetBackoff;
    private const uint ERROR_SERVICE_SPECIFIC_ERROR = 1066;

    public static int Run()
    {
        fixed (char* name = BrokerProtocol.ServiceName)
        {
            var table = stackalloc SERVICE_TABLE_ENTRYW[2];
            table[0] = new SERVICE_TABLE_ENTRYW { lpServiceName = name, lpServiceProc = &ServiceMain };
            table[1] = default;
            // Fails with 1063 when started from a console instead of the service manager.
            return Native.StartServiceCtrlDispatcher(table) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ServiceMain(uint argc, char** argv)
    {
        var failed = false;
        try
        {
            fixed (char* name = BrokerProtocol.ServiceName)
                s_handle = Native.RegisterServiceCtrlHandlerEx(name, &Control, 0);
            if (s_handle == 0) { failed = true; return; }
            Report(START_PENDING, waitHint: 5000);
            using var supervisor = new AgentSupervisor(Environment.ProcessPath!);
            Report(RUNNING);
            while (!s_stop.IsSet)
            {
                if (s_resetBackoff) { s_resetBackoff = false; supervisor.ResetBackoff(); }
                supervisor.Tick();
                s_wake.WaitOne(2000);
            }
            Report(STOP_PENDING, waitHint: 5000);
        }
        catch (Exception)
        {
            // Stop, but with a failure code: a service that ends on its own with "success" is not restarted by the recovery actions.
            failed = true;
        }
        finally
        {
            if (s_handle != 0) Report(STOPPED, exitCode: failed ? 1u : 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Control(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
            case SERVICE_CONTROL_SHUTDOWN:
                s_stop.Set();
                s_wake.Set();
                return 0;
            case SERVICE_CONTROL_SESSIONCHANGE: // logon, logoff, console connect/disconnect: look again now, and do not wait out an old backoff
                s_resetBackoff = true;
                s_wake.Set();
                return 0;
            case SERVICE_CONTROL_INTERROGATE:
                return 0;
            default:
                return ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    private static void Report(uint state, uint waitHint = 0, uint exitCode = 0)
    {
        s_state = state;
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = state == RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_SESSIONCHANGE : 0,
            dwWin32ExitCode = exitCode == 0 ? 0 : ERROR_SERVICE_SPECIFIC_ERROR,
            dwServiceSpecificExitCode = exitCode,
            dwWaitHint = waitHint,
            dwCheckPoint = state is START_PENDING or STOP_PENDING ? 1u : 0,
        };
        Native.SetServiceStatus(s_handle, status);
    }
}
