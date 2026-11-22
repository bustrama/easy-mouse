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
    /// <summary>Largest image in an 0x12 art frame (server to client only). Client-to-server frames stay under 4 KiB.</summary>
    public const int MaxArtworkBytes = 128 * 1024;
    public const int MaxArtworkIdLength = 32;

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
            case Op.Art when !c2s:
                return TryParseArt(payload, out message);
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
            case ArtMessage m: return EncodeArt(m);
            case PingMessage: return [(byte)Op.Ping];
            default: throw new ArgumentException("Unsupported message type", nameof(message));
        }
    }

    // 0x12 art: u8 idLen (1..32) | id (lowercase a-z0-9) | u8 type (0 jpeg, 1 png) | image bytes (1..MaxArtworkBytes)
    private static FrameError TryParseArt(ReadOnlySpan<byte> payload, out Message? message)
    {
        message = null;
        if (payload.Length < 3) return FrameError.BadLength;
        int idLen = payload[0];
        if (idLen is < 1 or > MaxArtworkIdLength || payload.Length < 1 + idLen + 2) return FrameError.BadLength;
        var idBytes = payload.Slice(1, idLen);
        foreach (var b in idBytes)
            if (!(b is >= (byte)'0' and <= (byte)'9' || b is >= (byte)'a' and <= (byte)'z')) return FrameError.BadValue;
        var type = payload[1 + idLen];
        if (type > (byte)ArtworkType.Png) return FrameError.BadValue;
        var data = payload[(2 + idLen)..];
        if (data.Length > MaxArtworkBytes) return FrameError.TooLong;
        message = new ArtMessage(Encoding.ASCII.GetString(idBytes), (ArtworkType)type, data.ToArray());
        return FrameError.None;
    }

    private static byte[] EncodeArt(ArtMessage m)
    {
        var id = Encoding.ASCII.GetBytes(m.Id);
        if (id.Length is < 1 or > MaxArtworkIdLength) throw new ArgumentException("Invalid artwork id", nameof(m));
        if (m.Data.Length is < 1 or > MaxArtworkBytes) throw new ArgumentException("Artwork size out of range", nameof(m));
        var result = new byte[1 + 1 + id.Length + 1 + m.Data.Length];
        result[0] = (byte)Op.Art;
        result[1] = (byte)id.Length;
        id.CopyTo(result, 2);
        result[2 + id.Length] = (byte)m.Type;
        m.Data.CopyTo(result, 3 + id.Length);
        return result;
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
