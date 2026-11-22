using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyMouse.Remotes.Approvals;

/// <summary>
/// The user approved remote <paramref name="RemoteId"/> exactly as it was when its file hashed to <paramref name="Hash"/>.
/// </summary>
/// <param name="ApprovedHosts">Non-loopback hosts the remote's http actions may contact.</param>
/// <param name="LaunchTargets">Slot name to the full path of the program the user picked.</param>
public sealed record RemoteApproval(
    string RemoteId,
    string Hash,
    DateTimeOffset ApprovedAt,
    IReadOnlyList<string> ApprovedHosts,
    IReadOnlyDictionary<string, string> LaunchTargets);

public interface IRemoteApprovalStore
{
    /// <summary>The approval for this remote if it is pinned to exactly this hash; otherwise null.</summary>
    RemoteApproval? Get(string remoteId, string currentHash);
    void Save(RemoteApproval approval);
    void Revoke(string remoteId);
}

public sealed class InMemoryRemoteApprovalStore : IRemoteApprovalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteApproval> _items = new();

    public RemoteApproval? Get(string remoteId, string currentHash)
    {
        lock (_gate) return _items.TryGetValue(remoteId, out var a) && a.Hash == currentHash ? a : null;
    }

    public void Save(RemoteApproval approval) { lock (_gate) _items[approval.RemoteId] = approval; }
    public void Revoke(string remoteId) { lock (_gate) _items.Remove(remoteId); }
}

/// <summary>
/// JSON file under a configurable folder (<c>remote-approvals.json</c>). Writes are atomic (temp file + replace).
/// An unreadable file is treated as "nothing approved" (fail closed) and reported through <see cref="LoadError"/>.
/// </summary>
public sealed class FileRemoteApprovalStore : IRemoteApprovalStore
{
    public const string FileName = "remote-approvals.json";
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, RemoteApproval> _items = new();

    public string? LoadError { get; private set; }

    public FileRemoteApprovalStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, FileName);
        Load();
    }

    public RemoteApproval? Get(string remoteId, string currentHash)
    {
        lock (_gate) return _items.TryGetValue(remoteId, out var a) && a.Hash == currentHash ? a : null;
    }

    public void Save(RemoteApproval approval)
    {
        lock (_gate)
        {
            var had = _items.TryGetValue(approval.RemoteId, out var before);
            _items[approval.RemoteId] = approval;
            try { Persist(); }
            catch
            {
                // not saved means not approved: never keep an approval in memory that a restart would lose
                if (had) _items[approval.RemoteId] = before!; else _items.Remove(approval.RemoteId);
                throw;
            }
        }
    }

    public void Revoke(string remoteId)
    {
        lock (_gate)
        {
            if (_items.Remove(remoteId)) Persist();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_path), ApprovalJson.Default.ApprovalFile);
            if (file is null || file.Version != 1) throw new JsonException("unsupported file version");
            _items = file.Approvals.ToDictionary(a => a.RemoteId, a => new RemoteApproval(
                a.RemoteId, a.Hash, a.ApprovedAt, a.ApprovedHosts, a.LaunchTargets));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _items = new();
            LoadError = $"{_path}: {ex.Message}";
        }
    }

    private void Persist()
    {
        var file = new ApprovalFile
        {
            Version = 1,
            Approvals = _items.Values.Select(a => new ApprovalDto
            {
                RemoteId = a.RemoteId,
                Hash = a.Hash,
                ApprovedAt = a.ApprovedAt,
                ApprovedHosts = [.. a.ApprovedHosts],
                LaunchTargets = new Dictionary<string, string>(a.LaunchTargets),
            }).ToList(),
        };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, ApprovalJson.Default.ApprovalFile));
        File.Move(tmp, _path, overwrite: true);
        LoadError = null;
    }
}

internal sealed class ApprovalFile
{
    public int Version { get; set; }
    public List<ApprovalDto> Approvals { get; set; } = [];
}

internal sealed class ApprovalDto
{
    public string RemoteId { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; set; }
    public List<string> ApprovedHosts { get; set; } = [];
    public Dictionary<string, string> LaunchTargets { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ApprovalFile))]
internal sealed partial class ApprovalJson : JsonSerializerContext;
