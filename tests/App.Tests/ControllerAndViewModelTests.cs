using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EasyMouse.App.Logic;
using EasyMouse.App.Platform;
using EasyMouse.App.ViewModels;
using EasyMouse.Core.Devices;
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

    private async Task<AppController?> StartAsync(bool needsAdapter, AppSettings? settings = null)
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
        Assert.Equal($"http://office-pc.local:{app.Settings.Port}", vm.Origin);
        Assert.StartsWith($"http://office-pc.local:{app.Settings.Port}/#k=", app.QrUrl());
        Assert.Equal("Code is valid for 2:00", vm.CountdownText);
        Assert.Equal(QrStatusKind.Waiting, vm.StatusKind);

        _time.Advance(TimeSpan.FromSeconds(72));
        vm.Refresh();
        Assert.Equal("Code is valid for 0:48", vm.CountdownText);
        Assert.False(vm.IsExpired);

        _time.Advance(TimeSpan.FromSeconds(49));
        vm.Refresh();
        Assert.True(vm.IsExpired);
        Assert.True(vm.ShowExpiredOverlay);

        vm.NewCodeCommand.Execute(null);
        Assert.False(vm.IsExpired);
        Assert.Equal("Code is valid for 2:00", vm.CountdownText);
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

    [Fact]
    public async Task Admin_toggle_is_only_persisted()
    {
        var app = await StartAsync(needsAdapter: false);
        var settings = new SettingsViewModel(app!);
        settings.ControlAdmin = true;
        for (int i = 0; i < 100 && !app!.Settings.ControlAdminWindows; i++) await Task.Delay(10);
        Assert.True(new SettingsStore(Path.Combine(_dir, "settings.json")).Load().ControlAdminWindows);
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
