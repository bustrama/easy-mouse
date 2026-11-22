namespace EasyMouse.App.Logic;

/// <summary>What goes into the HKCU Run key. The <c>--autostart</c> flag lets the app tell a sign-in launch from a double-click.</summary>
public static class AutostartCommand
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "EasyMouse";
    public const string Flag = "--autostart";

    public static string Build(string exePath) => $"\"{exePath}\" {Flag}";

    public static bool IsAutostart(IEnumerable<string> args) => args.Contains(Flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the window should open at launch: always on a manual launch; on a sign-in launch only if "start minimised" is off.</summary>
    public static bool ShouldShowWindowAtLaunch(bool autostart, bool startMinimised) => !(autostart && startMinimised);
}
