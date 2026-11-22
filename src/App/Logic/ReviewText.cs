using System.Text.RegularExpressions;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Model;
using EasyMouse.Remotes.Security;

namespace EasyMouse.App.Logic;

/// <param name="Where">Which button, e.g. <c>Button "Open site"</c>.</param>
/// <param name="What">What it will do, in plain words.</param>
public sealed record ReviewLine(string Where, string What, bool Dangerous);

/// <summary>
/// Turns the sensitive and dangerous actions of a remote into sentences a person can judge. The wording comes from the remote's own
/// definition (never from what the phone says), and what is shown is what <c>RemoteCatalog.Approve</c> will pin to the file hash.
/// </summary>
public static partial class ReviewText
{
    [GeneratedRegex(@"\$\{secret\.([a-z][a-z0-9_]{0,31})\}")] private static partial Regex SecretRef();

    public static IReadOnlyList<ReviewLine> For(RemoteEntry entry)
    {
        var lines = new List<ReviewLine>();
        if (entry.Definition is null) return lines;
        foreach (var item in entry.Definition.Items)
        {
            var where = item.Label is { Length: > 0 } label ? Loc.Format("Rm_ItemWhere", label) : Loc.Format("Rm_ItemUnnamed", item.Index + 1);
            foreach (var action in item.Actions)
            {
                var cls = ActionClassifier.Classify(action);
                if (cls == ActionClass.Safe) continue;
                lines.Add(new ReviewLine(where, Describe(action), cls == ActionClass.Dangerous));
            }
        }
        return lines;
    }

    private static string Describe(RemoteAction action) => action switch
    {
        TextAction t => Loc.Format("Rm_Act_Text", t.Text.Length, Shorten(t.Text)),
        OpenUrlAction u => Loc.Format("Rm_Act_OpenUrl", u.Url),
        LaunchAction l => Loc.Format("Rm_Act_Launch", l.Slot)
            + (l.Args.Count == 0 ? "" : Loc.Format("Rm_Act_LaunchArgs", string.Join(" ", l.Args.Select(a => $"“{a}”")))),
        HttpAction h => DescribeHttp(h),
        _ => Loc.Instance["Rm_Act_Other"],
    };

    private static string DescribeHttp(HttpAction h)
    {
        var target = HttpPolicy.Redact(h.Url);
        var host = HttpPolicy.HostOf(h.Url);
        if (host is not null && !HttpPolicy.IsLoopbackHost(host)) target += $" ({Loc.Instance["Rm_Act_HttpOutside"]})";
        var text = Loc.Format("Rm_Act_Http", h.Method, target);
        var secrets = SecretRef().Matches(h.Url + string.Concat(h.Headers.Values) + h.Body)
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        return secrets.Count == 0 ? text : text + ". " + Loc.Format("Rm_Act_Secrets", string.Join(", ", secrets));
    }

    private static string Shorten(string text)
    {
        var flat = text.Replace("\r\n", " ⏎ ").Replace('\n', '⏎').Replace('\r', '⏎').Replace('\t', ' ');
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }
}
