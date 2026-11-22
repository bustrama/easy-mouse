using System.Net;
using System.Net.NetworkInformation;
using EasyMouse.App.Logic;
using EasyMouse.Core.Devices;

namespace EasyMouse.App.Tests;

public class HotkeyTests
{
    [Fact]
    public void Default_hotkey_parses_and_round_trips()
    {
        Assert.True(AppSettings.DefaultHotkey.TryParseHotkey(out var hk));
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, hk.Modifiers);
        Assert.Equal((uint)'K', hk.VirtualKey);
        Assert.Equal("Ctrl+Alt+Shift+K", hk.ToString());
    }

    [Theory]
    [InlineData("ctrl + alt + f12", "Ctrl+Alt+F12")]
    [InlineData("Win+Esc", "Win+Esc")]
    [InlineData("shift+Ctrl+9", "Ctrl+Shift+9")]
    [InlineData("Alt+PageDown", "Alt+PageDown")]
    public void Parses_common_forms(string text, string canonical)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(canonical, hk.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("Shift+K")]            // would hijack ordinary typing
    [InlineData("Ctrl+Alt")]           // no key
    [InlineData("Ctrl+K+L")]           // two keys
    [InlineData("Ctrl+Ctrl+K")]        // repeated modifier
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl++K")]
    [InlineData("Ctrl+Banana")]
    public void Rejects_invalid(string text) => Assert.False(Hotkey.TryParse(text, out _));
}

public class CountdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(120, "2:00")]
    [InlineData(108, "1:48")]
    [InlineData(107.2, "1:48")] // rounds up: never 0:00 while valid
    [InlineData(59, "0:59")]
    [InlineData(0.2, "0:01")]
    [InlineData(0, "0:00")]
    public void Formats_remaining(double seconds, string expected)
        => Assert.Equal(expected, Countdown.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Remaining_never_goes_negative()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Countdown.Remaining(Now.AddSeconds(30), Now));
        Assert.Equal(TimeSpan.Zero, Countdown.Remaining(Now.AddSeconds(-5), Now));
    }
}

public class OriginTests
{
    [Theory]
    [InlineData("OFFICE-PC", "office-pc.local")]
    [InlineData("pc1", "pc1.local")]
    public void Valid_names_get_a_local_host(string machine, string expected)
        => Assert.Equal(expected, OriginSelector.LocalName(machine));

    [Theory]
    [InlineData("my_pc")]       // underscore is not valid in a DNS label
    [InlineData("-pc")]
    [InlineData("pc-")]
    [InlineData("מחשב")]
    [InlineData("")]
    public void Invalid_names_fall_back_to_the_ip(string machine)
    {
        Assert.Null(OriginSelector.LocalName(machine));
        Assert.Equal("192.168.1.20", OriginSelector.PreferredHost(machine, "192.168.1.20"));
        Assert.Equal(["192.168.1.20"], OriginSelector.AllowedHosts(machine, "192.168.1.20"));
    }

    [Fact]
    public void Qr_url_carries_the_secret_in_the_fragment_only()
    {
        Assert.Equal("http://office-pc.local:8765/#k=AbC", OriginSelector.QrUrl("office-pc.local", 8765, "k=AbC"));
        Assert.Equal("http://office-pc.local:8765", OriginSelector.Origin("office-pc.local", 8765));
        Assert.Equal(["192.168.1.20", "office-pc.local"], OriginSelector.AllowedHosts("office-pc", "192.168.1.20"));
    }
}

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(Path.Combine(_dir, "settings.json")).Load();
        Assert.Equal(8765, s.Port);
        Assert.Equal("Ctrl+Alt+Shift+K", s.Hotkey);
        Assert.True(s.StartMinimised);
        Assert.False(s.StartWithWindows);
    }

    [Fact]
    public void Round_trips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var saved = new AppSettings { Port = 9000, Language = "he", NetworkAdapterId = "{ABC}", Hotkey = "Ctrl+Alt+F9", StartWithWindows = true };
        store.Save(saved);
        Assert.Equal(saved, store.Load());
    }

    [Fact]
    public void Out_of_range_values_are_replaced_with_defaults()
    {
        var n = new AppSettings { Port = 80, Language = "fr", Hotkey = "K" }.Normalize();
        Assert.Equal(8765, n.Port);
        Assert.Equal("auto", n.Language);
        Assert.Equal("Ctrl+Alt+Shift+K", n.Hotkey);
    }

    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    [InlineData(8765, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void Port_range(int port, bool valid) => Assert.Equal(valid, AppSettings.IsValidPort(port));

    [Fact]
    public void A_corrupt_file_reports_the_error_and_uses_defaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new SettingsStore(path);
        Assert.Equal(new AppSettings(), store.Load());
        Assert.NotNull(store.LoadError);
    }
}

