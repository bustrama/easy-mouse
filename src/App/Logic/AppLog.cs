using System.Globalization;

namespace EasyMouse.App.Logic;

/// <summary>
/// A tiny diagnostic log (<c>easymouse.log</c> in the data folder) for failures that must not crash the app or go unnoticed.
/// It never throws, keeps only about 256 KB (the older half is dropped) and never receives secrets: only messages and
/// exception text. Does nothing until <see cref="Initialize"/> is called, so tests leave no file behind.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string path) { lock (Gate) _path = path; }

    public static void Error(string what, Exception? ex = null)
    {
        System.Diagnostics.Debug.WriteLine($"{what} {ex}");
        string? path;
        lock (Gate) path = _path;
        if (path is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    var keep = File.ReadAllBytes(path).AsSpan((int)(MaxBytes / 2)).ToArray();
                    File.WriteAllBytes(path, keep);
                }
                File.AppendAllText(path, $"{DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture)} {what}{(ex is null ? "" : " | " + ex)}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* logging must never become the next failure */ }
    }
}
