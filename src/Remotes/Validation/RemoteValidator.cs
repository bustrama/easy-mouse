using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Remotes.Validation;

public sealed record ValidationResult(RemoteDefinition? Remote, IReadOnlyList<string> Errors)
{
    public bool IsValid => Remote is not null && Errors.Count == 0;
}

/// <summary>
/// Strict, hand-written validator for remote schema 1 (remotes/remote.schema.json is its published form).
/// Unknown fields and unknown actions are errors. Nothing is deserialised by reflection.
/// </summary>
public static partial class RemoteValidator
{
    public const int SchemaVersion = 1;
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxItems = 64;
    public const int MaxActionsPerItem = 8;
    public const int MaxTextBytes = 1024;
    public const int MaxUrlChars = 2048;
    public const int MaxHeaders = 8;
    public const int MaxDelayMs = 10_000;
    public const int MaxTotalDelayMs = 30_000;
    public const int MaxLaunchArgs = 16;
    public const int DefaultHttpTimeoutMs = 5_000;
    public const int MaxHttpTimeoutMs = 10_000;

    /// <summary>Icons the phone can draw (web/src/ui/iconPaths.ts). A test keeps this in sync.</summary>
    public static readonly IReadOnlySet<string> Icons = new HashSet<string>(
    [
        "mouse", "keyboard", "media", "remotes", "play", "pause", "next", "prev", "fullscreen", "subtitles", "skip",
        "back", "up", "down", "left", "right", "volume", "mute", "plus", "minus", "home", "power", "settings",
        "check", "rewind", "forward",
    ]);

    public static readonly IReadOnlyList<string> Bindings = ["smtc.title", "smtc.artist", "volume", "volume.master", "volume.app"];

