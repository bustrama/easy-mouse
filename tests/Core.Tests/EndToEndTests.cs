using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Crypto;
using EasyMouse.Core.Pairing;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;
using EasyMouse.Server;

namespace EasyMouse.Core.Tests;

/// <summary>Drives the real Kestrel host over real sockets with a client written from docs/protocol.md.</summary>
public class EndToEndTests
{
    // ------------------------------------------------------------ helpers

    private static async Task<JsonElement> ReadStateAsync(TestClient client)
    {
        while (true)
        {
            if (await client.ReceiveFrameAsync() is StateMessage s) return JsonDocument.Parse(s.Json).RootElement.Clone();
        }
    }

    /// <summary>Sends a ping and waits for the reply: everything sent before it has been processed.</summary>
    private static async Task SyncAsync(TestClient client)
    {
        await client.SendFrameAsync(new PingMessage());
        while (await client.ReceiveFrameAsync() is not PingMessage) { }
    }

    /// <summary>Raw HTTP/1.1 request; returns the status code and lower-cased response headers.</summary>
    private static async Task<(int Status, Dictionary<string, string> Headers, string Body)> RawAsync(int port, string path, string host, string? origin = null, bool upgrade = false, string method = "GET")
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var sb = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n");
        if (origin is not null) sb.Append($"Origin: {origin}\r\n");
        if (upgrade) sb.Append("Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n");
        sb.Append("\r\n");
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var status = int.Parse((await reader.ReadLineAsync())!.Split(' ')[1]);
        var headers = new Dictionary<string, string>();
        while (await reader.ReadLineAsync() is { Length: > 0 } line)
        {
            var i = line.IndexOf(':');
            headers[line[..i].ToLowerInvariant()] = line[(i + 1)..].Trim();
        }
        // A WebSocket upgrade keeps the connection open; do not wait for a body in that case.
        var body = status == 101 ? "" : await reader.ReadToEndAsync();
        return (status, headers, body);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task Pair_connect_and_drive_the_pc()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync("Dana's iPhone");
        Assert.Equal("Dana's iPhone", host.Services.Devices.List().Single().Name);

        await using var client = await host.ConnectDeviceAsync(device);
        var state = await ReadStateAsync(client); // controlling devices get a snapshot first
        Assert.Equal("state", state.GetProperty("t").GetString());
        Assert.False(state.GetProperty("host").GetProperty("locked").GetBoolean());

        await client.SendFrameAsync(new MoveMessage(10, -5));
        await client.SendFrameAsync(new ButtonMessage(MouseButton.Right, true));
        await client.SendFrameAsync(new ButtonMessage(MouseButton.Right, false));
        await client.SendFrameAsync(new WheelMessage(0, -120));
        await client.SendFrameAsync(new TextMessage("שלום"));
        await client.SendFrameAsync(new KeyMessage(0x0D, KeyAction.Press));
        await client.SendFrameAsync(new MediaMessage(MediaCommand.PlayPause));
        await SyncAsync(client);

