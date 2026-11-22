using System.IO.Pipes;
using System.Security.Principal;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Platform.Windows.Broker;

/// <summary>Why a command to the broker did not happen. Subclass of <see cref="InputBlockedException"/>: the session drops that input and stays connected.</summary>
public sealed class BrokerException(BrokerFailure failure, string message) : InputBlockedException(message)
{
    public BrokerFailure Failure { get; } = failure;
}

public enum BrokerFailure { NotRunning, Untrusted, Timeout, Refused }

/// <summary>
/// The App's side of the broker: sends one command and waits for its one-byte answer. Connects on demand and verifies the other
/// end first (the pipe must be owned by SYSTEM or an administrator, and the client never lets the server impersonate it).
/// Failures throw <see cref="BrokerException"/>; nothing pretends to have worked.
/// </summary>
public sealed class BrokerInputSink : IInputSink, IDisposable
{
    private readonly TimeSpan _replyTimeout;
    private readonly Func<SecurityIdentifier?, bool> _trustedOwner;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly Func<long> _now;
    private NamedPipeClientStream? _pipe;
    private long _nextAttempt;
    private long _lastUse;
    private readonly HashSet<int> _held = []; // buttons and keys this client pressed and has not lifted
    /// <summary>Idle time after which the pipe is closed (when nothing is held), so the connection exists per burst of input rather than for the life of the App.</summary>
    private const long IdleMilliseconds = 300;
    private BrokerException? _lastFailure;
    private bool _disposed;

    public BrokerInputSink() : this(BrokerProtocol.PipeName, () => Environment.TickCount64) { }

