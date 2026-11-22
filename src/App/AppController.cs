using System.Net.NetworkInformation;
using EasyMouse.App.Logic;
using EasyMouse.App.Platform;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Sessions;

namespace EasyMouse.App;

/// <summary>What the controller needs from the machine; tests and the screenshot harness replace these.</summary>
public sealed record AppEnvironment
{
    public Func<NetworkSnapshot> Probe { get; init; } = NetworkProbe.Collect;
    public ISecretProtector Protector { get; init; } = new DpapiSecretProtector();
    /// <summary>Real autostart registry value, global hotkey and the elevated "make Private" command.</summary>
    public bool SystemIntegration { get; init; } = true;
    /// <summary>The DNS host name (what mDNS answers for), not the 15-character NetBIOS name.</summary>
    public string MachineName { get; init; } = System.Net.Dns.GetHostName();
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public enum HotkeyResult { Ok, Invalid, InUse }

public enum ListenerStatusKind { Listening, Public, NoNetwork, Emergency, PortBusy, Error }

/// <summary>
/// The app's brain, without any window: owns the core services, the listener, settings and network state, and tells the
/// views when something changed (<see cref="StateChanged"/>, always on the UI thread).
/// </summary>
public sealed class AppController : IAsyncDisposable
{
    private readonly AppEnvironment _env;
    private readonly SettingsStore _settingsStore;
    private readonly HotkeyService? _hotkey;
    private readonly IDisposable? _monitor;
    private readonly SemaphoreSlim _evalGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlySet<string> _connected = new HashSet<string>();
    private string _lastSignature = "";

