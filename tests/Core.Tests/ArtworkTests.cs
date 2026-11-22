using System.Text.Json;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.Core.Tests;

public class ArtworkCodecTests
{
    private static byte[] Image(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 31)).ToArray();

    [Theory]
    [InlineData("a1", ArtworkType.Jpeg, 1)]
    [InlineData("0123456789abcdef0123456789abcdef", ArtworkType.Png, 5000)]
    [InlineData("x", ArtworkType.Jpeg, FrameCodec.MaxArtworkBytes)]
    public void Roundtrip_server_to_client(string id, ArtworkType type, int size)
    {
        var message = new ArtMessage(id, type, Image(size));
        Assert.Equal(FrameError.None, FrameCodec.TryParse(FrameCodec.Encode(message), FrameDirection.ServerToClient, out var parsed));
        var art = Assert.IsType<ArtMessage>(parsed);
        Assert.Equal(id, art.Id);
        Assert.Equal(type, art.Type);
        Assert.Equal(message.Data, art.Data);
    }

    [Fact]
    public void Art_is_server_to_client_only()
    {
        var bytes = FrameCodec.Encode(new ArtMessage("a", ArtworkType.Jpeg, Image(10)));
        Assert.Equal(FrameError.UnknownOp, FrameCodec.TryParse(bytes, FrameDirection.ClientToServer, out _));
    }

    [Fact]
    public void Size_limit_applies_when_encoding_and_parsing()
    {
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(new ArtMessage("a", ArtworkType.Jpeg, Image(FrameCodec.MaxArtworkBytes + 1))));
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(new ArtMessage("a", ArtworkType.Jpeg, [])));
        var tooBig = new byte[3 + 1 + FrameCodec.MaxArtworkBytes + 1];
        tooBig[0] = (byte)Op.Art; tooBig[1] = 1; tooBig[2] = (byte)'a'; tooBig[3] = 0;
        Assert.Equal(FrameError.TooLong, FrameCodec.TryParse(tooBig, FrameDirection.ServerToClient, out _));
    }

    public static TheoryData<byte[]> Malformed => new()
    {
        new byte[] { 0x12 },
        new byte[] { 0x12, 0, 0, 1 },
        new byte[] { 0x12, 33, 0x61, 0, 1 },
        new byte[] { 0x12, 1, (byte)'A', 0, 1 },
        new byte[] { 0x12, 1, (byte)'a', 2, 1 },
        new byte[] { 0x12, 1, (byte)'a', 0 },
        new byte[] { 0x12, 5, (byte)'a', 0, 1 },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_art_is_rejected(byte[] frame)
    {
        Assert.NotEqual(FrameError.None, FrameCodec.TryParse(frame, FrameDirection.ServerToClient, out var m));
        Assert.Null(m);
    }

    [Fact]
    public void Client_to_server_frames_keep_the_4_KiB_limit()
        => Assert.Equal(4096, new SessionLimits().MaxFrameBytes);
}

public class ArtworkEndToEndTests
{
    private static Artwork Art(string id, int size = 3000) => new(id, ArtworkType.Jpeg, Enumerable.Range(0, size).Select(i => (byte)i).ToArray());

    private static MediaSnapshot Snap(Artwork? art, long position = 1000)
        => new(new NowPlaying(PlaybackStatus.Playing, "Song", "Artist", position, 200000), new VolumeState(30, false), art);

    private static JsonElement Json(Message m) => JsonDocument.Parse(Assert.IsType<StateMessage>(m).Json).RootElement.Clone();

    /// <summary>Ping and read until the reply: returns everything the server sent before it.</summary>
    private static async Task<List<Message>> DrainAsync(TestClient c)
    {
        await c.SendFrameAsync(new PingMessage());
        var all = new List<Message>();
        while (true)
        {
            var m = await c.ReceiveFrameAsync();
            if (m is PingMessage) return all;
            all.Add(m);
        }
    }

    private static async Task<JsonElement> NextStateAsync(TestClient c)
    {
        while (true)
        {
            if (await c.ReceiveFrameAsync() is StateMessage s) return JsonDocument.Parse(s.Json).RootElement.Clone();
        }
    }

    [Fact]
    public async Task Art_is_sent_once_per_id_after_the_state_and_not_on_every_tick()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);

        var first = await DrainAsync(client);
        Assert.Equal(JsonValueKind.Null, Json(Assert.Single(first)).GetProperty("artId").ValueKind);

        var cover = Art("aaaa");
        host.Media.SetSnapshot(Snap(cover));
        Assert.Equal("aaaa", (await NextStateAsync(client)).GetProperty("artId").GetString());
        var art = Assert.IsType<ArtMessage>(await client.ReceiveFrameAsync());
        Assert.Equal("aaaa", art.Id);
        Assert.Equal(cover.Data, art.Data);

        // position ticks with the same artwork id: state only, no second art frame
        host.Media.SetSnapshot(Snap(cover, 2000));
        await NextStateAsync(client);
        host.Media.SetSnapshot(Snap(cover, 3000));
        await NextStateAsync(client);
        Assert.Empty(await DrainAsync(client));

        // a new image: one more art frame
        host.Media.SetSnapshot(Snap(Art("bbbb", 100)));
        Assert.Equal("bbbb", (await NextStateAsync(client)).GetProperty("artId").GetString());
        Assert.Equal("bbbb", Assert.IsType<ArtMessage>(await client.ReceiveFrameAsync()).Id);
        Assert.Empty(await DrainAsync(client));

        // artwork disappears: artId null, nothing sent; the same image returning is sent again
        host.Media.SetSnapshot(Snap(null));
        Assert.Equal(JsonValueKind.Null, (await NextStateAsync(client)).GetProperty("artId").ValueKind);
        Assert.Empty(await DrainAsync(client));
        host.Media.SetSnapshot(Snap(Art("bbbb", 100)));
        await NextStateAsync(client);
        Assert.Equal("bbbb", Assert.IsType<ArtMessage>(await client.ReceiveFrameAsync()).Id);
    }

    [Fact]
    public async Task A_device_that_connects_later_gets_the_current_art_after_ready()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        host.Media.SetSnapshot(Snap(Art("cccc")));
        await using var client = await host.ConnectDeviceAsync(device);
        Assert.Equal("cccc", (await NextStateAsync(client)).GetProperty("artId").GetString());
        Assert.Equal("cccc", Assert.IsType<ArtMessage>(await client.ReceiveFrameAsync()).Id);
    }

    [Fact]
    public async Task Oversized_artwork_is_dropped_and_artId_stays_null()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);
        await DrainAsync(client);
        host.Media.SetSnapshot(Snap(Art("big", FrameCodec.MaxArtworkBytes + 1)));
        var state = await NextStateAsync(client);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("artId").ValueKind);
        Assert.Equal("Song", state.GetProperty("media").GetProperty("title").GetString()); // now-playing is unaffected
        Assert.Empty(await DrainAsync(client));
    }

    [Fact]
    public async Task A_phone_cannot_send_art_or_frames_over_4_KiB()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using (var client = await host.ConnectDeviceAsync(device))
        {
            await DrainAsync(client);
            await client.SendFrameAsync(new ArtMessage("a", ArtworkType.Jpeg, [1, 2, 3]));
            Assert.Equal(4400, await client.WaitForCloseAsync());
        }
        await using (var client = await host.ConnectDeviceAsync(device))
        {
            await DrainAsync(client);
            await client.SendRawAsync(client.SealRaw(new byte[5000]));
            Assert.Equal(4400, await client.WaitForCloseAsync());
        }
    }
}
