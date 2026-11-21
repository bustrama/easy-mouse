using EasyMouse.Core.Crypto;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Tests;

public class FrameCodecTests
{
    public static TheoryData<Message> Samples => [.. AllSamples];

    private static readonly Message[] AllSamples =
    [
        new MoveMessage(-32768, 32767),
        new ButtonMessage(MouseButton.Middle, true),
        new ButtonMessage(MouseButton.Left, false),
        new WheelMessage(120, -120),
        new TextMessage(""),
        new TextMessage("שלום עולם 👋"),
        new KeyMessage(0xFFFF, KeyAction.Press),
        new MediaMessage(MediaCommand.Mute),
        new JsonMessage("{\"t\":\"invoke\"}"),
        new PingMessage(),
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Roundtrip(Message message)
    {
        Assert.Equal(FrameError.None, FrameCodec.TryParse(FrameCodec.Encode(message), FrameDirection.ClientToServer, out var parsed));
        Assert.Equal(message, parsed);
    }

    [Fact]
    public void State_is_server_to_client_only()
    {
        var bytes = FrameCodec.Encode(new StateMessage("{}"));
        Assert.Equal(FrameError.UnknownOp, FrameCodec.TryParse(bytes, FrameDirection.ClientToServer, out _));
        Assert.Equal(FrameError.None, FrameCodec.TryParse(bytes, FrameDirection.ServerToClient, out _));
    }

    [Theory]
    [InlineData(new byte[] { }, FrameError.Empty)]
    [InlineData(new byte[] { 0x00 }, FrameError.UnknownOp)]
    [InlineData(new byte[] { 0x55, 1, 2 }, FrameError.UnknownOp)]
    [InlineData(new byte[] { 0x01, 0, 0, 0 }, FrameError.BadLength)]
    [InlineData(new byte[] { 0x01, 0, 0, 0, 0, 0 }, FrameError.BadLength)]
    [InlineData(new byte[] { 0x02, 3, 1 }, FrameError.BadValue)]
    [InlineData(new byte[] { 0x02, 0, 2 }, FrameError.BadValue)]
    [InlineData(new byte[] { 0x05, 0, 65, 0 }, FrameError.BadValue)]
    [InlineData(new byte[] { 0x05, 0, 65, 4 }, FrameError.BadValue)]
    [InlineData(new byte[] { 0x06, 7 }, FrameError.BadValue)]
    [InlineData(new byte[] { 0x06 }, FrameError.BadLength)]
    [InlineData(new byte[] { 0x04, 0xFF, 0xFE }, FrameError.BadUtf8)]
    [InlineData(new byte[] { 0x04, 0xED, 0xA0, 0x80 }, FrameError.BadUtf8)] // lone surrogate
    [InlineData(new byte[] { 0x7F, 0 }, FrameError.BadLength)]
    public void Rejects(byte[] bytes, FrameError expected)
        => Assert.Equal(expected, FrameCodec.TryParse(bytes, FrameDirection.ClientToServer, out _));

    [Fact]
    public void Text_limit_is_in_bytes()
    {
        var atLimit = new byte[1 + FrameCodec.MaxTextBytes];
        atLimit[0] = (byte)Op.Text;
        Array.Fill(atLimit, (byte)'a', 1, FrameCodec.MaxTextBytes);
        Assert.Equal(FrameError.None, FrameCodec.TryParse(atLimit, FrameDirection.ClientToServer, out _));
        Assert.Equal(FrameError.TooLong, FrameCodec.TryParse([.. atLimit, (byte)'a'], FrameDirection.ClientToServer, out _));
        // 513 Hebrew letters are 1026 bytes: over the limit although under 1024 characters.
        Assert.Equal(FrameError.TooLong, FrameCodec.TryParse(FrameCodec.Encode(new TextMessage(new string('א', 513))), FrameDirection.ClientToServer, out _));
    }

    [Fact]
    public void Fuzz_random_bytes_never_throw()
    {
        var rng = new Random(12345);
        var buffer = new byte[64];
        for (int i = 0; i < 200_000; i++)
        {
            var len = rng.Next(0, buffer.Length);
            rng.NextBytes(buffer);
            if (rng.Next(2) == 0 && len > 0) buffer[0] = (byte)rng.Next(0, 0x20); // bias towards real opcodes
            foreach (var dir in new[] { FrameDirection.ClientToServer, FrameDirection.ServerToClient })
            {
                var err = FrameCodec.TryParse(buffer.AsSpan(0, len), dir, out var msg);
                Assert.Equal(err == FrameError.None, msg is not null);
            }
        }
    }

    [Fact]
    public void Fuzz_mutated_valid_frames_never_throw_and_valid_results_reencode()
    {
        var rng = new Random(777);
        var seeds = new List<byte[]>();
        foreach (var m in AllSamples) seeds.Add(FrameCodec.Encode(m));
        for (int i = 0; i < 100_000; i++)
        {
            var bytes = (byte[])seeds[rng.Next(seeds.Count)].Clone();
            for (int m = rng.Next(1, 4); m > 0 && bytes.Length > 0; m--) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            if (rng.Next(4) == 0 && bytes.Length > 1) bytes = bytes[..rng.Next(bytes.Length)];
            if (FrameCodec.TryParse(bytes, FrameDirection.ClientToServer, out var msg) == FrameError.None)
                Assert.Equal(bytes, FrameCodec.Encode(msg!)); // parsing is canonical: nothing is silently ignored
        }
    }
}

public class SecureChannelTests
{
    private static (SecureChannel Client, SecureChannel Server) Pair()
    {
        var secret = HandshakeCrypto.Random(32);
        var nc = HandshakeCrypto.Random(16);
        var ns = HandshakeCrypto.Random(16);
        return (SecureChannel.ForClient(secret, nc, ns), SecureChannel.ForServer(secret, nc, ns));
    }

