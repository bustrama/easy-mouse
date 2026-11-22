using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Crypto;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Pairing;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Sessions;

/// <summary>
/// One WebSocket connection: pairing or connect handshake, then the encrypted frame loop. It is
/// transport-agnostic: the host feeds it received messages and gives it an <see cref="ISessionTransport"/>
/// to send on. Receive calls must be sequential (one at a time); everything else is thread-safe.
/// Malformed or unauthorised input closes the socket with the code from docs/protocol.md.
/// </summary>
public sealed class ServerSession
{
    private enum Phase { AwaitHello, AwaitAuth, AwaitPairApproval, Waiting, Controlling, Closed }

    private const int MaxOutstandingInvocations = 4;
    private const int MaxFieldLength = 64;

    private readonly CoreServices _svc;
    private readonly ISessionTransport _transport;
    private readonly RateLimiter _limiter;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _invocations = new(MaxOutstandingInvocations, MaxOutstandingInvocations);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private Phase _phase = Phase.AwaitHello;
    private byte[]? _nc;
    private byte[]? _ns;
    private Device? _candidate;
    private SecureChannel? _channel;
    private ApprovalRequest? _pairRequest;
    private bool _takeoverRequested;
    private int _invalidFrames;
    private EventHandler? _mediaHandler;
    private string? _sentArtId; // guarded by _gate: the artwork id this device already has
    private EventHandler? _statusHandler;
    private EventHandler? _layoutHandler;
    private readonly SemaphoreSlim _layoutLock = new(1, 1);
    private string? _lastLayoutJson;

