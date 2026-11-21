using System.Buffers.Binary;
using System.Text;

namespace EasyMouse.Core.Protocol;

public enum FrameDirection : byte { ClientToServer = 0, ServerToClient = 1 }

public enum FrameError
{
    None,
    Empty,
    UnknownOp,
    BadLength,
    BadValue,
    BadUtf8,
    TooLong,
}

/// <summary>
/// Plaintext layout: <c>op (u8) | payload</c>, big-endian integers. Parsing is strict: an unknown
/// opcode (or an opcode sent in the wrong direction), a wrong payload length, an out-of-range
/// value or invalid UTF-8 is an error. Never throws on untrusted input.
/// </summary>
public static class FrameCodec
{
    public const int MaxTextBytes = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static FrameError TryParse(ReadOnlySpan<byte> plaintext, FrameDirection direction, int maxTextBytes, out Message? message)
    {
        message = null;
        if (plaintext.Length == 0) return FrameError.Empty;
        var payload = plaintext[1..];
        bool c2s = direction == FrameDirection.ClientToServer;

        switch ((Op)plaintext[0])
        {
            case Op.Move when c2s:
                if (payload.Length != 4) return FrameError.BadLength;
                message = new MoveMessage(BinaryPrimitives.ReadInt16BigEndian(payload), BinaryPrimitives.ReadInt16BigEndian(payload[2..]));
                return FrameError.None;
            case Op.Button when c2s:
                if (payload.Length != 2) return FrameError.BadLength;
                if (payload[0] > 2 || payload[1] > 1) return FrameError.BadValue;
                message = new ButtonMessage((MouseButton)payload[0], payload[1] == 1);
                return FrameError.None;
            case Op.Wheel when c2s:
                if (payload.Length != 4) return FrameError.BadLength;
                message = new WheelMessage(BinaryPrimitives.ReadInt16BigEndian(payload), BinaryPrimitives.ReadInt16BigEndian(payload[2..]));
                return FrameError.None;
            case Op.Text when c2s:
                if (payload.Length > maxTextBytes) return FrameError.TooLong;
                if (!TryDecode(payload, out var text)) return FrameError.BadUtf8;
                message = new TextMessage(text);
                return FrameError.None;
            case Op.Key when c2s:
                if (payload.Length != 3) return FrameError.BadLength;
                if (payload[2] is < 1 or > 3) return FrameError.BadValue;
                message = new KeyMessage(BinaryPrimitives.ReadUInt16BigEndian(payload), (KeyAction)payload[2]);
                return FrameError.None;
            case Op.Media when c2s:
                if (payload.Length != 1) return FrameError.BadLength;
                if (payload[0] > 6) return FrameError.BadValue;
                message = new MediaMessage((MediaCommand)payload[0]);
                return FrameError.None;
            case Op.Json:
                if (!TryDecode(payload, out var json)) return FrameError.BadUtf8;
                message = new JsonMessage(json);
                return FrameError.None;
            case Op.State when !c2s:
                if (!TryDecode(payload, out var state)) return FrameError.BadUtf8;
                message = new StateMessage(state);
                return FrameError.None;
            case Op.Ping:
                if (payload.Length != 0) return FrameError.BadLength;
                message = new PingMessage();
                return FrameError.None;
            default:
                return FrameError.UnknownOp;
        }
    }

    public static FrameError TryParse(ReadOnlySpan<byte> plaintext, FrameDirection direction, out Message? message)
        => TryParse(plaintext, direction, MaxTextBytes, out message);

    public static byte[] Encode(Message message)
    {
        switch (message)
        {
            case MoveMessage m: return Pack(Op.Move, w => { WriteI16(w, 0, m.Dx); WriteI16(w, 2, m.Dy); }, 4);
            case WheelMessage m: return Pack(Op.Wheel, w => { WriteI16(w, 0, m.Dx); WriteI16(w, 2, m.Dy); }, 4);
            case ButtonMessage m: return [(byte)Op.Button, (byte)m.Button, (byte)(m.Down ? 1 : 0)];
            case KeyMessage m: return Pack(Op.Key, w => { BinaryPrimitives.WriteUInt16BigEndian(w, m.VirtualKey); w[2] = (byte)m.Action; }, 3);
            case MediaMessage m: return [(byte)Op.Media, (byte)m.Command];
            case TextMessage m: return WithBody(Op.Text, StrictUtf8.GetBytes(m.Text));
            case JsonMessage m: return WithBody(Op.Json, StrictUtf8.GetBytes(m.Json));
            case StateMessage m: return WithBody(Op.State, StrictUtf8.GetBytes(m.Json));
            case PingMessage: return [(byte)Op.Ping];
            default: throw new ArgumentException("Unsupported message type", nameof(message));
        }
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        try { text = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { text = ""; return false; }
    }

    private static byte[] WithBody(Op op, byte[] body)
    {
        var result = new byte[body.Length + 1];
        result[0] = (byte)op;
        body.CopyTo(result, 1);
        return result;
    }

    private delegate void Filler(Span<byte> span);

    private static byte[] Pack(Op op, Filler fill, int length)
    {
        var result = new byte[length + 1];
        result[0] = (byte)op;
        fill(result.AsSpan(1));
        return result;
    }

    private static void WriteI16(Span<byte> span, int offset, short value) => BinaryPrimitives.WriteInt16BigEndian(span[offset..], value);
}
