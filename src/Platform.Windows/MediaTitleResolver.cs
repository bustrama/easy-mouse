using System.Text.RegularExpressions;

namespace EasyMouse.Platform.Windows;

/// <summary>
/// Makes the title the phone shows readable. Order: (1) Stremio puts a torrent infoHash in the SMTC title, so ask
/// Stremio's local server for the file name; (2) a release-style file name is cleaned; (3) clearly junk text (magnet or
/// tracker fragments, URLs, long hex) is replaced by the app name. <see cref="Resolve"/> never blocks: it answers from the
/// cache at once and starts a background lookup, calling <c>onResolved</c> when a better title is available.
/// </summary>
internal sealed partial class MediaTitleResolver
{
    private readonly StremioNameLookup? _stremio;
    private readonly Action _onResolved;

    public MediaTitleResolver(StremioNameLookup? stremio, Action onResolved)
    {
        _stremio = stremio;
        _onResolved = onResolved;
    }

    /// <summary>The display title and artist for one SMTC session. A null title means there is nothing sensible to show.</summary>
    public (string? Title, string? Artist) Resolve(string appId, string? title, string? artist)
    {
        var appName = AppNames.Friendly(appId);

        if (IsStremio(appId) && _stremio is not null)
        {
            var hash = InfoHashIn(title) ?? InfoHashIn(artist);
            if (hash is not null)
            {
                var (known, name) = _stremio.TryGetCached(hash);
                if (!known) _ = _stremio.BeginLookup(hash, _ => _onResolved());
                // While the lookup runs, or when Stremio has no such torrent, the app name is the sensible stand-in.
                return (name is null ? appName : ReleaseNameCleaner.Clean(name), null);
            }
        }

        return (Display(title, appName), IsJunk(artist) ? null : artist);
    }

    private static string? Display(string? title, string appName)
    {
        if (title is null) return null;
        if (IsJunk(title)) return appName;
        return ReleaseNameCleaner.LooksLikeReleaseName(title) ? ReleaseNameCleaner.Clean(title) : title;
    }

    private static bool IsStremio(string appId) => appId.Contains("stremio", StringComparison.OrdinalIgnoreCase);

    /// <summary>A 40-hex torrent infoHash inside the text (a magnet fragment, "dht:..." or a bare hash), lowercased.</summary>
    public static string? InfoHashIn(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > ReleaseNameCleaner.MaxInput) return null;
        try
        {
            var m = InfoHash().Match(text);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>True for text a person should never be shown as a title: magnet/tracker fragments, URLs, long hex strings.</summary>
    public static bool IsJunk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Length > ReleaseNameCleaner.MaxInput ? text[..ReleaseNameCleaner.MaxInput] : text;
        try { return JunkMarkers().IsMatch(s) || LongHex().IsMatch(s); }
        catch (RegexMatchTimeoutException) { return true; } // cannot tell in bounded time: do not show it
    }

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-fA-F]{40}(?![0-9A-Za-z])", RegexOptions.CultureInvariant, 100)]
    private static partial Regex InfoHash();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-fA-F]{32,}(?![0-9A-Za-z])", RegexOptions.CultureInvariant, 100)]
    private static partial Regex LongHex();

    [GeneratedRegex(@"magnet:|xt=urn:|[&?]tr=|[&?]dn=|\bdht:|\btracker:|\b(?:https?|ftp|file|udp|wss?)://|^\s*www\.\S+$|\bannounce(?:\?|&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex JunkMarkers();
}

/// <summary>A readable app name from an SMTC source id such as <c>stremio-shell-ng.exe</c> or <c>SpotifyAB.SpotifyMusic_x!Spotify</c>.</summary>
internal static class AppNames
{
    private static readonly (string Needle, string Name)[] s_known =
    [
        ("stremio", "Stremio"), ("spotify", "Spotify"), ("vlc", "VLC"), ("chrome", "Chrome"), ("msedge", "Edge"),
        ("firefox", "Firefox"), ("zunemusic", "Media Player"), ("music.ui", "Media Player"), ("netflix", "Netflix"),
    ];

    public static string Friendly(string appId)
    {
        foreach (var (needle, name) in s_known)
            if (appId.Contains(needle, StringComparison.OrdinalIgnoreCase)) return name;

        var s = appId;
        var bang = s.LastIndexOf('!');
        if (bang >= 0 && bang < s.Length - 1) s = s[(bang + 1)..];
        var slash = s.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        s = s.Replace('-', ' ').Replace('_', ' ').Trim();
        if (s.Length > 40) s = s[..40];
        return s.Length == 0 ? "Media" : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
