using System.Text;
using System.Text.Json;

namespace EasyMouse.App.Updates;

public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>A published release that carries every asset an update needs, already checked for shape and trusted URLs.</summary>
public sealed record ReleaseInfo(
    SemVer Version,
    string Tag,
    bool Prerelease,
    string Notes,
    DateTimeOffset? PublishedAt,
    ReleaseAsset Installer,
    ReleaseAsset Checksums,
    ReleaseAsset Signature);

/// <summary>
/// Turns the GitHub releases API response into a <see cref="ReleaseInfo"/>. The response is untrusted input: anything that
/// does not look exactly like one of our releases is skipped, and a body that is not JSON of the expected kind is an error.
/// </summary>
public static class ReleaseParser
{
    /// <summary>
    /// Accepts the <c>/releases/latest</c> object or a <c>/releases</c> array. Returns the highest usable release, or null when
    /// there is none (drafts, pre-releases when not wanted, and releases without the expected assets are ignored).
    /// </summary>
    /// <exception cref="UpdateException">The body is not valid JSON, or is neither an object nor an array.</exception>
    public static ReleaseInfo? SelectBest(byte[] json, bool includePrereleases)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorKind.Malformed, "The release list is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind switch
            {
                JsonValueKind.Object => [root],
                JsonValueKind.Array => root.EnumerateArray().Take(50),
                _ => throw new UpdateException(UpdateErrorKind.Malformed, "The release list has an unexpected shape."),
            };

            ReleaseInfo? best = null;
            foreach (var item in items)
            {
                var release = TryParseOne(item, includePrereleases);
                if (release is not null && (best is null || release.Version > best.Version)) best = release;
            }
            return best;
        }
    }

    private static ReleaseInfo? TryParseOne(JsonElement e, bool includePrereleases)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (Bool(e, "draft") != false) return null; // missing or not a boolean counts as "not trustworthy"
        var flaggedPre = Bool(e, "prerelease");
        if (flaggedPre is null) return null;

        var tag = Str(e, "tag_name");
        if (tag is null || tag.Length < 6 || tag[0] != 'v' || !SemVer.TryParse(tag[1..], out var version)) return null;
        if (tag != "v" + version) return null; // no build metadata, no odd spellings: the tag fully determines the asset names
        var prerelease = flaggedPre.Value || version.IsPrerelease;
        if (prerelease && !includePrereleases) return null;

        if (!e.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        ReleaseAsset? installer = null, sums = null, sig = null;
        foreach (var a in assets.EnumerateArray().Take(100))
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            var name = Str(a, "name");
            var url = Str(a, "browser_download_url");
            if (name is null || url is null || !a.TryGetProperty("size", out var sizeEl) || !sizeEl.TryGetInt64(out var size) || size <= 0) continue;
            if (name == UpdateTrust.InstallerName(version)) installer = Make(name, url, size, tag);
            else if (name == UpdateTrust.ChecksumsName) sums = Make(name, url, size, tag);
            else if (name == UpdateTrust.SignatureName) sig = Make(name, url, size, tag);
        }
        if (installer is null || sums is null || sig is null) return null;
        if (installer.Size > UpdateTrust.MaxInstallerBytes || sums.Size > UpdateTrust.MaxChecksumsBytes || sig.Size != UpdateTrust.SignatureBytes) return null;

        DateTimeOffset? published = e.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var dt) ? dt : null;
        return new ReleaseInfo(version, tag, prerelease, CleanNotes(Str(e, "body")), published, installer, sums, sig);
    }

    /// <summary>The URL must be exactly the canonical download address of this tag and file; anything else is dropped.</summary>
    private static ReleaseAsset? Make(string name, string url, long size, string tag)
    {
        if (url != UpdateTrust.AssetUrlPrefix + tag + "/" + name) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new ReleaseAsset(name, uri, size) : null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>Release notes are shown as plain text: bounded length, no control characters except line breaks and tabs.</summary>
    public static string CleanNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var sb = new StringBuilder(Math.Min(body.Length, UpdateTrust.MaxNotesChars + 1));
        foreach (var c in body)
        {
            if (sb.Length >= UpdateTrust.MaxNotesChars) { sb.Append('…'); break; }
            if (c == '\r') continue;
            if (c is '\n' or '\t' || !char.IsControl(c) && c is not ('‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩'))
                sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
