namespace EasyMouse.Core.Pairing;

public enum ApprovalKind { Pair, Takeover }

/// <summary>
/// A decision the PC user must make ("Allow this device?" or a takeover). The UI binds to its state:
/// Deny is always possible; <see cref="Allow"/> is refused until <see cref="AllowEnabledAt"/> (the
/// 1-second delay that stops a reflex click), so the rule is enforced here and not only by the button.
/// </summary>
public sealed class ApprovalRequest
{
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ApprovalRequest(ApprovalKind kind, string deviceName, string? code, TimeProvider time, TimeSpan allowDelay)
    {
        Kind = kind;
        DeviceName = deviceName;
        Code = code;
        _time = time;
        CreatedAt = time.GetUtcNow();
        AllowEnabledAt = CreatedAt + allowDelay;
    }

    public ApprovalKind Kind { get; }
    public string DeviceName { get; }
    /// <summary>4-digit confirmation code for pairing; null for takeover.</summary>
    public string? Code { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset AllowEnabledAt { get; }

    public bool IsDecided => _decision.Task.IsCompleted;
    public bool CanAllow => !IsDecided && _time.GetUtcNow() >= AllowEnabledAt;

    /// <summary>Completes with true (allowed) or false (denied, timed out or cancelled).</summary>
    public Task<bool> Decision => _decision.Task;

    /// <summary>Returns false (and does nothing) if the Allow delay has not elapsed or the request is already decided.</summary>
    public bool Allow() => CanAllow && _decision.TrySetResult(true);

    public void Deny() => _decision.TrySetResult(false);
}
