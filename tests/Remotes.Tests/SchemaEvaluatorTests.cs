using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyMouse.Remotes.Tests;

/// <summary>
/// A small evaluator for the subset of JSON Schema that remotes/remote.schema.json uses, so the published schema
/// is itself tested (not just compared by name with the C# validator).
/// </summary>
internal sealed class MiniSchema(JsonElement root)
{
    public bool IsValid(JsonElement instance) => Check(root, instance);

    private JsonElement Resolve(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var r))
        {
            var node = root;
            foreach (var part in r.GetString()!.TrimStart('#', '/').Split('/')) node = node.GetProperty(part);
            return node;
        }
        return schema;
    }

    private bool Check(JsonElement schemaIn, JsonElement v)
    {
        if (schemaIn.ValueKind == JsonValueKind.True) return true;
        if (schemaIn.ValueKind == JsonValueKind.False) return false;
        var s = Resolve(schemaIn);
        if (s.TryGetProperty("const", out var c) && !Eq(c, v)) return false;
        if (s.TryGetProperty("enum", out var en) && !en.EnumerateArray().Any(e => Eq(e, v))) return false;
        if (s.TryGetProperty("type", out var t) && !TypeOk(t.GetString()!, v)) return false;
        if (s.TryGetProperty("oneOf", out var one) && one.EnumerateArray().Count(x => Check(x, v)) != 1) return false;
        if (s.TryGetProperty("anyOf", out var any) && !any.EnumerateArray().Any(x => Check(x, v))) return false;
        if (s.TryGetProperty("allOf", out var all) && !all.EnumerateArray().All(x => Check(x, v))) return false;
        if (s.TryGetProperty("not", out var not) && Check(not, v)) return false;
        if (s.TryGetProperty("if", out var iff) && Check(iff, v) && s.TryGetProperty("then", out var then) && !Check(then, v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var str = v.GetString()!;
                if (s.TryGetProperty("minLength", out var mn) && str.Length < mn.GetInt32()) return false;
                if (s.TryGetProperty("maxLength", out var mx) && str.Length > mx.GetInt32()) return false;
                if (s.TryGetProperty("pattern", out var p) && !Regex.IsMatch(str, p.GetString()!)) return false;
                break;
            case JsonValueKind.Number:
                if (s.TryGetProperty("minimum", out var lo) && v.GetDouble() < lo.GetDouble()) return false;
                if (s.TryGetProperty("maximum", out var hi) && v.GetDouble() > hi.GetDouble()) return false;
                break;
            case JsonValueKind.Array:
                var n = v.GetArrayLength();
                if (s.TryGetProperty("minItems", out var mi) && n < mi.GetInt32()) return false;
                if (s.TryGetProperty("maxItems", out var ma) && n > ma.GetInt32()) return false;
                if (s.TryGetProperty("items", out var items) && !v.EnumerateArray().All(x => Check(items, x))) return false;
                break;
            case JsonValueKind.Object:
                var props = s.TryGetProperty("properties", out var pr) ? pr : default;
                if (s.TryGetProperty("required", out var req) && req.EnumerateArray().Any(r => !v.TryGetProperty(r.GetString()!, out _))) return false;
                if (s.TryGetProperty("maxProperties", out var mp) && v.EnumerateObject().Count() > mp.GetInt32()) return false;
                foreach (var member in v.EnumerateObject())
                {
                    if (s.TryGetProperty("propertyNames", out var pn) && !Check(pn, JsonDocument.Parse(JsonSerializer.Serialize(member.Name)).RootElement)) return false;
                    if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(member.Name, out var ps)) { if (!Check(ps, member.Value)) return false; }
                    else if (s.TryGetProperty("additionalProperties", out var ap) && !Check(ap, member.Value)) return false;
                }
                break;
        }
        return true;
    }

    private static bool TypeOk(string type, JsonElement v) => type switch
    {
        "object" => v.ValueKind == JsonValueKind.Object,
        "array" => v.ValueKind == JsonValueKind.Array,
        "string" => v.ValueKind == JsonValueKind.String,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "number" => v.ValueKind == JsonValueKind.Number,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };

    private static bool Eq(JsonElement a, JsonElement b) => a.GetRawText() == b.GetRawText();
}

public class SchemaEvaluatorTests
{
    private static MiniSchema Load()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "remotes", "remote.schema.json")));
        return new MiniSchema(doc.RootElement);
    }

    private static bool Accepts(string json) => Load().IsValid(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void The_schema_file_accepts_every_builtin_remote()
    {
        foreach (var f in Directory.GetFiles(Repo.BuiltIn, "*.json"))
            Assert.True(Accepts(File.ReadAllText(f)), f);
    }

    [Fact]
    public void The_schema_file_agrees_with_the_validator_on_accept_and_reject_cases()
    {
        var cases = new[]
        {
            Json.Remote(Json.Button("""{"do":"key","keys":"Ctrl+F"}""")),
            Json.Remote(Json.Button("""{"do":"http","url":"http://127.0.0.1:1/?t=${secret.a}"}""")),
            Json.Remote(Json.Button("""{"do":"shell","cmd":"calc"}""")),
            Json.Remote(Json.Button("""{"do":"key","keys":"F","extra":1}""")),
            Json.Remote(Json.Button("""{"do":"openUrl","url":"file:///c:/x"}""")),
            Json.Remote(Json.Button("""{"do":"http","url":"http://${secret.h}/"}""")),
            Json.Remote(Json.Button("""{"do":"launch","exe":"C:\\x.exe"}""")),
            Json.Remote("""{"type":"slider","action":{"do":"volume"}}"""),
            Json.Remote("""{"type":"label","text":"x","onclick":"y"}"""),
            Json.Remote("""{"type":"pad"}"""),
            Json.Remote("""{"type":"button","action":{"do":"key","keys":"F"}}"""),
            Json.Remote("""{"type":"button","icon":"evil","action":{"do":"key","keys":"F"}}"""),
            Json.Remote("""{"type":"label","text":"x"}""", extra: ""","extra":1"""),
        };
        foreach (var c in cases)
            Assert.Equal(EasyMouse.Remotes.Validation.RemoteValidator.Validate(c).IsValid, Accepts(c));
    }

    [Fact]
    public void The_schema_file_rejects_shell_and_unknown_fields()
    {
        Assert.False(Accepts(Json.Remote(Json.Button("""{"do":"shell","cmd":"calc"}"""))));
        Assert.False(Accepts("""{"schema":1,"id":"a","name":"A","layout":{"cols":1,"items":[{"type":"label","text":"x"}]},"x":1}"""));
        Assert.True(Accepts("""{"schema":1,"id":"a","name":"A","layout":{"cols":1,"items":[{"type":"label","text":"x"}]}}"""));
    }
}
