using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Remotes.Security;

public enum ActionClass { Safe, Sensitive, Dangerous }

/// <summary>docs/security.md, "Command safety": the one place that decides how risky an action is.</summary>
public static class ActionClassifier
{
    public static ActionClass Classify(RemoteAction action) => action switch
    {
        KeyPressAction or MediaAction or MouseAction or SmtcAction or VolumeAction or FocusAction or DelayAction => ActionClass.Safe,
        TextAction or OpenUrlAction => ActionClass.Sensitive,
        LaunchAction or HttpAction => ActionClass.Dangerous,
        _ => ActionClass.Dangerous, // an action kind added without being classified fails closed
    };

    public static ActionClass Highest(RemoteDefinition remote)
        => remote.Items.SelectMany(i => i.Actions).Select(Classify).DefaultIfEmpty(ActionClass.Safe).Max();
}

/// <summary>One sensitive or dangerous action, as shown in the approval prompt and by the lint tool.</summary>
public sealed record ReviewItem(string Path, ActionClass Class, string Kind, string Summary);

public static class ReviewBuilder
{
    /// <summary>A launcher chord or a long run of key presses can type a command into a shell, so it needs review.</summary>
    public const int MaxSafeKeyPresses = 3;

    public static IReadOnlyList<ReviewItem> Build(RemoteDefinition remote)
    {
        var list = new List<ReviewItem>();
        foreach (var item in remote.Items)
        {
            var keys = item.Actions.OfType<KeyPressAction>().ToList();
            var sequence = keys.Count > MaxSafeKeyPresses
                ? $"presses {keys.Count} keys in a row (enough to type a command)"
                : keys.FirstOrDefault(k => IsLauncherChord(k.Chord)) is { } l ? $"presses {l.Keys}, which opens the Start menu or Task Manager" : null;
            if (sequence is not null)
                list.Add(new ReviewItem($"layout.items[{item.Index}].action", ActionClass.Sensitive, "key", sequence));

            for (var a = 0; a < item.Actions.Count; a++)
            {
                var act = item.Actions[a];
                var cls = ActionClassifier.Classify(act);
                if (cls == ActionClass.Safe) continue;
                list.Add(new ReviewItem($"layout.items[{item.Index}].action[{a}]", cls, act.Kind, Describe(act)));
            }
        }
        return list;
    }

    /// <summary>Ctrl+Escape opens the Start menu (a search box that runs what is typed) and Ctrl+Shift+Escape opens Task Manager; the Win key itself is already refused by the key parser.</summary>
    internal static bool IsLauncherChord(KeyChord chord) => chord.Key == 0x1B && chord.Modifiers.Contains(KeyParser.Ctrl);

    private static string Describe(RemoteAction a) => a switch
    {
        TextAction t => $"types text ({t.Text.Length} chars)",
        OpenUrlAction u => $"opens {u.Url} in the default browser",
        LaunchAction l => $"starts program slot \"{l.Slot}\" with arguments [{string.Join(" ", l.Args.Select(x => $"\"{x}\""))}]",
        HttpAction h => $"{h.Method} {HttpPolicy.Redact(h.Url)}",
        _ => a.Kind,
    };

    /// <summary>Non-loopback hosts that http actions of this remote contact (the user approves exactly these).</summary>
    public static IReadOnlyList<string> ExternalHosts(RemoteDefinition remote)
        => remote.Items.SelectMany(i => i.Actions).OfType<HttpAction>()
            .Select(h => HttpPolicy.HostOf(h.Url))
            .Where(h => h is not null && !HttpPolicy.IsLoopbackHost(h))
            .Select(h => h!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> LaunchSlots(RemoteDefinition remote)
        => remote.Items.SelectMany(i => i.Actions).OfType<LaunchAction>().Select(l => l.Slot).Distinct().ToList();
}

public static class RemoteHash
{
    /// <summary>SHA-256 of the exact file bytes, lowercase hex. Any edit, even whitespace, changes it.</summary>
    public static string Of(ReadOnlySpan<byte> fileBytes) => Convert.ToHexStringLower(SHA256.HashData(fileBytes));
}

public static class HttpPolicy
{
    public static string? HostOf(string url)
    {
        var probe = SecretSubstitution.Placeholder().Replace(url, "x");
        return Uri.TryCreate(probe, UriKind.Absolute, out var u) ? u.IdnHost.ToLowerInvariant() : null;
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (host is null) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        host = host.Trim('[', ']');
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>Loopback is always allowed; any other host only if the user approved it for this remote.</summary>
    public static bool IsHostAllowed(string host, IReadOnlyCollection<string> approvedHosts)
        => IsLoopbackHost(host) || approvedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The URL without query and fragment, for prompts and logs (tokens usually live in the query).</summary>
    public static string Redact(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut] + "?...";
    }
}

public interface ISecretStore
{
    /// <summary>The secret's value, or null when it is not set on this PC.</summary>
    string? Get(string name);
}

public sealed class InMemorySecretStore(IDictionary<string, string>? values = null) : ISecretStore
{
    private readonly Dictionary<string, string> _v = new(values ?? new Dictionary<string, string>());
    public string? Get(string name) => _v.GetValueOrDefault(name);
    public void Set(string name, string value) => _v[name] = value;
}

public static partial class SecretSubstitution
{
    [GeneratedRegex(@"\$\{([^}]*)\}")] internal static partial Regex Placeholder();

    /// <summary>Replaces <c>${secret.name}</c>. On a missing secret returns null and its name (never a value) in <paramref name="missing"/>.</summary>
    public static string? Apply(string template, ISecretStore secrets, Func<string, string>? encode, out string? missing)
    {
        string? miss = null;
        var result = Placeholder().Replace(template, m =>
        {
            var token = m.Groups[1].Value;
            if (!token.StartsWith("secret.", StringComparison.Ordinal)) { miss ??= token; return ""; }
            var name = token["secret.".Length..];
            var v = secrets.Get(name);
            if (v is null) { miss ??= name; return ""; }
            return encode is null ? v : encode(v);
        });
        missing = miss;
        return miss is null ? result : null;
    }
}
