namespace EasyMouse.App;

/// <summary>Where the app keeps its files: <c>%APPDATA%\EasyMouse</c> (override with <c>EASYMOUSE_DATA_DIR</c>, used by tests).</summary>
public sealed record AppPaths(string Root)
{
    public string Devices => Path.Combine(Root, "devices.json");
    public string Settings => Path.Combine(Root, "settings.json");
    /// <summary>DPAPI-protected values for <c>${secret.name}</c> in remotes. Never sent to a phone.</summary>
    public string Secrets => Path.Combine(Root, "remote-secrets.json");
    /// <summary>The user's own and imported remotes (<c>%APPDATA%\EasyMouse\remotes</c>).</summary>
    public string UserRemotes => Path.Combine(Root, "remotes");

    /// <summary>The remotes shipped with the app (<c>remotes\builtin</c> next to the exe); tests point this elsewhere.</summary>
    public string BuiltInRemotes { get; init; } = Path.Combine(AppContext.BaseDirectory, "remotes", "builtin");

    public static AppPaths Default()
    {
        var overrideDir = Environment.GetEnvironmentVariable("EASYMOUSE_DATA_DIR");
        return new AppPaths(!string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyMouse"));
    }
}
