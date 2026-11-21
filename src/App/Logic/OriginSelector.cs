namespace EasyMouse.App.Logic;

/// <summary>Chooses the host part of the QR URL: <c>&lt;pcname&gt;.local</c> when the name is a valid mDNS label, else the IP.</summary>
public static class OriginSelector
{
    public static string? LocalName(string machineName)
    {
        var name = machineName.Trim().ToLowerInvariant();
        if (name.Length is 0 or > 63 || name[0] == '-' || name[^1] == '-') return null;
        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return null;
        return name + ".local";
    }

    /// <summary>The host to put in the QR: the .local name if usable, otherwise <paramref name="ip"/>.</summary>
    public static string PreferredHost(string machineName, string ip) => LocalName(machineName) ?? ip;

    /// <summary>Host header values the server accepts: the IP, and the .local name when valid.</summary>
    public static IReadOnlyList<string> AllowedHosts(string machineName, string ip)
        => LocalName(machineName) is { } local ? [ip, local] : [ip];

    public static string Origin(string host, int port) => $"http://{host}:{port}";

    public static string QrUrl(string host, int port, string fragment) => $"{Origin(host, port)}/#{fragment}";
}