    internal BrokerInputSink(string pipeName, Func<long> nowMilliseconds, Func<SecurityIdentifier?, bool>? trustedOwner = null, TimeSpan? replyTimeout = null)
    {
        _pipeName = pipeName;
        _now = nowMilliseconds;
        _trustedOwner = trustedOwner ?? BrokerTrust.IsTrustedPipeOwner;
        _replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Whether a broker that the service manager says is running actually answers and is the real one: connects, checks the pipe owner,
    /// hangs up. Null means healthy. (The broker serves one client at a time, so do not probe while this App already has a connection.)
    /// </summary>
    public static BrokerFailure? Probe() => Probe(BrokerProtocol.PipeName, BrokerTrust.IsTrustedPipeOwner);

    internal static BrokerFailure? Probe(string pipeName, Func<SecurityIdentifier?, bool> trustedOwner)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeout: 200);
            if (!trustedOwner(OwnerOf(pipe))) return BrokerFailure.Untrusted;
            // The agent checks WHO connected only after it accepts, and hangs up on a caller it does not accept (a portable or dev copy of
            // the App). A harmless release-all tells those apart from a working broker. Ok, Locked and Blocked all mean "it accepted us".
            Span<byte> frame = stackalloc byte[BrokerProtocol.MaxCommandBytes];
            var length = BrokerProtocol.Write(new BrokerCommand(BrokerOp.ReleaseAll), frame);
            using var cts = new CancellationTokenSource(500);
            pipe.WriteAsync(frame[..length].ToArray(), cts.Token).AsTask().GetAwaiter().GetResult();
            var reply = new byte[1];
            if (pipe.ReadAsync(reply, cts.Token).AsTask().GetAwaiter().GetResult() != 1) return BrokerFailure.Refused;
            return reply[0] is (byte)BrokerReply.Ok or (byte)BrokerReply.Locked or (byte)BrokerReply.Blocked ? null : BrokerFailure.Refused;
        }
        catch (OperationCanceledException) { return BrokerFailure.Timeout; }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException) { return BrokerFailure.NotRunning; }
    }

    public bool IsConnected { get { lock (_gate) return _pipe is { IsConnected: true }; } }

    public void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Send(new BrokerCommand(BrokerOp.Move, Math.Clamp(dx, short.MinValue, short.MaxValue), Math.Clamp(dy, short.MinValue, short.MaxValue)));
    }

    public void Button(MouseButton button, bool down) => Send(new BrokerCommand(BrokerOp.Button, (int)button, down ? 1 : 0));

    public void Wheel(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Send(new BrokerCommand(BrokerOp.Wheel, Math.Clamp(dx, short.MinValue, short.MaxValue), Math.Clamp(dy, short.MinValue, short.MaxValue)));
    }

    public void Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return;
        Send(new BrokerCommand(BrokerOp.Text, Text: text));
    }

    public void Key(ushort virtualKey, KeyAction action) => Send(new BrokerCommand(BrokerOp.Key, virtualKey, (int)action));

    /// <summary>Only talks to a broker that is already connected: there is nothing to release in one that was never used.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_pipe is not { IsConnected: true }) return;
        }
        Send(new BrokerCommand(BrokerOp.ReleaseAll));
    }

    private void Send(in BrokerCommand command)
    {
        Span<byte> frame = stackalloc byte[BrokerProtocol.MaxCommandBytes];
        int length;
        try { length = BrokerProtocol.Write(command, frame); }
        catch (ArgumentException) { throw new BrokerException(BrokerFailure.Refused, "The command cannot be sent to the broker."); }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pipe = Connected();
            try
            {
                using var cts = new CancellationTokenSource(_replyTimeout);
                pipe.WriteAsync(frame[..length].ToArray(), cts.Token).AsTask().GetAwaiter().GetResult();
                var reply = new byte[1];
                if (pipe.ReadAsync(reply, cts.Token).AsTask().GetAwaiter().GetResult() != 1) throw new EndOfStreamException();
                if (reply[0] == (byte)BrokerReply.Ok) { Track(command); return; }
                if (reply[0] == (byte)BrokerReply.Invalid) Drop();
                throw new BrokerException(BrokerFailure.Refused, $"The broker refused the input ({(BrokerReply)reply[0]}).");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or EndOfStreamException)
            {
                Drop();
                throw Failed(ex is OperationCanceledException ? BrokerFailure.Timeout : BrokerFailure.NotRunning, "The broker stopped answering.");
            }
        }
    }

    private void Track(in BrokerCommand c)
    {
        _lastUse = _now();
        switch (c.Op)
        {
            case BrokerOp.Button: if (c.B == 1) _held.Add(0x100 + c.A); else _held.Remove(0x100 + c.A); break;
            case BrokerOp.Key: if (c.B == 1) _held.Add(c.A); else if (c.B == 2) _held.Remove(c.A); break;
            case BrokerOp.ReleaseAll: _held.Clear(); break;
        }
    }

    private NamedPipeClientStream Connected()
    {
        // A handle that stays open for the whole session is a handle another process of this user could duplicate (docs/security.md),
        // so after a pause the connection is closed and the next burst connects, and is checked, afresh. Never while something is
        // held down: closing would make the broker lift it.
        if (_pipe is { IsConnected: true } && (_held.Count > 0 || _now() - _lastUse <= IdleMilliseconds)) return _pipe;
        if (_pipe is { IsConnected: true }) Drop();
        Drop();
        // After a failure do not hammer the pipe for every mouse move: answer from the last failure for a second.
        if (_lastFailure is not null && _now() < _nextAttempt) throw _lastFailure;

        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeout: 200);
            _lastUse = _now();
            if (!_trustedOwner(OwnerOf(pipe)))
                throw Failed(BrokerFailure.Untrusted, "Something other than the Easy Mouse broker owns the broker pipe.");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            throw Failed(BrokerFailure.NotRunning, "The Easy Mouse broker is not running.");
        }
        catch (BrokerException)
        {
            pipe.Dispose();
            throw;
        }
        _pipe = pipe;
        _lastFailure = null;
        return pipe;
    }

    private static SecurityIdentifier? OwnerOf(NamedPipeClientStream pipe)
        => pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    private BrokerException Failed(BrokerFailure failure, string message)
    {
        _lastFailure = new BrokerException(failure, message);
        _nextAttempt = _now() + (long)RetryAfterFailure.TotalMilliseconds;
        return _lastFailure;
    }

    private void Drop()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Drop();
        }
    }
}
