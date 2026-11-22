using System.Text;
using System.Text.Json;
using EasyMouse.Core.Devices;
using EasyMouse.Remotes.Security;

namespace EasyMouse.App.Logic;

/// <summary>
/// Secrets for <c>${secret.name}</c> in remotes. Each value is protected (DPAPI in the real app) before it is written; the
/// file holds nothing readable and nothing here is ever sent to a phone. A broken file means "no secrets" (a remote that needs one
/// then fails with the secret's name) and is reported through <see cref="LoadError"/>.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private Dictionary<string, string> _values = new(StringComparer.Ordinal); // name -> plain text, in memory only

    public string? LoadError { get; private set; }

    public FileSecretStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        Load();
    }

    public string? Get(string name) { lock (_gate) return _values.GetValueOrDefault(name); }

    public IReadOnlyList<string> Names { get { lock (_gate) return [.. _values.Keys.Order(StringComparer.Ordinal)]; } }

    public void Set(string name, string value)
    {
        lock (_gate)
        {
            _values[name] = value;
            Persist();
        }
    }

    public void Remove(string name)
    {
        lock (_gate)
        {
            if (_values.Remove(name)) Persist();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, b64) in raw)
                values[name] = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(b64)));
            _values = values;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            _values = new(StringComparer.Ordinal);
            LoadError = $"{_path}: {ex.Message}";
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var raw = _values.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(kv.Value))));
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
        LoadError = null;
    }
}
