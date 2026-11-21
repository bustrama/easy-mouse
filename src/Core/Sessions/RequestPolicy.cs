using System.Net;
using System.Net.Sockets;

namespace EasyMouse.Core.Sessions;

/// <summary>
/// DNS-rebinding and cross-origin defence. The Host header must name the PC itself (its LAN IP or
/// <c>&lt;pcname&gt;.local</c>) on the port we listen on, and a browser <c>Origin</c> header, when present,
/// must be exactly <c>http://</c> plus such a host. A request without Origin (not sent by browsers for
/// same-origin GET or by non-browser clients) is not cross-origin and still needs the device secret.
/// IPv6 literals are not accepted (IPv6 is off in v1).
/// </summary>
public sealed class RequestPolicy
{
    private readonly HashSet<string> _hosts;
    private readonly IPNetworkRange? _peers;

    public RequestPolicy(IEnumerable<string> allowedHosts, IPAddress? interfaceAddress = null, int? peerPrefixLength = null)
    {
        _hosts = new HashSet<string>(allowedHosts.Select(h => h.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        if (interfaceAddress is not null && peerPrefixLength is int prefix) _peers = new IPNetworkRange(interfaceAddress, prefix);
    }

    /// <summary>The port the listener is bound to; set once the server has started.</summary>
    public int Port { get; set; }

    public bool IsHostAllowed(string? hostHeader) => TryParseAuthority(hostHeader, out var host, out var port) && Matches(host, port);

    /// <summary>True when no Origin header was sent or it names an allowed host.</summary>
    public bool IsOriginAllowed(string? originHeader)
    {
        if (originHeader is null) return true;
        const string prefix = "http://";
        if (!originHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var authority = originHeader[prefix.Length..];
        if (authority.Contains('/') || authority.Contains('@')) return false;
        return TryParseAuthority(authority, out var host, out var port) && Matches(host, port);
    }

    /// <summary>When a peer network was configured, only addresses in the interface's subnet are served.</summary>
    public bool IsPeerAllowed(IPAddress? remote)
    {
        if (_peers is null) return true;
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return _peers.Contains(remote);
    }

    private bool Matches(string host, int port) => port == Port && _hosts.Contains(host);

    private bool TryParseAuthority(string? authority, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrEmpty(authority) || authority.Length > 255) return false;
        if (authority.Contains('[') || authority.Contains(',') || authority.Contains(' ')) return false;

        var colon = authority.LastIndexOf(':');
        string portText;
        if (colon < 0) { host = authority; portText = ""; }
        else { host = authority[..colon]; portText = authority[(colon + 1)..]; }
        host = host.ToLowerInvariant();
        if (host.Length == 0 || host.Contains(':')) return false;

        if (portText.Length == 0)
        {
            if (colon >= 0) return false; // "host:" is malformed
            port = 80;
        }
        else if (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            return false;
        }
        return true;
    }

    private sealed class IPNetworkRange
    {
        private readonly byte[] _network;
        private readonly int _prefix;

        public IPNetworkRange(IPAddress address, int prefix)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("Only IPv4 is supported.", nameof(address));
            if (prefix is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefix));
            _network = address.GetAddressBytes();
            _prefix = prefix;
        }

        public bool Contains(IPAddress other)
        {
            if (other.AddressFamily != AddressFamily.InterNetwork) return false;
            var bytes = other.GetAddressBytes();
            int full = _prefix / 8;
            for (int i = 0; i < full; i++) if (bytes[i] != _network[i]) return false;
            int rest = _prefix % 8;
            if (rest == 0) return true;
            int mask = 0xFF << (8 - rest) & 0xFF;
            return (bytes[full] & mask) == (_network[full] & mask);
        }
    }
}
