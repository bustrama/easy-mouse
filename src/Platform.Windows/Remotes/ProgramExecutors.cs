using System.ComponentModel;
using System.Diagnostics;
using EasyMouse.Remotes.Execution;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Platform.Windows.Remotes;

/// <summary>Starts a process. Tests record the request instead of starting anything.</summary>
public interface IProcessStarter
{
    void Start(ProcessStartInfo info);
}

public sealed class SystemProcessStarter : IProcessStarter
{
    public void Start(ProcessStartInfo info) => Process.Start(info)?.Dispose();
}

/// <summary>
/// Starts the program the user picked for the slot, with the remote's fixed arguments. No shell: the file must be an
/// existing .exe at a full path and is started with UseShellExecute=false, one argument per list entry (so nothing is re-parsed).
/// </summary>
public sealed class LaunchExecutor(IProcessStarter starter) : IActionExecutor<ResolvedLaunch>
{
    public Task ExecuteAsync(ResolvedLaunch action, ActionContext context, CancellationToken cancellationToken)
    {
        var exe = action.ExePath;
        if (!Path.IsPathFullyQualified(exe) || exe.Contains('\0') || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ActionFailedException("denied", "not a program file");
        if (!File.Exists(exe)) throw new ActionFailedException("failed", "the chosen program no longer exists");

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            CreateNoWindow = false,
        };
        foreach (var a in action.Args) info.ArgumentList.Add(a);
        try { starter.Start(info); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new ActionFailedException("failed", "could not start the program");
        }
        return Task.CompletedTask;
    }
}

/// <summary>Opens an http or https address in the default browser; any other scheme is refused.</summary>
public sealed class OpenUrlExecutor(IProcessStarter starter) : IActionExecutor<OpenUrlAction>
{
    public Task ExecuteAsync(OpenUrlAction action, ActionContext context, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(action.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || action.Url.Any(char.IsControl) || string.IsNullOrEmpty(uri.Host))
            throw new ActionFailedException("denied", "only http and https addresses can be opened");
        try { starter.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new ActionFailedException("failed", "could not open the address");
        }
        return Task.CompletedTask;
    }
}

/// <summary>The one place the Windows executors are put together.</summary>
public static class WindowsRemoteExecutors
{
    public static ActionExecutors Create(Core.Abstractions.IInputSink input, Core.Abstractions.IMediaController media,
        ISmtcControl smtc, IVolumeControl volume, IWindowSystem? windows = null, IProcessStarter? starter = null)
    {
        starter ??= new SystemProcessStarter();
        return new ActionExecutors
        {
            Key = new InputSinkKeyExecutor(input),
            Text = new InputSinkTextExecutor(input),
            Mouse = new InputSinkMouseExecutor(input),
            Media = new MediaControllerExecutor(media),
            Smtc = new SmtcExecutor(smtc),
            Volume = new VolumeExecutor(volume),
            Focus = new FocusExecutor(windows ?? new Win32WindowSystem()),
            Launch = new LaunchExecutor(starter),
            OpenUrl = new OpenUrlExecutor(starter),
            Http = new HttpClientExecutor(),
        };
    }
}