public class AutostartTests
{
    [Fact]
    public void Run_key_command_quotes_the_path_and_adds_the_flag()
        => Assert.Equal("\"C:\\Program Files\\Easy Mouse\\EasyMouse.exe\" --autostart", AutostartCommand.Build(@"C:\Program Files\Easy Mouse\EasyMouse.exe"));

    [Theory]
    [InlineData(true, true, false)]   // sign-in launch + start minimised: tray only
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]   // double-click: always show the window
    [InlineData(false, false, true)]
    public void Window_at_launch(bool autostart, bool minimised, bool shown)
        => Assert.Equal(shown, AutostartCommand.ShouldShowWindowAtLaunch(autostart, minimised));

    [Fact]
    public void Detects_the_flag() => Assert.True(AutostartCommand.IsAutostart(["--x", "--AUTOSTART"]));
}

public class HelpAdvisorTests
{
    private readonly ManualClock _clock = new();

    [Fact]
    public void Shows_help_only_after_the_quiet_period_with_no_lan_request()
    {
        var advisor = new HelpAdvisor(_clock, TimeSpan.FromSeconds(90));
        Assert.False(advisor.ShouldShowHelp); // window not open
        advisor.WindowOpened();
        _clock.Advance(TimeSpan.FromSeconds(89));
        Assert.False(advisor.ShouldShowHelp);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(advisor.ShouldShowHelp);
        advisor.WindowClosed();
        Assert.False(advisor.ShouldShowHelp);
    }

    [Fact]
    public void A_request_from_the_lan_silences_it_but_loopback_does_not()
    {
        var advisor = new HelpAdvisor(_clock, TimeSpan.FromSeconds(10));
        advisor.WindowOpened();
        advisor.RequestSeen(IPAddress.Loopback);
        advisor.RequestSeen(null);
        _clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(advisor.ShouldShowHelp);
        advisor.RequestSeen(IPAddress.Parse("192.168.1.50"));
        Assert.False(advisor.ShouldShowHelp);
    }

    [Fact]
    public void Reopening_the_window_restarts_the_clock()
    {
        var advisor = new HelpAdvisor(_clock, TimeSpan.FromSeconds(10));
        advisor.WindowOpened();
        _clock.Advance(TimeSpan.FromSeconds(11));
        advisor.WindowOpened();
        Assert.False(advisor.ShouldShowHelp);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

public class DeviceRowTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static Device Dev(int pairedDaysAgo, int seenDaysAgo) =>
        new("id", "Pixel 8", new byte[32], Now.AddDays(-pairedDaysAgo), Now.AddDays(-seenDaysAgo));

    [Fact]
    public void Recently_seen_phone_shows_days_to_expiry()
    {
        var row = DeviceRowInfo.From(Dev(30, 1), Now, DeviceManager.DefaultExpiry, connected: false);
        Assert.Equal(DeviceRowKind.Seen, row.Kind);
        Assert.Equal(1, row.LastSeenDaysAgo);
        Assert.Equal(89, row.DaysUntilExpiry);
        Assert.Equal(30, row.PairedDaysAgo);
    }

    [Fact]
    public void Phone_unused_for_80_days_expires_soon()
    {
        var row = DeviceRowInfo.From(Dev(100, 80), Now, DeviceManager.DefaultExpiry, connected: false);
        Assert.Equal(DeviceRowKind.ExpiresSoon, row.Kind);
        Assert.Equal(10, row.DaysUntilExpiry);
    }

    [Fact]
    public void Connected_wins_and_expiry_never_negative()
    {
        Assert.Equal(DeviceRowKind.Connected, DeviceRowInfo.From(Dev(5, 0), Now, DeviceManager.DefaultExpiry, connected: true).Kind);
        Assert.Equal(0, DeviceRowInfo.From(Dev(200, 200), Now, DeviceManager.DefaultExpiry, connected: false).DaysUntilExpiry);
    }
}

public class NetworkAnalyzerTests
{
    private static int _index;

