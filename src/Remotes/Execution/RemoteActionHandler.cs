using System.Collections.Concurrent;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Sessions;
using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Model;
using EasyMouse.Remotes.Security;

namespace EasyMouse.Remotes.Execution;

/// <summary>Per-remote token bucket.</summary>
public sealed record RemoteRateLimit(double PerSecond = 20, int Burst = 40);

/// <summary>Where the host records what ran. Only ids and short codes: never URLs, headers, bodies or secrets.</summary>
public interface IRemoteAudit
{
    void Record(string remoteId, string itemId, string outcome);
}

/// <summary>
/// Executes <see cref="RemoteInvocation"/>s from the phone. The phone names an item; the action comes from the
/// PC's own hash-pinned definition. Error codes follow docs/protocol.md: unsupported, denied, busy, failed.
/// </summary>
public sealed class RemoteActionHandler : IRemoteActionHandler
{
    private readonly IRemoteCatalog _catalog;
    private readonly ActionExecutors _executors;
    private readonly ISecretStore _secrets;
    private readonly RemoteRateLimit _limit;
    private readonly TimeProvider _time;
    private readonly IRemoteAudit? _audit;
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();

    public RemoteActionHandler(IRemoteCatalog catalog, ActionExecutors executors, ISecretStore secrets,
        RemoteRateLimit? limit = null, TimeProvider? time = null, IRemoteAudit? audit = null)
    {
        _catalog = catalog;
        _executors = executors;
        _secrets = secrets;
        _limit = limit ?? new RemoteRateLimit();
        _time = time ?? TimeProvider.System;
        _audit = audit;
    }

    public async Task<RemoteActionResult> InvokeAsync(RemoteInvocation invocation, CancellationToken cancellationToken)
    {
        var result = await RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        _audit?.Record(invocation.Remote, invocation.Item, result.Ok ? "ok" : result.Error ?? "failed");
        return result;
    }

    private async Task<RemoteActionResult> RunAsync(RemoteInvocation inv, CancellationToken ct)
    {
        var entry = _catalog.Resolve(inv.Remote);
        if (entry?.Definition is not { } remote) return Fail("unsupported");
        var item = remote.FindItem(inv.Item);
        if (item is null || item.Type is not (ItemType.Button or ItemType.Slider) || item.Actions.Count == 0) return Fail("unsupported");
        if (entry.State != RemoteState.Ready) return Fail("denied");

        double? value = null;
        if (item.Type == ItemType.Slider)
        {
            if (inv.Value is not { } v || !double.IsFinite(v)) return Fail("unsupported");
            value = Math.Clamp(Math.Round(v), 0, 100);
        }

        var limiter = _limiters.GetOrAdd(remote.Id, _ => new RateLimiter(_limit.PerSecond, _limit.Burst, _time));
        bool allowed;
        lock (limiter) allowed = limiter.TryTake();
        if (!allowed) return Fail("busy");

        var ctx = new ActionContext(remote.Id, remote.TargetProcess, value);
        // Resolve every action first so a refused action never leaves the earlier ones half run.
        List<Func<Task>> steps;
        try { steps = item.Actions.Select(a => Prepare(a, ctx, entry.Approval, ct)).ToList(); }
        catch (ActionFailedException ex) { return Fail(ex.Code); }

        try
        {
            foreach (var step in steps) await step().ConfigureAwait(false);
            return new RemoteActionResult(true);
        }
        catch (ActionFailedException ex) { return Fail(ex.Code); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Fail("failed"); } // executor bugs and OS errors: no details to the phone
    }

    private static RemoteActionResult Fail(string code) => new(false, code);

    private Func<Task> Prepare(RemoteAction action, ActionContext ctx, RemoteApproval? approval, CancellationToken ct)
    {
        switch (action)
        {
            case KeyPressAction a: return Bind(_executors.Key, a);
            case TextAction a: return Bind(_executors.Text, a);
            case MediaAction a: return Bind(_executors.Media, a);
            case MouseAction a: return Bind(_executors.Mouse, a);
            case SmtcAction a: return Bind(_executors.Smtc, a);
            case VolumeAction a: return Bind(_executors.Volume, a);
            case FocusAction a: return Bind(_executors.Focus, a);
            case OpenUrlAction a: return Bind(_executors.OpenUrl, a);
            case DelayAction d: return () => Task.Delay(TimeSpan.FromMilliseconds(d.Ms), _time, ct);
            case LaunchAction l:
            {
                if (approval is null || !approval.LaunchTargets.TryGetValue(l.Slot, out var exe) || !Path.IsPathFullyQualified(exe))
                    throw new ActionFailedException("denied", "program not chosen");
                return Bind(_executors.Launch, new ResolvedLaunch(exe, l.Args));
            }
            case HttpAction h: return Bind(_executors.Http, ResolveHttp(h, approval));
            default: throw new ActionFailedException("unsupported", "unknown action");
        }

        Func<Task> Bind<T>(IActionExecutor<T>? executor, T a)
        {
            if (executor is null) throw new ActionFailedException("unsupported", "no executor");
            return () => executor.ExecuteAsync(a, ctx, ct);
        }
    }

    private ResolvedHttp ResolveHttp(HttpAction h, RemoteApproval? approval)
    {
        // Placeholders cannot be in the host (validator), so the host is known before any secret is used.
        var host = HttpPolicy.HostOf(h.Url) ?? throw new ActionFailedException("denied", "bad url");
        if (!HttpPolicy.IsHostAllowed(host, approval?.ApprovedHosts ?? []))
            throw new ActionFailedException("denied", "host not approved");

        var url = Substitute(h.Url, Uri.EscapeDataString);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase))
            throw new ActionFailedException("denied", "bad url");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in h.Headers)
        {
            var v = Substitute(value, null);
            if (v.Any(char.IsControl)) throw new ActionFailedException("failed", "invalid header value");
            headers[name] = v;
        }
        var body = h.Body is null ? null : Substitute(h.Body, null);
        return new ResolvedHttp(h.Method, uri, headers, body, TimeSpan.FromMilliseconds(h.TimeoutMs));
    }

    private string Substitute(string template, Func<string, string>? encode)
    {
        var s = SecretSubstitution.Apply(template, _secrets, encode, out var missing);
        // The message carries the secret's name, never its value.
        return s ?? throw new ActionFailedException("failed", $"secret \"{missing}\" is not set");
    }
}
