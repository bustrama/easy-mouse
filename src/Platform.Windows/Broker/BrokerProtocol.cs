using System.Buffers.Binary;
using System.Text;

namespace EasyMouse.Platform.Windows.Broker;

/// <summary>Operations the broker will perform. Nothing else exists: no run, no read, no settings.</summary>
public enum BrokerOp : byte { Move = 1, Button = 2, Wheel = 3, Key = 4, Text = 5, ReleaseAll = 6 }

/// <summary>One byte the broker answers every command with.</summary>
public enum BrokerReply : byte
{
    Ok = 0,
    /// <summary>Windows refused the input (a desktop it could not attach to).</summary>
    Blocked = 1,
    /// <summary>Malformed command. The broker closes the connection after this.</summary>
    Invalid = 2,
    RateLimited = 3,
    /// <summary>The workstation is locked. The broker never injects input on the lock screen.</summary>
    Locked = 4,
    Failed = 5,
}

/// <summary>A decoded command. <c>A</c>/<c>B</c> carry the numbers of the operation (see <see cref="BrokerProtocol"/>).</summary>
public readonly record struct BrokerCommand(BrokerOp Op, int A = 0, int B = 0, string? Text = null);

/// <summary>
/// The whole wire format between the App and the broker, over a byte-mode named pipe. Big endian, one command at a time,
/// each answered by one <see cref="BrokerReply"/> byte. Nothing is variable except the text, whose length is declared first
/// and capped. This is all the parsing the privileged side does.
/// <code>
/// 01 Move       i16 dx, i16 dy
/// 02 Button     u8 button (0 left, 1 right, 2 middle), u8 down (0/1)
/// 03 Wheel      i16 dx, i16 dy         (1/120 notch units)
/// 04 Key        u16 virtual key (1..254), u8 action (1 down, 2 up, 3 press)
/// 05 Text       u16 byte length (1..1024), UTF-8 bytes (valid UTF-8, no NUL)
/// 06 ReleaseAll (no payload)
/// </code>
/// </summary>
public static class BrokerProtocol
{
    public const string ServiceName = "EasyMouseBroker";
    public const string ServiceDisplayName = "Easy Mouse Broker";
    public const string PipeName = "EasyMouse.Broker";
    public const int MaxTextBytes = 1024;
    /// <summary>Largest command on the wire: op, u16 length, text.</summary>
    public const int MaxCommandBytes = 1 + 2 + MaxTextBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Bytes after the opcode for fixed-size commands; -1 for the length-prefixed text; -2 for an unknown opcode.</summary>
    private static int PayloadLength(BrokerOp op) => op switch
    {
        BrokerOp.Move or BrokerOp.Wheel => 4,
        BrokerOp.Button => 2,
        BrokerOp.Key => 3,
        BrokerOp.ReleaseAll => 0,
        BrokerOp.Text => -1,
        _ => -2,
    };

