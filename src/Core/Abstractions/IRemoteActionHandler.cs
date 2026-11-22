namespace EasyMouse.Core.Abstractions;

/// <summary>
/// A phone asks the PC to run an item of a remote it knows by id. The phone never sends the action
/// body: the PC looks it up in its own (hash-pinned) remote definitions. Phase 2 provides the engine.
/// </summary>
public sealed record RemoteInvocation(string Remote, string Item, double? Value);

public sealed record RemoteActionResult(bool Ok, string? Error = null);

public interface IRemoteActionHandler
{
    Task<RemoteActionResult> InvokeAsync(RemoteInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Default handler until the remote engine exists: every invocation is refused.</summary>
public sealed class NoRemoteActions : IRemoteActionHandler
{
    public Task<RemoteActionResult> InvokeAsync(RemoteInvocation invocation, CancellationToken cancellationToken)
        => Task.FromResult(new RemoteActionResult(false, "unsupported"));
}

/// <summary>
/// Supplies the <c>{"t":"remotes",...}</c> message (docs/protocol.md, "0x10 json, remote layouts") the controlling phone draws.
/// The remote engine implements it; the Core only forwards the text.
/// </summary>
public interface IRemoteLayoutSource
{
    /// <summary>The message to send now, or null when there is nothing to show.</summary>
    string? CurrentJson { get; }

    /// <summary>Raised (any thread) after <see cref="CurrentJson"/> may have changed.</summary>
    event EventHandler? Changed;
}

/// <summary>Default until a remote engine is wired in: nothing is ever pushed.</summary>
public sealed class NoRemoteLayouts : IRemoteLayoutSource
{
    public string? CurrentJson => null;
#pragma warning disable CS0067 // never raised
    public event EventHandler? Changed;
#pragma warning restore CS0067
}
