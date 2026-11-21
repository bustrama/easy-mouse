using System.Text.Json;

namespace EasyMouse.Remotes.Validation;

/// <summary>Collects validation errors with JSON-path style locations.</summary>
public sealed class ErrorList
{
    private readonly List<string> _errors = [];
    public IReadOnlyList<string> Errors => _errors;
    public void Add(string path, string message) => _errors.Add($"{path}: {message}");
}

/// <summary>
/// Reads one JSON object field by field. Every field must be consumed through a getter; <see cref="Finish"/>
/// reports whatever is left as an unknown field. Duplicate keys are reported by <see cref="StrictJson.Parse"/>.
/// </summary>
public sealed class ObjectReader
{
    private readonly JsonElement _obj;
    private readonly ErrorList _errors;
    private readonly HashSet<string> _used = [];
    public string Path { get; }
    public bool Valid { get; }

    public ObjectReader(JsonElement element, string path, ErrorList errors)
    {
        _obj = element; Path = path; _errors = errors;
        Valid = element.ValueKind == JsonValueKind.Object;
        if (!Valid) errors.Add(path, "must be an object");
    }

    public bool Has(string name) => Valid && _obj.TryGetProperty(name, out _);

    public JsonElement? Raw(string name)
    {
        if (!Valid) return null;
        _used.Add(name);
        return _obj.TryGetProperty(name, out var v) ? v : null;
    }

    public string? String(string name, bool required, int maxChars, int minChars = 1)
    {
        var v = Raw(name);
        if (v is null) { if (required) _errors.Add($"{Path}.{name}", "is required"); return null; }
        if (v.Value.ValueKind != JsonValueKind.String) { _errors.Add($"{Path}.{name}", "must be a string"); return null; }
        var s = v.Value.GetString()!;
        if (s.Length < minChars || s.Length > maxChars)
        { _errors.Add($"{Path}.{name}", $"length must be {minChars}..{maxChars}"); return null; }
        return s;
    }

    public int? Int(string name, bool required, int min, int max)
    {
        var v = Raw(name);
        if (v is null) { if (required) _errors.Add($"{Path}.{name}", "is required"); return null; }
        if (v.Value.ValueKind != JsonValueKind.Number || !v.Value.TryGetInt32(out var i))
        { _errors.Add($"{Path}.{name}", "must be an integer"); return null; }
        if (i < min || i > max) { _errors.Add($"{Path}.{name}", $"must be {min}..{max}"); return null; }
        return i;
    }

    public T? Enum<T>(string name, bool required, IReadOnlyDictionary<string, T> map) where T : struct
    {
        var s = String(name, required, 32);
        if (s is null) return null;
        if (!map.TryGetValue(s, out var t))
        { _errors.Add($"{Path}.{name}", $"unknown value \"{s}\" (allowed: {string.Join(", ", map.Keys)})"); return null; }
        return t;
    }

    public void Finish()
    {
        if (!Valid) return;
        foreach (var p in _obj.EnumerateObject())
            if (!_used.Contains(p.Name)) _errors.Add($"{Path}.{p.Name}", "unknown field");
    }
}

public static class StrictJson
{
    public const int MaxDepth = 16;

    /// <summary>Parses UTF-8 JSON (no comments, no trailing commas) and rejects duplicate keys.</summary>
    public static JsonDocument? Parse(ReadOnlySpan<byte> utf8, ErrorList errors)
    {
        // Strict UTF-8 (a leading BOM from Windows editors is fine). Invalid bytes would otherwise throw later,
        // when a string is read.
        if (utf8.StartsWith<byte>([0xEF, 0xBB, 0xBF])) utf8 = utf8[3..];
        try { _ = new System.Text.UTF8Encoding(false, true).GetCharCount(utf8); }
        catch (System.Text.DecoderFallbackException)
        {
            errors.Add("$", "file is not valid UTF-8");
            return null;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException ex)
        {
            errors.Add("$", $"invalid JSON ({ex.Message})");
            return null;
        }
        if (!NoDuplicates(doc.RootElement, "$", errors)) { doc.Dispose(); return null; }
        return doc;
    }

    private static bool NoDuplicates(JsonElement e, string path, ErrorList errors)
    {
        var ok = true;
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>();
            foreach (var p in e.EnumerateObject())
            {
                if (!seen.Add(p.Name)) { errors.Add($"{path}.{p.Name}", "duplicate key"); ok = false; }
                ok &= NoDuplicates(p.Value, $"{path}.{p.Name}", errors);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var x in e.EnumerateArray()) ok &= NoDuplicates(x, $"{path}[{i++}]", errors);
        }
        return ok;
    }
}