    /// <summary>Throws <see cref="InvalidDataException"/> if the device file is unreadable (never starts with a silently empty list).</summary>
    public AppController(AppPaths paths, AppEnvironment? env = null)
    {
        _env = env ?? new AppEnvironment();
        Paths = paths;
        _settingsStore = new SettingsStore(paths.Settings);
        Settings = _settingsStore.Load();
        Loc.Instance.SetLanguage(Settings.Language);

        var store = new FileDeviceStore(paths.Devices, _env.Protector);
        var (input, media) = PlatformFactory.Create(_env.SystemIntegration);
        Core = CoreServices.Create(store, input, media, time: _env.Time);
        Listener = new ListenerController(Core, _env.MachineName);
        Approvals = new ApprovalCoordinator();
        Help = new HelpAdvisor(_env.Time);

        Approvals.Attach(Core.Pairing, Core.Hub);
        Listener.RequestReceived += Help.RequestSeen;
        Listener.Changed += (_, _) => RaiseStateChanged();
        Core.Pairing.Changed += (_, _) => RaiseStateChanged();
        Core.Hub.Changed += OnHubChanged;
        Approvals.CurrentChanged += (_, _) => RaiseStateChanged();
        _monitor = PlatformFactory.StartHostStatusMonitor(Core.Status, _env.SystemIntegration);
        if (_env.SystemIntegration)
        {
            _hotkey = new HotkeyService();
            _hotkey.Pressed += (_, _) => UiThread.Post(() => _ = EmergencyStopAsync());
        }
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public CoreServices Core { get; }
    public ListenerController Listener { get; }
    public ApprovalCoordinator Approvals { get; }
    public HelpAdvisor Help { get; }
    public string MachineName => _env.MachineName;

    public NetworkSnapshot Network { get; private set; } = NetworkSnapshot.Empty;
    public AdapterInfo? ChosenAdapter { get; private set; }
    public NetCategory Category { get; private set; }
    public bool EmergencyStopped { get; private set; }
    public bool HotkeyRegistered => _hotkey?.Registered ?? false;
    public string? SettingsLoadError => _settingsStore.LoadError;

    /// <summary>Raised on the UI thread after anything the windows show may have changed.</summary>
    public event EventHandler? StateChanged;
    /// <summary>A known phone connected (device name). UI thread.</summary>
    public event Action<string>? DeviceConnected;
    /// <summary>The emergency stop ran. UI thread.</summary>
    public event EventHandler? EmergencyStopRan;

    // ------------------------------------------------------------------ status

    public ListenerStatusKind StatusKind => Listener.Error switch
    {
        ListenerError.PortInUse => ListenerStatusKind.PortBusy,
        ListenerError.Other => ListenerStatusKind.Error,
        _ => Listener.Plan?.Reason switch
        {
            StopReason.PublicNetwork => ListenerStatusKind.Public,
            StopReason.NoNetwork => ListenerStatusKind.NoNetwork,
            StopReason.EmergencyStop => ListenerStatusKind.Emergency,
            _ => ListenerStatusKind.Listening,
        },
    };

    public bool IsListening => Listener.Running;
    public int ConnectedCount => Core.Hub.ConnectedDeviceIds.Count;

    public string StatusText => StatusKind switch
    {
        ListenerStatusKind.Listening => Loc.Format("Net_Listening", Origin()),
        ListenerStatusKind.Public => Loc.Instance["Net_Public"],
        ListenerStatusKind.NoNetwork => Loc.Instance["Net_NoNetwork"],
        ListenerStatusKind.Emergency => Loc.Instance["Net_Emergency"],
        ListenerStatusKind.PortBusy => Loc.Format("Net_PortBusy", Settings.Port),
        _ => Loc.Format("Net_Error", Listener.ErrorDetail ?? ""),
    };

    /// <summary>The origin phones open, e.g. <c>http://192.168.1.20:8765</c>. Always the IP: a .local name often does not resolve on phones (and may resolve to IPv6 on the PC), while the IP always works.</summary>
    public string Origin()
    {
        var ep = Listener.EndPoint;
        if (ep is null) return "";
        return OriginSelector.Origin(ep.Address.ToString(), ep.Port);
    }

    /// <summary>The QR content, or null when nothing listens or no code is active.</summary>
    public string? QrUrl()
    {
        var ep = Listener.EndPoint;
        var secret = Core.Pairing.CurrentSecret;
        if (ep is null || secret is null) return null;
        return OriginSelector.QrUrl(ep.Address.ToString(), ep.Port, secret.Fragment);
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync()
    {
        if (_env.SystemIntegration)
        {
            ApplyHotkey(Settings.Hotkey);
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            _ = PollNetworkAsync(_cts.Token);
        }
        await RefreshNetworkAsync().ConfigureAwait(false);
        _ = Core.Devices.PurgeExpiredAsync();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _ = RefreshNetworkAsync();

    private async Task PollNetworkAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await RefreshNetworkAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task RefreshNetworkAsync()
    {
        var snapshot = await Task.Run(_env.Probe).ConfigureAwait(false);
        await ApplyNetworkAsync(snapshot).ConfigureAwait(false);
    }

    /// <summary>Decide where to listen for this snapshot and make the listener match.</summary>
    public async Task ApplyNetworkAsync(NetworkSnapshot snapshot)
    {
        await _evalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Network = snapshot;
            ChosenAdapter = NetworkAnalyzer.Choose(snapshot.Adapters, Settings.NetworkAdapterId);
            Category = snapshot.CategoryOf(ChosenAdapter);
            var plan = ListenerPlan.Decide(ChosenAdapter, Category, EmergencyStopped, Settings.Port, _env.MachineName);
            await Listener.ApplyAsync(plan).ConfigureAwait(false);
        }
        finally { _evalGate.Release(); }

        var signature = string.Join('|', snapshot.Adapters.Select(a => $"{a.Id}/{a.Address}/{snapshot.CategoryOf(a)}")) + $"#{Settings.Port}#{EmergencyStopped}";
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            RaiseStateChanged();
        }
    }

    // ------------------------------------------------------------------ pairing window

    public void OpenPairing()
    {
        Help.WindowOpened();
        Core.Pairing.OpenWindow();
    }

    public void ClosePairing()
    {
        Help.WindowClosed();
        Core.Pairing.CloseWindow();
    }

    public void NewCode()
    {
        Help.WindowOpened();
        Core.Pairing.OpenWindow(); // a fresh secret and a restarted countdown
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Closes every phone socket and stops the listener until <see cref="ResumeAsync"/>.</summary>
    public async Task EmergencyStopAsync()
    {
        if (EmergencyStopped) return;
        EmergencyStopped = true;
        Core.Pairing.CloseWindow();
        Approvals.DenyAll();
        (Core.Input as global::EasyMouse.Platform.Windows.SendInputSink)?.ReleaseAll(); // no key or button left held down
        await ApplyNetworkAsync(Network).ConfigureAwait(false);
        await Listener.DisconnectAllAsync().ConfigureAwait(false);
        UiThread.Post(() => EmergencyStopRan?.Invoke(this, EventArgs.Empty));
    }

    public async Task ResumeAsync()
    {
        if (!EmergencyStopped) return;
        EmergencyStopped = false;
        await RefreshNetworkAsync().ConfigureAwait(false);
        RaiseStateChanged();
    }

    public Task DisconnectAllAsync() => Listener.DisconnectAllAsync();

    public async Task RemoveDeviceAsync(string id)
    {
        await Core.Devices.RevokeAsync(id).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>Asks Windows (UAC, for this one command) to make the active network Private, then re-checks.</summary>
    public async Task<bool> MakeNetworkPrivateAsync()
    {
        var chosen = ChosenAdapter;
        if (chosen is null || chosen.InterfaceIndex <= 0 || !_env.SystemIntegration) return false;
        bool ok = await Task.Run(() => NetworkProfileFixer.MakePrivate(chosen.InterfaceIndex)).ConfigureAwait(false);
        await RefreshNetworkAsync().ConfigureAwait(false);
        return ok;
    }

    // ------------------------------------------------------------------ settings

    public async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        var old = Settings;
        var next = change(old).Normalize();
        if (next == old) return;
        Settings = next;

        if (next.StartWithWindows != old.StartWithWindows && _env.SystemIntegration)
        {
            try { Autostart.Set(next.StartWithWindows); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Settings = next with { StartWithWindows = Autostart.IsEnabled() }; // blocked by policy: show the truth
            }
        }
        if (next.Language != old.Language) Loc.Instance.SetLanguage(next.Language);
        _settingsStore.Save(Settings);

        if (next.Port != old.Port || next.NetworkAdapterId != old.NetworkAdapterId) await ApplyNetworkAsync(Network).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>Checks and applies a new emergency hotkey; the setting is saved only if it registered.</summary>
    public async Task<HotkeyResult> SetHotkeyAsync(string text)
    {
        if (!text.TryParseHotkey(out var hk)) return HotkeyResult.Invalid;
        if (_env.SystemIntegration && _hotkey is not null && !_hotkey.Register(hk))
        {
            ApplyHotkey(Settings.Hotkey); // put the old one back
            return HotkeyResult.InUse;
        }
        await UpdateSettingsAsync(s => s with { Hotkey = hk.ToString() }).ConfigureAwait(false);
        return HotkeyResult.Ok;
    }

    private void ApplyHotkey(string text)
    {
        if (_hotkey is not null && text.TryParseHotkey(out var hk)) _hotkey.Register(hk);
    }

    // ------------------------------------------------------------------ events

    private void OnHubChanged(object? sender, EventArgs e)
    {
        var now = Core.Hub.ConnectedDeviceIds.ToHashSet();
        var added = now.Except(_connected).ToList();
        _connected = now;
        foreach (var id in added)
        {
            var name = Core.Devices.Store.Get(id)?.Name;
            if (name is not null) UiThread.Post(() => DeviceConnected?.Invoke(name));
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => UiThread.Post(() => StateChanged?.Invoke(this, EventArgs.Empty));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _hotkey?.Dispose();
        _monitor?.Dispose();
        (Core.Input as IDisposable)?.Dispose();
        (Core.Media as IDisposable)?.Dispose();
        Approvals.DenyAll();
        await Listener.DisposeAsync().ConfigureAwait(false);
    }
}
