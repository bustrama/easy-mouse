using System.Buffers.Binary;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Crypto;

/// <summary>
/// An established session: seals outgoing frames with an incrementing counter (starting at 1) and
/// opens incoming ones, accepting only strictly increasing counters. The receive counter advances
/// only after authentication succeeds, so forged frames cannot desynchronise the peers.
/// </summary>
public sealed class SecureChannel
{
    private readonly byte[] _sendKey;
    private readonly byte[] _recvKey;
    private readonly FrameDirection _sendDirection;
    private readonly FrameDirection _recvDirection;
    private readonly object _gate = new();
    private ulong _sendCounter;
    private ulong _lastReceived;

    public SecureChannel(byte[] sendKey, byte[] recvKey, FrameDirection sendDirection)
    {
        _sendKey = sendKey;
        _recvKey = recvKey;
        _sendDirection = sendDirection;
        _recvDirection = sendDirection == FrameDirection.ClientToServer ? FrameDirection.ServerToClient : FrameDirection.ClientToServer;
    }

    public static SecureChannel ForServer(byte[] deviceSecret, byte[] nc, byte[] ns)
    {
        var (c2s, s2c) = HandshakeCrypto.DeriveSessionKeys(deviceSecret, nc, ns);
        return new SecureChannel(s2c, c2s, FrameDirection.ServerToClient);
    }

    public static SecureChannel ForClient(byte[] deviceSecret, byte[] nc, byte[] ns)
    {
        var (c2s, s2c) = HandshakeCrypto.DeriveSessionKeys(deviceSecret, nc, ns);
        return new SecureChannel(c2s, s2c, FrameDirection.ClientToServer);
    }

    public ulong LastReceivedCounter { get { lock (_gate) return _lastReceived; } }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        lock (_gate)
        {
            if (_sendCounter == ulong.MaxValue) throw new InvalidOperationException("Send counter exhausted.");
            return FrameCrypto.Seal(_sendKey, _sendDirection, ++_sendCounter, plaintext);
        }
    }

    public OpenResult TryOpen(ReadOnlySpan<byte> frame, out byte[] plaintext)
    {
        plaintext = [];
        if (frame.Length < FrameCrypto.Overhead + 1) return OpenResult.Malformed;
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(frame);
        lock (_gate)
        {
            if (counter == 0 || counter <= _lastReceived) return OpenResult.Replay;
        }
        var result = FrameCrypto.TryOpen(_recvKey, _recvDirection, frame, out counter, out plaintext);
        if (result != OpenResult.Ok) return result;
        lock (_gate)
        {
            if (counter <= _lastReceived) { plaintext = []; return OpenResult.Replay; }
            _lastReceived = counter;
        }
        return OpenResult.Ok;
    }
}
