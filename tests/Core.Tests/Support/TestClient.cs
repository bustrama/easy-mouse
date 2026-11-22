using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EasyMouse.Core.Crypto;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Tests.Support;

public abstract record Received;
public sealed record ReceivedText(string Text) : Received
{
    public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();
    public string Type => Json.GetProperty("t").GetString()!;
}
public sealed record ReceivedBinary(byte[] Data) : Received;
public sealed record ReceivedClose(int Code) : Received;

/// <summary>A protocol client over a real WebSocket, written from docs/protocol.md.</summary>
public sealed class TestClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ClientWebSocket _ws;
    private SecureChannel? _channel;

    private TestClient(ClientWebSocket ws) => _ws = ws;

    public static async Task<TestClient> ConnectAsync(Uri uri, string? origin = null)
    {
        var ws = new ClientWebSocket();
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        using var cts = new CancellationTokenSource(Timeout);
        await ws.ConnectAsync(uri, cts.Token);
        return new TestClient(ws);
    }

    public async Task SendTextAsync(string json)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
    }

    public async Task SendRawAsync(byte[] data)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await _ws.SendAsync(data, WebSocketMessageType.Binary, true, cts.Token);
    }

    public Task SendFrameAsync(Message message) => SendRawAsync(_channel!.Seal(FrameCodec.Encode(message)));

    /// <summary>Seals a message without sending it (to craft replays and tampered frames).</summary>
    public byte[] Seal(Message message) => _channel!.Seal(FrameCodec.Encode(message));

    public byte[] SealRaw(byte[] plaintext) => _channel!.Seal(plaintext);

    public async Task<Received> ReceiveAsync()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[192 * 1024]; // art frames are up to 128 KiB
        int total = 0;
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer.AsMemory(total), cts.Token);
            if (r.MessageType == WebSocketMessageType.Close)
                return new ReceivedClose((int)(_ws.CloseStatus ?? WebSocketCloseStatus.Empty));
            total += r.Count;
            if (!r.EndOfMessage) continue;
            return r.MessageType == WebSocketMessageType.Text
                ? new ReceivedText(Encoding.UTF8.GetString(buffer, 0, total))
                : new ReceivedBinary(buffer[..total]);
        }
    }

    public async Task<ReceivedText> ReceiveTextAsync()
        => Assert.IsType<ReceivedText>(await ReceiveAsync());

    public async Task<Message> ReceiveFrameAsync()
    {
        var received = Assert.IsType<ReceivedBinary>(await ReceiveAsync());
        Assert.Equal(OpenResult.Ok, _channel!.TryOpen(received.Data, out var plain));
        Assert.Equal(FrameError.None, FrameCodec.TryParse(plain, FrameDirection.ServerToClient, out var message));
        return message!;
    }

    /// <summary>Reads until the server closes the socket; returns the close code. Frames in between are discarded.</summary>
    public async Task<int> WaitForCloseAsync()
    {
        while (true)
        {
            if (await ReceiveAsync() is ReceivedClose c) return c.Code;
        }
    }

    public async Task<DeviceCredentials> PairAsync(byte[] k, string name)
    {
        var nc = HandshakeCrypto.Random(16);
        var mac = HandshakeCrypto.PairMac(k, nc, name);
        await SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name, nc = B64.Encode(nc), mac = B64.Encode(mac) }));
        var pending = await ReceiveTextAsync();
        Assert.Equal("pair.pending", pending.Type);
        Assert.True(B64.TryDecode(pending.Json.GetProperty("ns").GetString(), 16, out var ns));
        var ok = await ReceiveTextAsync();
        Assert.Equal("pair.ok", ok.Type);
        var id = ok.Json.GetProperty("id").GetString()!;
        Assert.True(B64.TryDecode(ok.Json.GetProperty("wrap").GetString(), 48, out var wrap));
        Assert.True(HandshakeCrypto.TryUnwrap(k, nc, ns, wrap, out var secret));
        Assert.Equal(1000, await WaitForCloseAsync());
        return new DeviceCredentials(id, secret);
    }

    /// <summary>The connect handshake; leaves the client with an established channel. Does not read busy/state.</summary>
    public async Task AuthenticateAsync(DeviceCredentials device)
    {
        var nc = HandshakeCrypto.Random(16);
        await SendTextAsync(JsonSerializer.Serialize(new { t = "hello", id = device.Id, nc = B64.Encode(nc) }));
        var challenge = await ReceiveTextAsync();
        Assert.Equal("challenge", challenge.Type);
        Assert.True(B64.TryDecode(challenge.Json.GetProperty("ns").GetString(), 16, out var ns));
        await SendTextAsync(JsonSerializer.Serialize(new { t = "auth", mac = B64.Encode(HandshakeCrypto.ConnectMac(device.Secret, false, nc, ns)) }));
        var ready = await ReceiveTextAsync();
        Assert.Equal("ready", ready.Type);
        Assert.True(B64.TryDecode(ready.Json.GetProperty("mac").GetString(), 32, out var serverMac));
        Assert.True(HandshakeCrypto.MacEquals(serverMac, HandshakeCrypto.ConnectMac(device.Secret, true, nc, ns)));
        _channel = SecureChannel.ForClient(device.Secret, nc, ns);
    }

    public async ValueTask DisposeAsync()
    {
        _ws.Abort();
        _ws.Dispose();
        await Task.CompletedTask;
    }
}
