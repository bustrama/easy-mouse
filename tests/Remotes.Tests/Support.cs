using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Execution;
using EasyMouse.Remotes.Security;

namespace EasyMouse.Remotes.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "remotes", "builtin"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root with remotes/builtin not found");
    }

    public static string BuiltIn => Path.Combine(Root, "remotes", "builtin");
}

/// <summary>A remote with the given items, e.g. <c>Remote("""{"type":"button","label":"x","action":{"do":"key","keys":"F"}}""")</c>.</summary>
internal static class Json
{
    public static string Remote(string items, string id = "test", string extra = "") =>
        $$"""{ "schema": 1, "id": "{{id}}", "name": "Test"{{extra}}, "layout": { "cols": 2, "items": [ {{items}} ] } }""";

    public static string Button(string action, string label = "b") =>
        $$"""{ "type": "button", "label": "{{label}}", "action": {{action}} }""";
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "em-remotes-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Write(string name, string content)
    {
        var p = System.IO.Path.Combine(Path, name);
        File.WriteAllText(p, content);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

internal sealed class ManualTime : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(TimeSpan t) => Interlocked.Add(ref _ticks, (long)(t.TotalMilliseconds));
}

internal sealed class CapturingAudit : IRemoteAudit
{
    public List<string> Lines { get; } = [];
    public void Record(string remoteId, string itemId, string outcome) => Lines.Add($"{remoteId}/{itemId}/{outcome}");
}

/// <summary>User remotes in a temp folder, a file-backed approval store, recording executors and a handler.</summary>
internal sealed class Fixture : IDisposable
{
    public TempDir User { get; } = new();
    public TempDir Data { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public RecordingActionExecutors Rec { get; } = new();
    public CapturingAudit Audit { get; } = new();
    public ManualTime Time { get; } = new();
    public FileRemoteApprovalStore Approvals { get; }
    public RemoteCatalog Catalog { get; }
    public RemoteActionHandler Handler { get; }

    public Fixture(string? remoteJson = null, ActionExecutors? executors = null, RemoteRateLimit? limit = null)
    {
        if (remoteJson is not null) User.Write("test.json", remoteJson);
        Approvals = new FileRemoteApprovalStore(Data.Path);
        Catalog = new RemoteCatalog(null, User.Path, Approvals);
        Handler = new RemoteActionHandler(Catalog, executors ?? Rec.Executors, Secrets, limit, Time, Audit);
    }

    public string Hash => Catalog.Entries.Single().Hash;
    public string HashOf(string id) => Catalog.Entries.Single(e => e.Id == id).Hash;

    public Task<EasyMouse.Core.Abstractions.RemoteActionResult> Invoke(string item, double? value = null, string remote = "test")
        => Handler.InvokeAsync(new EasyMouse.Core.Abstractions.RemoteInvocation(remote, item, value), CancellationToken.None);

    public void Dispose() { User.Dispose(); Data.Dispose(); }
}
