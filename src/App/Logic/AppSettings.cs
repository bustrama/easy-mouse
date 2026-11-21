using System.Text.Json;

namespace EasyMouse.App.Logic;

/// <summary>User settings, stored as JSON under <c>%APPDATA%\EasyMouse\settings.json</c>.</summary>
public sealed record AppSettings
{
    public const int DefaultPort = 8765;
    public const string DefaultHotkey = "Ctrl+Alt+Shift+K";

    public bool StartWithWindows { get; init; }
    /// <summary>When launched at sign-in, open in the tray without showing the QR window.</summary>
    public bool StartMinimised { get; init; } = true;
    /// <summary>"auto" (follow Windows), "en" or "he".</summary>
    public string Language { get; init; } = "auto";
    public int Port { get; init; } = DefaultPort;
    /// <summary>Network interface id; null or empty means automatic.</summary>
    public string? NetworkAdapterId { get; init; }
    /// <summary>Persisted only: the SYSTEM broker that acts on it is a later piece of work.</summary>
    public bool ControlAdminWindows { get; init; }
    public string Hotkey { get; init; } = DefaultHotkey;

    public static bool IsValidPort(int port) => port is >= 1024 and <= 65535;

    /// <summary>Replaces out-of-range values (hand-edited file) with defaults so the app always starts.</summary>
    public AppSettings Normalize() => this with
    {
        Port = IsValidPort(Port) ? Port : DefaultPort,
        Language = Language is "en" or "he" ? Language : "auto",
        Hotkey = Hotkey.TryParseHotkey(out _) ? Hotkey : DefaultHotkey,
        NetworkAdapterId = string.IsNullOrWhiteSpace(NetworkAdapterId) ? null : NetworkAdapterId,
    };
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// <summary>Set when the file existed but could not be read; the defaults were used instead.</summary>
    public string? LoadError { get; private set; }

    public AppSettings Load()
    {
        LoadError = null;
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(_path)) ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadError = ex.Message;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(settings.Normalize(), Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