    public static readonly IReadOnlyList<string> ActionKinds =
        ["key", "text", "media", "mouse", "smtc", "volume", "focus", "launch", "openUrl", "http", "delay"];

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")] private static partial Regex RemoteIdRx();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")] private static partial Regex ItemIdRx();
    [GeneratedRegex("^[A-Za-z0-9._*?-]{1,64}$")] private static partial Regex ProcessRx();
    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")] private static partial Regex SlotRx();
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")] private static partial Regex HeaderNameRx();
    [GeneratedRegex(@"\$\{([^}]*)\}")] private static partial Regex PlaceholderRx();
    [GeneratedRegex(@"^secret\.[a-z][a-z0-9_]{0,31}$")] private static partial Regex SecretRefRx();

    private static readonly Dictionary<string, MediaCmd> MediaMap = new()
    { ["playPause"] = MediaCmd.PlayPause, ["next"] = MediaCmd.Next, ["prev"] = MediaCmd.Prev, ["stop"] = MediaCmd.Stop, ["volUp"] = MediaCmd.VolUp, ["volDown"] = MediaCmd.VolDown, ["mute"] = MediaCmd.Mute };
    private static readonly Dictionary<string, MouseCmd> MouseMap = new()
    { ["click"] = MouseCmd.Click, ["rightClick"] = MouseCmd.RightClick, ["middleClick"] = MouseCmd.MiddleClick, ["doubleClick"] = MouseCmd.DoubleClick, ["scrollUp"] = MouseCmd.ScrollUp, ["scrollDown"] = MouseCmd.ScrollDown };
    private static readonly Dictionary<string, SmtcCmd> SmtcMap = new()
    { ["toggle"] = SmtcCmd.Toggle, ["play"] = SmtcCmd.Play, ["pause"] = SmtcCmd.Pause, ["next"] = SmtcCmd.Next, ["prev"] = SmtcCmd.Prev, ["stop"] = SmtcCmd.Stop, ["seekForward"] = SmtcCmd.SeekForward, ["seekBack"] = SmtcCmd.SeekBack };
    private static readonly Dictionary<string, VolumeScope> ScopeMap = new() { ["master"] = VolumeScope.Master, ["app"] = VolumeScope.App };
    private static readonly Dictionary<string, VolumeCmd> VolMap = new()
    { ["set"] = VolumeCmd.Set, ["up"] = VolumeCmd.Up, ["down"] = VolumeCmd.Down, ["mute"] = VolumeCmd.Mute, ["unmute"] = VolumeCmd.Unmute, ["toggleMute"] = VolumeCmd.ToggleMute };
    private static readonly Dictionary<string, ItemType> TypeMap = new()
    { ["button"] = ItemType.Button, ["slider"] = ItemType.Slider, ["label"] = ItemType.Label, ["pad"] = ItemType.Pad };
    private static readonly HashSet<string> HttpMethods = ["GET", "POST", "PUT", "DELETE"];

    public static ValidationResult Validate(ReadOnlySpan<byte> utf8)
    {
        var errors = new ErrorList();
        if (utf8.Length > MaxFileBytes)
            return new(null, [$"$: file is {utf8.Length} bytes, the limit is {MaxFileBytes}"]);
        using var doc = StrictJson.Parse(utf8, errors);
        if (doc is null) return new(null, errors.Errors);
        RemoteDefinition? remote;
        try { remote = ValidateRoot(doc.RootElement, errors); }
        catch (InvalidOperationException)
        {
            // e.g. a lone surrogate escape (\ud800) that cannot become a string
            return new(null, [.. errors.Errors, "$: contains a string with invalid Unicode"]);
        }
        return new(errors.Errors.Count == 0 ? remote : null, errors.Errors);
    }

    public static ValidationResult Validate(string json) => Validate(Encoding.UTF8.GetBytes(json));

    private static RemoteDefinition? ValidateRoot(JsonElement root, ErrorList errors)
    {
        var o = new ObjectReader(root, "$", errors);
        if (!o.Valid) return null;
        o.String("$schema", false, 200);
        var schema = o.Int("schema", true, 0, int.MaxValue);
        if (schema is not null && schema != SchemaVersion) errors.Add("$.schema", $"unsupported version {schema} (this build reads {SchemaVersion})");
        var id = o.String("id", true, 64);
        if (id is not null && !RemoteIdRx().IsMatch(id)) errors.Add("$.id", "must match ^[a-z0-9][a-z0-9_-]{0,63}$");
        var name = o.String("name", true, 40);
        var description = o.String("description", false, 1000);
        string? process = null;
        if (o.Raw("target") is { } t)
        {
            var to = new ObjectReader(t, "$.target", errors);
            process = to.String("process", true, 64);
            if (process is not null && (!ProcessRx().IsMatch(process) || process.Contains("**")))
                errors.Add("$.target.process", "must be a file-name pattern: letters, digits, . _ - and the wildcards * ?");
            to.Finish();
        }
        var cols = 0;
        var items = new List<RemoteItem>();
        if (o.Raw("layout") is { } l)
        {
            var lo = new ObjectReader(l, "$.layout", errors);
            cols = lo.Int("cols", true, 1, 4) ?? 0;
            if (lo.Raw("items") is { } arr)
            {
                if (arr.ValueKind != JsonValueKind.Array) errors.Add("$.layout.items", "must be an array");
                else if (arr.GetArrayLength() == 0 || arr.GetArrayLength() > MaxItems) errors.Add("$.layout.items", $"must have 1..{MaxItems} items");
                else
                {
                    var i = 0;
                    foreach (var el in arr.EnumerateArray())
                    {
                        var it = ValidateItem(el, i, process, errors);
                        if (it is not null) items.Add(it);
                        i++;
                    }
                }
            }
            else errors.Add("$.layout.items", "is required");
            lo.Finish();
        }
        else errors.Add("$.layout", "is required");
        o.Finish();

        var seen = new HashSet<string>();
        foreach (var it in items)
            if (!seen.Add(it.Id)) errors.Add($"$.layout.items[{it.Index}].id", $"duplicate item id \"{it.Id}\" (ids default to the position)");

        if (errors.Errors.Count > 0 || id is null || name is null) return null;
        return new RemoteDefinition(id, name, description, process, cols, items);
    }

    private static RemoteItem? ValidateItem(JsonElement el, int index, string? process, ErrorList errors)
    {
        var path = $"$.layout.items[{index}]";
        var o = new ObjectReader(el, path, errors);
        if (!o.Valid) return null;
        var type = o.Enum("type", true, TypeMap);
        var explicitId = o.String("id", false, 64);
        if (explicitId is not null && !ItemIdRx().IsMatch(explicitId)) errors.Add($"{path}.id", "must match ^[A-Za-z0-9_-]{1,64}$");
        var icon = o.String("icon", false, 32);
        if (icon is not null && !Icons.Contains(icon)) errors.Add($"{path}.icon", $"\"{icon}\" is not in the built-in icon set");
        var label = o.String("label", false, 40);
        var text = o.String("text", false, 80);
        var bind = o.String("bind", false, 32);
        if (bind is not null && !Bindings.Contains(bind)) errors.Add($"{path}.bind", $"unknown binding \"{bind}\" (allowed: {string.Join(", ", Bindings)})");

        var actions = new List<RemoteAction>();
        var actionRaw = o.Raw("action");
        if (actionRaw is { } a)
        {
            if (a.ValueKind == JsonValueKind.Array)
            {
                if (a.GetArrayLength() is 0 or > MaxActionsPerItem) errors.Add($"{path}.action", $"must have 1..{MaxActionsPerItem} actions");
                else
                {
                    var j = 0;
                    foreach (var x in a.EnumerateArray())
                    {
                        var act = ValidateAction(x, $"{path}.action[{j++}]", process, errors);
                        if (act is not null) actions.Add(act);
                    }
                }
            }
            else
            {
                var act = ValidateAction(a, $"{path}.action", process, errors);
                if (act is not null) actions.Add(act);
            }
        }
        o.Finish();

        switch (type)
        {
            case ItemType.Button:
                if (icon is null && label is null) errors.Add(path, "a button needs an icon or a label");
                if (actionRaw is null) errors.Add($"{path}.action", "is required for a button");
                if (bind is not null || text is not null) errors.Add(path, "a button has no bind or text");
                break;
            case ItemType.Slider:
                if (actionRaw is null) errors.Add($"{path}.action", "is required for a slider");
                else if (actions.Count != 1 || actions[0] is not VolumeAction { Cmd: VolumeCmd.Set, Level: null })
                    errors.Add($"{path}.action", "a slider takes exactly one volume action with cmd \"set\" and no level");
                if (text is not null || icon is not null) errors.Add(path, "a slider has no text or icon");
                break;
            case ItemType.Label:
                if (bind is null && text is null && label is null) errors.Add(path, "a label needs bind, text or label");
                if (actionRaw is not null) errors.Add($"{path}.action", "a label cannot have an action");
                if (icon is not null) errors.Add(path, "a label has no icon");
                break;
            case ItemType.Pad:
                if (actionRaw is not null) errors.Add($"{path}.action", "a pad cannot have an action");
                if (bind is not null || text is not null || icon is not null) errors.Add(path, "a pad takes only id and label");
                break;
        }
        if (type is null) return null;
        var total = actions.OfType<DelayAction>().Sum(d => d.Ms);
        if (total > MaxTotalDelayMs) errors.Add($"{path}.action", $"total delay {total} ms exceeds {MaxTotalDelayMs}");
        return new RemoteItem(index, explicitId ?? index.ToString(CultureInfo.InvariantCulture), type.Value, icon, label, text, bind, actions);
    }

    private static RemoteAction? ValidateAction(JsonElement el, string path, string? process, ErrorList errors)
    {
        var o = new ObjectReader(el, path, errors);
        if (!o.Valid) return null;
        var kind = o.String("do", true, 16);
        RemoteAction? result = null;
        switch (kind)
        {
            case null: break;
            case "key":
            {
                var keys = o.String("keys", true, 40);
                var repeat = o.Int("repeat", false, 1, 50) ?? 1;
                if (keys is null) break;
                var err = KeyParser.TryParse(keys, out var chord);
                if (err is not null) errors.Add($"{path}.keys", err);
                else result = new KeyPressAction(chord, keys, repeat);
                break;
            }
            case "text":
            {
                var t = o.String("text", true, MaxTextBytes);
                if (t is not null && Encoding.UTF8.GetByteCount(t) > MaxTextBytes) errors.Add($"{path}.text", $"longer than {MaxTextBytes} UTF-8 bytes");
                else if (t is not null) result = new TextAction(t);
                break;
            }
            case "media": { var c = o.Enum("cmd", true, MediaMap); if (c is not null) result = new MediaAction(c.Value); break; }
            case "mouse": { var c = o.Enum("cmd", true, MouseMap); if (c is not null) result = new MouseAction(c.Value); break; }
            case "smtc":
            {
                var c = o.Enum("cmd", true, SmtcMap);
                var secs = o.Int("seconds", false, 1, 600);
                var seek = c is SmtcCmd.SeekForward or SmtcCmd.SeekBack;
                if (secs is not null && !seek) errors.Add($"{path}.seconds", "only valid with seekForward/seekBack");
                if (seek && secs is null) errors.Add($"{path}.seconds", "is required for seeking");
                if (c is not null) result = new SmtcAction(c.Value, secs ?? 0);
                break;
            }
            case "volume":
            {
                var scope = o.Enum("scope", false, ScopeMap) ?? VolumeScope.Master;
                var c = o.Enum("cmd", false, VolMap) ?? VolumeCmd.Set;
                var level = o.Int("level", false, 0, 100);
                var step = o.Int("step", false, 1, 20) ?? 2;
                if (level is not null && c != VolumeCmd.Set) errors.Add($"{path}.level", "only valid with cmd \"set\"");
                result = new VolumeAction(scope, c, level, step);
                break;
            }
            case "focus":
                if (process is null) errors.Add(path, "focus needs the remote to declare target.process");
                result = new FocusAction();
                break;
            case "launch":
            {
                var slot = o.String("exe", true, 32);
                if (slot is not null && !SlotRx().IsMatch(slot))
                    errors.Add($"{path}.exe", "is a slot name (^[a-z][a-z0-9_]{0,31}$) that the user binds to a program on the PC; it is not a path");
                var args = new List<string>();
                if (o.Raw("args") is { } av)
                {
                    if (av.ValueKind != JsonValueKind.Array || av.GetArrayLength() > MaxLaunchArgs)
                        errors.Add($"{path}.args", $"must be an array of at most {MaxLaunchArgs} strings");
                    else
                        foreach (var x in av.EnumerateArray())
                        {
                            var s = x.ValueKind == JsonValueKind.String ? x.GetString()! : null;
                            if (s is null || s.Length > 256 || s.Contains('\0')) errors.Add($"{path}.args", "each argument must be a string of at most 256 characters");
                            else if (s.Contains("${")) errors.Add($"{path}.args", "placeholders are not allowed in launch arguments");
                            else args.Add(s);
                        }
                }
                if (slot is not null) result = new LaunchAction(slot, args);
                break;
            }
            case "openUrl":
            {
                var u = o.String("url", true, MaxUrlChars);
                if (u is not null)
                {
                    var e = CheckWebUrl(u, allowSecrets: false);
                    if (e is not null) errors.Add($"{path}.url", e);
                    else result = new OpenUrlAction(u);
                }
                break;
            }
            case "http":
            {
                var method = o.String("method", false, 8) ?? "GET";
                if (!HttpMethods.Contains(method)) errors.Add($"{path}.method", $"must be one of {string.Join(", ", HttpMethods)}");
                var url = o.String("url", true, MaxUrlChars);
                if (url is not null)
                {
                    var e = CheckWebUrl(url, allowSecrets: true);
                    if (e is not null) errors.Add($"{path}.url", e);
                    PlaceholdersOk(url, $"{path}.url", errors);
                }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (o.Raw("headers") is { } hv)
                {
                    if (hv.ValueKind != JsonValueKind.Object || hv.EnumerateObject().Count() > MaxHeaders)
                        errors.Add($"{path}.headers", $"must be an object with at most {MaxHeaders} entries");
                    else
                        foreach (var h in hv.EnumerateObject())
                        {
                            var hp = $"{path}.headers.{h.Name}";
                            var hs = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString()! : null;
                            if (!HeaderNameRx().IsMatch(h.Name) || h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                                || h.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                                errors.Add(hp, "header name not allowed (Host and Content-* are set by the app)");
                            else if (hs is null || hs.Length > 512 || hs.Any(char.IsControl))
                                errors.Add(hp, "must be a string of at most 512 characters without control characters");
                            else { PlaceholdersOk(hs, hp, errors); headers[h.Name] = hs; }
                        }
                }
                var body = o.String("body", false, 2048, 0);
                if (body is not null) PlaceholdersOk(body, $"{path}.body", errors);
                if (body is not null && method == "GET") errors.Add($"{path}.body", "a GET has no body");
                var timeout = o.Int("timeoutMs", false, 100, MaxHttpTimeoutMs) ?? DefaultHttpTimeoutMs;
                if (url is not null) result = new HttpAction(method, url, headers, body, timeout);
                break;
            }
            case "delay":
            {
                var ms = o.Int("ms", true, 1, MaxDelayMs);
                if (ms is not null) result = new DelayAction(ms.Value);
                break;
            }
            default:
                errors.Add($"{path}.do", $"unknown action \"{kind}\" (allowed: {string.Join(", ", ActionKinds)})");
                break;
        }
        o.Finish();
        return result;
    }

    /// <summary>Only <c>${secret.name}</c> placeholders are allowed.</summary>
    private static void PlaceholdersOk(string s, string path, ErrorList errors)
    {
        foreach (Match m in PlaceholderRx().Matches(s))
            if (!SecretRefRx().IsMatch(m.Groups[1].Value))
                errors.Add(path, $"unknown placeholder \"{m.Value}\" (only ${{secret.name}} is supported)");
        if (PlaceholderRx().Replace(s, "").Contains("${")) errors.Add(path, "malformed placeholder");
    }

    /// <summary>http/https only, no credentials in the URL, and no placeholder in the host part (a secret must never choose the host).</summary>
    private static string? CheckWebUrl(string url, bool allowSecrets)
    {
        if (!allowSecrets && url.Contains("${")) return "placeholders are not allowed here";
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return "must be an absolute http:// or https:// URL";
        if (url[..schemeEnd] is not ("http" or "https")) return "only http and https are allowed";
        var afterScheme = url[(schemeEnd + 3)..];
        var authEnd = afterScheme.IndexOfAny(['/', '?', '#']);
        var authority = authEnd < 0 ? afterScheme : afterScheme[..authEnd];
        if (authority.Contains("${")) return "a placeholder cannot be part of the host";
        if (authority.Contains('@')) return "credentials in the URL are not allowed";
        var probe = PlaceholderRx().Replace(url, "x");
        if (probe.Any(char.IsControl) || probe.Contains(' ')) return "must not contain spaces or control characters";
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "is not a valid URL";
        return null;
    }
}
