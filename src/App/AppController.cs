using System.Net.NetworkInformation;
using EasyMouse.App.Logic;
using EasyMouse.App.Platform;
using EasyMouse.App.Updates;
using EasyMouse.Core;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Pairing;
using EasyMouse.Core.Sessions;
using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Execution;
using EasyMouse.Remotes.Layout;

namespace EasyMouse.App;

/// <summary>What the controller needs from the machine; tests and the screenshot harness replace these.</summary>
public sealed record AppEnvironment
{
    public Func<NetworkSnapshot> Probe { get; init; } = NetworkProbe.Collect;
    public ISecretProtector Protector { get; init; } = new DpapiSecretProtector();
    /// <summary>Protects the values of <c>${secret.name}</c> for remotes (its own DPAPI purpose, so it cannot unprotect device secrets).</summary>
    public ISecretProtector RemoteProtector { get; init; } = new DpapiSecretProtector("EasyMouse.RemoteSecrets.v1");
    /// <summary>Real autostart registry value, global hotkey and the elevated "make Private" command.</summary>
    public bool SystemIntegration { get; init; } = true;
    /// <summary>The DNS host name (what mDNS answers for), not the 15-character NetBIOS name.</summary>
    public string MachineName { get; init; } = System.Net.Dns.GetHostName();
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Update checks: tests pass a fake HTTP handler, installer launcher and version; null means the real ones.</summary>
    public HttpMessageHandler? UpdateHandler { get; init; }
    public IInstallerLauncher? UpdateLauncher { get; init; }
    public SemVer? CurrentVersion { get; init; }
    /// <summary>DEPRECATED (docs/remotes.md): follows <see cref="FeatureFlags.Remotes"/>; tests of the disabled remote engine turn it on.</summary>
    public bool RemotesEnabled { get; init; } = FeatureFlags.Remotes;
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
    private readonly FileSystemWatcher? _remotesWatcher;
    private readonly SemaphoreSlim _evalGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1); // one settings change at a time: no lost updates, no two writers of the file
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlySet<string> _connected = new HashSet<string>();
    private string _lastSignature = "";
    private readonly object _pairGate = new();
    private bool _pairingOpen, _qrWindowVisible, _qrRequested, _closeAfterPending;

    /// <summary>Throws <see cref="InvalidDataException"/> if the device file is unreadable (never starts with a silently empty list).</summary>
    public AppController(AppPaths paths, AppEnvironment? env = null)
    {
        _env = env ?? new AppEnvironment();
        Paths = paths;
        _settingsStore = new SettingsStore(paths.Settings);
        Settings = _settingsStore.Load();
        Loc.Instance.SetLanguage(Settings.Language);
        if (_env.SystemIntegration) AppLog.Initialize(Path.Combine(paths.Root, "easymouse.log"));

        var store = new FileDeviceStore(paths.Devices, _env.Protector);
        var (input, media) = PlatformFactory.Create(_env.SystemIntegration);

        // DEPRECATED: disabled for 0.1, scheduled for removal (docs/remotes.md). With FeatureFlags.Remotes off nothing is read from
        // the built-in or user remotes folders, no folder is created, the phone gets no layouts and every invoke is refused.
        // The catalog stays as an empty object (no folders) so the properties below keep their types.
        var remotesOn = _env.RemotesEnabled;
        if (remotesOn) Directory.CreateDirectory(paths.UserRemotes);
        RemoteApprovalStore = new FileRemoteApprovalStore(paths.Root);
        Secrets = new FileSecretStore(paths.Secrets, _env.RemoteProtector);
        Remotes = new RemoteCatalog(remotesOn ? paths.BuiltInRemotes : null, remotesOn ? paths.UserRemotes : null, RemoteApprovalStore, _env.Time);
        LayoutSource = new CatalogLayoutSource(Remotes);
        if (remotesOn)
        {
            var executors = PlatformFactory.CreateExecutors(input, media, _env.SystemIntegration, out var recorded);
            RecordedActions = recorded;
            var actions = new RemoteActionHandler(Remotes, executors, Secrets, time: _env.Time);
            Core = CoreServices.Create(store, input, media, actions, LayoutSource, time: _env.Time);
            _remotesWatcher = _env.SystemIntegration ? WatchUserRemotes(paths.UserRemotes) : null;
        }
        else
        {
            // NoRemoteActions: invoke -> {ok:false, err:"unsupported"}; NoRemoteLayouts: nothing is pushed.
            Core = CoreServices.Create(store, input, media, time: _env.Time);
        }
        Listener = new ListenerController(Core, _env.MachineName);
        Approvals = new ApprovalCoordinator();
        Help = new HelpAdvisor(_env.Time);
        Updates = new UpdateService(new GitHubHttp(_env.UpdateHandler), new UpdateVerifier(), _env.UpdateLauncher ?? new InstallerLauncher(),
            _env.SystemIntegration ? new FileUpdateCache(Path.Combine(paths.Root, "update-cache.json")) : new NullUpdateCache(),
            () => Settings, UpdateSettingsAsync, _env.CurrentVersion ?? AppVersion.Current, _env.Time);
        Updates.Changed += (_, _) => RaiseStateChanged();

        Approvals.Attach(Core.Pairing, Core.Hub);
        Listener.RequestReceived += Help.RequestSeen;
        Listener.Changed += (_, _) => RaiseStateChanged();
        Core.Pairing.Changed += (_, _) => { CloseIfPendingEnded(); RaiseStateChanged(); };
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
    public IRemoteCatalog Remotes { get; }
    public FileRemoteApprovalStore RemoteApprovalStore { get; }
    public FileSecretStore Secrets { get; }
    public CatalogLayoutSource LayoutSource { get; }
    /// <summary>The recorders standing in for the Windows executors when not running for real (tests); null in the real app.</summary>
    public RecordingActionExecutors? RecordedActions { get; }
    public ListenerController Listener { get; }
    public ApprovalCoordinator Approvals { get; }
    public HelpAdvisor Help { get; }
    public UpdateService Updates { get; }
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
            Updates.CleanOldDownloads();
            _ = Updates.RunSchedulerAsync(_cts.Token);
        }
        await RefreshNetworkAsync().ConfigureAwait(false);
        _ = Core.Devices.PurgeExpiredAsync();
    }

    /// <summary>A file dropped into (or edited in) the user's remotes folder shows up without a restart.</summary>
    private FileSystemWatcher WatchUserRemotes(string folder)
    {
        var watcher = new FileSystemWatcher(folder, "*.json") { EnableRaisingEvents = true, IncludeSubdirectories = false };
        FileSystemEventHandler changed = (_, _) => ScheduleRemotesReload();
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Renamed += (_, _) => ScheduleRemotesReload();
        return watcher;
    }

    private int _reloadPending;

    private void ScheduleRemotesReload()
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false); // an editor writes a file in several steps
            Interlocked.Exchange(ref _reloadPending, 0);
            Remotes.Reload();
        });
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _ = RefreshNetworkAsync();

    private async Task PollNetworkAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            // RefreshNetworkAsync handles its own failures: the polling (which notices Private -> Public) must never stop.
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await RefreshNetworkAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task RefreshNetworkAsync()
    {
        NetworkSnapshot snapshot;
        try { snapshot = await Task.Run(_env.Probe).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Windows could not tell us (adapter enumeration failed, ...). Not being able to see the network is not proof it is
            // Private, so stop listening until a later poll succeeds, and say why.
            AppLog.Error("network probe failed; not listening until it works again", ex);
            snapshot = NetworkSnapshot.Empty; // nothing known: nothing is served
        }
        try { await ApplyNetworkAsync(snapshot).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("applying the network state failed", ex);
        }
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

    /// <summary>True while pairing is accepted, i.e. while a one-time secret exists and the QR can be on screen.</summary>
    public bool PairingOpen { get { lock (_pairGate) return _pairingOpen; } }

    /// <summary>The QR window became visible or hidden. Pairing is open only while the QR is shown (see <see cref="SyncPairing"/>).</summary>
    public void SetQrWindowVisible(bool visible)
    {
        lock (_pairGate)
        {
            _qrWindowVisible = visible;
            if (!visible) _qrRequested = false; // reopening the window with a phone connected starts hidden again
            SyncPairingLocked();
        }
    }

    /// <summary>The user asked to see the QR although a phone is connected: opens pairing with a fresh secret.</summary>
    public void ShowQr()
    {
        lock (_pairGate) { _qrRequested = true; SyncPairingLocked(); }
    }

    /// <summary>Hides the QR again (a phone is connected): closes pairing and discards the secret.</summary>
    public void HideQr()
    {
        lock (_pairGate) { _qrRequested = false; SyncPairingLocked(); }
    }

    /// <summary>
    /// Pairing is accepted only while the QR is actually shown: the window is visible, no emergency stop is active, and
    /// either no phone is connected or the user asked for the QR. Hiding it closes the pairing window, which discards the
    /// one-time secret (and denies a pending request); showing it again creates a new secret. The one exception: a request that is
    /// already waiting for the user keeps the window open when only the "a phone is connected" condition turns false, and the window
    /// closes when that request is answered (see CloseIfPendingEnded).
    /// </summary>
    private void SyncPairingLocked()
    {
        // A request waiting for the user's answer keeps the window open: a phone that reconnects meanwhile must not deny it.
        // The close it postponed happens when the request ends (see the Pairing.Changed handler).
        bool waiting = _pairingOpen && Core.Pairing.State == PairingState.Pending;
        bool wanted = _qrWindowVisible && !EmergencyStopped && (ConnectedCount == 0 || _qrRequested);
        bool want = wanted || (waiting && _qrWindowVisible && !EmergencyStopped);
        _closeAfterPending = want && !wanted;
        if (want == _pairingOpen) return;
        _pairingOpen = want;
        if (want) { Help.WindowOpened(); Core.Pairing.OpenWindow(); }
        else { Help.WindowClosed(); Core.Pairing.CloseWindow(); }
    }

    private void SyncPairing() { lock (_pairGate) SyncPairingLocked(); }

    /// <summary>Closes the pairing window that was kept open for a waiting request, once that request has been answered.</summary>
    private void CloseIfPendingEnded()
    {
        lock (_pairGate)
        {
            if (_closeAfterPending && Core.Pairing.State != PairingState.Pending) SyncPairingLocked();
        }
    }

    /// <summary>Opens pairing directly (tests, and the window logic above).</summary>
    public void OpenPairing()
    {
        lock (_pairGate)
        {
            _pairingOpen = true;
            Help.WindowOpened();
            Core.Pairing.OpenWindow();
        }
    }

    public void ClosePairing()
    {
        lock (_pairGate)
        {
            _pairingOpen = false;
            Help.WindowClosed();
            Core.Pairing.CloseWindow();
        }
    }

    public void NewCode()
    {
        lock (_pairGate)
        {
            if (!_pairingOpen) return; // no QR on screen, so nothing to renew
            Help.WindowOpened();
            Core.Pairing.OpenWindow(); // a fresh secret and a restarted countdown
        }
    }
    // ------------------------------------------------------------------ actions

    /// <summary>Closes every phone socket and stops the listener until <see cref="ResumeAsync"/>.</summary>
    public async Task EmergencyStopAsync()
    {
        if (EmergencyStopped) return;
        EmergencyStopped = true;
        SyncPairing(); // EmergencyStopped is set: closes pairing and denies a pending request
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
        SyncPairing();
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
        AppSettings old, next;
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            old = Settings;
            next = change(old).Normalize();
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
            if (next.IncludePrereleases != old.IncludePrereleases) Updates.Reevaluate();
            if (!_settingsStore.Save(Settings)) AppLog.Error("settings could not be saved (kept for this session): " + _settingsStore.SaveError);
        }
        finally { _settingsGate.Release(); }

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
        lock (_pairGate)
        {
            // A phone just connected: hide the QR again, unless a pairing request is waiting for the user's answer.
            if (added.Count > 0 && Core.Pairing.State != PairingState.Pending) _qrRequested = false;
            SyncPairingLocked();
        }
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
        _remotesWatcher?.Dispose();
        LayoutSource.Dispose();
        _monitor?.Dispose();
        (Core.Input as IDisposable)?.Dispose();
        (Core.Media as IDisposable)?.Dispose();
        Approvals.DenyAll();
        await Listener.DisposeAsync().ConfigureAwait(false);
    }
}
