using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows;

namespace EasyMouse.Platform.Windows.Tests;

public class MediaLogicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlayingPositionAdvancesFromLastUpdate()
    {
        var t = new Timeline(10_000, 0, 200_000, T0, 1.0);
        Assert.Equal(15_000, TimelineMath.PositionMs(t, PlaybackStatus.Playing, T0.AddSeconds(5)));
    }

    [Fact]
    public void PausedPositionDoesNotAdvance()
    {
        var t = new Timeline(10_000, 0, 200_000, T0, 1.0);
        Assert.Equal(10_000, TimelineMath.PositionMs(t, PlaybackStatus.Paused, T0.AddSeconds(5)));
    }

    [Fact]
    public void PlaybackRateScalesElapsedTime()
    {
        var t = new Timeline(0, 0, 200_000, T0, 2.0);
        Assert.Equal(10_000, TimelineMath.PositionMs(t, PlaybackStatus.Playing, T0.AddSeconds(5)));
    }

    [Fact]
    public void PositionIsClampedToDuration()
    {
        var t = new Timeline(58_000, 0, 60_000, T0, 1.0);
        Assert.Equal(60_000, TimelineMath.PositionMs(t, PlaybackStatus.Playing, T0.AddMinutes(5)));
    }

    [Fact]
    public void StartTimeOffsetIsRemoved()
    {
        var t = new Timeline(1_010_000, 1_000_000, 1_100_000, T0, 1.0);
        Assert.Equal(10_000, TimelineMath.PositionMs(t, PlaybackStatus.Paused, T0));
        Assert.Equal(100_000, TimelineMath.DurationMs(t));
    }

    [Fact]
    public void ClockSkewNeverMovesBackwards()
    {
        var t = new Timeline(10_000, 0, 200_000, T0, 1.0);
        Assert.Equal(10_000, TimelineMath.PositionMs(t, PlaybackStatus.Playing, T0.AddSeconds(-3)));
    }

    [Fact]
    public void AllZeroTimelineMeansNoPositionAndNoDuration()
    {
        var t = new Timeline(0, 0, 0, default, 1.0);
        Assert.Null(TimelineMath.PositionMs(t, PlaybackStatus.Playing, T0));
        Assert.Null(TimelineMath.DurationMs(t));
    }

    [Fact]
    public void LiveStreamHasPositionButNoDuration()
    {
        var t = new Timeline(5_000, 0, 0, T0, 1.0);
        Assert.Equal(7_000, TimelineMath.PositionMs(t, PlaybackStatus.Playing, T0.AddSeconds(2)));
        Assert.Null(TimelineMath.DurationMs(t));
    }

    [Fact]
    public void NonPositiveRateFallsBackToRealTime()
    {
        var t = new Timeline(0, 0, 200_000, T0, 0);
        Assert.Equal(1_000, TimelineMath.PositionMs(t, PlaybackStatus.Playing, T0.AddSeconds(1)));
    }

    private static SessionCandidate C(string id, PlaybackStatus s, bool cur) => new(id, s, cur);
    private static int Pick(params SessionCandidate[] c) => SessionSelector.Select(c);

    [Fact]
    public void NoSessionsSelectsNothing() => Assert.Equal(-1, Pick());

    [Fact]
    public void ClosedSessionsAreIgnored() => Assert.Equal(-1, Pick(C("a", PlaybackStatus.None, true)));

    [Fact]
    public void PlayingBeatsCurrentPaused()
        => Assert.Equal(1, Pick(C("a", PlaybackStatus.Paused, true), C("b", PlaybackStatus.Playing, false)));

    [Fact]
    public void CurrentWinsAmongPlaying()
        => Assert.Equal(1, Pick(C("a", PlaybackStatus.Playing, false), C("b", PlaybackStatus.Playing, true)));

    [Fact]
    public void FirstPlayingWinsWhenNoneIsCurrent()
        => Assert.Equal(0, Pick(C("a", PlaybackStatus.Playing, false), C("b", PlaybackStatus.Playing, false)));

    [Fact]
    public void CurrentWinsWhenNothingPlays()
        => Assert.Equal(1, Pick(C("a", PlaybackStatus.Paused, false), C("b", PlaybackStatus.Stopped, true)));

    [Fact]
    public void PausedBeatsStoppedWhenNoneIsCurrent()
        => Assert.Equal(1, Pick(C("a", PlaybackStatus.Stopped, false), C("b", PlaybackStatus.Paused, false)));

    [Theory]
    [InlineData(MediaCommand.PlayPause, (ushort)0xB3)]
    [InlineData(MediaCommand.Next, (ushort)0xB0)]
    [InlineData(MediaCommand.Previous, (ushort)0xB1)]
    [InlineData(MediaCommand.Stop, (ushort)0xB2)]
    [InlineData(MediaCommand.VolumeUp, (ushort)0xAF)]
    [InlineData(MediaCommand.VolumeDown, (ushort)0xAE)]
    [InlineData(MediaCommand.Mute, (ushort)0xAD)]
    public void MediaCommandVirtualKeys(MediaCommand c, ushort vk) => Assert.Equal(vk, MediaKeys.For(c));

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    public void ElevationBlocksOnlyWhenWeAreLessPrivileged(bool self, int fg, bool blocks)
        => Assert.Equal(blocks, ElevationPolicy.BlocksInput(self, (ProcessElevation)fg));
}
