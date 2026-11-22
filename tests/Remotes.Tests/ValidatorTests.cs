using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyMouse.Remotes.Model;
using EasyMouse.Remotes.Validation;

namespace EasyMouse.Remotes.Tests;

public class ValidatorTests
{
    private const string ArchitectureExample = """
        { "schema": 1, "id": "stremio", "name": "Stremio",
          "target": { "process": "stremio*.exe" },
          "layout": { "cols": 4, "items": [
            { "type": "button", "icon": "play", "action": { "do": "smtc", "cmd": "toggle" } },
            { "type": "button", "label": "Fullscreen",
              "action": [ { "do": "focus" }, { "do": "key", "keys": "F" } ] },
            { "type": "slider", "bind": "volume.app", "action": { "do": "volume", "scope": "app" } },
            { "type": "label", "bind": "smtc.title" } ] } }
        """;

    private static ValidationResult V(string json) => RemoteValidator.Validate(json);

    private static void AssertError(string json, string expectedFragment)
    {
        var r = V(json);
        Assert.False(r.IsValid, "expected invalid");
        Assert.Contains(r.Errors, e => e.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_the_architecture_example_and_defaults_item_ids_to_positions()
    {
        var r = V(ArchitectureExample);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal(["0", "1", "2", "3"], r.Remote!.Items.Select(i => i.Id));
        Assert.Equal("stremio*.exe", r.Remote.TargetProcess);
        Assert.IsType<FocusAction>(r.Remote.Items[1].Actions[0]);
    }

    [Fact]
    public void Accepts_every_action_kind()
    {
        const string items = """
            { "type":"button","label":"a","action":[
              {"do":"key","keys":"Ctrl+Shift+Right","repeat":3},{"do":"text","text":"שלום"},{"do":"media","cmd":"playPause"},
              {"do":"mouse","cmd":"doubleClick"},{"do":"smtc","cmd":"seekForward","seconds":30},
              {"do":"volume","cmd":"up","step":5},{"do":"launch","exe":"player","args":["--fs"]}] },
            { "type":"button","label":"b","action":[
              {"do":"focus"},{"do":"openUrl","url":"https://example.com/a?b=c"},
              {"do":"http","method":"POST","url":"http://127.0.0.1:3000/x?t=${secret.tok}","headers":{"X-A":"${secret.tok}"},"body":"{}"},
              {"do":"delay","ms":100}] }
            """;
        var r = V(Json.Remote(items, extra: """, "target": {"process":"a.exe"}"""));
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal(RemoteValidator.ActionKinds.OrderBy(x => x), r.Remote!.Items.SelectMany(i => i.Actions).Select(a => a.Kind).OrderBy(x => x));
    }

    [Theory]
    [InlineData("""{"do":"shell","cmd":"calc"}""", "unknown action \"shell\"")]
    [InlineData("""{"do":"powershell","script":"x"}""", "unknown action")]
    [InlineData("""{"do":"key","keys":"F","extra":1}""", "$.layout.items[0].action.extra: unknown field")]
    [InlineData("""{"do":"key","keys":"Win+R"}""", "unknown modifier")]
    [InlineData("""{"do":"key","keys":"Ctrl+Nope"}""", "unknown key")]
    [InlineData("""{"do":"key"}""", "$.layout.items[0].action.keys: is required")]
    [InlineData("""{"do":"focus"}""", "focus needs the remote to declare target.process")]
    [InlineData("""{"do":"launch","exe":"C:\\Windows\\calc.exe"}""", "slot name")]
    [InlineData("""{"do":"launch","exe":"p","args":["${secret.a}"]}""", "placeholders are not allowed in launch arguments")]
    [InlineData("""{"do":"openUrl","url":"file:///C:/x"}""", "only http and https")]
    [InlineData("""{"do":"openUrl","url":"javascript:alert(1)"}""", "absolute http")]
    [InlineData("""{"do":"openUrl","url":"http://user:pw@example.com/"}""", "credentials")]
    [InlineData("""{"do":"http","url":"http://${secret.h}/x"}""", "placeholder cannot be part of the host")]
    [InlineData("""{"do":"http","url":"http://a.example/${env.PATH}"}""", "only ${secret.name}")]
    [InlineData("""{"do":"http","url":"ftp://a.example/"}""", "only http and https")]
    [InlineData("""{"do":"http","url":"http://a.example/","method":"PATCH"}""", "must be one of")]
    [InlineData("""{"do":"http","url":"http://a.example/","body":"x"}""", "a GET has no body")]
    [InlineData("""{"do":"http","url":"http://a.example/","headers":{"Host":"evil"}}""", "header name not allowed")]
    [InlineData("""{"do":"smtc","cmd":"seekBack"}""", "seconds")]
    [InlineData("""{"do":"smtc","cmd":"toggle","seconds":5}""", "only valid with seekForward")]
    [InlineData("""{"do":"delay","ms":99999}""", "must be 1..10000")]
    [InlineData("""{"do":"volume","cmd":"up","level":5}""", "only valid with cmd")]
    public void Rejects_bad_actions_with_a_path(string action, string expected)
    {
        var json = Json.Remote(Json.Button(action), extra: "");
        // focus needs no target in the failing case above; every other case is independent of it.
        AssertError(json, expected);
    }

    [Fact]
    public void Error_messages_name_the_exact_path()
    {
        var json = Json.Remote(Json.Button("""{"do":"key","keys":"F"}""") + "," + Json.Button("""{"do":"nope"}"""));
        Assert.Contains(V(json).Errors, e => e.StartsWith("$.layout.items[1].action.do:", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_unknown_fields_everywhere()
    {
        AssertError("""{"schema":1,"id":"a","name":"A","layout":{"cols":1,"items":[{"type":"label","text":"x"}]},"x":1}""", "$.x: unknown field");
        AssertError("""{"schema":1,"id":"a","name":"A","layout":{"cols":1,"items":[{"type":"label","text":"x","onclick":"y"}]}}""", "$.layout.items[0].onclick: unknown field");
        AssertError("""{"schema":1,"id":"a","name":"A","layout":{"cols":1,"rows":2,"items":[{"type":"label","text":"x"}]}}""", "$.layout.rows: unknown field");
        AssertError("""{"schema":1,"id":"a","name":"A","target":{"process":"a.exe","path":"c:"},"layout":{"cols":1,"items":[{"type":"label","text":"x"}]}}""", "$.target.path: unknown field");
    }

    [Fact]
    public void Rejects_duplicate_keys_wrong_schema_and_malformed_json()
    {
        AssertError("""{"schema":1,"schema":1,"id":"a","name":"A","layout":{"cols":1,"items":[{"type":"label","text":"x"}]}}""", "duplicate key");
        AssertError("""{"schema":2,"id":"a","name":"A","layout":{"cols":1,"items":[{"type":"label","text":"x"}]}}""", "unsupported version 2");
        AssertError("{ not json", "invalid JSON");
        AssertError("[]", "must be an object");
        AssertError("""{"schema":1,"id":"A b","name":"A","layout":{"cols":1,"items":[{"type":"label","text":"x"}]}}""", "$.id");
    }

    [Fact]
    public void Enforces_size_limits()
    {
        var many = string.Join(",", Enumerable.Range(0, 65).Select(i => $$"""{"type":"label","text":"t{{i}}"}"""));
        AssertError(Json.Remote(many), "1..64 items");
        var ok = string.Join(",", Enumerable.Range(0, 64).Select(i => $$"""{"type":"label","text":"t{{i}}"}"""));
        Assert.True(V(Json.Remote(ok)).IsValid);
        AssertError(Json.Remote("""{"type":"label","text":"x"}""").Replace("\"cols\": 2", "\"cols\": 5"), "$.layout.cols");
        AssertError(Json.Remote(Json.Button("""{"do":"text","text":"x"}""", new string('l', 41))), "$.layout.items[0].label");
        var nine = string.Join(",", Enumerable.Repeat("""{"do":"delay","ms":1}""", 9));
        AssertError(Json.Remote(Json.Button($"[{nine}]")), "1..8 actions");
        var longDelay = string.Join(",", Enumerable.Repeat("""{"do":"delay","ms":10000}""", 4));
        AssertError(Json.Remote(Json.Button($"[{longDelay}]")), "total delay");
        AssertError(Json.Remote(Json.Button($$"""{"do":"text","text":"{{new string('a', 1025)}}"}""")), "text");
        var huge = Json.Remote(Json.Button("""{"do":"key","keys":"F"}""")) + new string(' ', RemoteValidator.MaxFileBytes);
        AssertError(huge, "the limit is");
    }

    [Fact]
    public void Enforces_item_rules()
    {
        AssertError(Json.Remote("""{"type":"button","action":{"do":"key","keys":"F"}}"""), "needs an icon or a label");
        AssertError(Json.Remote("""{"type":"button","label":"x"}"""), "action: is required for a button");
        AssertError(Json.Remote("""{"type":"button","icon":"http://evil/x.svg","action":{"do":"key","keys":"F"}}"""), "not in the built-in icon set");
        AssertError(Json.Remote("""{"type":"slider","action":{"do":"key","keys":"F"}}"""), "exactly one volume action");
        AssertError(Json.Remote("""{"type":"slider","action":{"do":"volume","cmd":"up"}}"""), "exactly one volume action");
        AssertError(Json.Remote("""{"type":"label","text":"x","action":{"do":"key","keys":"F"}}"""), "cannot have an action");
        AssertError(Json.Remote("""{"type":"label","bind":"smtc.secret"}"""), "unknown binding");
        AssertError(Json.Remote("""{"type":"iframe"}"""), "unknown value");
        AssertError(Json.Remote("""{"type":"pad","action":{"do":"key","keys":"F"}}"""), "a pad cannot have an action");
        Assert.True(V(Json.Remote("""{"type":"pad","label":"Trackpad"}""")).IsValid);
        // an explicit id may not collide with another item's positional id
        AssertError(Json.Remote("""{"type":"label","text":"a","id":"1"},{"type":"label","text":"b"}"""), "duplicate item id \"1\"");
        AssertError(Json.Remote("""{"type":"label","text":"a","id":"x"},{"type":"label","text":"b","id":"x"}"""), "duplicate item id");
    }

    [Fact]
    public void Process_patterns_are_file_names_not_paths()
    {
        foreach (var bad in new[] { "c:\\\\x.exe", "..\\\\x", "a/b.exe", "a b.exe" })
            AssertError(Json.Remote("""{"type":"label","text":"x"}""", extra: $$""", "target": { "process": "{{bad}}" }"""), "$.target.process");
        Assert.True(V(Json.Remote("""{"type":"label","text":"x"}""", extra: """, "target": { "process": "vlc.exe" }""")).IsValid);
    }

    [Fact]
    public void Random_mutations_of_a_valid_file_never_throw()
    {
        var seed = File.ReadAllBytes(Path.Combine(Repo.BuiltIn, "stremio.json"));
        var rng = new Random(12345);
        for (var n = 0; n < 2000; n++)
        {
            var b = (byte[])seed.Clone();
            for (var k = rng.Next(1, 6); k > 0; k--) b[rng.Next(b.Length)] = (byte)rng.Next(256);
            if (n % 5 == 0) b = b[..rng.Next(b.Length)];
            var r = RemoteValidator.Validate(b);
            Assert.True(r.IsValid == (r.Errors.Count == 0));
        }
    }

    // ---- keep the published schema, the web client and the validator in sync ----

    [Fact]
    public void Icons_match_the_web_client_icon_set()
    {
        var ts = File.ReadAllText(Path.Combine(Repo.Root, "web", "src", "ui", "iconPaths.ts"));
        var block = ts[ts.IndexOf("ICON_PATHS", StringComparison.Ordinal)..ts.IndexOf("};", StringComparison.Ordinal)];
        var names = Regex.Matches(block, @"^\s{2}(?:'([\w-]+)'|(\w+)):", RegexOptions.Multiline).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToHashSet();
        Assert.NotEmpty(names);
        Assert.True(names.SetEquals(RemoteValidator.Icons), $"web: {string.Join(",", names.Except(RemoteValidator.Icons))} / validator: {string.Join(",", RemoteValidator.Icons.Except(names))}");
    }

    private static JsonElement Schema()
    {
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "remotes", "remote.schema.json")));
        return d.RootElement.Clone();
    }

    [Fact]
    public void Schema_file_lists_exactly_the_validators_actions_and_forbids_extra_fields()
    {
        var defs = Schema().GetProperty("$defs");
        var kinds = new List<string>();
        foreach (var name in RemoteValidator.ActionKinds)
        {
            var def = defs.GetProperty(name);
            Assert.False(def.GetProperty("additionalProperties").GetBoolean(), name);
            kinds.Add(def.GetProperty("properties").GetProperty("do").GetProperty("const").GetString()!);
        }
        Assert.Equal(RemoteValidator.ActionKinds, kinds);
        var refs = defs.GetProperty("action").GetProperty("oneOf").EnumerateArray().Select(e => e.GetProperty("$ref").GetString()![8..]).ToList();
        Assert.Equal(RemoteValidator.ActionKinds.OrderBy(x => x), refs.OrderBy(x => x));
        Assert.DoesNotContain(defs.EnumerateObject(), p => p.Name == "shell");
        var root = Schema();
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.False(defs.GetProperty("item").GetProperty("additionalProperties").GetBoolean());
        Assert.False(root.GetProperty("properties").GetProperty("layout").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Schema_file_enums_match_the_validator()
    {
        var s = Schema();
        var item = s.GetProperty("$defs").GetProperty("item").GetProperty("properties");
        Assert.Equal(RemoteValidator.Icons.OrderBy(x => x), item.GetProperty("icon").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).OrderBy(x => x));
        Assert.Equal(RemoteValidator.Bindings, item.GetProperty("bind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(["button", "slider", "label", "pad"], item.GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(RemoteValidator.SchemaVersion, s.GetProperty("properties").GetProperty("schema").GetProperty("const").GetInt32());
        Assert.Equal(RemoteValidator.MaxItems, s.GetProperty("properties").GetProperty("layout").GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
    }
}