        Assert.Equal(
            ["move 10 -5", "button Right down", "button Right up", "wheel 0 -120", "text שלום", "key 13 Press"],
            host.Input.Calls);
        Assert.Equal([MediaCommand.PlayPause], host.Media.Commands);
        Assert.Equal(host.Services.Devices.List().Single().Id, host.Services.Hub.ControllerDeviceId);
    }

    [Fact]
    public async Task Pairing_shows_a_matching_code_and_waits_for_allow_with_the_one_second_delay()
    {
        await using var host = await TestHost.StartAsync(pairing: new PairingOptions());
        host.AutoApprovePairing = false;
        host.Services.Pairing.OpenWindow();
        var k = host.Services.Pairing.CurrentSecret!.Secret;

        await using var client = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "Phone", nc = B64.Encode(nc), mac = B64.Encode(HandshakeCrypto.PairMac(k, nc, "Phone")) }));
        var pending = await client.ReceiveTextAsync();
        Assert.Equal("pair.pending", pending.Type);
        Assert.True(B64.TryDecode(pending.Json.GetProperty("ns").GetString(), 16, out var ns));

        var request = host.Services.Pairing.PendingRequest!;
        Assert.Equal(HandshakeCrypto.PairCode(k, nc, ns), request.Code); // the phone shows the same number
        Assert.False(request.Allow()); // too early
        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(request.Allow());

        var ok = await client.ReceiveTextAsync();
        Assert.Equal("pair.ok", ok.Type);
        Assert.Equal(1000, await client.WaitForCloseAsync());
        Assert.Single(host.Services.Devices.List());
    }

    [Fact]
    public async Task Denied_pairing_creates_no_device()
    {
        await using var host = await TestHost.StartAsync();
        host.AutoApprovePairing = false;
        host.Services.Pairing.OpenWindow();
        var k = host.Services.Pairing.CurrentSecret!.Secret;
        await using var client = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "Phone", nc = B64.Encode(nc), mac = B64.Encode(HandshakeCrypto.PairMac(k, nc, "Phone")) }));
        Assert.Equal("pair.pending", (await client.ReceiveTextAsync()).Type);
        host.Services.Pairing.PendingRequest!.Deny();
        Assert.Equal("pair.denied", (await client.ReceiveTextAsync()).Type);
        Assert.Empty(host.Services.Devices.List());
    }

    // ------------------------------------------------------------ CI check 1: unauthenticated and wrong secret

    [Fact]
    public async Task Frames_without_authentication_are_rejected()
    {
        await using var host = await TestHost.StartAsync();
        await using var client = await host.ConnectAsync();
        await client.SendRawAsync(FrameCodec.Encode(new MoveMessage(1, 1)));
        Assert.Equal(CloseCode.Malformed, await client.WaitForCloseAsync());
        Assert.Empty(host.Input.Calls);
    }

    [Fact]
    public async Task Unknown_device_id_fails_at_auth_with_4401()
    {
        await using var host = await TestHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "hello", id = B64.Encode(HandshakeCrypto.Random(8)), nc = B64.Encode(nc) }));
        Assert.Equal("challenge", (await client.ReceiveTextAsync()).Type); // existence of ids is not revealed
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "auth", mac = B64.Encode(new byte[32]) }));
        Assert.Equal(CloseCode.Unauthorized, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Wrong_device_secret_is_rejected_and_nothing_is_sent_before_close()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "hello", id = device.Id, nc = B64.Encode(nc) }));
        var challenge = await client.ReceiveTextAsync();
        B64.TryDecode(challenge.Json.GetProperty("ns").GetString(), 16, out var ns);
        var wrong = HandshakeCrypto.ConnectMac(HandshakeCrypto.Random(32), false, nc, ns);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "auth", mac = B64.Encode(wrong) }));
        Assert.IsType<ReceivedClose>(await client.ReceiveAsync()); // no "ready", straight to close
    }

    [Fact]
    public async Task Pairing_with_a_wrong_qr_secret_or_a_closed_window_is_rejected()
    {
        await using var host = await TestHost.StartAsync();

        await using (var closed = await host.ConnectAsync())
        {
            var nc = HandshakeCrypto.Random(16);
            await closed.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "P", nc = B64.Encode(nc), mac = B64.Encode(HandshakeCrypto.PairMac(new byte[16], nc, "P")) }));
            Assert.Equal(CloseCode.PairingClosed, await closed.WaitForCloseAsync());
        }

        host.Services.Pairing.OpenWindow();
        await using var wrong = await host.ConnectAsync();
        var nc2 = HandshakeCrypto.Random(16);
        await wrong.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "P", nc = B64.Encode(nc2), mac = B64.Encode(HandshakeCrypto.PairMac(HandshakeCrypto.Random(16), nc2, "P")) }));
        Assert.Equal(CloseCode.Unauthorized, await wrong.WaitForCloseAsync());
        Assert.Empty(host.Services.Devices.List());
    }

    [Fact]
    public async Task A_silent_socket_is_closed_after_the_handshake_timeout()
    {
        await using var host = await TestHost.StartAsync();
        await using var client = await host.ConnectAsync();
        var closed = client.WaitForCloseAsync();
        while (!closed.IsCompleted)
        {
            host.Time.Advance(TimeSpan.FromSeconds(11));
            await Task.Delay(25);
        }
        Assert.Equal(CloseCode.Unauthorized, await closed);
    }

    // ------------------------------------------------------------ CI check 2: Host and Origin

    [Theory]
    [InlineData("evil.example", null)]
    [InlineData("evil.example:PORT", null)]
    [InlineData("127.0.0.1:1", null)]
    [InlineData("127.0.0.1:PORT", "http://evil.example")]
    [InlineData("127.0.0.1:PORT", "http://evil.example:PORT")]
    [InlineData("127.0.0.1:PORT", "null")]
    [InlineData("127.0.0.1:PORT", "https://127.0.0.1:PORT")]
    public async Task Wrong_host_or_foreign_origin_cannot_upgrade_or_read(string host, string? origin)
    {
        await using var h = await TestHost.StartAsync();
        host = host.Replace("PORT", h.Port.ToString());
        origin = origin?.Replace("PORT", h.Port.ToString());

        var ws = await RawAsync(h.Port, "/ws", host, origin, upgrade: true);
        Assert.Equal(403, ws.Status);
        var info = await RawAsync(h.Port, "/api/info", host, origin);
        Assert.Equal(403, info.Status);
        Assert.DoesNotContain("Test PC", info.Body);
        var page = await RawAsync(h.Port, "/", host, origin);
        Assert.Equal(403, page.Status);
        Assert.False(info.Headers.ContainsKey("access-control-allow-origin"));
    }

    [Fact]
    public async Task Correct_host_and_own_origin_can_upgrade()
    {
        await using var host = await TestHost.StartAsync();
        foreach (var (hostHeader, origin) in new[]
        {
            ($"127.0.0.1:{host.Port}", (string?)$"http://127.0.0.1:{host.Port}"),
            ($"testpc.local:{host.Port}", $"http://testpc.local:{host.Port}"),
            ($"127.0.0.1:{host.Port}", null),
        })
        {
            Assert.Equal(101, (await RawAsync(host.Port, "/ws", hostHeader, origin, upgrade: true)).Status);
        }
        // Through the real client class, with a browser-style Origin.
        await using var client = await host.ConnectAsync(origin: $"http://127.0.0.1:{host.Port}");
        await client.SendRawAsync([1]);
        Assert.Equal(CloseCode.Malformed, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task A_foreign_origin_websocket_is_refused_by_the_real_client_stack_too()
    {
        await using var host = await TestHost.StartAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync(origin: "http://evil.example"));
    }

    // ------------------------------------------------------------ CI check 3: QR secret lifetime and reuse

    [Fact]
    public async Task A_used_qr_secret_cannot_pair_a_second_device()
    {
        await using var host = await TestHost.StartAsync();
        host.Services.Pairing.OpenWindow();
        var k = host.Services.Pairing.CurrentSecret!.Secret;
        await using (var first = await host.ConnectAsync()) await first.PairAsync(k, "First");

        await using var second = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await second.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "Second", nc = B64.Encode(nc), mac = B64.Encode(HandshakeCrypto.PairMac(k, nc, "Second")) }));
        Assert.Equal(CloseCode.Unauthorized, await second.WaitForCloseAsync());
        Assert.Single(host.Services.Devices.List());
    }

    [Fact]
    public async Task An_expired_qr_secret_is_refused_with_4408()
    {
        await using var host = await TestHost.StartAsync();
        host.Services.Pairing.OpenWindow();
        var k = host.Services.Pairing.CurrentSecret!.Secret;
        host.Time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        await using var client = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "Late", nc = B64.Encode(nc), mac = B64.Encode(HandshakeCrypto.PairMac(k, nc, "Late")) }));
        Assert.Equal(CloseCode.PairingClosed, await client.WaitForCloseAsync());
        Assert.Empty(host.Services.Devices.List());
    }

    [Fact]
    public async Task Five_failed_pair_attempts_over_the_network_regenerate_the_secret()
    {
        await using var host = await TestHost.StartAsync();
        host.Services.Pairing.OpenWindow();
        var k = host.Services.Pairing.CurrentSecret!.Secret;
        for (int i = 0; i < 5; i++)
        {
            await using var bad = await host.ConnectAsync();
            await bad.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "X", nc = B64.Encode(HandshakeCrypto.Random(16)), mac = B64.Encode(new byte[32]) }));
            Assert.Equal(CloseCode.Unauthorized, await bad.WaitForCloseAsync());
        }
        Assert.NotEqual(k, host.Services.Pairing.CurrentSecret!.Secret);

        await using var old = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await old.SendTextAsync(JsonSerializer.Serialize(new { t = "pair.hello", name = "X", nc = B64.Encode(nc), mac = B64.Encode(HandshakeCrypto.PairMac(k, nc, "X")) }));
        Assert.Equal(CloseCode.Unauthorized, await old.WaitForCloseAsync());
    }

    // ------------------------------------------------------------ CI check 4: replay and tampering

    [Fact]
    public async Task Replayed_reordered_and_tampered_frames_are_dropped()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);

        var first = client.Seal(new MoveMessage(1, 1));      // counter 1
        var second = client.Seal(new MoveMessage(2, 2));     // counter 2
        var third = client.Seal(new MoveMessage(3, 3));      // counter 3
        var tampered = client.Seal(new MoveMessage(4, 4));   // counter 4, then corrupted
        tampered[^1] ^= 0x01;
        var fifth = client.Seal(new MoveMessage(5, 5));      // counter 5

        await client.SendRawAsync(first);
        await client.SendRawAsync(first);      // replay
        await client.SendRawAsync(third);      // gap is fine
        await client.SendRawAsync(second);     // reorder: older than the last seen
        await client.SendRawAsync(tampered);   // bad tag
        await client.SendRawAsync(fifth);
        await SyncAsync(client);

        Assert.Equal(["move 1 1", "move 3 3", "move 5 5"], host.Input.Calls);
    }

    [Fact]
    public async Task A_frame_from_another_session_cannot_be_replayed_into_this_one()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        byte[] captured;
        await using (var old = await host.ConnectDeviceAsync(device))
        {
            captured = old.Seal(new MoveMessage(9, 9));
            await old.SendRawAsync(captured);
            await SyncAsync(old);
        }
        await using var current = await host.ConnectDeviceAsync(device);
        await current.SendRawAsync(captured); // fresh nonces give fresh keys: the old ciphertext does not authenticate
        await SyncAsync(current);
        Assert.Equal(["move 9 9"], host.Input.Calls);
    }

    // ------------------------------------------------------------ CI check 5: revocation

    [Fact]
    public async Task Revoking_a_device_disconnects_it_at_once_and_it_cannot_return()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);
        await SyncAsync(client);

        var closed = client.WaitForCloseAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await host.Services.Devices.RevokeAsync(device.Id));
        Assert.Equal(CloseCode.Unauthorized, await closed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Empty(host.Services.Devices.List());
        Assert.Equal(0, host.Services.Hub.SessionCount);

        // Frames sent after the revoke never reach the input sink.
        try { await client.SendFrameAsync(new MoveMessage(1, 1)); } catch (Exception) { /* socket may already be closed */ }
        Assert.Empty(host.Input.Calls);

        await using var again = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await again.SendTextAsync(JsonSerializer.Serialize(new { t = "hello", id = device.Id, nc = B64.Encode(nc) }));
        var challenge = await again.ReceiveTextAsync();
        B64.TryDecode(challenge.Json.GetProperty("ns").GetString(), 16, out var ns);
        await again.SendTextAsync(JsonSerializer.Serialize(new { t = "auth", mac = B64.Encode(HandshakeCrypto.ConnectMac(device.Secret, false, nc, ns)) }));
        Assert.Equal(CloseCode.Unauthorized, await again.WaitForCloseAsync());
    }

    [Fact]
    public async Task Revoke_all_closes_every_live_socket()
    {
        await using var host = await TestHost.StartAsync();
        var a = await host.PairAsync("A");
        var b = await host.PairAsync("B");
        await using var ca = await host.ConnectDeviceAsync(a);
        await using var cb = await host.ConnectDeviceAsync(b);
        Assert.Equal("busy", (await cb.ReceiveTextAsync()).Type);

        var closeA = ca.WaitForCloseAsync();
        var closeB = cb.WaitForCloseAsync();
        Assert.Equal(2, await host.Services.Devices.RevokeAllAsync());
        Assert.Equal(CloseCode.Unauthorized, await closeA);
        Assert.Equal(CloseCode.Unauthorized, await closeB);
        Assert.Empty(host.Services.Devices.List());
    }

    [Fact]
    public async Task An_expired_device_cannot_connect()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        host.Time.Advance(TimeSpan.FromDays(91));
        await using var client = await host.ConnectAsync();
        var nc = HandshakeCrypto.Random(16);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "hello", id = device.Id, nc = B64.Encode(nc) }));
        var challenge = await client.ReceiveTextAsync();
        B64.TryDecode(challenge.Json.GetProperty("ns").GetString(), 16, out var ns);
        await client.SendTextAsync(JsonSerializer.Serialize(new { t = "auth", mac = B64.Encode(HandshakeCrypto.ConnectMac(device.Secret, false, nc, ns)) }));
        Assert.Equal(CloseCode.Unauthorized, await client.WaitForCloseAsync());
    }

    // ------------------------------------------------------------ CI check 6: listener

    [Fact]
    public async Task Listener_is_bound_to_the_chosen_address_only()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(IPAddress.Loopback, host.Server.EndPoint!.Address);

        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(l => l.Port == host.Port).ToList();
        Assert.Single(listeners);
        Assert.Equal(IPAddress.Loopback, listeners[0].Address);
        Assert.DoesNotContain(listeners, l => l.Address.Equals(IPAddress.Any) || l.Address.Equals(IPAddress.IPv6Any));
    }

    [Fact]
    public void Binding_to_all_interfaces_is_refused()
    {
        var services = CoreServices.Create(new EasyMouse.Core.Devices.InMemoryDeviceStore(), new EasyMouse.Core.Fakes.RecordingInputSink(), new EasyMouse.Core.Fakes.RecordingMediaController());
        foreach (var any in new[] { IPAddress.Any, IPAddress.IPv6Any })
            Assert.Throws<ArgumentException>(() => new EasyMouseServer(new ServerOptions { BindAddress = any, AllowedHosts = ["x"] }, services));
    }

    // ------------------------------------------------------------ CI check 7 (protocol part): unknown ops and limits

    [Theory]
    [InlineData(new byte[] { 0x55 })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x11, (byte)'{', (byte)'}' })] // state is server-to-client only
    [InlineData(new byte[] { 0x01, 0, 0 })]                  // wrong length
    [InlineData(new byte[] { 0x06, 99 })]                    // out-of-range value
    [InlineData(new byte[] { 0x04, 0xFF })]                  // invalid UTF-8
    public async Task Authenticated_but_invalid_frames_are_rejected(byte[] plaintext)
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);
        await client.SendRawAsync(client.SealRaw(plaintext));
        Assert.Equal(CloseCode.Malformed, await client.WaitForCloseAsync());
        Assert.Empty(host.Input.Calls);
        Assert.Empty(host.Media.Commands);
    }

    [Fact]
    public async Task Oversized_text_and_oversized_frames_are_rejected()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();

        await using (var client = await host.ConnectDeviceAsync(device))
        {
            var text = new byte[1 + 1025];
            text[0] = (byte)Op.Text;
            Array.Fill(text, (byte)'a', 1, 1025);
            await client.SendRawAsync(client.SealRaw(text));
            Assert.Equal(CloseCode.Malformed, await client.WaitForCloseAsync());
        }
        await using (var client = await host.ConnectDeviceAsync(device))
        {
            await client.SendRawAsync(new byte[4097]);
            Assert.Equal(CloseCode.Malformed, await client.WaitForCloseAsync());
        }
        Assert.Empty(host.Input.Calls);
    }

    [Fact]
    public async Task Flooding_is_rate_limited()
    {
        await using var host = await TestHost.StartAsync(limits: new SessionLimits { FramesPerSecond = 5, FrameBurst = 5 });
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);
        for (int i = 0; i < 30; i++)
        {
            try { await client.SendFrameAsync(new MoveMessage(1, 1)); } catch (Exception) { break; }
        }
        Assert.Equal(CloseCode.RateLimited, await client.WaitForCloseAsync());
        Assert.True(host.Input.Calls.Count <= 5);
    }

    [Fact]
    public async Task Text_frames_after_ready_are_rejected()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);
        await client.SendTextAsync("{\"t\":\"move\"}");
        Assert.Equal(CloseCode.Malformed, await client.WaitForCloseAsync());
    }

    // ------------------------------------------------------------ controllers

    [Fact]
    public async Task A_second_device_is_told_busy_cannot_drive_and_can_take_over_with_approval()
    {
        await using var host = await TestHost.StartAsync();
        var a = await host.PairAsync("A");
        var b = await host.PairAsync("B");
        await using var ca = await host.ConnectDeviceAsync(a);
        await SyncAsync(ca);
        await using var cb = await host.ConnectDeviceAsync(b);
        Assert.Equal("busy", (await cb.ReceiveTextAsync()).Type);

        await cb.SendFrameAsync(new MoveMessage(7, 7)); // authenticated but not the controller
        await Task.Delay(100);
        Assert.Empty(host.Input.Calls);

        ApprovalRequest? seen = null;
        host.Services.Hub.TakeoverRequested += (_, r) => { seen = r; r.Allow(); };
        await cb.SendTextAsync("{\"t\":\"takeover\"}");
        Assert.Equal("active", (await cb.ReceiveTextAsync()).Type);
        Assert.Equal(CloseCode.Replaced, await ca.WaitForCloseAsync());
        Assert.Equal(ApprovalKind.Takeover, seen!.Kind);
        Assert.Equal("B", seen.DeviceName);

        await ReadStateAsync(cb);
        await cb.SendFrameAsync(new MoveMessage(8, 8));
        await SyncAsync(cb);
        Assert.Equal(["move 8 8"], host.Input.Calls);
    }

    [Fact]
    public async Task A_denied_takeover_leaves_the_first_device_in_control()
    {
        await using var host = await TestHost.StartAsync();
        var a = await host.PairAsync("A");
        var b = await host.PairAsync("B");
        await using var ca = await host.ConnectDeviceAsync(a);
        await using var cb = await host.ConnectDeviceAsync(b);
        Assert.Equal("busy", (await cb.ReceiveTextAsync()).Type);
        host.Services.Hub.TakeoverRequested += (_, r) => r.Deny();
        await cb.SendTextAsync("{\"t\":\"takeover\"}");
        Assert.Equal("takeover.denied", (await cb.ReceiveTextAsync()).Type);
        await ca.SendFrameAsync(new MoveMessage(1, 2));
        await SyncAsync(ca);
        Assert.Equal(["move 1 2"], host.Input.Calls);
    }

    [Fact]
    public async Task A_device_that_reconnects_replaces_its_own_old_socket_without_a_prompt()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var old = await host.ConnectDeviceAsync(device);
        await SyncAsync(old);
        await using var fresh = await host.ConnectDeviceAsync(device);
        Assert.Equal(CloseCode.Replaced, await old.WaitForCloseAsync());
        await ReadStateAsync(fresh);
        await fresh.SendFrameAsync(new MoveMessage(2, 2));
        await SyncAsync(fresh);
        Assert.Equal(["move 2 2"], host.Input.Calls);
    }

    // ------------------------------------------------------------ state, lock, remote actions

    [Fact]
    public async Task Media_and_lock_state_are_pushed_and_a_locked_pc_drops_input()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);
        await ReadStateAsync(client);

        host.Media.SetSnapshot(new MediaSnapshot(new NowPlaying(PlaybackStatus.Playing, "Song", "Artist", 1000, 200000), new VolumeState(30, true)));
        var s = await ReadStateAsync(client);
        Assert.Equal("playing", s.GetProperty("media").GetProperty("status").GetString());
        Assert.Equal("Song", s.GetProperty("media").GetProperty("title").GetString());
        Assert.Equal(30, s.GetProperty("volume").GetProperty("level").GetInt32());
        Assert.True(s.GetProperty("volume").GetProperty("muted").GetBoolean());

        host.Services.Status.Update(locked: true, elevated: false);
        Assert.True((await ReadStateAsync(client)).GetProperty("host").GetProperty("locked").GetBoolean());
        await client.SendFrameAsync(new MoveMessage(1, 1));
        await client.SendFrameAsync(new MediaMessage(MediaCommand.Next));
        await SyncAsync(client); // pings still work while locked
        Assert.Empty(host.Input.Calls);
        Assert.Empty(host.Media.Commands);

        host.Services.Status.Update(locked: false, elevated: false);
        await ReadStateAsync(client);
        await client.SendFrameAsync(new MoveMessage(2, 2));
        await SyncAsync(client);
        Assert.Equal(["move 2 2"], host.Input.Calls);
    }

    [Fact]
    public async Task Remote_actions_are_refused_until_an_engine_exists_and_garbage_json_closes()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();

        await using (var client = await host.ConnectDeviceAsync(device))
        {
            await ReadStateAsync(client);
            await client.SendFrameAsync(new JsonMessage("{\"t\":\"invoke\",\"rid\":7,\"remote\":\"stremio\",\"item\":\"play\"}"));
            var reply = await client.ReceiveFrameAsync();
            var json = JsonDocument.Parse(Assert.IsType<JsonMessage>(reply).Json).RootElement;
            Assert.Equal("result", json.GetProperty("t").GetString());
            Assert.Equal(7, json.GetProperty("rid").GetInt64());
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.Equal("unsupported", json.GetProperty("err").GetString());
        }

        foreach (var bad in new[] { "not json", "[]", "{\"t\":\"shell\",\"cmd\":\"calc\"}", "{\"t\":\"invoke\",\"rid\":-1,\"remote\":\"a\",\"item\":\"b\"}", "{\"t\":\"invoke\",\"rid\":1,\"remote\":\"a\",\"item\":\"b\",\"value\":\"x\"}" })
        {
            await using var client = await host.ConnectDeviceAsync(device);
            await client.SendFrameAsync(new JsonMessage(bad));
            Assert.Equal(CloseCode.Malformed, await client.WaitForCloseAsync());
        }
    }

    // ------------------------------------------------------------ CI check 10: headers; static files

    [Fact]
    public async Task Responses_carry_strict_security_headers_and_no_cors()
    {
        await using var host = await TestHost.StartAsync();
        var h = $"127.0.0.1:{host.Port}";

        foreach (var path in new[] { "/api/info", "/", "/missing" })
        {
            var r = await RawAsync(host.Port, path, h);
            Assert.Contains("default-src 'self'", r.Headers["content-security-policy"]);
            Assert.DoesNotContain("unsafe-inline", r.Headers["content-security-policy"]);
            Assert.DoesNotContain("unsafe-eval", r.Headers["content-security-policy"]);
            Assert.Contains("frame-ancestors 'none'", r.Headers["content-security-policy"]);
            Assert.Equal("DENY", r.Headers["x-frame-options"]);
            Assert.Equal("nosniff", r.Headers["x-content-type-options"]);
            Assert.Equal("no-referrer", r.Headers["referrer-policy"]);
            Assert.False(r.Headers.ContainsKey("access-control-allow-origin"));
            Assert.False(r.Headers.ContainsKey("server"));
            Assert.False(r.Headers.ContainsKey("set-cookie"));
        }
        // Rejected requests are covered too.
        var denied = await RawAsync(host.Port, "/", "evil.example");
        Assert.Equal(403, denied.Status);
        Assert.Contains("default-src", denied.Headers["content-security-policy"]);
        Assert.Equal("DENY", denied.Headers["x-frame-options"]);
    }

    [Fact]
    public async Task Cross_origin_preflight_gets_no_cors_grant()
    {
        await using var host = await TestHost.StartAsync();
        var r = await RawAsync(host.Port, "/api/info", $"127.0.0.1:{host.Port}", "http://evil.example", method: "OPTIONS");
        Assert.Equal(403, r.Status);
        Assert.False(r.Headers.ContainsKey("access-control-allow-origin"));
    }

    [Fact]
    public async Task Api_info_and_static_files()
    {
        var assets = new StaticAssets(new Dictionary<string, byte[]>
        {
            ["index.html"] = "<!doctype html><title>app</title>"u8.ToArray(),
            ["assets/app.js"] = "console.log(1)"u8.ToArray(),
        });
        await using var host = await TestHost.StartAsync(assets: assets);
        var h = $"127.0.0.1:{host.Port}";

        var info = await RawAsync(host.Port, "/api/info", h);
        Assert.Equal(200, info.Status);
        var json = JsonDocument.Parse(info.Body).RootElement;
        Assert.Equal("Test PC", json.GetProperty("name").GetString());
        Assert.Equal(0, json.GetProperty("proto").GetInt32());
        Assert.True(json.TryGetProperty("version", out _));

        Assert.Contains("<title>app</title>", (await RawAsync(host.Port, "/", h)).Body);
        var js = await RawAsync(host.Port, "/assets/app.js", h);
        Assert.Equal(200, js.Status);
        Assert.Contains("javascript", js.Headers["content-type"]);
        Assert.Equal(404, (await RawAsync(host.Port, "/nope.txt", h)).Status);
        Assert.Equal(404, (await RawAsync(host.Port, "/../secret", h)).Status);
        Assert.Equal(405, (await RawAsync(host.Port, "/", h, method: "POST")).Status);
    }

    [Fact]
    public async Task Without_a_built_web_client_a_stub_page_is_served()
    {
        await using var host = await TestHost.StartAsync();
        var page = await RawAsync(host.Port, "/", $"127.0.0.1:{host.Port}");
        Assert.Equal(200, page.Status);
        Assert.Contains("web client was not built", page.Body);
    }
}
