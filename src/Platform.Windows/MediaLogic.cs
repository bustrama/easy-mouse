using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Platform.Windows;

/// <summary>A player timeline as SMTC reports it, in milliseconds. Position is a snapshot taken at <see cref="LastUpdated"/>.</summary>
internal readonly record struct Timeline(long PositionMs, long StartMs, long EndMs, DateTimeOffset LastUpdated, double Rate);

/// <summary>Pure timeline maths (no WinRT), so it is unit tested.</summary>
internal static class TimelineMath
{
    private static readonly DateTimeOffset s_unsetBefore = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Track length, or null for live streams and players that report no end time.</summary>
    public static long? DurationMs(Timeline t) => t.EndMs > t.StartMs ? t.EndMs - t.StartMs : null;

    /// <summary>
    /// Where playback is now. SMTC only reports a snapshot, so while playing we add the time elapsed since
    /// <see cref="Timeline.LastUpdated"/> (scaled by the playback rate). Null when the player gave no timeline at all.
    /// </summary>
    public static long? PositionMs(Timeline t, PlaybackStatus status, DateTimeOffset now)
    {
        var stamped = t.LastUpdated >= s_unsetBefore;
        var duration = DurationMs(t);
        if (!stamped && duration is null) return null; // browsers and some apps report an all-zero timeline

        var position = t.PositionMs - t.StartMs;
        if (status == PlaybackStatus.Playing && stamped)
        {
            var elapsed = (now - t.LastUpdated).TotalMilliseconds;
            if (elapsed > 0)
            {
                var rate = t.Rate > 0 ? t.Rate : 1.0;
                position += (long)(elapsed * rate);
            }
        }
        if (duration is { } d && position > d) position = d;
        return Math.Max(0, position);
    }
}

/// <summary>One SMTC session as far as selection is concerned.</summary>
internal readonly record struct SessionCandidate(string Id, PlaybackStatus Status, bool IsCurrent);

internal static class SessionSelector
{
    /// <summary>
    /// The session worth showing: a playing one (the OS "current" session wins ties, otherwise the first),
    /// else the current session, else the first paused or stopped one. Closed sessions (status None) never count.
    /// Returns the index into <paramref name="candidates"/>, or -1.
    /// </summary>
    public static int Select(IReadOnlyList<SessionCandidate> candidates)
    {
        var best = -1;
        var bestRank = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (c.Status == PlaybackStatus.None) continue;
            var rank = (c.Status == PlaybackStatus.Playing ? 4 : 0) + (c.IsCurrent ? 2 : 0) + (c.Status == PlaybackStatus.Paused ? 1 : 0);
            if (rank > bestRank) { best = i; bestRank = rank; }
        }
        return best;
    }
}

internal static class MediaKeys
{
    public const ushort VK_VOLUME_MUTE = 0xAD;
    public const ushort VK_VOLUME_DOWN = 0xAE;
    public const ushort VK_VOLUME_UP = 0xAF;
    public const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    public const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    public const ushort VK_MEDIA_STOP = 0xB2;
    public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static ushort For(MediaCommand command) => command switch
    {
        MediaCommand.PlayPause => VK_MEDIA_PLAY_PAUSE,
        MediaCommand.Next => VK_MEDIA_NEXT_TRACK,
        MediaCommand.Previous => VK_MEDIA_PREV_TRACK,
        MediaCommand.Stop => VK_MEDIA_STOP,
        MediaCommand.VolumeUp => VK_VOLUME_UP,
        MediaCommand.VolumeDown => VK_VOLUME_DOWN,
        MediaCommand.Mute => VK_VOLUME_MUTE,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
}

internal enum ProcessElevation { NotElevated, Elevated, Inaccessible }

internal static class ElevationPolicy
{
    /// <summary>
    /// UIPI drops our input when the foreground process is more privileged than we are. A process we cannot even
    /// query (access denied on its token) is more privileged than us, so it counts as blocking.
    /// </summary>
    public static bool BlocksInput(bool selfElevated, ProcessElevation foreground)
        => !selfElevated && foreground != ProcessElevation.NotElevated;
}
