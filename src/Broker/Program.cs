using System.ComponentModel;
using System.Security.Principal;
using EasyMouse.Platform.Windows.Broker;

namespace EasyMouse.Broker;

/// <summary>
/// One executable, four jobs, chosen by the first argument:
/// <c>--service</c> (what the Service Control Manager runs, as SYSTEM), <c>--agent --session N</c> (what the service starts in the
/// signed-in user's session), <c>--enable</c> / <c>--disable</c> (what the App runs elevated, after the user agreed, to switch the
/// service on or off) and <c>--status</c>.
/// Exit codes: 0 ok, 1 failed, 2 wrong use, 3 not an administrator, 4 not installed, 13 not run from under Program Files.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--service"] or ["--agent", ..] && !RunsFromProtectedFolder())
        {
            Console.Error.WriteLine("The broker runs as SYSTEM and only from under Program Files, in a folder only administrators can write to.");
            return 13;
        }
        try
        {
            return args switch
            {
                ["--service"] => ServiceHost.Run(),
                ["--agent", "--session", var session] when uint.TryParse(session, out var id) => Agent.Run(id),
                ["--enable"] => Switch(enable: true),
                ["--disable"] => Switch(enable: false),
                ["--status"] => Status(),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool RunsFromProtectedFolder()
        => BrokerTrust.IsInstallLocationSafe(Environment.ProcessPath, [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]);

    private static int Switch(bool enable)
    {
        if (!IsAdministrator()) { Console.Error.WriteLine("Run this as an administrator."); return 3; }
        if (enable && !RunsFromProtectedFolder()) { Console.Error.WriteLine("Only a copy under Program Files can be switched on."); return 13; }
        try
        {
            if (enable) BrokerService.Enable(); else BrokerService.Disable();
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1060)
        {
            Console.Error.WriteLine("The Easy Mouse Broker service is not installed.");
            return 4;
        }
    }

    private static int Status()
    {
        Console.WriteLine(BrokerService.Query());
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Easy Mouse Broker. Started by the service manager; not meant to be run by hand.\nOptions: --status, --enable, --disable (administrator).");
        return 2;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
