using EasyMouse.Core.Pairing;
using EasyMouse.Core.Sessions;

namespace EasyMouse.App.Logic;

/// <summary>
/// One approval dialog at a time. Pairing and takeover requests queue up; <see cref="Current"/> is the one on
/// screen. A request that is decided elsewhere (timeout, phone left, window closed) drops out by itself.
/// </summary>
public sealed class ApprovalCoordinator
{
    private readonly object _gate = new();
    private readonly Queue<ApprovalRequest> _queue = new();
    private readonly HashSet<ApprovalRequest> _known = new(ReferenceEqualityComparer.Instance);
    private ApprovalRequest? _current;

    /// <summary>Raised (on any thread) when <see cref="Current"/> changed.</summary>
    public event EventHandler? CurrentChanged;

    public ApprovalRequest? Current { get { lock (_gate) return _current; } }

    public void Attach(PairingService pairing, ConnectionHub hub)
    {
        pairing.Changed += (_, _) => { if (pairing.PendingRequest is { } r) Enqueue(r); };
        hub.TakeoverRequested += (_, r) => Enqueue(r);
        if (pairing.PendingRequest is { } existing) Enqueue(existing);
    }

    public void Enqueue(ApprovalRequest request)
    {
        lock (_gate)
        {
            if (request.IsDecided || !_known.Add(request)) return;
            _queue.Enqueue(request);
        }
        request.Decision.ContinueWith(_ => Advance(), TaskScheduler.Default);
        Advance();
    }

    private void Advance()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_current is { IsDecided: true })
            {
                _known.Remove(_current);
                _current = null;
                changed = true;
            }
            while (_current is null && _queue.TryDequeue(out var next))
            {
                if (next.IsDecided) { _known.Remove(next); continue; }
                _current = next;
                changed = true;
            }
        }
        if (changed) CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Denies everything queued and shown (window closing, app quitting).</summary>
    public void DenyAll()
    {
        List<ApprovalRequest> all;
        lock (_gate) { all = [.. _queue]; if (_current is not null) all.Add(_current); }
        foreach (var r in all) r.Deny();
    }
}
