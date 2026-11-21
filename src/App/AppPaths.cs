namespace EasyMouse.App;

/// <summary>Where the app keeps its files: <c>%APPDATA%\EasyMouse</c> (override with <c>EASYMOUSE_DATA_DIR</c>, used by tests).</summary>
public sealed record AppPaths(string Root)
{
    public string Devices => Path.Combine(Root, "devices.json");
    public string Settings => Path.Combine(Root, "settings.json");

    public static AppPaths Default()
    {
        var overrideDir = Environment.GetEnvironmentVariable("EASYMOUSE_DATA_DIR");
        return new AppPaths(!string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyMouse"));
    }
}