    private static AdapterInfo Nic(string name, string ip, bool gateway = true, NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        string? description = null, bool up = true)
        => new(Guid.NewGuid().ToString("B").ToUpperInvariant(), name, description ?? name, type, IPAddress.Parse(ip), 24, gateway, ++_index, up);

    [Fact]
    public void Picks_the_card_with_a_default_gateway()
    {
        var offline = Nic("Ethernet 2", "10.0.0.5", gateway: false);
        var wifi = Nic("Wi-Fi", "192.168.1.20", type: NetworkInterfaceType.Wireless80211);
        Assert.Same(wifi, NetworkAnalyzer.Choose([offline, wifi], null));
    }

    [Fact]
    public void Skips_vpn_and_virtual_adapters()
    {
        var vpn = Nic("Tailscale", "100.64.0.3", type: NetworkInterfaceType.Tunnel, description: "Tailscale Tunnel");
        var wg = Nic("WireGuard", "10.9.0.2", description: "WireGuard Tunnel");
        var wsl = Nic("vEthernet (WSL)", "172.20.0.1", gateway: false, description: "Hyper-V Virtual Ethernet Adapter");
        var vbox = Nic("Ethernet 3", "192.168.56.1", gateway: false, description: "VirtualBox Host-Only Ethernet Adapter");
        var lan = Nic("Ethernet", "192.168.1.20");
        var all = new[] { vpn, wg, wsl, vbox, lan };
        Assert.Same(lan, NetworkAnalyzer.Choose(all, null));
        Assert.Equal([lan], NetworkAnalyzer.Candidates(all));
    }

    [Fact]
    public void A_hyper_v_external_switch_with_a_gateway_is_still_usable_when_nothing_else_is()
    {
        var external = Nic("vEthernet (Local LAN)", "192.168.1.9", gateway: true, description: "Hyper-V Virtual Ethernet Adapter #2");
        var nat = Nic("vEthernet (Default Switch)", "172.17.0.1", gateway: false, description: "Hyper-V Virtual Ethernet Adapter");
        Assert.Same(external, NetworkAnalyzer.Choose([nat, external], null));
        var real = Nic("Wi-Fi", "192.168.1.20", type: NetworkInterfaceType.Wireless80211);
        Assert.Same(real, NetworkAnalyzer.Choose([external, real], null)); // a real card is preferred
    }

    [Fact]
    public void Ignores_down_apipa_and_loopback()
    {
        var down = Nic("Ethernet", "192.168.1.5", up: false);
        var apipa = Nic("Ethernet 2", "169.254.10.4");
        Assert.Null(NetworkAnalyzer.Choose([down, apipa], null));
    }

    [Fact]
    public void Honors_the_users_choice_while_it_exists_else_falls_back()
    {
        var a = Nic("Ethernet", "192.168.1.20");
        var b = Nic("Wi-Fi", "192.168.1.21", gateway: false, type: NetworkInterfaceType.Wireless80211);
        Assert.Same(b, NetworkAnalyzer.Choose([a, b], b.Id));
        Assert.Same(a, NetworkAnalyzer.Choose([a, b], "{GONE}"));
    }

