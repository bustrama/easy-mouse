namespace EasyMouse.App.Updates;

public enum UpdateErrorKind
{
    Offline,
    RateLimited,
    Http,
    Malformed,
    Untrusted,
    TooLarge,
    SignatureInvalid,
    HashMismatch,
    Downgrade,
    NoRelease,
    InstallerFailed,
    Cancelled,
}

/// <summary>A failed check or update. The kind drives the user-facing text; the message is for the log only.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(UpdateErrorKind kind, string message, Exception? inner = null, DateTimeOffset? retryAfter = null)
        : base(message, inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public UpdateErrorKind Kind { get; }
    public DateTimeOffset? RetryAfter { get; }
}
