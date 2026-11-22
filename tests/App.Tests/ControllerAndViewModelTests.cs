using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EasyMouse.App.Logic;
using EasyMouse.App.Platform;
using EasyMouse.App.ViewModels;
using EasyMouse.Core.Devices;
using EasyMouse.Platform.Windows.Broker;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.App.Tests;

[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection;

/// <summary>Secret protection with the real DPAPI (current user), through the real file-backed device store.</summary>
public class DpapiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-dpapi-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Device_secrets_round_trip_and_are_not_stored_in_clear()
    {
        var path = Path.Combine(_dir, "devices.json");
        var secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var protector = new DpapiSecretProtector();

        var first = new FileDeviceStore(path, protector);
        first.Add(new Device("abc", "Dana's iPhone", secret, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(Convert.ToBase64String(secret), text);
        Assert.DoesNotContain("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=", text);

        var reloaded = new FileDeviceStore(path, protector).Get("abc");
        Assert.NotNull(reloaded);
        Assert.Equal(secret, reloaded!.Secret);
    }

    [Fact]
    public void A_secret_that_cannot_be_unprotected_throws_instead_of_starting_empty()
    {
        var path = Path.Combine(_dir, "devices.json");
        new FileDeviceStore(path, new DpapiSecretProtector())
            .Add(new Device("abc", "x", new byte[32], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        // A different entropy (another app, another user) cannot read it; the app shows an error and quits (App.OnStartup).
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new FileDeviceStore(path, new WrongEntropy()));
    }

    private sealed class WrongEntropy : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] protectedData)
            => System.Security.Cryptography.ProtectedData.Unprotect(protectedData, [1, 2, 3], System.Security.Cryptography.DataProtectionScope.CurrentUser);
    }
}

