using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Abstractions;

public enum PlaybackStatus { None, Playing, Paused, Stopped }

public sealed record NowPlaying(PlaybackStatus Status, string? Title, string? Artist, long? PositionMs, long? DurationMs);

public sealed record VolumeState(int Level, bool Muted);

/// <summary>Cover image of the current track. <c>Id</c> changes whenever the image changes (lowercase hex/base36, 1-32 chars).</summary>
public sealed record Artwork(string Id, ArtworkType Type, byte[] Data);

public sealed record MediaSnapshot(NowPlaying? NowPlaying, VolumeState? Volume, Artwork? Artwork = null);

/// <summary>Media keys, volume and now-playing. The Windows implementation (SMTC, Core Audio) lives in the App.</summary>
public interface IMediaController
{
    void Execute(MediaCommand command);
    MediaSnapshot GetSnapshot();
    /// <summary>Raised (on any thread) when now-playing or volume changed.</summary>
    event EventHandler? Changed;
}
