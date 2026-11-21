using System.Net;

namespace EasyMouse.App.Logic;

/// <summary>
/// "Your phone cannot reach this PC" heuristic. Client isolation (guest Wi-Fi) cannot be detected from the PC,
/// so the only signal is silence: the QR window has been open for <see cref="Threshold"/> and no request ever
/// arrived from another machine on the network.
/// </summary>
public sealed class HelpAdvisor
{
    private readonly TimeProvider _time;
    private DateTimeOffset? _since;
    private bool _lanRequestSeen;

    public HelpAdvisor(TimeProvider time, TimeSpan? threshold = null)
    {
        _time = time;
        Threshold = threshold ?? TimeSpan.FromSeconds(90);
    }

    public TimeSpan Threshold { get; }

    /// <summary>The QR window became visible.</summary>
    public void WindowOpened()
    {
        _since = _time.GetUtcNow();
        _lanRequestSeen = false;
    }

    public void WindowClosed() => _since = null;

    /// <summary>Any request from a non-loopback peer proves the phone can reach the PC.</summary>
    public void RequestSeen(IPAddress? peer)
    {
        if (peer is null || IPAddress.IsLoopback(peer)) return;
        _lanRequestSeen = true;
    }

    public bool LanRequestSeen => _lanRequestSeen;

    public bool ShouldShowHelp => _since is { } since && !_lanRequestSeen && _time.GetUtcNow() - since >= Threshold;
}