/// <summary>
/// <see cref="AppController"/> and the view models over it, with a fake network probe and clock. Where a test needs a
/// listener it binds a real address of this machine on a free port; without one the test is skipped (returns).
/// </summary>
[Collection("Loc")]
public class ControllerAndViewModelTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-ctl-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly List<AppController> _controllers = [];
    private NetCategory _category = NetCategory.Private;
    private AdapterInfo? _adapter;

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _controllers) await c.DisposeAsync();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static AdapterInfo? RealLocalAdapter()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var ua = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                && !a.Address.ToString().StartsWith("169.254", StringComparison.Ordinal));
            if (ua is not null)
                return new AdapterInfo("{TEST-LAN}", "Test LAN", "Test LAN", NetworkInterfaceType.Ethernet, ua.Address, ua.PrefixLength, true, 1);
        }
        return null;
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port is var p && p >= 1024 ? p : 20000;
    }

    private async Task<AppController?> StartAsync(bool needsAdapter, AppSettings? settings = null, IAdminControl? admin = null)
    {
        _adapter = RealLocalAdapter();
        if (_adapter is null && needsAdapter) return null;
        _adapter ??= new AdapterInfo("{TEST-LAN}", "Test LAN", "Test LAN", NetworkInterfaceType.Ethernet, IPAddress.Parse("192.0.2.10"), 24, true, 1);
        Directory.CreateDirectory(_dir);
        new SettingsStore(Path.Combine(_dir, "settings.json")).Save((settings ?? new AppSettings()) with { Language = "en", Port = FreePort() });
        var controller = new AppController(new AppPaths(_dir), new AppEnvironment
        {
            SystemIntegration = false,
            MachineName = "OFFICE-PC",
            Time = _time,
            Protector = new NoopProtector(),
            Admin = admin,
            Probe = () => new NetworkSnapshot([_adapter], new Dictionary<string, NetCategory> { [_adapter.Id] = _category }),
        });
        _controllers.Add(controller);
        await controller.StartAsync();
        return controller;
    }

    [Fact]
    public async Task A_public_network_stops_listening_shows_the_help_and_offers_to_fix_it()
    {
        _category = NetCategory.Public;
        var app = await StartAsync(needsAdapter: false);
        var vm = new QrViewModel(app!, () => { });

        Assert.False(app!.IsListening);
        Assert.Equal(ListenerStatusKind.Public, app.StatusKind);
        Assert.True(vm.ShowHelp);
        Assert.True(vm.CanMakePrivate);
        Assert.Null(vm.Matrix);
        Assert.Equal(QrStatusKind.Stopped, vm.StatusKind);
        Assert.Equal("This network is set to Public", vm.HelpItems[0].Title);
        Assert.True(vm.HelpItems[0].Found);
        Assert.Equal(["Guest or isolated Wi-Fi", "Phone is on mobile data"], vm.HelpItems.Skip(1).Select(i => i.Title));
        Assert.Contains("Public", app.StatusText);
    }

    [Fact]
    public async Task A_private_network_listens_and_shows_a_real_qr_for_the_local_name_and_a_countdown()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.OpenPairing();
        var vm = new QrViewModel(app, () => { });

        Assert.True(app.IsListening);
        Assert.NotNull(vm.Matrix);
        Assert.False(vm.ShowHelp);
        Assert.Equal($"http://{app.Listener.EndPoint!.Address}:{app.Settings.Port}", vm.Origin);
        Assert.StartsWith($"http://{app.Listener.EndPoint!.Address}:{app.Settings.Port}/#k=", app.QrUrl());
        Assert.Equal("Code is valid for 5:00", vm.CountdownText);
        Assert.Equal(QrStatusKind.Waiting, vm.StatusKind);

        _time.Advance(TimeSpan.FromSeconds(72));
        vm.Refresh();
        Assert.Equal("Code is valid for 3:48", vm.CountdownText);
        Assert.False(vm.IsExpired);

        _time.Advance(TimeSpan.FromSeconds(229));
        vm.Refresh();
        Assert.True(vm.IsExpired);
        Assert.True(vm.ShowExpiredOverlay);

        vm.NewCodeCommand.Execute(null);
        Assert.False(vm.IsExpired);
        Assert.Equal("Code is valid for 5:00", vm.CountdownText);
    }

    [Fact]
    public async Task Help_appears_after_the_quiet_period_but_not_while_a_phone_is_connected()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.OpenPairing();
        var vm = new QrViewModel(app, () => { });
        _time.Advance(TimeSpan.FromSeconds(80));
        vm.Refresh();
        Assert.False(vm.ShowHelp);
        _time.Advance(TimeSpan.FromSeconds(20));
        vm.Refresh();
        Assert.True(vm.ShowHelp);

        vm.ShowQrAgainCommand.Execute(null);
        Assert.False(vm.ShowHelp);
        _time.Advance(TimeSpan.FromSeconds(30));
        vm.Refresh();
        Assert.False(vm.ShowHelp); // the quiet-period clock restarted
    }

    [Fact]
    public async Task Emergency_stop_closes_everything_stays_stopped_and_resumes_on_request()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.OpenPairing();
        Assert.True(app.IsListening);

        await app.EmergencyStopAsync();
        Assert.False(app.IsListening);
        Assert.Equal(ListenerStatusKind.Emergency, app.StatusKind);
        Assert.Equal(EasyMouse.Core.Pairing.PairingState.Closed, app.Core.Pairing.State);

        await app.RefreshNetworkAsync(); // the 10-second network poll must not undo it
        Assert.False(app.IsListening);

        await app.ResumeAsync();
        Assert.True(app.IsListening);
        Assert.Equal(ListenerStatusKind.Listening, app.StatusKind);
    }

    // ------------------------------------------------------------------ QR visibility: pairing is open only while the QR is shown

    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static Uri WsOf(AppController app) => new($"ws://{app.Listener.EndPoint!.Address}:{app.Listener.EndPoint.Port}/ws");

    /// <summary>Pairs a fake phone through the real listener and leaves it connected.</summary>
    private async Task<TestClient> ConnectPhoneAsync(AppController app, string name)
    {
        await using var pairing = await TestClient.ConnectAsync(WsOf(app));
        var secret = app.Core.Pairing.CurrentSecret!.Secret;
        var paired = pairing.PairAsync(secret, name);
        await Eventually(() => app.Approvals.Current is not null);
        _time.Advance(TimeSpan.FromSeconds(2)); // the Allow button's one-second delay
        Assert.True(app.Approvals.Current!.Allow());
        var credentials = await paired;
        var phone = await TestClient.ConnectAsync(WsOf(app));
        await phone.AuthenticateAsync(credentials);
        await Eventually(() => app.ConnectedCount > 0);
        return phone;
    }

    [Fact]
    public async Task A_paired_phone_reconnecting_does_not_close_pairing_while_another_phones_request_waits()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);

        // Phone A pairs, then drops: nobody is connected again, so the QR is back.
        await using var pairingA = await TestClient.ConnectAsync(WsOf(app));
        var pairedA = pairingA.PairAsync(app.Core.Pairing.CurrentSecret!.Secret, "A");
        await Eventually(() => app.Approvals.Current is not null);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(app.Approvals.Current!.Allow());
        var credentialsA = await pairedA;
        var phoneA = await TestClient.ConnectAsync(WsOf(app));
        await phoneA.AuthenticateAsync(credentialsA);
        await Eventually(() => app.ConnectedCount > 0);
        await phoneA.DisposeAsync();
        await Eventually(() => app.ConnectedCount == 0 && app.PairingOpen);

        // Phone B scans the QR and waits for the user's answer.
        await using var pairingB = await TestClient.ConnectAsync(WsOf(app));
        var pairedB = pairingB.PairAsync(app.Core.Pairing.CurrentSecret!.Secret, "B");
        await Eventually(() => app.Approvals.Current is { IsDecided: false });
        var request = app.Approvals.Current!;

        // A reconnects meanwhile. That must not close pairing, which would deny B's waiting request.
        await using var phoneA2 = await TestClient.ConnectAsync(WsOf(app));
        await phoneA2.AuthenticateAsync(credentialsA);
        await Eventually(() => app.ConnectedCount > 0);
        Assert.True(app.PairingOpen);
        Assert.False(request.IsDecided);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(request.Allow());
        Assert.NotNull(await pairedB);

        // With the request answered the QR was not asked for and a phone is connected, so pairing closes.
        await Eventually(() => !app.PairingOpen);
    }

    [Fact]
    public async Task With_no_phone_connected_the_visible_window_shows_the_qr_and_accepts_pairing()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        var vm = new QrViewModel(app, () => { });

        Assert.True(app.PairingOpen);
        Assert.NotNull(app.Core.Pairing.CurrentSecret);
        Assert.NotNull(vm.Matrix);
        Assert.True(vm.ShowQr);
        Assert.False(vm.ShowConnected);
        Assert.False(vm.HasConnected);
    }

    [Fact]
    public async Task A_hidden_window_never_accepts_pairing()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);

        app.SetQrWindowVisible(true);
        app.SetQrWindowVisible(false);
        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);
        Assert.Equal(EasyMouse.Core.Pairing.PairingState.Closed, app.Core.Pairing.State);
    }

    [Fact]
    public async Task A_connected_phone_hides_the_qr_and_closes_pairing_and_the_old_secret_stops_working()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        var vm = new QrViewModel(app, () => { });
        var oldSecret = app.Core.Pairing.CurrentSecret!.Secret.ToArray();

        await using var phone = await ConnectPhoneAsync(app, "Dana's iPhone");
        vm.Refresh();

        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);
        Assert.Null(app.QrUrl());
        Assert.Equal(EasyMouse.Core.Pairing.PairingState.Closed, app.Core.Pairing.State);
        Assert.True(vm.ShowConnected);
        Assert.False(vm.ShowQr);
        Assert.True(vm.HasConnected);
        Assert.Null(vm.Matrix);

        // The code that was on screen no longer pairs anyone.
        await using var intruder = await TestClient.ConnectAsync(WsOf(app));
        await Assert.ThrowsAnyAsync<Exception>(() => intruder.PairAsync(oldSecret, "Intruder"));
        Assert.Null(app.Approvals.Current);
        Assert.Single(app.Core.Hub.ConnectedDeviceIds);
    }

    [Fact]
    public async Task Show_connection_qr_reopens_pairing_with_a_new_secret_and_hide_closes_it_again()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        var vm = new QrViewModel(app, () => { });
        var firstSecret = app.Core.Pairing.CurrentSecret!.Secret.ToArray();
        await using var phone = await ConnectPhoneAsync(app, "Dana's iPhone");
        vm.Refresh();
        Assert.True(vm.ShowConnected);

        vm.ShowConnectionQrCommand.Execute(null);
        Assert.True(app.PairingOpen);
        Assert.False(vm.ShowConnected);
        Assert.True(vm.ShowQr);
        Assert.NotNull(vm.Matrix);
        Assert.True(vm.HasConnected); // so the Hide QR button is offered
        Assert.NotEqual(firstSecret, app.Core.Pairing.CurrentSecret!.Secret);

        vm.HideQrCommand.Execute(null);
        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);
        Assert.True(vm.ShowConnected);
    }

    [Fact]
    public async Task Pairing_a_second_phone_from_the_requested_qr_hides_it_again_once_connected()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        await using var first = await ConnectPhoneAsync(app, "Phone A");
        app.ShowQr();
        Assert.True(app.PairingOpen);

        await using var second = await ConnectPhoneAsync(app, "Phone B");
        Assert.Equal(2, app.ConnectedCount);
        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);
    }

    [Fact]
    public async Task Closing_the_window_while_the_qr_was_requested_resets_to_hidden_on_the_next_open()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        await using var phone = await ConnectPhoneAsync(app, "Dana's iPhone");
        app.ShowQr();
        Assert.True(app.PairingOpen);

        app.SetQrWindowVisible(false);
        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);

        app.SetQrWindowVisible(true); // reopened from the tray with a phone still connected
        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);
    }

    [Fact]
    public async Task Emergency_stop_closes_pairing_and_resume_reopens_it_only_if_the_qr_is_due()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        Assert.True(app.PairingOpen);

        await app.EmergencyStopAsync();
        Assert.False(app.PairingOpen);
        Assert.Null(app.Core.Pairing.CurrentSecret);
        app.ShowQr(); // even an explicit request cannot open pairing during an emergency stop
        Assert.False(app.PairingOpen);

        await app.ResumeAsync();
        Assert.True(app.PairingOpen); // visible window, nobody connected
        Assert.NotNull(app.Core.Pairing.CurrentSecret);
    }

    [Fact]
    public async Task New_code_does_nothing_while_the_qr_is_hidden()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        await using var phone = await ConnectPhoneAsync(app, "Dana's iPhone");
        app.NewCode();
        Assert.Null(app.Core.Pairing.CurrentSecret);
        Assert.False(app.PairingOpen);
    }

    [Fact]
    public async Task Copy_link_copies_the_address_without_any_pairing_secret_and_confirms_briefly()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        app.SetQrWindowVisible(true);
        string? copied = null;
        var vm = new QrViewModel(app, () => { }) { CopyToClipboard = t => { copied = t; return true; } };
        Assert.Equal("Copy link", vm.CopyLinkText);

        vm.CopyLinkCommand.Execute(null);
        Assert.Equal($"http://{app.Listener.EndPoint!.Address}:{app.Settings.Port}", copied);
        Assert.DoesNotContain("#k=", copied);
        Assert.Equal("Link copied", vm.CopyLinkText);

        _time.Advance(TimeSpan.FromSeconds(3));
        vm.Refresh();
        Assert.Equal("Copy link", vm.CopyLinkText);
    }

    [Fact]
    public async Task Changing_the_port_in_settings_moves_the_listener_and_rejects_bad_ports()
    {
        var app = await StartAsync(needsAdapter: true);
        if (app is null) return;
        var settings = new SettingsViewModel(app);

        settings.PortText = "80";
        settings.ApplyPortCommand.Execute(null);
        Assert.True(settings.HasPortError);
        Assert.Equal(app.Settings.Port, app.Listener.EndPoint!.Port);

        var newPort = FreePort();
        settings.PortText = newPort.ToString();
        settings.ApplyPortCommand.Execute(null);
        for (int i = 0; i < 100 && app.Listener.EndPoint?.Port != newPort; i++) await Task.Delay(25);
        Assert.Equal(newPort, app.Listener.EndPoint!.Port);
        Assert.Equal(newPort, new SettingsStore(Path.Combine(_dir, "settings.json")).Load().Port);
        Assert.False(settings.HasPortError);
    }

    [Fact]
    public async Task Settings_lists_the_cards_and_rejects_a_hotkey_without_ctrl_alt_or_win()
    {
        var app = await StartAsync(needsAdapter: false);
        var settings = new SettingsViewModel(app!);

        Assert.StartsWith("Automatic (", settings.Adapters[0].Display);
        Assert.Contains(settings.Adapters, a => a.Id == "{TEST-LAN}");

        await settings.CaptureHotkeyAsync(HotkeyModifiers.Shift, 'K');
        Assert.True(settings.HasHotkeyMessage);
        Assert.Equal("Ctrl+Alt+Shift+K", app!.Settings.Hotkey);

        await settings.CaptureHotkeyAsync(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x78); // F9
        Assert.Equal("Ctrl+Alt+F9", app.Settings.Hotkey);
        Assert.False(settings.HasHotkeyMessage);
    }

    private sealed class FakeAdminControl(BrokerServiceState state) : IAdminControl
    {
        public BrokerServiceState State { get; set; } = state;
        public bool Wanted => State is BrokerServiceState.On or BrokerServiceState.Faulted;
        public AdminSwitchResult Result { get; set; } = AdminSwitchResult.Done;
        public List<bool> Requests { get; } = [];
        public Task<AdminSwitchResult> SetEnabledAsync(bool enabled)
        {
            Requests.Add(enabled);
            if (Result == AdminSwitchResult.Done) State = enabled ? BrokerServiceState.On : BrokerServiceState.Off;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Admin_setting_without_the_broker_installed_is_off_unavailable_and_says_why()
    {
        var app = await StartAsync(needsAdapter: false); // tests run without system integration: the feature reads as not installed
        var settings = new SettingsViewModel(app!);
        Assert.False(settings.AdminAvailable);
        Assert.False(settings.AdminChecked);
        Assert.True(settings.AdminStatusIsProblem);
        Assert.Contains("not installed", settings.AdminStatusText);
        Assert.DoesNotContain("Not available in this version", settings.AdminStatusText);
    }

    [Fact]
    public async Task Turning_admin_control_on_needs_the_warning_confirmed_on_the_pc_first()
    {
        var fake = new FakeAdminControl(BrokerServiceState.Off);
        var app = await StartAsync(needsAdapter: false, admin: fake);
        var declined = new SettingsViewModel(app!, confirmAdmin: () => false);
        Assert.True(declined.AdminAvailable);
        Assert.False(declined.AdminChecked);

        await declined.SetAdminAsync(true);
        Assert.Empty(fake.Requests); // no warning accepted: Windows is not even asked
        Assert.False(declined.AdminChecked);

        var accepted = new SettingsViewModel(app!, confirmAdmin: () => true);
        await accepted.SetAdminAsync(true);
        Assert.Equal([true], fake.Requests);
        Assert.True(accepted.AdminChecked);
        Assert.Contains("any program running as you", accepted.AdminStatusText);
    }

    [Fact]
    public async Task Turning_it_off_needs_no_warning()
    {
        var fake = new FakeAdminControl(BrokerServiceState.On);
        var app = await StartAsync(needsAdapter: false, admin: fake);
        var asked = false;
        var settings = new SettingsViewModel(app!, confirmAdmin: () => { asked = true; return false; });
        Assert.True(settings.AdminChecked);
        await settings.SetAdminAsync(false);
        Assert.False(asked);
        Assert.Equal([false], fake.Requests);
        Assert.False(settings.AdminChecked);
    }

    [Fact]
    public async Task Declining_the_windows_prompt_changes_nothing_and_says_so()
    {
        var fake = new FakeAdminControl(BrokerServiceState.Off) { Result = AdminSwitchResult.Cancelled };
        var app = await StartAsync(needsAdapter: false, admin: fake);
        var settings = new SettingsViewModel(app!);
        await settings.SetAdminAsync(true);
        Assert.False(settings.AdminChecked);
        Assert.True(settings.HasAdminMessage);
        Assert.Equal(Loc.Instance["Admin_Cancelled"], settings.AdminMessage);
    }

    [Fact]
    public async Task A_broker_that_is_on_but_not_running_is_shown_as_a_problem_not_as_working()
    {
        var fake = new FakeAdminControl(BrokerServiceState.Faulted);
        var app = await StartAsync(needsAdapter: false, admin: fake);
        var settings = new SettingsViewModel(app!);
        Assert.True(settings.AdminChecked);
        Assert.True(settings.AdminStatusIsProblem);
        Assert.Equal(Loc.Instance["Admin_Faulted"], settings.AdminStatusText);
    }

    [Fact]
    public async Task Every_admin_string_exists_in_hebrew_and_english()
    {
        var app = await StartAsync(needsAdapter: false);
        foreach (var language in new[] { "en", "he" })
        {
            await app!.UpdateSettingsAsync(s => s with { Language = language });
            foreach (var key in new[] { "Set_AdminDesc", "Admin_ConfirmBody", "Admin_NotInstalled", "Admin_Off", "Admin_On", "Admin_Faulted", "Admin_Unknown", "Admin_Cancelled", "Admin_Failed" })
            {
                var text = Loc.Instance[key];
                Assert.False(text.StartsWith('['), $"{key} is missing in {language}");
                Assert.DoesNotContain("Not available in this version", text);
            }
        }
        Assert.Contains("UAC", Loc.Instance["Admin_ConfirmBody"]);
    }

    [Fact]
    public async Task The_broker_switch_reads_its_state_from_the_service_and_never_from_a_settings_file()
    {
        var states = new Queue<BrokerServiceState>([BrokerServiceState.Off, BrokerServiceState.On]);
        long now = 0;
        var runs = new List<bool>();
        var control = new BrokerAdminControl(() => states.Count > 1 ? states.Dequeue() : states.Peek(), e => { runs.Add(e); return Task.FromResult(AdminSwitchResult.Done); }, () => now, () => null);

        Assert.Equal(BrokerServiceState.Off, control.State);
        Assert.Equal(BrokerServiceState.Off, control.State); // cached for a second
        Assert.False(control.Wanted);
        Assert.Equal(AdminSwitchResult.Done, await control.SetEnabledAsync(true));
        Assert.Equal([true], runs);
        Assert.Equal(BrokerServiceState.On, control.State); // re-read right after a change
        Assert.True(control.Wanted);
    }


    [Fact]
    public void A_running_service_whose_pipe_does_not_answer_is_shown_as_faulted_not_on()
    {
        long now = 0;
        BrokerFailure? probe = BrokerFailure.NotRunning;
        var control = new BrokerAdminControl(() => BrokerServiceState.On, _ => Task.FromResult(AdminSwitchResult.Done), () => now, () => probe, a => a());
        Assert.Equal(BrokerServiceState.Faulted, control.State);
        Assert.True(control.Wanted); // the user still wants it: input is tried, and fails visibly, rather than silently using the normal path
        probe = null;
        now += 3000;
        Assert.Equal(BrokerServiceState.On, control.State);
        probe = BrokerFailure.Untrusted; // a pipe that something else owns
        now += 3000;
        Assert.Equal(BrokerServiceState.Faulted, control.State);
    }

    [Fact]
    public void A_service_that_is_off_is_never_probed()
    {
        var probes = 0;
        var control = new BrokerAdminControl(() => BrokerServiceState.Off, _ => Task.FromResult(AdminSwitchResult.Done), () => 0, () => { probes++; return null; }, a => a());
        Assert.Equal(BrokerServiceState.Off, control.State);
        Assert.Equal(0, probes);
    }


    [Fact]
    public async Task Nothing_is_run_elevated_when_the_service_is_not_installed()
    {
        var runs = 0;
        var control = new BrokerAdminControl(() => BrokerServiceState.NotInstalled, _ => { runs++; return Task.FromResult(AdminSwitchResult.Done); }, () => 0, () => null);
        Assert.Equal(AdminSwitchResult.NotInstalled, await control.SetEnabledAsync(true));
        Assert.Equal(0, runs);
        Assert.False(control.Wanted);
    }

    [Fact]
    public async Task Language_switch_flips_to_right_to_left_and_back()
    {
        var app = await StartAsync(needsAdapter: false);
        try
        {
            await app!.UpdateSettingsAsync(s => s with { Language = "he" });
            Assert.Equal(System.Windows.FlowDirection.RightToLeft, Loc.Instance.FlowDirection);
            Assert.Equal("סרקו כדי לחבר את הטלפון", Loc.Instance["Qr_Title"]);
        }
        finally
        {
            await app!.UpdateSettingsAsync(s => s with { Language = "en" });
        }
        Assert.Equal(System.Windows.FlowDirection.LeftToRight, Loc.Instance.FlowDirection);
        Assert.Equal("Scan to connect your phone", Loc.Instance["Qr_Title"]);
    }

    [Fact]
    public void Every_english_string_has_a_hebrew_one()
    {
        var resx = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "App", "Resources");
        if (!Directory.Exists(resx)) return; // run from an unusual output folder
        static HashSet<string> Keys(string file) => [.. System.Xml.Linq.XDocument.Load(file).Root!.Elements("data").Select(e => (string)e.Attribute("name")!)];
        Assert.Equal(Keys(Path.Combine(resx, "Strings.resx")).Order(), Keys(Path.Combine(resx, "Strings.he.resx")).Order());
    }

    private sealed class NoopProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
