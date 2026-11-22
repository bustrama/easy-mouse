using System.ComponentModel;
using System.Diagnostics;
using EasyMouse.Platform.Windows.Broker;

namespace EasyMouse.App.Platform;

public enum AdminSwitchResult { Done, Cancelled, NotInstalled, Failed }

/// <summary>
/// The "Control admin windows and the UAC prompt" capability as the App sees it. Its state is the state of the broker service
/// (on = set to start automatically and running), so it cannot be changed without Windows' own administrator prompt, by the
/// phone, or by a settings file.
/// </summary>
public interface IAdminControl
{
    BrokerServiceState State { get; }
    /// <summary>The user asked for the feature: the broker should be used when input would otherwise be blocked.</summary>
    bool Wanted { get; }
    /// <summary>Turns the broker service on or off through Windows' consent prompt. Never throws; says what happened.</summary>
    Task<AdminSwitchResult> SetEnabledAsync(bool enabled);
}

/// <summary>Used when nothing real is available (tests, the screenshot harness): the feature reads as not installed.</summary>
public sealed class NoAdminControl : IAdminControl
{
    public BrokerServiceState State => BrokerServiceState.NotInstalled;
    public bool Wanted => false;
    public Task<AdminSwitchResult> SetEnabledAsync(bool enabled) => Task.FromResult(AdminSwitchResult.NotInstalled);
}

/// <summary>
/// The real thing. Reading the state needs no rights. Changing it runs <c>EasyMouse.Broker.exe --enable|--disable</c>
/// with the "runas" verb, so Windows shows its own consent prompt for that exact file; declining it changes nothing.
/// </summary>
public sealed class BrokerAdminControl : IAdminControl
{
    private const int ErrorCancelled = 1223;
    private static readonly long CacheMilliseconds = 1000;
    private static readonly long ProbeMilliseconds = 2000;

    private readonly Func<BrokerServiceState> _query;
    private readonly Func<bool, Task<AdminSwitchResult>> _run;
    private readonly Func<long> _now;
    private readonly Func<BrokerFailure?> _probe;
    private Func<bool> _clientConnected = () => false;
    private BrokerServiceState _state;
    private long? _readAt;
    private BrokerFailure? _health;
    private long? _probedAt;

    public BrokerAdminControl() : this(BrokerService.Query, RunElevatedAsync, () => Environment.TickCount64, BrokerInputSink.Probe) { }

    internal BrokerAdminControl(Func<BrokerServiceState> query, Func<bool, Task<AdminSwitchResult>> run, Func<long> nowMilliseconds, Func<BrokerFailure?> probe)
    {
        _query = query;
        _run = run;
        _now = nowMilliseconds;
        _probe = probe;
    }

    /// <summary>The input client this App uses: while it holds the pipe the broker is plainly alive, and a probe would only get in its way.</summary>
    internal void UseClient(BrokerInputSink client) => _clientConnected = () => client.IsConnected;

    /// <summary>What the service manager says (cached for a second). Nothing here talks to the broker.</summary>
    private BrokerServiceState ServiceState
    {
        get
        {
            var now = _now();
            lock (this)
            {
                if (_readAt is not { } at || now - at >= CacheMilliseconds)
                {
                    _state = _query();
                    _readAt = now;
                }
                return _state;
            }
        }
    }

    /// <summary>
    /// What Settings shows. A service that is "running" but does not answer on its pipe, or answers from something that is not the broker
    /// (the agent exited, the pipe name was taken), is shown as <see cref="BrokerServiceState.Faulted"/>, never as On.
    /// </summary>
    public BrokerServiceState State
    {
        get
        {
            var state = ServiceState;
            if (state != BrokerServiceState.On) return state;
            if (_clientConnected()) return state;
            var now = _now();
            lock (this)
            {
                if (_probedAt is not { } at || now - at >= ProbeMilliseconds)
                {
                    _health = _probe();
                    _probedAt = now;
                }
                return _health is null ? state : BrokerServiceState.Faulted;
            }
        }
    }

    /// <summary>Whether the user asked for the feature. Cheap: the broker is used (and failures reported) per action; this never probes.</summary>
    public bool Wanted => ServiceState is BrokerServiceState.On or BrokerServiceState.Faulted;

    public async Task<AdminSwitchResult> SetEnabledAsync(bool enabled)
    {
        if (ServiceState == BrokerServiceState.NotInstalled) return AdminSwitchResult.NotInstalled;
        var result = await _run(enabled).ConfigureAwait(false);
        lock (this) { _readAt = null; _probedAt = null; } // look again at once
        return result;
    }

    private static Task<AdminSwitchResult> RunElevatedAsync(bool enable) => Task.Run(() =>
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "EasyMouse.Broker.exe");
        // Only a copy under Program Files may be started as administrator: anywhere else a normal user could replace the file.
        if (!File.Exists(exe) || !BrokerTrust.IsProtectedInstallPath(exe, [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]))
            return AdminSwitchResult.NotInstalled;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, enable ? "--enable" : "--disable") { UseShellExecute = true, Verb = "runas", CreateNoWindow = true });
            if (process is null) return AdminSwitchResult.Failed;
            process.WaitForExit();
            return process.ExitCode switch { 0 => AdminSwitchResult.Done, 4 => AdminSwitchResult.NotInstalled, _ => AdminSwitchResult.Failed };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return AdminSwitchResult.Cancelled; // the user said no at the Windows prompt
        }
        catch (Win32Exception)
        {
            return AdminSwitchResult.Failed;
        }
    });
}
