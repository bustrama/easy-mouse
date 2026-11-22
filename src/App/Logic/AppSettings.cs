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
    /// <summary>Reserved for a later version (SYSTEM broker). Nothing reads it, and the Settings window shows it disabled.</summary>
    public bool ControlAdminWindows { get; init; }
    public string Hotkey { get; init; } = DefaultHotkey;
    /// <summary>Ask GitHub for a newer release at most once a day. Never downloads or installs without the user's confirmation.</summary>
    public bool CheckForUpdates { get; init; } = true;
    public bool IncludePrereleases { get; init; }
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

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

    /// <summary>Why the last <see cref="Save"/> failed; null after a successful one.</summary>
    public string? SaveError { get; private set; }

    /// <summary>Writes the file atomically (own temp file, then replace). Returns false, with <see cref="SaveError"/> set, when the disk refuses; the caller keeps the settings in memory.</summary>
    public bool Save(AppSettings settings)
    {
        var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(settings.Normalize(), Json));
            File.Move(tmp, _path, overwrite: true);
            SaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = ex.Message;
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }
}
