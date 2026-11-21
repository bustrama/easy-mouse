using EasyMouse.Core.Pairing;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Sessions;

public enum JoinResult { Controlling, Waiting }

/// <summary>
/// Tracks authenticated sockets and enforces "one controlling device at a time". A second device is
/// told <c>busy</c> and may ask to take over; the PC user approves. A device that reconnects (a reloaded
/// page whose old socket has not died yet) replaces its own old socket without a prompt.
/// </summary>
public sealed class ConnectionHub
{
    private readonly object _gate = new();
    private readonly List<ServerSession> _sessions = [];
    private readonly TimeProvider _time;
    private readonly PairingOptions _options;
    private ServerSession? _controller;
    private ApprovalRequest? _pendingTakeover;
    private ServerSession? _pendingTakeoverOwner;

    public ConnectionHub(TimeProvider time, PairingOptions? options = null)
    {
        _time = time;
        _options = options ?? new PairingOptions();
    }

    /// <summary>Raised when a waiting device asks to take over. The UI must call Allow or Deny on the request.</summary>
    public event EventHandler<ApprovalRequest>? TakeoverRequested;
    /// <summary>Raised after a device connected or disconnected.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<string> ConnectedDeviceIds { get { lock (_gate) return [.. _sessions.Select(s => s.DeviceId!).Distinct()]; } }
    public string? ControllerDeviceId { get { lock (_gate) return _controller?.DeviceId; } }
    public int SessionCount { get { lock (_gate) return _sessions.Count; } }

    internal bool IsController(ServerSession session) { lock (_gate) return ReferenceEquals(_controller, session); }

    internal async Task<JoinResult> JoinAsync(ServerSession session)
    {
        List<ServerSession> replaced;
        JoinResult result;
        lock (_gate)
        {
            replaced = [.. _sessions.Where(s => s.DeviceId == session.DeviceId)];
            foreach (var s in replaced) _sessions.Remove(s);
            if (_controller is not null && replaced.Contains(_controller)) _controller = null;
            _sessions.Add(session);
            if (_controller is null) { _controller = session; result = JoinResult.Controlling; }
            else result = JoinResult.Waiting;
        }
        foreach (var s in replaced) await s.CloseAsync(CloseCode.Replaced, "replaced").ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    internal void Leave(ServerSession session)
    {
        ApprovalRequest? orphaned = null;
        lock (_gate)
        {
            _sessions.Remove(session);
            if (ReferenceEquals(_controller, session)) _controller = null;
            if (ReferenceEquals(_pendingTakeoverOwner, session)) orphaned = _pendingTakeover; // its dialog must go away
        }
        orphaned?.Deny();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the PC user to let <paramref name="session"/> take over; grants at once when nobody controls.</summary>
    internal async Task<bool> RequestTakeoverAsync(ServerSession session)
    {
        ApprovalRequest request;
        lock (_gate)
        {
            if (!_sessions.Contains(session)) return false;
            if (_controller is null || ReferenceEquals(_controller, session))
            {
                _controller = session;
                return true;
            }
            if (_pendingTakeover is not null) return false;
            request = new ApprovalRequest(ApprovalKind.Takeover, session.DeviceName ?? "", null, _time, _options.AllowDelay);
            _pendingTakeover = request;
            _pendingTakeoverOwner = session;
        }

        try
        {
            TakeoverRequested?.Invoke(this, request);
            var timeout = Task.Delay(_options.ApprovalTimeout, _time);
            if (await Task.WhenAny(request.Decision, timeout).ConfigureAwait(false) == timeout) request.Deny();
            if (!await request.Decision.ConfigureAwait(false)) return false;

            ServerSession? old;
            lock (_gate)
            {
                if (!_sessions.Contains(session)) return false;
                old = _controller;
                _controller = session;
            }
            if (old is not null && !ReferenceEquals(old, session)) await old.CloseAsync(CloseCode.Replaced, "replaced").ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingTakeover, request)) { _pendingTakeover = null; _pendingTakeoverOwner = null; }
            }
            request.Deny();
        }
    }

    /// <summary>Closes every socket of one device. The sessions are marked closed synchronously, so no further input is processed.</summary>
    public Task CloseDeviceAsync(string deviceId, int code, string reason)
    {
        List<ServerSession> targets;
        lock (_gate) targets = [.. _sessions.Where(s => s.DeviceId == deviceId)];
        return CloseAll(targets, code, reason);
    }

    public Task CloseAllAsync(int code, string reason)
    {
        List<ServerSession> targets;
        lock (_gate) targets = [.. _sessions];
        return CloseAll(targets, code, reason);
    }

    private static Task CloseAll(List<ServerSession> targets, int code, string reason)
        => Task.WhenAll(targets.Select(s => s.CloseAsync(code, reason)));
}
