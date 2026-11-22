using System.Text;
using System.Text.RegularExpressions;

namespace EasyMouse.Platform.Windows;

/// <summary>
/// Turns release file names ("[SubsPlease] Yuusha-kei ni Shosu - 09 (1080p) [AFD2CAE7].mkv",
/// "Show.Name.S02E05.1080p.WEB-DL.x264-GRP.mkv") into readable titles, and recognises junk SMTC titles
/// (magnet or tracker fragments, URLs, long hex strings). Pure string work: no I/O, bounded regexes with timeouts.
/// </summary>
internal static partial class ReleaseNameCleaner
{
    /// <summary>Longer input is cut before any regex runs, so hostile titles cost a bounded amount of work.</summary>
    public const int MaxInput = 1024;
    public const int MaxOutput = 200;

    private const int RegexTimeoutMs = 100;

    // Tags that are unambiguous enough to say "this is a release name": resolution, source, video codec.
    private const string HardTags =
        @"(?:480|540|576|720|1080|1440|2160|4320)[pi]|\d{3,4}x\d{3,4}|WEB[-. ]?DL|WEB[-. ]?Rip|Blu[-. ]?Ray|BD[-. ]?Rip|BR[-. ]?Rip|BD[-. ]?Remux|REMUX|HDTV|HDRip|DVD[-. ]?Rip|x26[45]|h[. ]?26[45]|HEVC|XviD|DivX";

    // Everything above plus audio and picture tags; used to find where the title ends.
    private const string CutTags =
        HardTags + @"|4K|UHD|AAC(?:[. ]?[257][. ][01])?|AC3|E-?AC-?3|DDP?[. ]?[257][. ][01]|DTS(?:-?HD)?(?:[-. ]?MA)?|TrueHD|Atmos|FLAC|HDR10\+?|HDR|DoVi|10[-. ]?bit|8[-. ]?bit|(?-i:PROPER|REPACK)";

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:" + HardTags + @")(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex HardTag();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:" + CutTags + @")(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex CutTag();

    [GeneratedRegex(@"\.(?:mkv|mp4|m4v|avi|mov|wmv|flv|webm|ts|m2ts|mpg|mpeg|ogv|mp3|flac|m4a|aac|ogg|opus|wav|wma|srt|ass)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex Extension();

    [GeneratedRegex(@"\s*\[[^\[\]]{0,80}\]\s*$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex TrailingSquare();

    [GeneratedRegex(@"^\s*\[[^\]]{1,60}\]\s*", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex LeadingGroup();

    [GeneratedRegex(@"\[([^\[\]]{0,80})\]|\(([^()]{0,80})\)", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"^[0-9a-fA-F]{6,40}$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex HexOnly();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex Spaces();

    /// <summary>True when the text reads like a release file name rather than a human title.</summary>
    public static bool LooksLikeReleaseName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = Prepare(text);
        try
        {
            if (Extension().IsMatch(s)) return true;
            var hard = HardTag().Count(s);
            if (hard >= 2) return true;
            if (hard == 1)
            {
                bool dotted = !s.Any(char.IsWhiteSpace) && s.Count(c => c is '.' or '_') >= 2;
                return dotted || LeadingGroup().IsMatch(s);
            }
            return false;
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>A readable title for a release name. Never throws; returns the sanitised input if nothing is left to show.</summary>
    public static string Clean(string? text)
    {
        var original = Prepare(text);
        if (original.Length == 0) return "";
        try
        {
            var s = Extension().Replace(original, "");

            var afterGroup = LeadingGroup().Replace(s, "", 1);
            if (afterGroup.Trim().Length > 0) s = afterGroup;

            s = Bracketed().Replace(s, m =>
            {
                var inner = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                return HexOnly().IsMatch(inner) || CutTag().IsMatch(inner) ? " " : m.Value;
            });

            // Scene style "Show.Name.S02E05.1080p..." uses dots (or underscores) as spaces; a normal title keeps its dots ("Mr. Robot").
            bool noSpaces = !s.Any(char.IsWhiteSpace);
            s = noSpaces && s.Count(c => c is '.' or '_') >= 2 ? s.Replace('.', ' ').Replace('_', ' ') : s.Replace('_', ' ');

            var cut = CutTag().Match(s);
            if (cut.Success && cut.Index > 0 && s[..cut.Index].Any(char.IsLetterOrDigit)) s = s[..cut.Index];

            for (var i = 0; i < 4 && TrailingSquare().IsMatch(s) && TrailingSquare().Replace(s, "").Any(char.IsLetterOrDigit); i++) s = TrailingSquare().Replace(s, ""); // [Dual-Audio], [Batch]...
            s = Spaces().Replace(s, " ").Trim(' ', '-', '–', '—', '.', '_').TrimStart(']', ')').TrimEnd('[', '(').Trim(' ', '-', '–', '—');
            if (s.Length == 0) s = original;
            return Truncate(s);
        }
        catch (RegexMatchTimeoutException)
        {
            return Truncate(original);
        }
    }

    /// <summary>Removes control and bidi-override characters (a title is untrusted text) and bounds the length.</summary>
    private static string Prepare(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var span = text.Length > MaxInput ? text.AsSpan(0, MaxInput) : text.AsSpan();
        var sb = new StringBuilder(span.Length);
        foreach (var ch in span)
        {
            if (char.IsControl(ch)) { sb.Append(' '); continue; }
            if (ch is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏') continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static string Truncate(string s)
    {
        if (s.Length <= MaxOutput) return s;
        var cut = s.AsSpan(0, MaxOutput - 1);
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1]; // never split a surrogate pair
        return cut.ToString().TrimEnd() + "…";
    }
}
