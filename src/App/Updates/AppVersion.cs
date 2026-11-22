using System.Reflection;

namespace EasyMouse.App.Updates;

/// <summary>The running app's version, stamped from the release tag at build time (<c>-p:Version=X.Y.Z</c>).</summary>
public static class AppVersion
{
    public static SemVer Current { get; } = Read();

    private static SemVer Read()
    {
        var info = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVer.TryParse(info, out var v)) return v;
        var asm = typeof(AppVersion).Assembly.GetName().Version;
        return asm is null ? SemVer.Parse("0.0.0") : SemVer.Parse($"{asm.Major}.{asm.Minor}.{Math.Max(asm.Build, 0)}");
    }
}
