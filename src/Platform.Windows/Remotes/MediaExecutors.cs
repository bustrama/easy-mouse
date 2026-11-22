using EasyMouse.Core.Abstractions;
using EasyMouse.Remotes.Execution;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Platform.Windows.Remotes;

public enum SmtcTransport { Toggle, Play, Pause, Next, Previous, Stop, SeekForward, SeekBack }

/// <summary>Controls the Windows "current media session" (SMTC), the one the media overlay shows.</summary>
public interface ISmtcControl
{
    /// <summary>True when the media app accepted the command; false when there is no session or the app refused it.</summary>
    Task<bool> SendAsync(SmtcTransport command, int seconds, CancellationToken cancellationToken);
}

/// <summary>Master volume of the default playback device.</summary>
public interface IVolumeControl
{
    VolumeState? GetVolume();
    void SetVolume(int percent);
    void SetMuted(bool muted);
}

public sealed class SmtcExecutor(ISmtcControl smtc) : IActionExecutor<SmtcAction>
{
    public async Task ExecuteAsync(SmtcAction action, ActionContext context, CancellationToken cancellationToken)
    {
        var cmd = action.Cmd switch
        {
            SmtcCmd.Toggle => SmtcTransport.Toggle,
            SmtcCmd.Play => SmtcTransport.Play,
            SmtcCmd.Pause => SmtcTransport.Pause,
            SmtcCmd.Next => SmtcTransport.Next,
            SmtcCmd.Prev => SmtcTransport.Previous,
            SmtcCmd.Stop => SmtcTransport.Stop,
            SmtcCmd.SeekForward => SmtcTransport.SeekForward,
            _ => SmtcTransport.SeekBack,
        };
        if (!await smtc.SendAsync(cmd, action.Seconds, cancellationToken).ConfigureAwait(false))
            throw new ActionFailedException("failed", "no media app accepted the command");
    }
}

public sealed class VolumeExecutor(IVolumeControl volume) : IActionExecutor<VolumeAction>
{
    public Task ExecuteAsync(VolumeAction action, ActionContext context, CancellationToken cancellationToken)
    {
        if (action.Scope != VolumeScope.Master) throw new ActionFailedException("unsupported", "per-app volume is not supported yet");
        switch (action.Cmd)
        {
            case VolumeCmd.Set:
            {
                var level = action.Level ?? (context.Value is { } v ? (int)Math.Round(v) : (int?)null)
                    ?? throw new ActionFailedException("unsupported", "no volume level");
                volume.SetVolume(Math.Clamp(level, 0, 100));
                break;
            }
            case VolumeCmd.Up or VolumeCmd.Down:
            {
                var current = volume.GetVolume() ?? throw new ActionFailedException("failed", "volume is not available");
                var delta = Math.Max(1, action.Step) * (action.Cmd == VolumeCmd.Up ? 1 : -1);
                volume.SetVolume(Math.Clamp(current.Level + delta, 0, 100));
                break;
            }
            case VolumeCmd.Mute: volume.SetMuted(true); break;
            case VolumeCmd.Unmute: volume.SetMuted(false); break;
            default:
            {
                var current = volume.GetVolume() ?? throw new ActionFailedException("failed", "volume is not available");
                volume.SetMuted(!current.Muted);
                break;
            }
        }
        return Task.CompletedTask;
    }
}
