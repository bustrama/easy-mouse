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
        try
        {
            fixed (char* name = BrokerProtocol.ServiceName)
                s_handle = Native.RegisterServiceCtrlHandlerEx(name, &Control, 0);
            if (s_handle == 0) return;
            Report(START_PENDING, waitHint: 5000);
            using var supervisor = new AgentSupervisor(Environment.ProcessPath!);
            Report(RUNNING);
            while (!s_stop.IsSet)
            {
                supervisor.Tick();
                s_wake.WaitOne(2000);
            }
            Report(STOP_PENDING, waitHint: 5000);
        }
        catch (Exception)
        {
            // A service that throws would take the process down without a word; stopping cleanly lets the manager's recovery decide.
        }
        finally
        {
            if (s_handle != 0) Report(STOPPED);
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
            case SERVICE_CONTROL_SESSIONCHANGE: // logon, logoff, console connect/disconnect: look again now
                s_wake.Set();
                return 0;
            case SERVICE_CONTROL_INTERROGATE:
                return 0;
            default:
                return ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    private static void Report(uint state, uint waitHint = 0)
    {
        s_state = state;
        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = state == RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_SESSIONCHANGE : 0,
            dwWaitHint = waitHint,
            dwCheckPoint = state is START_PENDING or STOP_PENDING ? 1u : 0,
        };
        Native.SetServiceStatus(s_handle, status);
    }
}
