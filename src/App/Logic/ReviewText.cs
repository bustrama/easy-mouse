using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Model;
using EasyMouse.Remotes.Security;

namespace EasyMouse.App.Logic;

/// <param name="Where">Which button, e.g. <c>Button "Open site"</c>.</param>
/// <param name="What">What it will do, in plain words.</param>
public sealed record ReviewLine(string Where, string What, bool Dangerous);

/// <summary>
/// Turns everything that made a remote need review into sentences a person can judge: sensitive and dangerous actions, and the key
/// presses that <see cref="ReviewBuilder"/> flags (a long chain, or a launcher chord). The wording comes from the remote's own
/// definition (never from what the phone says), and what is shown is what <c>RemoteCatalog.Approve</c> will pin to the file hash.
/// Text taken from the file is shown in full, with invisible and direction-changing characters made visible (see <see cref="Clean"/>).
/// </summary>
public static partial class ReviewText
{
    [GeneratedRegex(@"\$\{secret\.([a-z][a-z0-9_]{0,31})\}")] private static partial Regex SecretRef();

    public static IReadOnlyList<ReviewLine> For(RemoteEntry entry)
    {
        var lines = new List<ReviewLine>();
        if (entry.Definition is null) return lines;
        var target = entry.Definition.TargetProcess;
        foreach (var item in entry.Definition.Items)
        {
            var where = item.Label is { Length: > 0 } label ? Loc.Format("Rm_ItemWhere", Clean(label)) : Loc.Format("Rm_ItemUnnamed", item.Index + 1);
            var found = new List<ReviewLine>();

            var (kind, keys) = ReviewBuilder.KeyReview(item);
            if (kind == KeyReviewKind.Chain)
                found.Add(new(where, Loc.Format("Rm_Act_KeyChain", keys.Count, string.Join(", ", keys.Select(SpellKey))), false));
            else if (kind == KeyReviewKind.Launcher)
                found.Add(new(where, Loc.Format("Rm_Act_KeyLauncher", Clean(keys[0].Keys)), false));

            foreach (var action in item.Actions)
            {
                var cls = ActionClassifier.Classify(action);
                if (cls == ActionClass.Safe) continue;
                found.Add(new ReviewLine(where, Describe(action), cls == ActionClass.Dangerous));
            }

            // Where the keys and text go depends on the focus step, so it is shown whenever the item is reviewed at all.
            if (found.Count > 0 && target is not null && item.Actions.OfType<FocusAction>().Any())
                found.Insert(0, new ReviewLine(where, Loc.Format("Rm_Act_Focus", Clean(target)), false));
            lines.AddRange(found);
        }
        return lines;
    }

    private static string SpellKey(KeyPressAction k) => k.Repeat > 1 ? $"{Clean(k.Keys)} ×{k.Repeat}" : Clean(k.Keys);

    private static string Describe(RemoteAction action) => action switch
    {
        TextAction t => Loc.Format("Rm_Act_Text", t.Text.Length, Clean(t.Text)),
        OpenUrlAction u => Loc.Format("Rm_Act_OpenUrl", Clean(u.Url)),
        LaunchAction l => Loc.Format("Rm_Act_Launch", Clean(l.Slot))
            + (l.Args.Count == 0 ? "" : Loc.Format("Rm_Act_LaunchArgs", string.Join(" ", l.Args.Select(a => $"“{Clean(a)}”")))),
        HttpAction h => DescribeHttp(h),
        _ => Loc.Instance["Rm_Act_Other"],
    };

    private static string DescribeHttp(HttpAction h)
    {
        var target = Clean(HttpPolicy.Redact(h.Url));
        var host = HttpPolicy.HostOf(h.Url);
        if (host is not null && !HttpPolicy.IsLoopbackHost(host)) target += $" ({Loc.Instance["Rm_Act_HttpOutside"]})";
        var text = Loc.Format("Rm_Act_Http", h.Method, target);
        var secrets = SecretRef().Matches(h.Url + string.Concat(h.Headers.Values) + h.Body)
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        return secrets.Count == 0 ? text : text + ". " + Loc.Format("Rm_Act_Secrets", string.Join(", ", secrets));
    }

    /// <summary>
    /// Makes text from a remote file safe to read in a prompt: line breaks and tabs become visible symbols, and format characters
    /// (bidi overrides such as U+202E, zero-width marks), other control characters and line/paragraph separators become <c>⟨U+XXXX⟩</c>.
    /// Nothing is cut off.
    /// </summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { sb.Append('⏎'); i++; continue; }
            switch (c)
            {
                case '\n' or '\r': sb.Append('⏎'); continue;
                case '\t': sb.Append('⇥'); continue;
            }
            if (char.IsSurrogate(c)) { sb.Append(c); continue; }
            var cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                sb.Append("⟨U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('⟩');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
