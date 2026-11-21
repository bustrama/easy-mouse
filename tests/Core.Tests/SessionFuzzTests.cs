using System.Text;
using EasyMouse.Core.Crypto;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Fakes;
using EasyMouse.Core.Pairing;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.Core.Tests;

internal sealed class FakeTransport : ISessionTransport
{
    public List<string> Texts { get; } = [];
    public List<byte[]> Binaries { get; } = [];
    public int? ClosedWith { get; private set; }

    public Task SendTextAsync(string text, CancellationToken cancellationToken) { lock (Texts) Texts.Add(text); return Task.CompletedTask; }
    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { lock (Binaries) Binaries.Add(data.ToArray()); return Task.CompletedTask; }
    public Task CloseAsync(int code, string reason) { ClosedWith ??= code; return Task.CompletedTask; }
}

/// <summary>Garbage in every phase must close or ignore, never throw and never reach the input sink.</summary>
public class SessionFuzzTests
{
    private static (CoreServices Services, RecordingInputSink Input) Services()
    {
        var input = new RecordingInputSink();
        var svc = CoreServices.Create(new InMemoryDeviceStore(), input, new RecordingMediaController(), time: new ManualTimeProvider());
        return (svc, input);
    }

    private static readonly string[] Seeds =
    [
        "{\"t\":\"hello\",\"id\":\"AAAAAAAAAAA\",\"nc\":\"AAAAAAAAAAAAAAAAAAAAAA\"}",
        "{\"t\":\"auth\",\"mac\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}",
        "{\"t\":\"pair.hello\",\"name\":\"x\",\"nc\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"mac\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}",
        "{\"t\":\"takeover\"}",
        "{\"t\":\"invoke\"}",
        "[]", "null", "\"t\"", "{}", "{\"t\":1}", "{\"t\":{\"a\":{\"b\":{}}}}", "",
    ];

    [Fact]
    public async Task Random_and_mutated_text_never_throws_or_injects_input()
    {
        var rng = new Random(2024);
        for (int i = 0; i < 3_000; i++)
        {
            var (svc, input) = Services();
            var transport = new FakeTransport();
            var session = new ServerSession(svc, transport);
            for (int step = 0; step < 4 && !session.IsClosed; step++)
            {
                var chars = Seeds[rng.Next(Seeds.Length)].ToCharArray();
                for (int m = rng.Next(0, 4); m > 0 && chars.Length > 0; m--) chars[rng.Next(chars.Length)] = (char)rng.Next(32, 127);
                var text = rng.Next(5) == 0 ? new string([.. Enumerable.Range(0, rng.Next(0, 40)).Select(_ => (char)rng.Next(0, 0x2FF))]) : new string(chars);
                await session.OnTextAsync(text);
            }
            Assert.Empty(input.Calls);
        }
    }

    [Fact]
    public async Task Random_binary_before_authentication_closes_with_4400()
    {
        var rng = new Random(5);
        for (int i = 0; i < 500; i++)
        {
            var (svc, input) = Services();
            var transport = new FakeTransport();
            var session = new ServerSession(svc, transport);
            var data = new byte[rng.Next(0, 100)];
            rng.NextBytes(data);
            await session.OnBinaryAsync(data);
            Assert.Equal(CloseCode.Malformed, transport.ClosedWith);
            Assert.Empty(input.Calls);
        }
    }

    [Fact]
    public async Task Oversized_text_is_rejected()
    {
        var (svc, _) = Services();
        var transport = new FakeTransport();
        var session = new ServerSession(svc, transport);
        await session.OnTextAsync(new string('a', 5000));
        Assert.Equal(CloseCode.Malformed, transport.ClosedWith);
    }

    [Fact]
    public async Task Established_session_survives_random_frames_but_closes_on_authenticated_garbage()
    {
        // Drive an authenticated session by hand (no network): random ciphertext is dropped silently.
        var time = new ManualTimeProvider();
        var input = new RecordingInputSink();
        var svc = CoreServices.Create(new InMemoryDeviceStore(), input, new RecordingMediaController(), time: time,
            limits: new SessionLimits { MaxInvalidFrames = 100_000, FramesPerSecond = 1e9, FrameBurst = 1_000_000 });
        var secret = HandshakeCrypto.Random(32);
        var now = time.GetUtcNow();
        svc.Devices.Store.Add(new Device("AAAAAAAAAAA", "Phone", secret, now, now));

        var transport = new FakeTransport();
        var session = new ServerSession(svc, transport);
        var nc = HandshakeCrypto.Random(16);
        await session.OnTextAsync($"{{\"t\":\"hello\",\"id\":\"AAAAAAAAAAA\",\"nc\":\"{B64.Encode(nc)}\"}}");
        var ns = Convert.FromBase64String(Pad(System.Text.Json.JsonDocument.Parse(transport.Texts[0]).RootElement.GetProperty("ns").GetString()!));
        var mac = HandshakeCrypto.ConnectMac(secret, false, nc, ns);
        await session.OnTextAsync($"{{\"t\":\"auth\",\"mac\":\"{B64.Encode(mac)}\"}}");
        Assert.True(session.IsControlling);

        var rng = new Random(8);
        var junk = new byte[100];
        for (int i = 0; i < 5_000; i++)
        {
            rng.NextBytes(junk);
            await session.OnBinaryAsync(junk.AsMemory(0, rng.Next(0, junk.Length)));
        }
        Assert.False(session.IsClosed);
        Assert.Empty(input.Calls);
        Assert.True(session.DroppedFrames >= 5_000);

        var client = SecureChannel.ForClient(secret, nc, ns);
        await session.OnBinaryAsync(client.Seal(FrameCodec.Encode(new MoveMessage(3, 4))));
        Assert.Equal(["move 3 4"], input.Calls);
        await session.OnBinaryAsync(client.Seal([0x55, 1, 2, 3])); // authentic but not a valid op
        Assert.Equal(CloseCode.Malformed, transport.ClosedWith);
        Assert.Single(input.Calls);
    }

    private static string Pad(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }
}