    private static readonly byte[] Plain = FrameCodec.Encode(new MoveMessage(1, 2));

    [Fact]
    public void Frames_roundtrip_in_both_directions_and_counters_start_at_one()
    {
        var (client, server) = Pair();
        var frame = client.Seal(Plain);
        Assert.Equal(1UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(frame));
        Assert.Equal(OpenResult.Ok, server.TryOpen(frame, out var plain));
        Assert.Equal(Plain, plain);
        Assert.Equal(OpenResult.Ok, client.TryOpen(server.Seal(Plain), out _));
    }

    [Fact]
    public void Replayed_frame_is_dropped()
    {
        var (client, server) = Pair();
        var frame = client.Seal(Plain);
        Assert.Equal(OpenResult.Ok, server.TryOpen(frame, out _));
        Assert.Equal(OpenResult.Replay, server.TryOpen(frame, out _));
    }

    [Fact]
    public void Reordered_frame_is_dropped_and_gaps_are_fine()
    {
        var (client, server) = Pair();
        var f1 = client.Seal(Plain);
        var f2 = client.Seal(Plain);
        var f3 = client.Seal(Plain);
        Assert.Equal(OpenResult.Ok, server.TryOpen(f3, out _));
        Assert.Equal(OpenResult.Replay, server.TryOpen(f2, out _));
        Assert.Equal(OpenResult.Replay, server.TryOpen(f1, out _));
    }

    [Fact]
    public void Tampering_anywhere_is_detected_and_does_not_advance_the_counter()
    {
        var (client, server) = Pair();
        var frame = client.Seal(Plain);
        for (int i = 0; i < frame.Length; i++)
        {
            var bad = (byte[])frame.Clone();
            bad[i] ^= 0x01;
            var result = server.TryOpen(bad, out _);
            Assert.NotEqual(OpenResult.Ok, result); // header, ciphertext or tag
        }
        Assert.Equal(0UL, server.LastReceivedCounter);
        Assert.Equal(OpenResult.Ok, server.TryOpen(frame, out _)); // the genuine frame still works
    }

    [Fact]
    public void A_forged_high_counter_cannot_lock_out_the_real_peer()
    {
        var (client, server) = Pair();
        var forged = new byte[40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(forged, ulong.MaxValue - 1);
        Assert.NotEqual(OpenResult.Ok, server.TryOpen(forged, out _));
        Assert.Equal(OpenResult.Ok, server.TryOpen(client.Seal(Plain), out _));
    }

    [Fact]
    public void A_frame_cannot_be_reflected_back_to_its_sender()
    {
        var (client, server) = Pair();
        // Different keys per direction, and the direction byte is part of the nonce.
        Assert.NotEqual(OpenResult.Ok, client.TryOpen(client.Seal(Plain), out _));
        Assert.NotEqual(OpenResult.Ok, server.TryOpen(server.Seal(Plain), out _));
    }

    [Fact]
    public void Frames_from_another_session_are_rejected()
    {
        var (client1, _) = Pair();
        var (_, server2) = Pair();
        Assert.Equal(OpenResult.AuthFailed, server2.TryOpen(client1.Seal(Plain), out _));
    }

    [Fact]
    public void Short_and_empty_input_is_malformed_not_an_exception()
    {
        var (_, server) = Pair();
        Assert.Equal(OpenResult.Malformed, server.TryOpen([], out _));
        Assert.Equal(OpenResult.Malformed, server.TryOpen(new byte[FrameCrypto.Overhead], out _));
    }

    [Fact]
    public void Fuzz_random_input_never_throws_or_authenticates()
    {
        var (_, server) = Pair();
        var rng = new Random(99);
        var buffer = new byte[200];
        for (int i = 0; i < 20_000; i++)
        {
            rng.NextBytes(buffer);
            Assert.NotEqual(OpenResult.Ok, server.TryOpen(buffer.AsSpan(0, rng.Next(buffer.Length)), out _));
        }
    }

    [Fact]
    public void B64_is_strict()
    {
        var bytes = Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray();
        var text = B64.Encode(bytes);
        Assert.True(B64.TryDecode(text, 16, out var back));
        Assert.Equal(bytes, back);
        Assert.False(B64.TryDecode(text + "=", 16, out _));
        Assert.False(B64.TryDecode(text.Replace('-', '+').Replace('_', '/') + "==", 16, out _));
        Assert.False(B64.TryDecode(text[..^1], 16, out _));
        Assert.False(B64.TryDecode(null, 16, out _));
        // The last character of an unpadded 16-byte string carries two spare bits; non-zero spare bits are rejected.
        Assert.False(B64.TryDecode(new string('A', 21) + "B", 16, out _));
    }
}
