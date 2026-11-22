using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Fakes;
using EasyMouse.Remotes.Execution;
using Win = global::EasyMouse.Platform.Windows;

namespace EasyMouse.App.Platform;

/// <summary>
/// THE ONE PLACE where the PC-side implementations are chosen: SendInput, Windows media/volume and the lock/elevation
/// monitor from <c>src/Platform.Windows</c> in the real app; the recording fakes when <paramref name="real"/> is false
/// (tests and the screenshot harness, which must never move the real mouse, focus a window or start a program).
/// </summary>
public static class PlatformFactory
{
    /// <summary>
    /// Real: normal input goes through SendInput; only when the admin feature is on AND the foreground window or desktop is out of its
    /// reach does it go to the SYSTEM broker (see <see cref="Win.Broker.RoutingInputSink"/>). <c>status</c> is read lazily: it exists only after the core services do.
    /// </summary>
    public static (IInputSink Input, IMediaController Media) Create(bool real, IAdminControl admin, Func<HostStatus?> status)
    {
        if (!real) return (new RecordingInputSink(), new RecordingMediaController());
        var route = new Win.Broker.BrokerRoute(status, () => admin.Wanted);
        var client = new Win.Broker.BrokerInputSink();
        (admin as BrokerAdminControl)?.UseClient(client);
        return (new Win.Broker.RoutingInputSink(new Win.SendInputSink(), client, route.UseBroker), new Win.WindowsMediaController());
    }

    /// <summary>
    /// The remote-action executors for the pair <see cref="Create"/> returned. Real: Windows executors (key/text/mouse/media over the
    /// same input sink and media controller, SMTC, volume, focus, launch, openUrl, http). Not real: recorders, returned in <paramref name="recorded"/>.
    /// </summary>
    public static ActionExecutors CreateExecutors(IInputSink input, IMediaController media, bool real, out RecordingActionExecutors? recorded)
    {
        if (!real)
        {
            recorded = new RecordingActionExecutors();
            return recorded.Executors;
        }
        recorded = null;
        var windowsMedia = (Win.WindowsMediaController)media;
        return Win.Remotes.WindowsRemoteExecutors.Create(input, media, windowsMedia, windowsMedia);
    }

    /// <summary>Keeps <see cref="HostStatus"/> (locked / foreground elevated) current; null when not real.</summary>
    public static IDisposable? StartHostStatusMonitor(HostStatus status, bool real)
        => real ? new Win.HostStatusMonitor(status) : null;
}
