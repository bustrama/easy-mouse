using System.Runtime.InteropServices;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows.Audio;
using EasyMouse.Platform.Windows.Native;
using Windows.Foundation;
using Windows.Media.Control;

namespace EasyMouse.Platform.Windows;

/// <summary>
/// Media keys, master volume (Core Audio) and now-playing (SMTC). Commands go out as media virtual keys, so the
/// Windows volume OSD appears and the focused media app reacts exactly as it does for a keyboard.
/// Now-playing is cached and refreshed from SMTC events; <see cref="GetSnapshot"/> never blocks and extrapolates the position.
/// </summary>
public sealed class WindowsMediaController : IMediaController, IDisposable
{
    private readonly MasterVolume? _volume;
    private readonly object _gate = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _tracked = [];
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onProps;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlayback;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _onTimeline;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Playing? _playing;
    private int _refreshState; // 0 idle, 1 running, 2 running with another refresh requested
    private volatile bool _disposed;

    private sealed record Playing(PlaybackStatus Status, string? Title, string? Artist, Timeline? Timeline, Artwork? Artwork);

    private const int ArtworkAttempts = 3; // a player may publish the thumbnail a moment after the title
    private string? _artKey;               // app + title + artist the cached artwork belongs to (refresh loop only)
    private Artwork? _artCached;
    private int _artTries;

    public event EventHandler? Changed;

    /// <summary>Raised when background SMTC refreshing failed in an unexpected way (it keeps trying on the next event).</summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>Completes when the SMTC session manager is ready. Now-playing is empty until then.</summary>
    public Task Initialization { get; }

    public WindowsMediaController()
    {
        _onProps = (_, _) => RequestRefresh();
        _onPlayback = (_, _) => RequestRefresh();
        _onTimeline = (_, _) => RequestRefresh();
        _volume = new MasterVolume();
        _volume.Changed += () => Changed?.Invoke(this, EventArgs.Empty);
        Initialization = Task.Run(InitializeSmtcAsync);
    }

    public void Execute(MediaCommand command)
    {
        var vk = MediaKeys.For(command);
        NativeInput.Send([InputBuilder.Key(vk, true), InputBuilder.Key(vk, false)]);
    }

    public MediaSnapshot GetSnapshot()
    {
        Playing? p;
        lock (_gate) p = _playing;
        NowPlaying? np = null;
        if (p is not null)
        {
            var now = DateTimeOffset.UtcNow;
            np = new NowPlaying(
                p.Status, p.Title, p.Artist,
                p.Timeline is { } t ? TimelineMath.PositionMs(t, p.Status, now) : null,
                p.Timeline is { } t2 ? TimelineMath.DurationMs(t2) : null);
        }
        return new MediaSnapshot(np, _volume?.Read(), p?.Artwork);
    }

    /// <summary>Sets the master volume (0-100) of the default playback device.</summary>
    public void SetVolume(int percent) => _volume!.SetLevel(percent);

    public void SetMuted(bool muted) => _volume!.SetMuted(muted);

    private async Task InitializeSmtcAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (_disposed) return;
        _manager = manager;
        manager.SessionsChanged += (_, _) => RequestRefresh();
        manager.CurrentSessionChanged += (_, _) => RequestRefresh();
        await RefreshOnceAsync();
    }

    private void RequestRefresh()
    {
        while (!_disposed)
        {
            switch (Volatile.Read(ref _refreshState))
            {
                case 0:
                    if (Interlocked.CompareExchange(ref _refreshState, 1, 0) == 0)
                    {
                        _ = Task.Run(RefreshLoopAsync);
                        return;
                    }
                    break;
                case 1:
                    if (Interlocked.CompareExchange(ref _refreshState, 2, 1) == 1) return;
                    break;
                default:
                    return;
            }
        }
    }

    private async Task RefreshLoopAsync()
    {
        do
        {
            Volatile.Write(ref _refreshState, 1);
            try { await RefreshOnceAsync(); }
            catch (Exception ex) when (!_disposed) { Faulted?.Invoke(this, ex); }
        }
        while (Interlocked.CompareExchange(ref _refreshState, 0, 1) != 1);
    }

    private async Task RefreshOnceAsync()
    {
        var manager = _manager;
        if (manager is null || _disposed) return;

        var sessions = manager.GetSessions();
        SyncSubscriptions(sessions);
        var currentId = Safe(() => manager.GetCurrentSession()?.SourceAppUserModelId);

        var candidates = new List<SessionCandidate>(sessions.Count);
        var live = new List<GlobalSystemMediaTransportControlsSession>(sessions.Count);
        foreach (var s in sessions)
        {
            // A session can close between GetSessions and these reads; it then throws and is simply skipped.
            var info = Safe(() => (s.SourceAppUserModelId, s.GetPlaybackInfo().PlaybackStatus));
            if (info.SourceAppUserModelId is null) continue;
            live.Add(s);
            candidates.Add(new SessionCandidate(info.SourceAppUserModelId, MapStatus(info.PlaybackStatus), info.SourceAppUserModelId == currentId));
        }

        Playing? result = null;
        var idx = SessionSelector.Select(candidates);
        if (idx >= 0)
        {
            var session = live[idx];
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                var rate = session.GetPlaybackInfo().PlaybackRate ?? 1.0;
                var title = Blank(props?.Title);
                var artist = Blank(props?.Artist);
                var artwork = await GetArtworkAsync($"{candidates[idx].Id}\n{title}\n{artist}", props);
                result = new Playing(
                    candidates[idx].Status,
                    title, artist,
                    timeline is null ? null : new Timeline(
                        (long)timeline.Position.TotalMilliseconds, (long)timeline.StartTime.TotalMilliseconds,
                        (long)timeline.EndTime.TotalMilliseconds, timeline.LastUpdatedTime, rate),
                    artwork);
            }
            catch (COMException)
            {
                result = null; // the session went away mid-read; the SessionsChanged event will trigger a fresh pass
            }
        }

        lock (_gate) _playing = result;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the thumbnail once per track (retrying a few times while it is still missing); refresh events then reuse the cached JPEG.</summary>
    private async Task<Artwork?> GetArtworkAsync(string key, GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (key != _artKey)
        {
            _artKey = key;
            _artCached = null;
            _artTries = 0;
        }
        if (_artCached is null && _artTries < ArtworkAttempts && props is not null)
        {
            _artTries++;
            _artCached = await ArtworkReader.ReadAsync(Safe(() => props.Thumbnail));
        }
        return _artCached;
    }

    private void SyncSubscriptions(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        lock (_gate)
        {
            foreach (var old in _tracked.Where(t => !sessions.Contains(t)).ToList())
            {
                Detach(old);
                _tracked.Remove(old);
            }
            foreach (var s in sessions.Where(s => !_tracked.Contains(s)))
            {
                s.MediaPropertiesChanged += _onProps;
                s.PlaybackInfoChanged += _onPlayback;
                s.TimelinePropertiesChanged += _onTimeline;
                _tracked.Add(s);
            }
        }
    }

    private void Detach(GlobalSystemMediaTransportControlsSession s)
    {
        s.MediaPropertiesChanged -= _onProps;
        s.PlaybackInfoChanged -= _onPlayback;
        s.TimelinePropertiesChanged -= _onTimeline;
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (COMException) { return default; }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static PlaybackStatus MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus s) => s switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackStatus.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackStatus.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackStatus.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackStatus.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackStatus.Stopped,
        _ => PlaybackStatus.None, // Closed
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var s in _tracked) Detach(s);
            _tracked.Clear();
        }
        _volume?.Dispose();
    }
}
