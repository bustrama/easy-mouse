using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Fakes;
using Win = global::EasyMouse.Platform.Windows;

namespace EasyMouse.App.Platform;

/// <summary>
/// THE ONE PLACE where the PC-side implementations are chosen: SendInput, Windows media/volume and the lock/elevation
/// monitor from <c>src/Platform.Windows</c> in the real app; the recording fakes when <paramref name="real"/> is false
/// (tests and the screenshot harness, which must never move the real mouse).
/// </summary>
public static class PlatformFactory
{
    public static (IInputSink Input, IMediaController Media) Create(bool real)
        => real ? (new Win.SendInputSink(), new Win.WindowsMediaController()) : (new RecordingInputSink(), new RecordingMediaController());

    /// <summary>Keeps <see cref="HostStatus"/> (locked / foreground elevated) current; null when not real.</summary>
    public static IDisposable? StartHostStatusMonitor(HostStatus status, bool real)
        => real ? new Win.HostStatusMonitor(status) : null;
}