    /// <summary>Writes <paramref name="command"/> into <paramref name="destination"/> (at least <see cref="MaxCommandBytes"/>). Returns the length. Throws if the command would not pass <see cref="Read"/>.</summary>
    public static int Write(in BrokerCommand command, Span<byte> destination)
    {
        destination[0] = (byte)command.Op;
        var p = destination[1..];
        switch (command.Op)
        {
            case BrokerOp.Move:
            case BrokerOp.Wheel:
                BinaryPrimitives.WriteInt16BigEndian(p, checked((short)command.A));
                BinaryPrimitives.WriteInt16BigEndian(p[2..], checked((short)command.B));
                return 5;
            case BrokerOp.Button:
                if (command.A is < 0 or > 2 || command.B is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(command));
                p[0] = (byte)command.A;
                p[1] = (byte)command.B;
                return 3;
            case BrokerOp.Key:
                if (command.A is < 1 or > 254 || command.B is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(command));
                BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)command.A);
                p[2] = (byte)command.B;
                return 4;
            case BrokerOp.Text:
            {
                var text = command.Text ?? throw new ArgumentNullException(nameof(command));
                var length = StrictUtf8.GetByteCount(text);
                if (length is < 1 or > MaxTextBytes || text.Contains('\0')) throw new ArgumentOutOfRangeException(nameof(command));
                BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)length);
                StrictUtf8.GetBytes(text, p[2..]);
                return 3 + length;
            }
            case BrokerOp.ReleaseAll:
                return 1;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    /// <summary>
    /// Reads one command. Returns <see cref="BrokerReply.Ok"/> or <see cref="BrokerReply.Invalid"/> (after which the stream is
    /// out of step and must be closed). Throws <see cref="EndOfStreamException"/> when the peer is gone.
    /// </summary>
    public static BrokerReply Read(Stream stream, out BrokerCommand command)
    {
        command = default;
        Span<byte> head = stackalloc byte[2];
        ReadExactly(stream, head[..1]);
        var op = (BrokerOp)head[0];
        var length = PayloadLength(op);
        if (length == -2) return BrokerReply.Invalid;

        if (length == -1)
        {
            ReadExactly(stream, head);
            var n = BinaryPrimitives.ReadUInt16BigEndian(head);
            if (n is < 1 or > MaxTextBytes) return BrokerReply.Invalid;
            var buffer = new byte[n];
            ReadExactly(stream, buffer);
            if (buffer.Contains((byte)0)) return BrokerReply.Invalid;
            string text;
            try { text = StrictUtf8.GetString(buffer); }
            catch (DecoderFallbackException) { return BrokerReply.Invalid; }
            command = new BrokerCommand(op, Text: text);
            return BrokerReply.Ok;
        }

        Span<byte> p = stackalloc byte[4];
        ReadExactly(stream, p[..length]);
        switch (op)
        {
            case BrokerOp.Move:
            case BrokerOp.Wheel:
                command = new BrokerCommand(op, BinaryPrimitives.ReadInt16BigEndian(p), BinaryPrimitives.ReadInt16BigEndian(p[2..]));
                return BrokerReply.Ok;
            case BrokerOp.Button:
                if (p[0] > 2 || p[1] > 1) return BrokerReply.Invalid;
                command = new BrokerCommand(op, p[0], p[1]);
                return BrokerReply.Ok;
            case BrokerOp.Key:
            {
                var vk = BinaryPrimitives.ReadUInt16BigEndian(p);
                if (vk is < 1 or > 254 || p[2] is < 1 or > 3) return BrokerReply.Invalid;
                command = new BrokerCommand(op, vk, p[2]);
                return BrokerReply.Ok;
            }
            default: // ReleaseAll
                command = new BrokerCommand(op);
                return BrokerReply.Ok;
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var n = stream.Read(buffer[done..]);
            if (n == 0) throw new EndOfStreamException();
            done += n;
        }
    }
}

/// <summary>Token bucket: a steady rate with a short burst. Cheap commands cost 1, text costs more.</summary>
public sealed class BrokerRateLimiter
{
    public const double DefaultPerSecond = 1000;
    public const double DefaultBurst = 400;
    public const double TextCost = 25;

    private readonly double _perSecond, _capacity;
    private readonly Func<long> _now;
    private double _tokens;
    private long _last;

    public BrokerRateLimiter(double perSecond, double capacity, Func<long> nowMilliseconds)
    {
        (_perSecond, _capacity, _now) = (perSecond, capacity, nowMilliseconds);
        _tokens = capacity;
        _last = nowMilliseconds();
    }

    public BrokerRateLimiter() : this(DefaultPerSecond, DefaultBurst, () => Environment.TickCount64) { }

    public static double CostOf(BrokerOp op) => op == BrokerOp.Text ? TextCost : 1;

    public bool TryTake(BrokerOp op)
    {
        var now = _now();
        _tokens = Math.Min(_capacity, _tokens + (now - _last) * _perSecond / 1000.0);
        _last = now;
        var cost = CostOf(op);
        if (_tokens < cost) return false;
        _tokens -= cost;
        return true;
    }
}
