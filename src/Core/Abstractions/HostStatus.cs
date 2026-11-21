namespace EasyMouse.Core.Abstractions;

/// <summary>
/// PC-side conditions the phone must know about. While <see cref="Locked"/> the session drops input
/// (Easy Mouse never unlocks the PC). The App updates it from session-lock and elevation events.
/// </summary>
public sealed class HostStatus
{
    private readonly object _gate = new();
    private bool _locked;
    private bool _elevated;

    public event EventHandler? Changed;

    public bool Locked { get { lock (_gate) return _locked; } }
    public bool Elevated { get { lock (_gate) return _elevated; } }

    public void Update(bool locked, bool elevated)
    {
        lock (_gate)
        {
            if (_locked == locked && _elevated == elevated) return;
            _locked = locked;
            _elevated = elevated;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
