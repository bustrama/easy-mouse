using EasyMouse.Core.Crypto;
using EasyMouse.Core.Devices;

namespace EasyMouse.Core.Pairing;

public sealed record PairingOptions
{
    public TimeSpan SecretLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxFailedAttempts { get; init; } = 5;
    public TimeSpan AllowDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>How long the PC user has to click before the request is denied.</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxNameLength { get; init; } = 64;
}

public enum PairingState { Closed, Open, Pending }

public enum PairingError { None, WindowClosed, Expired, Busy, BadMac, BadName }

public enum PairingResultKind { Approved, Denied }

/// <summary>Result of an approved pairing: the new device and the <c>wrap</c> blob (AEAD of its secret).</summary>
public sealed record PairingOutcome(PairingResultKind Kind, Device? Device, byte[]? Wrap);

public sealed record PairingBegin(PairingError Error, byte[]? Ns, ApprovalRequest? Request, Task<PairingOutcome>? Outcome)
{
    public bool Ok => Error == PairingError.None;
}

/// <summary>The QR content currently on screen.</summary>
public sealed record PairingSecret(byte[] Secret, DateTimeOffset ExpiresAt)
{
    /// <summary>Fragment for the QR URL: <c>k=&lt;b64&gt;</c>.</summary>
    public string Fragment => "k=" + B64.Encode(Secret);
}

/// <summary>
/// Pairing state machine. Pairing is accepted only while the QR window is open. The one-time secret
/// is spent by the first valid <c>pair.hello</c> (whatever the user then decides) and replaced; five bad
/// attempts also replace it. One request may be pending at a time. An expired secret is not replaced on its
/// own: the UI calls <see cref="Regenerate"/> (or reopens the window) when it refreshes the QR.
/// </summary>
public sealed class PairingService
{
    private readonly DeviceManager _devices;
    private readonly TimeProvider _time;
    private readonly PairingOptions _options;
    private readonly object _gate = new();

    private PairingState _state = PairingState.Closed;
    private bool _windowOpen;
    private PairingSecret? _secret;
    private ApprovalRequest? _pending;
    private int _failures;

    public PairingService(DeviceManager devices, TimeProvider time, PairingOptions? options = null)
    {
        _devices = devices;
        _time = time;
        _options = options ?? new PairingOptions();
    }

    /// <summary>Raised after any state change (window, secret, pending request). May run on any thread.</summary>
    public event EventHandler? Changed;

    public PairingState State { get { lock (_gate) return _state; } }
    public PairingSecret? CurrentSecret { get { lock (_gate) return _secret; } }
    public ApprovalRequest? PendingRequest { get { lock (_gate) return _pending; } }
    public int FailedAttempts { get { lock (_gate) return _failures; } }

    public void OpenWindow()
    {
        lock (_gate)
        {
            _windowOpen = true;
            NewSecretLocked();
            _state = _pending is null ? PairingState.Open : PairingState.Pending;
        }
        RaiseChanged();
    }

    /// <summary>Closes the QR window: the secret is discarded and a pending request is denied.</summary>
    public void CloseWindow()
    {
        ApprovalRequest? pending;
        lock (_gate)
        {
            _windowOpen = false;
            _secret = null;
            _failures = 0;
            pending = _pending;
            _state = pending is null ? PairingState.Closed : PairingState.Pending;
        }
        pending?.Deny();
        RaiseChanged();
    }

    public void Regenerate()
    {
        lock (_gate)
        {
            if (!_windowOpen) return;
            NewSecretLocked();
        }
        RaiseChanged();
    }

    private void NewSecretLocked()
    {
        _secret = new PairingSecret(HandshakeCrypto.Random(HandshakeCrypto.PairSecretLength), _time.GetUtcNow() + _options.SecretLifetime);
        _failures = 0;
    }

    /// <summary>Handles a validated-shape <c>pair.hello</c>. Never throws on bad input.</summary>
    public PairingBegin Begin(string name, byte[] nc, byte[] mac)
    {
        byte[] k;
        byte[] ns;
        ApprovalRequest request;
        bool failureRegenerated = false;
        lock (_gate)
        {
            if (!_windowOpen || _secret is null) return Fail(PairingError.WindowClosed);
            if (_time.GetUtcNow() >= _secret.ExpiresAt) return Fail(PairingError.Expired);
            if (_pending is not null) return Fail(PairingError.Busy);

            k = _secret.Secret;
            var expected = HandshakeCrypto.PairMac(k, nc, name);
            if (nc.Length != HandshakeCrypto.NonceLength || !HandshakeCrypto.MacEquals(expected, mac))
            {
                if (++_failures >= _options.MaxFailedAttempts)
                {
                    NewSecretLocked();
                    failureRegenerated = true;
                }
                goto Failed;
            }
            if (!IsValidName(name)) return Fail(PairingError.BadName);

            ns = HandshakeCrypto.Random(HandshakeCrypto.NonceLength);
            var code = HandshakeCrypto.PairCode(k, nc, ns);
            request = new ApprovalRequest(ApprovalKind.Pair, name, code, _time, _options.AllowDelay);
            _pending = request;
            _state = PairingState.Pending;
            NewSecretLocked(); // spent: the next scan needs a fresh QR
        }
        RaiseChanged();
        return new PairingBegin(PairingError.None, ns, request, CompleteAsync(request, name, k, nc, ns));

    Failed:
        if (failureRegenerated) RaiseChanged();
        return new PairingBegin(PairingError.BadMac, null, null, null);

        static PairingBegin Fail(PairingError error) => new(error, null, null, null);
    }

    private async Task<PairingOutcome> CompleteAsync(ApprovalRequest request, string name, byte[] k, byte[] nc, byte[] ns)
    {
        try
        {
            var timeout = Task.Delay(_options.ApprovalTimeout, _time);
            var first = await Task.WhenAny(request.Decision, timeout).ConfigureAwait(false);
            if (first == timeout) request.Deny();

            if (!await request.Decision.ConfigureAwait(false))
                return new PairingOutcome(PairingResultKind.Denied, null, null);

            var now = _time.GetUtcNow();
            var secret = HandshakeCrypto.Random(HandshakeCrypto.DeviceSecretLength);
            var device = new Device(B64.Encode(HandshakeCrypto.Random(HandshakeCrypto.DeviceIdLength)), name, secret, now, now);
            _devices.Store.Add(device);
            return new PairingOutcome(PairingResultKind.Approved, device, HandshakeCrypto.Wrap(k, nc, ns, secret));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, request))
                {
                    _pending = null;
                    _state = _windowOpen ? PairingState.Open : PairingState.Closed;
                }
            }
            RaiseChanged();
        }
    }

    private bool IsValidName(string name)
    {
        if (name.Length == 0 || name.Length > _options.MaxNameLength) return false;
        foreach (var ch in name)
        {
            if (char.IsControl(ch)) return false;
            // Bidi overrides/isolates could make the PC dialog show a different name than the phone sent.
            if (ch is >= '‪' and <= '‮' or >= '⁦' and <= '⁩') return false;
        }
        return string.IsNullOrWhiteSpace(name) is false;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