    [Fact]
    public void Hints_report_public_vpn_and_several_cards_plus_the_two_that_cannot_be_detected()
    {
        var eth = Nic("Ethernet", "192.168.1.20");
        var wifi = Nic("Wi-Fi", "192.168.1.21", type: NetworkInterfaceType.Wireless80211);
        var vpn = Nic("NordLynx", "10.5.0.2", description: "NordLynx Tunnel");
        var hints = NetworkAnalyzer.Hints([eth, wifi, vpn], eth, NetCategory.Public, includeGeneric: true);
        Assert.Equal([HintKind.PublicNetwork, HintKind.Vpn, HintKind.MultipleCards, HintKind.GuestWifi, HintKind.MobileData], hints.Select(h => h.Kind));
        Assert.All(hints.Take(3), h => Assert.True(h.Found));
        Assert.All(hints.Skip(3), h => Assert.False(h.Found));
        Assert.Equal("Ethernet", hints[2].Detail);
    }

    [Fact]
    public void No_network_is_reported()
        => Assert.Equal(HintKind.NoNetwork, NetworkAnalyzer.Hints([], null, NetCategory.Unknown, includeGeneric: false).Single().Kind);
}

public class ListenerPlanTests
{
    private static readonly AdapterInfo Lan = new("{1}", "Ethernet", "Ethernet", NetworkInterfaceType.Ethernet, IPAddress.Parse("192.168.1.20"), 24, true, 5);

    [Fact]
    public void Private_network_listens_on_that_address_only()
    {
        var plan = ListenerPlan.Decide(Lan, NetCategory.Private, emergencyStopped: false, 8765, "OFFICE-PC");
        Assert.True(plan.Listening);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), plan.Address);
        Assert.NotEqual(IPAddress.Any, plan.Address);
        Assert.Equal(8765, plan.Port);
        Assert.Equal(24, plan.PrefixLength);
        Assert.Equal(["192.168.1.20", "office-pc.local"], plan.AllowedHosts);
    }

    [Theory]
    [InlineData(NetCategory.Domain)]
    [InlineData(NetCategory.Unknown)] // could not read the profile: do not refuse to run
    public void Other_profiles_listen(NetCategory category)
        => Assert.True(ListenerPlan.Decide(Lan, category, false, 8765, "pc").Listening);

    [Fact]
    public void Public_stops_listening()
    {
        var plan = ListenerPlan.Decide(Lan, NetCategory.Public, false, 8765, "pc");
        Assert.False(plan.Listening);
        Assert.Equal(StopReason.PublicNetwork, plan.Reason);
        Assert.Null(plan.Address);
    }

    [Fact]
    public void Emergency_stop_beats_everything_and_no_network_is_a_reason()
    {
        Assert.Equal(StopReason.EmergencyStop, ListenerPlan.Decide(Lan, NetCategory.Private, true, 8765, "pc").Reason);
        Assert.Equal(StopReason.NoNetwork, ListenerPlan.Decide(null, NetCategory.Unknown, false, 8765, "pc").Reason);
    }

    [Fact]
    public void Same_target_ignores_nothing_that_matters()
    {
        var a = ListenerPlan.Decide(Lan, NetCategory.Private, false, 8765, "pc");
        Assert.True(a.SameTarget(ListenerPlan.Decide(Lan, NetCategory.Private, false, 8765, "pc")));
        Assert.False(a.SameTarget(ListenerPlan.Decide(Lan, NetCategory.Private, false, 9000, "pc")));
        Assert.False(a.SameTarget(ListenerPlan.Decide(Lan, NetCategory.Public, false, 8765, "pc")));
    }
}

public class QrMatrixTests
{
    [Fact]
    public void Encodes_a_square_matrix_with_finder_patterns()
    {
        var m = QrMatrix.Encode("http://office-pc.local:8765/#k=AAAAAAAAAAAAAAAAAAAAAA");
        int n = m.GetLength(0);
        Assert.Equal(n, m.GetLength(1));
        Assert.InRange(n, 21, 57);
        Assert.True(m[0, 0] && m[n - 1, 0] && m[0, n - 1]); // three finder-pattern corners
    }
}
