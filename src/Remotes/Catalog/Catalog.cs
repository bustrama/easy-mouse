using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Model;
using EasyMouse.Remotes.Security;
using EasyMouse.Remotes.Validation;

namespace EasyMouse.Remotes.Catalog;

// DEPRECATED: disabled for 0.1, scheduled for removal (docs/remotes.md); see EasyMouse.Core.FeatureFlags.Remotes.
public enum RemoteSource { BuiltIn, User }

/// <summary>Ready: can run (only safe actions, or approved for this exact file hash). NeedsReview: has sensitive or dangerous actions and no matching approval. Invalid: failed validation.</summary>
public enum RemoteState { Ready, NeedsReview, Invalid }

public sealed record RemoteEntry(
    RemoteSource Source,
    string FilePath,
    string Hash,
    RemoteState State,
    RemoteDefinition? Definition,
    IReadOnlyList<ReviewItem> Review,
    IReadOnlyList<string> ExternalHosts,
    IReadOnlyList<string> LaunchSlots,
    IReadOnlyList<string> Errors,
    RemoteApproval? Approval)
{
    public string Id => Definition?.Id ?? Path.GetFileNameWithoutExtension(FilePath);
}

public enum ApproveFailure { None, Unknown, Changed, SlotNeeded, SaveFailed }

/// <param name="Detail">The slot name or the IO error text, depending on the failure.</param>
public sealed record ApproveResult(ApproveFailure Failure, string? Detail = null)
{
    public bool Ok => Failure == ApproveFailure.None;
    public string? Message => Failure switch
    {
        ApproveFailure.None => null,
        ApproveFailure.Unknown => "unknown or invalid remote",
        ApproveFailure.Changed => "the file changed after it was reviewed; review it again",
        ApproveFailure.SlotNeeded => $"program slot \"{Detail}\" needs the full path of a program the user picked",
        _ => $"could not save the approval ({Detail})",
    };
}

public interface IRemoteCatalog
{
    IReadOnlyList<RemoteEntry> Entries { get; }
    /// <summary>Raised after the set of remotes or any state changed (reload, approve, revoke).</summary>
    event EventHandler? Changed;
    void Reload();
    /// <summary>
    /// Looks the remote up for execution. Re-reads the file and compares its hash, so an edit after loading
    /// can never run on an old approval. Returns null for unknown, invalid or no longer readable remotes.
    /// </summary>
    RemoteEntry? Resolve(string remoteId);
    /// <summary>Approves the remote only if its current file hash is <paramref name="reviewedHash"/>, the hash the user saw in the prompt. Returns an error text, or null on success.</summary>
    ApproveResult TryApprove(string remoteId, string reviewedHash, IReadOnlyDictionary<string, string>? launchTargets = null);
    /// <summary>English error text of <see cref="TryApprove"/>, or null on success.</summary>
    string? Approve(string remoteId, string reviewedHash, IReadOnlyDictionary<string, string>? launchTargets = null)
        => TryApprove(remoteId, reviewedHash, launchTargets).Message;
    /// <summary>Withdraws the approval. Returns an error text when the change could not be saved (it then applies to this session only), else null.</summary>
    string? Revoke(string remoteId);
}

public sealed class RemoteCatalog : IRemoteCatalog
{
    private readonly string? _builtInFolder;
    private readonly string? _userFolder;
    private readonly IRemoteApprovalStore _approvals;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private IReadOnlyList<RemoteEntry> _entries = [];

    public event EventHandler? Changed;

    public RemoteCatalog(string? builtInFolder, string? userFolder, IRemoteApprovalStore approvals, TimeProvider? time = null)
    {
        _builtInFolder = builtInFolder;
        _userFolder = userFolder;
        _approvals = approvals;
        _time = time ?? TimeProvider.System;
        Reload();
    }

    public IReadOnlyList<RemoteEntry> Entries { get { lock (_gate) return _entries; } }

    public void Reload()
    {
        var list = new List<RemoteEntry>();
        var ids = new HashSet<string>();
        foreach (var (folder, source) in new[] { (_builtInFolder, RemoteSource.BuiltIn), (_userFolder, RemoteSource.User) })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var entry = Load(file, source);
                if (entry.Definition is not null && !ids.Add(entry.Definition.Id))
                    entry = entry with
                    {
                        State = RemoteState.Invalid, Definition = null, Review = [], ExternalHosts = [], LaunchSlots = [], Approval = null,
                        Errors = [$"$.id: \"{entry.Definition.Id}\" is already used by another remote"],
                    };
                list.Add(entry);
            }
        }
        lock (_gate) _entries = list;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public RemoteEntry? Resolve(string remoteId)
    {
        RemoteEntry? entry;
        lock (_gate) entry = _entries.FirstOrDefault(e => e.Definition?.Id == remoteId);
        if (entry is null) return null;
        string currentHash;
        try { currentHash = RemoteHash.Of(File.ReadAllBytes(entry.FilePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { currentHash = ""; }
        if (currentHash != entry.Hash)
        {
            Reload();
            lock (_gate) entry = _entries.FirstOrDefault(e => e.Definition?.Id == remoteId);
        }
        return entry is { State: not RemoteState.Invalid } ? entry : null;
    }

    public string? Approve(string remoteId, string reviewedHash, IReadOnlyDictionary<string, string>? launchTargets = null)
        => TryApprove(remoteId, reviewedHash, launchTargets).Message;

    public ApproveResult TryApprove(string remoteId, string reviewedHash, IReadOnlyDictionary<string, string>? launchTargets = null)
    {
        var entry = Resolve(remoteId);
        if (entry is null) return new(ApproveFailure.Unknown);
        if (!string.Equals(entry.Hash, reviewedHash, StringComparison.Ordinal)) return new(ApproveFailure.Changed);
        launchTargets ??= new Dictionary<string, string>();
        foreach (var slot in entry.LaunchSlots)
        {
            if (!launchTargets.TryGetValue(slot, out var path) || !Path.IsPathFullyQualified(path))
                return new(ApproveFailure.SlotNeeded, slot);
        }
        try
        {
            _approvals.Save(new RemoteApproval(
                entry.Id, entry.Hash, _time.GetUtcNow(), entry.ExternalHosts,
                entry.LaunchSlots.ToDictionary(s => s, s => launchTargets[s])));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ApproveFailure.SaveFailed, ex.Message);
        }
        Reload();
        return new(ApproveFailure.None);
    }

    public string? Revoke(string remoteId)
    {
        // If the file cannot be written the approval is still gone for this session (fail closed), but a restart would bring it
        // back, so the caller must tell the user.
        string? error = null;
        try { _approvals.Revoke(remoteId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        Reload();
        return error;
    }

    private RemoteEntry Load(string file, RemoteSource source)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(source, file, "", RemoteState.Invalid, null, [], [], [], [$"$: cannot read file ({ex.Message})"], null);
        }
        var hash = RemoteHash.Of(bytes);
        var result = RemoteValidator.Validate(bytes);
        if (!result.IsValid)
            return new(source, file, hash, RemoteState.Invalid, null, [], [], [], result.Errors, null);

        var def = result.Remote!;
        var review = ReviewBuilder.Build(def);
        var hosts = ReviewBuilder.ExternalHosts(def);
        var slots = ReviewBuilder.LaunchSlots(def);
        if (review.Count == 0)
            return new(source, file, hash, RemoteState.Ready, def, review, hosts, slots, [], null);
        var approval = _approvals.Get(def.Id, hash);
        return new(source, file, hash, approval is null ? RemoteState.NeedsReview : RemoteState.Ready, def, review, hosts, slots, [], approval);
    }
}
