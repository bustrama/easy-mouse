using System.ComponentModel;
using System.Runtime.InteropServices;
using EasyMouse.Platform.Windows.Native;

namespace EasyMouse.Platform.Windows.Broker;

/// <summary>What the Service Control Manager says about the broker service.</summary>
public enum BrokerServiceState
{
    /// <summary>The service is not installed (portable copy, or the installer's step failed).</summary>
    NotInstalled,
    /// <summary>Installed and not set to start: the feature is off and nothing privileged is running.</summary>
    Off,
    /// <summary>Set to start automatically and running.</summary>
    On,
    /// <summary>Set to start automatically but not running (crashed, or still starting).</summary>
    Faulted,
    /// <summary>The state could not be read.</summary>
    Unknown,
}

/// <summary>
/// The on/off switch of the feature IS the service: "on" means start type Automatic and running, "off" means stopped and set to
/// manual. Reading it needs no rights; changing it needs an administrator, which is what makes enabling it a deliberate act on the PC.
/// </summary>
public static unsafe class BrokerService
{
    public static BrokerServiceState Query()
    {
        var scm = Scm.OpenSCManager(null, null, Scm.SC_MANAGER_CONNECT);
        if (scm == 0) return BrokerServiceState.Unknown;
        try
        {
            var service = Scm.OpenService(scm, BrokerProtocol.ServiceName, Scm.SERVICE_QUERY_CONFIG | Scm.SERVICE_QUERY_STATUS);
            if (service == 0)
                return Marshal.GetLastPInvokeError() == Scm.ERROR_SERVICE_DOES_NOT_EXIST ? BrokerServiceState.NotInstalled : BrokerServiceState.Unknown;
            try
            {
                if (!Scm.QueryServiceStatus(service, out var status)) return BrokerServiceState.Unknown;
                var start = StartType(service);
                if (start is null) return BrokerServiceState.Unknown;
                var running = status.dwCurrentState == Scm.SERVICE_RUNNING;
                if (start == Scm.SERVICE_AUTO_START) return running ? BrokerServiceState.On : BrokerServiceState.Faulted;
                return running || status.dwCurrentState == Scm.SERVICE_START_PENDING ? BrokerServiceState.Faulted : BrokerServiceState.Off;
            }
            finally { Scm.CloseServiceHandle(service); }
        }
        finally { Scm.CloseServiceHandle(scm); }
    }

    private static uint? StartType(nint service)
    {
        Scm.QueryServiceConfig(service, null, 0, out var needed);
        if (needed == 0 || needed > 8192) return null;
        var buffer = stackalloc byte[(int)needed];
        return Scm.QueryServiceConfig(service, buffer, needed, out _) ? ((Scm.QUERY_SERVICE_CONFIGW*)buffer)->dwStartType : null;
    }

    /// <summary>Administrator only. Start type Automatic, then start the service. Throws <see cref="Win32Exception"/> (5 = access denied).</summary>
    public static void Enable() => Change(startType: Scm.SERVICE_AUTO_START, start: true);

    /// <summary>Administrator only. Stop the service, then set it to manual so nothing privileged runs.</summary>
    public static void Disable() => Change(startType: Scm.SERVICE_DEMAND_START, start: false);

    private static void Change(uint startType, bool start)
    {
        var scm = Scm.OpenSCManager(null, null, Scm.SC_MANAGER_CONNECT);
        if (scm == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var service = Scm.OpenService(scm, BrokerProtocol.ServiceName, Scm.SERVICE_CHANGE_CONFIG | Scm.SERVICE_START | Scm.SERVICE_STOP | Scm.SERVICE_QUERY_STATUS);
            if (service == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (!start) Stop(service);
                if (!Scm.ChangeServiceConfig(service, Scm.SERVICE_NO_CHANGE, startType, Scm.SERVICE_NO_CHANGE, null, null, null, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (start && !Scm.StartService(service, 0, null) && Marshal.GetLastPInvokeError() != Scm.ERROR_SERVICE_ALREADY_RUNNING)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally { Scm.CloseServiceHandle(service); }
        }
        finally { Scm.CloseServiceHandle(scm); }
    }

    private static void Stop(nint service)
    {
        if (!Scm.ControlService(service, Scm.SERVICE_CONTROL_STOP, out var status))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == Scm.ERROR_SERVICE_NOT_ACTIVE) return;
            throw new Win32Exception(error);
        }
        var deadline = Environment.TickCount64 + 10_000;
        while (status.dwCurrentState != Scm.SERVICE_STOPPED && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(200);
            if (!Scm.QueryServiceStatus(service, out status)) break;
        }
    }
}
