using System.Net;
using System.Net.NetworkInformation;

namespace EasyMouse.App.Logic;

public enum NetCategory { Unknown, Public, Private, Domain }

/// <summary>One IPv4 network adapter, as collected by <see cref="NetworkProbe"/> (plain data so the rules are testable).</summary>
public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    IPAddress Address,
    int PrefixLength,
    bool HasGateway,
    int InterfaceIndex,
    bool IsUp = true);

/// <summary>A point-in-time view of the machine's networks.</summary>
public sealed record NetworkSnapshot(IReadOnlyList<AdapterInfo> Adapters, IReadOnlyDictionary<string, NetCategory> Categories)
{
    public static NetworkSnapshot Empty { get; } = new([], new Dictionary<string, NetCategory>());

    public NetCategory CategoryOf(AdapterInfo? adapter)
        => adapter is not null && Categories.TryGetValue(adapter.Id, out var c) ? c : NetCategory.Unknown;
}

public enum HintKind { PublicNetwork, GuestWifi, MobileData, Vpn, MultipleCards, NoNetwork }

/// <summary>A line of the "your phone cannot reach this PC" screen. <see cref="Found"/> = detected, otherwise "Possible".</summary>
public sealed record Hint(HintKind Kind, bool Found, string? Detail = null);

/// <summary>Rules for picking the LAN card and explaining network problems. No Win32 here.</summary>
public static class NetworkAnalyzer
{
    private static readonly string[] VpnWords =
    [
        "vpn", "wireguard", "tailscale", "zerotier", "openvpn", "tap-windows", "tap-win", "wintun", "nordlynx",
        "proton", "anyconnect", "fortinet", "globalprotect", "hamachi", "pulse secure", "mullvad", "cloudflare warp",
    ];

    private static readonly string[] VirtualWords =
    [
        "vethernet", "hyper-v", "virtualbox", "vmware", "virtual", "docker", "wsl", "loopback", "npcap",
        "bluetooth", "miniport", "teredo", "isatap", "6to4", "host-only", "vmnet",
    ];

    public static bool IsVpn(AdapterInfo a)
        => a.Type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
           || ContainsAny(a.Description, VpnWords) || ContainsAny(a.Name, VpnWords);

    public static bool IsVirtual(AdapterInfo a)
        => IsVpn(a) || ContainsAny(a.Description, VirtualWords) || ContainsAny(a.Name, VirtualWords);

    private static bool ContainsAny(string text, string[] words)
    {
        foreach (var w in words)
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsLanType(NetworkInterfaceType t)
        => t is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Wireless80211;

    private static bool IsUsableAddress(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        return !(b[0] == 169 && b[1] == 254) && !ip.Equals(IPAddress.Any); // APIPA means "no DHCP answer"
    }

    /// <summary>
    /// Wi-Fi/Ethernet cards that are up with a usable IPv4 address: what the picker offers. VPN and tunnel adapters are
    /// never offered. A virtual adapter (Hyper-V, VirtualBox, WSL) is offered only when it has a default gateway, which is
    /// how a Hyper-V external switch that owns the PC&apos;s only real connection looks; NAT-only switches have none.
    /// Real cards sort before virtual ones.
    /// </summary>
    public static IReadOnlyList<AdapterInfo> Candidates(IEnumerable<AdapterInfo> adapters)
        => [.. adapters.Where(a => a.IsUp && IsLanType(a.Type) && !IsVpn(a) && IsUsableAddress(a.Address) && (!IsVirtual(a) || a.HasGateway))
                       .OrderByDescending(a => a.HasGateway).ThenBy(IsVirtual).ThenBy(a => a.InterfaceIndex)];

    /// <summary>
    /// The card to listen on. Honors <paramref name="preferredId"/> when that card is still a candidate; otherwise
    /// the card with a default gateway (virtual and VPN cards skipped), falling back to any other candidate.
    /// </summary>
    public static AdapterInfo? Choose(IEnumerable<AdapterInfo> adapters, string? preferredId)
    {
        var candidates = Candidates(adapters);
        if (!string.IsNullOrEmpty(preferredId))
        {
            var preferred = candidates.FirstOrDefault(a => a.Id == preferredId);
            if (preferred is not null) return preferred;
        }
        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Everything we can say about why a phone might not connect, most useful first. Guest/isolated Wi-Fi and
    /// mobile data can never be detected from the PC, so those are always "possible".
    /// </summary>
    public static IReadOnlyList<Hint> Hints(IEnumerable<AdapterInfo> adapters, AdapterInfo? chosen, NetCategory category, bool includeGeneric)
    {
        var all = adapters.ToList();
        var hints = new List<Hint>();
        if (chosen is null) hints.Add(new Hint(HintKind.NoNetwork, Found: true));
        if (category == NetCategory.Public) hints.Add(new Hint(HintKind.PublicNetwork, Found: true));
        if (all.Any(a => a.IsUp && IsVpn(a) && IsUsableAddress(a.Address))) hints.Add(new Hint(HintKind.Vpn, Found: true));
        var candidates = Candidates(all);
        if (candidates.Count > 1 && chosen is not null) hints.Add(new Hint(HintKind.MultipleCards, Found: true, chosen.Name));
        if (includeGeneric)
        {
            hints.Add(new Hint(HintKind.GuestWifi, Found: false));
            hints.Add(new Hint(HintKind.MobileData, Found: false));
        }
        return hints;
    }
}
