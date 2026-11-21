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