    public ServerSession(CoreServices services, ISessionTransport transport)
    {
        _svc = services;
        _transport = transport;
        _limiter = new RateLimiter(services.Limits.FramesPerSecond, services.Limits.FrameBurst, services.Time);
    }

    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }
    public bool IsClosed { get { lock (_gate) return _phase == Phase.Closed; } }
    public bool IsControlling { get { lock (_gate) return _phase == Phase.Controlling; } }
    /// <summary>Frames dropped because of replay, bad tag or bad length.</summary>
    public int DroppedFrames => Volatile.Read(ref _invalidFrames);

    /// <summary>Arms the handshake timeout. Call once, right after accepting the socket.</summary>
    public void Start() => _ = HandshakeTimeoutAsync();

    private async Task HandshakeTimeoutAsync()
    {
        try { await Task.Delay(_svc.Limits.HandshakeTimeout, _svc.Time, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (CurrentPhase is Phase.AwaitHello or Phase.AwaitAuth)
            await CloseAsync(CloseCode.Unauthorized, "handshake timeout").ConfigureAwait(false);
    }

    private Phase CurrentPhase { get { lock (_gate) return _phase; } }

    // ---------------------------------------------------------------- receive

    public async Task OnTextAsync(string text)
    {
        var phase = CurrentPhase;
        if (phase == Phase.Closed) return;
        if (Encoding.UTF8.GetByteCount(text) > _svc.Limits.MaxFrameBytes)
        {
            await CloseAsync(CloseCode.Malformed, "too large").ConfigureAwait(false);
            return;
        }
        if (!TryParseFlat(text, out var fields) || !fields.TryGetValue("t", out var type))
        {
            await CloseAsync(CloseCode.Malformed, "bad message").ConfigureAwait(false);
            return;
        }

        switch (phase)
        {
            case Phase.AwaitHello when type == "hello": await OnHelloAsync(fields).ConfigureAwait(false); break;
            case Phase.AwaitHello when type == "pair.hello": await OnPairHelloAsync(fields).ConfigureAwait(false); break;
            case Phase.AwaitAuth when type == "auth": await OnAuthAsync(fields).ConfigureAwait(false); break;
            case Phase.Waiting when type == "takeover": StartTakeover(); break;
            default: await CloseAsync(CloseCode.Malformed, "unexpected message").ConfigureAwait(false); break;
        }
    }

    public async Task OnBinaryAsync(ReadOnlyMemory<byte> data)
    {
        var phase = CurrentPhase;
        if (phase == Phase.Closed) return;
        if (phase is not (Phase.Controlling or Phase.Waiting))
        {
            await CloseAsync(CloseCode.Malformed, "unexpected frame").ConfigureAwait(false);
            return;
        }
        if (data.Length > _svc.Limits.MaxFrameBytes)
        {
            await CloseAsync(CloseCode.Malformed, "too large").ConfigureAwait(false);
            return;
        }
        if (!_limiter.TryTake())
        {
            await CloseAsync(CloseCode.RateLimited, "rate limited").ConfigureAwait(false);
            return;
        }

        var channel = _channel!;
        if (channel.TryOpen(data.Span, out var plaintext) != OpenResult.Ok)
        {
            if (Interlocked.Increment(ref _invalidFrames) >= _svc.Limits.MaxInvalidFrames)
                await CloseAsync(CloseCode.Malformed, "too many invalid frames").ConfigureAwait(false);
            return; // dropped: replayed, tampered or truncated
        }
        // A waiting (busy) device must not drive the PC even though it is authenticated.
        if (phase != Phase.Controlling || !_svc.Hub.IsController(this)) return;

        if (FrameCodec.TryParse(plaintext, FrameDirection.ClientToServer, _svc.Limits.MaxTextBytes, out var message) != FrameError.None)
        {
            await CloseAsync(CloseCode.Malformed, "bad frame").ConfigureAwait(false);
            return;
        }
        await DispatchAsync(message!).ConfigureAwait(false);
    }

    private async Task DispatchAsync(Message message)
    {
        bool locked = _svc.Status.Locked;
        try
        {
            switch (message)
            {
                case MoveMessage m when !locked: _svc.Input.MoveRelative(m.Dx, m.Dy); break;
                case ButtonMessage m when !locked: _svc.Input.Button(m.Button, m.Down); break;
                case WheelMessage m when !locked: _svc.Input.Wheel(m.Dx, m.Dy); break;
                case TextMessage m when !locked: _svc.Input.Text(m.Text); break;
                case KeyMessage m when !locked: _svc.Input.Key(m.VirtualKey, m.Action); break;
                case MediaMessage m when !locked: _svc.Media.Execute(m.Command); break;
                case JsonMessage m: await OnJsonAsync(m.Json).ConfigureAwait(false); break;
                case PingMessage: await SendFrameAsync(new PingMessage()).ConfigureAwait(false); break;
                // Locked: input is dropped; the phone already knows from the state message.
            }
        }
        catch (InputBlockedException)
        {
            // The PC refuses input right now (elevated foreground window, UAC, lock screen). That is a normal condition,
            // not a protocol error: drop this input and stay connected; host.elevated/locked in the state frame tells the phone.
        }
    }

    // ---------------------------------------------------------------- pairing

    private async Task OnPairHelloAsync(Dictionary<string, string> f)
    {
        if (!f.TryGetValue("name", out var name) || !B64.TryDecode(f.GetValueOrDefault("nc"), HandshakeCrypto.NonceLength, out var nc)
            || !B64.TryDecode(f.GetValueOrDefault("mac"), HandshakeCrypto.MacLength, out var mac))
        {
            await CloseAsync(CloseCode.Malformed, "bad pair.hello").ConfigureAwait(false);
            return;
        }

        var begin = _svc.Pairing.Begin(name, nc, mac);
        if (!begin.Ok)
        {
            var code = begin.Error switch
            {
                PairingError.BadMac => CloseCode.Unauthorized,
                PairingError.BadName => CloseCode.Malformed,
                _ => CloseCode.PairingClosed,
            };
            await CloseAsync(code, "pairing refused").ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (_phase == Phase.Closed) { begin.Request!.Deny(); return; }
            _phase = Phase.AwaitPairApproval;
        }
        _pairRequest = begin.Request;
        await SendTextAsync($"{{\"t\":\"pair.pending\",\"ns\":\"{B64.Encode(begin.Ns!)}\"}}").ConfigureAwait(false);
        _ = RunPairingAsync(begin.Outcome!);
    }

    private async Task RunPairingAsync(Task<PairingOutcome> outcomeTask)
    {
        try
        {
            var outcome = await outcomeTask.ConfigureAwait(false);
            if (outcome.Kind == PairingResultKind.Approved)
            {
                var device = outcome.Device!;
                if (IsClosed)
                {
                    _svc.Devices.Store.Remove(device.Id); // the phone never received its secret
                    return;
                }
                try
                {
                    await SendTextAsync($"{{\"t\":\"pair.ok\",\"id\":\"{device.Id}\",\"wrap\":\"{B64.Encode(outcome.Wrap!)}\"}}").ConfigureAwait(false);
                }
                catch
                {
                    _svc.Devices.Store.Remove(device.Id);
                    throw;
                }
                await CloseAsync(CloseCode.Normal, "paired").ConfigureAwait(false);
            }
            else
            {
                if (!IsClosed) await SendTextAsync("{\"t\":\"pair.denied\"}").ConfigureAwait(false);
                await CloseAsync(CloseCode.Normal, "denied").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Includes a failed device-store write after the user approved: report it and release the phone
            // instead of leaving it waiting on a task nobody observes.
            if (ex is not (WebSocketException or ObjectDisposedException or OperationCanceledException))
                _svc.OnError?.Invoke("pairing failed after approval", ex);
            await CloseAsync(CloseCode.InternalError, "pairing failed").ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- connect

    private async Task OnHelloAsync(Dictionary<string, string> f)
    {
        if (!B64.TryDecode(f.GetValueOrDefault("id"), HandshakeCrypto.DeviceIdLength, out _)
            || !B64.TryDecode(f.GetValueOrDefault("nc"), HandshakeCrypto.NonceLength, out var nc))
        {
            await CloseAsync(CloseCode.Malformed, "bad hello").ConfigureAwait(false);
            return;
        }

        // An unknown, revoked or expired id still gets a challenge and fails at auth with 4401: the
        // server does not reveal which ids exist.
        _candidate = _svc.Devices.FindActive(f["id"]);
        _nc = nc;
        _ns = HandshakeCrypto.Random(HandshakeCrypto.NonceLength);
        lock (_gate)
        {
            if (_phase == Phase.Closed) return;
            _phase = Phase.AwaitAuth;
        }
        await SendTextAsync($"{{\"t\":\"challenge\",\"ns\":\"{B64.Encode(_ns)}\"}}").ConfigureAwait(false);
    }

    private async Task OnAuthAsync(Dictionary<string, string> f)
    {
        if (!B64.TryDecode(f.GetValueOrDefault("mac"), HandshakeCrypto.MacLength, out var mac))
        {
            await CloseAsync(CloseCode.Malformed, "bad auth").ConfigureAwait(false);
            return;
        }

        var device = _candidate is null ? null : _svc.Devices.FindActive(_candidate.Id);
        if (device is null || !HandshakeCrypto.MacEquals(HandshakeCrypto.ConnectMac(device.Secret, false, _nc!, _ns!), mac))
        {
            await CloseAsync(CloseCode.Unauthorized, "unauthorised").ConfigureAwait(false);
            return;
        }

        DeviceId = device.Id;
        DeviceName = device.Name;
        _channel = SecureChannel.ForServer(device.Secret, _nc!, _ns!);
        var ready = HandshakeCrypto.ConnectMac(device.Secret, true, _nc!, _ns!);
        await SendTextAsync($"{{\"t\":\"ready\",\"mac\":\"{B64.Encode(ready)}\"}}").ConfigureAwait(false);

        var join = await _svc.Hub.JoinAsync(this).ConfigureAwait(false);
        // Revoked between the lookup and joining the hub: the hub close above would have missed us.
        if (_svc.Devices.Store.Get(device.Id) is null)
        {
            await CloseAsync(CloseCode.Unauthorized, "revoked").ConfigureAwait(false);
            return;
        }
        _svc.Devices.MarkSeen(device.Id);

        if (join == JoinResult.Controlling)
        {
            if (!SetPhase(Phase.Controlling)) return;
            await BecomeControllerAsync().ConfigureAwait(false);
        }
        else
        {
            if (!SetPhase(Phase.Waiting)) return;
            await SendTextAsync("{\"t\":\"busy\"}").ConfigureAwait(false);
        }
    }

    private bool SetPhase(Phase phase)
    {
        lock (_gate)
        {
            if (_phase == Phase.Closed) return false;
            _phase = phase;
            return true;
        }
    }

    private void StartTakeover()
    {
        lock (_gate)
        {
            if (_takeoverRequested) return;
            _takeoverRequested = true;
        }
        _ = RunTakeoverAsync();
    }

    private async Task RunTakeoverAsync()
    {
        try
        {
            bool granted = await _svc.Hub.RequestTakeoverAsync(this).ConfigureAwait(false);
            if (IsClosed) return;
            if (granted)
            {
                if (!SetPhase(Phase.Controlling)) return;
                await SendTextAsync("{\"t\":\"active\"}").ConfigureAwait(false);
                await BecomeControllerAsync().ConfigureAwait(false);
            }
            else
            {
                await SendTextAsync("{\"t\":\"takeover.denied\"}").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            await CloseAsync(CloseCode.InternalError, "send failed").ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _takeoverRequested = false;
        }
    }

    private async Task BecomeControllerAsync()
    {
        _mediaHandler = (_, _) => _ = PushStateAsync();
        _statusHandler = (_, _) => _ = PushStateAsync();
        _svc.Media.Changed += _mediaHandler;
        _svc.Status.Changed += _statusHandler;
        _layoutHandler = (_, _) => _ = PushLayoutAsync(initial: false);
        _svc.Layouts.Changed += _layoutHandler;
        if (IsClosed) Unsubscribe(); // closed while subscribing
        await PushStateAsync().ConfigureAwait(false);
        await PushLayoutAsync(initial: true).ConfigureAwait(false);
    }

    private void Unsubscribe()
    {
        if (_mediaHandler is not null) _svc.Media.Changed -= _mediaHandler;
        if (_statusHandler is not null) _svc.Status.Changed -= _statusHandler;
        if (_layoutHandler is not null) _svc.Layouts.Changed -= _layoutHandler;
    }

    // ---------------------------------------------------------------- state and JSON messages

    private async Task PushStateAsync()
    {
        try
        {
            if (IsClosed || !_svc.Hub.IsController(this)) return;
            var snapshot = _svc.Media.GetSnapshot();
            await SendFrameAsync(new StateMessage(BuildStateJson(snapshot, _svc.Status))).ConfigureAwait(false);
            // Artwork is sent once per id (after ready and when the image changes), never on every state tick.
            var art = snapshot.Artwork;
            bool sendArt;
            lock (_gate)
            {
                sendArt = art is not null && art.Id != _sentArtId && art.Data.Length is > 0 and <= FrameCodec.MaxArtworkBytes;
                _sentArtId = art?.Id;
            }
            if (sendArt) await SendFrameAsync(new ArtMessage(art!.Id, art.Type, art.Data)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            await CloseAsync(CloseCode.InternalError, "send failed").ConfigureAwait(false);
        }
    }

    private const string EmptyLayoutJson = "{\"t\":\"remotes\",\"remotes\":[]}";

    /// <summary>
    /// The 0x10 remote layouts, to the controller only. Nothing is sent at connect when there is nothing to show; afterwards
    /// a change is sent even when it empties the list (the phone replaces its list), but never twice with identical text.
    /// Serialised so a slow send cannot overtake a newer one.
    /// </summary>
    private async Task PushLayoutAsync(bool initial)
    {
        try
        {
            await _layoutLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (IsClosed || !_svc.Hub.IsController(this)) return;
                var json = _svc.Layouts.CurrentJson;
                if (initial && json is null) return;
                json ??= EmptyLayoutJson;
                if (json == _lastLayoutJson) return;
                _lastLayoutJson = json;
                await SendFrameAsync(new JsonMessage(json)).ConfigureAwait(false);
            }
            finally { _layoutLock.Release(); }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            await CloseAsync(CloseCode.InternalError, "send failed").ConfigureAwait(false);
        }
    }

    /// <summary>The 0x11 state message: always a full snapshot (docs/protocol.md).</summary>
    public static string BuildStateJson(MediaSnapshot media, HostStatus status)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("t", "state");
            if (media.NowPlaying is { } np)
            {
                w.WriteStartObject("media");
                w.WriteString("status", np.Status.ToString().ToLowerInvariant());
                if (np.Title is not null) w.WriteString("title", SanitizeText(np.Title));
                if (np.Artist is not null) w.WriteString("artist", SanitizeText(np.Artist));
                if (np.PositionMs is long p) w.WriteNumber("positionMs", p);
                if (np.DurationMs is long d) w.WriteNumber("durationMs", d);
                w.WriteEndObject();
            }
            else w.WriteNull("media");
            if (media.Artwork is { } art && art.Data.Length is > 0 and <= FrameCodec.MaxArtworkBytes) w.WriteString("artId", art.Id);
            else w.WriteNull("artId");
            if (media.Volume is { } v)
            {
                w.WriteStartObject("volume");
                w.WriteNumber("level", v.Level);
                w.WriteBoolean("muted", v.Muted);
                w.WriteEndObject();
            }
            else w.WriteNull("volume");
            w.WriteStartObject("host");
            w.WriteBoolean("locked", status.Locked);
            w.WriteBoolean("elevated", status.Elevated);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Longest title or artist sent to the phone (the web client keeps 200 as well).</summary>
    public const int MaxMediaTextChars = 200;

    /// <summary>
    /// Now-playing text is written by whatever app or web page owns the media session, so it is untrusted: control
    /// characters are dropped, unpaired surrogates (which cannot be encoded as UTF-8 and would make the state frame
    /// fail) become U+FFFD, and the length is capped so the frame stays within the 4096-byte limit.
    /// </summary>
    public static string SanitizeText(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxMediaTextChars));
        for (int i = 0; i < text.Length && sb.Length < MaxMediaTextChars; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (sb.Length + 2 > MaxMediaTextChars) break;
                    sb.Append(c).Append(text[++i]);
                }
                else sb.Append('�');
            }
            else if (char.IsLowSurrogate(c)) sb.Append('�');
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task OnJsonAsync(string json)
    {
        RemoteInvocation? invocation = null;
        long rid = 0;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "invoke"
                && root.TryGetProperty("rid", out var ridEl) && ridEl.TryGetInt64(out rid) && rid >= 0
                && root.TryGetProperty("remote", out var remote) && remote.ValueKind == JsonValueKind.String
                && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String
                && remote.GetString() is { Length: > 0 and <= MaxFieldLength } remoteId
                && item.GetString() is { Length: > 0 and <= MaxFieldLength } itemId)
            {
                double? value = null;
                bool valueOk = true;
                if (root.TryGetProperty("value", out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) value = d;
                    else valueOk = false;
                }
                if (valueOk) invocation = new RemoteInvocation(remoteId, itemId, value);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            invocation = null; // malformed JSON, or an escape that decodes to invalid UTF-16
        }

        if (invocation is null)
        {
            await CloseAsync(CloseCode.Malformed, "bad json").ConfigureAwait(false);
            return;
        }
        if (_svc.Status.Locked)
        {
            // Remote actions can inject input (key, text, launch...): not while the PC is locked.
            await SendResultAsync(rid, new RemoteActionResult(false, "locked")).ConfigureAwait(false);
            return;
        }
        _ = RunInvocationAsync(invocation, rid);
    }

    private async Task RunInvocationAsync(RemoteInvocation invocation, long rid)
    {
        if (!_invocations.Wait(0))
        {
            await SendResultAsync(rid, new RemoteActionResult(false, "busy")).ConfigureAwait(false);
            return;
        }
        try
        {
            var result = await _svc.Actions.InvokeAsync(invocation, _cts.Token).ConfigureAwait(false);
            await SendResultAsync(rid, result).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Session closed while the action ran.
        }
        finally
        {
            _invocations.Release();
        }
    }

    private async Task SendResultAsync(long rid, RemoteActionResult result)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("t", "result");
            w.WriteNumber("rid", rid);
            w.WriteBoolean("ok", result.Ok);
            if (result.Error is not null) w.WriteString("err", result.Error);
            w.WriteEndObject();
        }
        try { await SendFrameAsync(new JsonMessage(Encoding.UTF8.GetString(stream.ToArray()))).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- send, close

    private async Task SendTextAsync(string text)
    {
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try { await _transport.SendTextAsync(text, _cts.Token).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task SendFrameAsync(Message message)
    {
        if (_channel is null || IsClosed) return;
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (IsClosed) return;
            // Seal and send under one lock so counters reach the wire in order.
            var frame = _channel.Seal(FrameCodec.Encode(message));
            await _transport.SendBinaryAsync(frame, _cts.Token).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Marks the session closed at once (no further input is processed), then closes the transport.</summary>
    public async Task CloseAsync(int code, string reason)
    {
        if (!Terminate()) return;
        try { await _transport.CloseAsync(code, reason).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or IOException)
        {
            // The peer is already gone.
        }
    }

    /// <summary>The transport dropped (peer closed or network error). Releases everything the session holds.</summary>
    public void OnDisconnected() => Terminate();

    private bool Terminate()
    {
        bool wasController;
        lock (_gate)
        {
            if (_phase == Phase.Closed) return false;
            wasController = _phase == Phase.Controlling;
            _phase = Phase.Closed;
        }
        _pairRequest?.Deny();
        _cts.Cancel();
        Unsubscribe();
        _svc.Hub.Leave(this);
        // The phone may have held a key or button when it vanished; nothing else will ever lift it.
        if (wasController) ReleaseHeldInput();
        return true;
    }

    private void ReleaseHeldInput()
    {
        try { _svc.Input.ReleaseAll(); }
        catch (InputBlockedException) { /* injection is blocked right now (secure desktop); the keys cannot be lifted from here */ }
    }

    private static bool TryParseFlat(string text, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) return false;
                if (!fields.TryAdd(p.Name, p.Value.GetString()!)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: an escape such as "\ud800" decodes to invalid UTF-16 on GetString().
            return false;
        }
    }
}
