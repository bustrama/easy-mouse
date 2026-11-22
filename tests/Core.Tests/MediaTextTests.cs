using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;
using System.Text.Json;

namespace EasyMouse.Core.Tests;

/// <summary>SR-03 (docs/security-review.md): now-playing text is written by any app or web page, so it is untrusted.</summary>
public class MediaTextTests
{
    private static async Task<JsonElement> ReadStateAsync(TestClient client)
    {
        while (true)
        {
            if (await client.ReceiveFrameAsync() is StateMessage s) return JsonDocument.Parse(s.Json).RootElement.Clone();
        }
    }

    [Theory]
    [InlineData("\ud800 broken")]            // lone high surrogate
    [InlineData("tail \udc00")]              // lone low surrogate
    public async Task Invalid_utf16_in_media_metadata_does_not_kill_the_session(string title)
    {
        await using var host = await TestHost.StartAsync();
        host.Media.SetSnapshot(new MediaSnapshot(new NowPlaying(PlaybackStatus.Playing, title, "artist", 0, 1000), null));
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);

        var state = await ReadStateAsync(client);

        Assert.Equal("playing", state.GetProperty("media").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Huge_media_metadata_is_truncated_so_the_state_frame_stays_small()
    {
        await using var host = await TestHost.StartAsync();
        host.Media.SetSnapshot(new MediaSnapshot(new NowPlaying(PlaybackStatus.Playing, new string('a', 100_000), new string('b', 100_000), 0, 1000), null));
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);

        var received = await client.ReceiveAsync();
        var data = Assert.IsType<ReceivedBinary>(received).Data;
        Assert.True(data.Length <= 4096, $"state frame is {data.Length} bytes");
    }
}

public class MediaTextSanitizerTests
{
    [Fact]
    public void Truncates_without_splitting_a_surrogate_pair()
    {
        var text = new string('a', ServerSession.MaxMediaTextChars - 1) + "😀";
        var clean = ServerSession.SanitizeText(text);
        Assert.Equal(ServerSession.MaxMediaTextChars - 1, clean.Length);
        Assert.False(char.IsHighSurrogate(clean[^1]));
    }

    [Fact]
    public void Keeps_valid_text_replaces_unpaired_surrogates_and_drops_controls()
    {
        Assert.Equal("שלום 😀", ServerSession.SanitizeText("שלום 😀"));
        Assert.Equal("a\uFFFDb\uFFFDc", ServerSession.SanitizeText("a\ud800b\udc00c"));
        Assert.Equal("ab", ServerSession.SanitizeText("a\u0000\r\nb"));
    }
}
