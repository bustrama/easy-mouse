using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Security;
using EasyMouse.Remotes.Validation;

namespace EasyMouse.App.Logic;

public enum ImportOutcome { Imported, Replaced, AlreadyImported, Invalid, Rejected, ReadFailed }

/// <param name="Messages">Every validation error (path and reason), or the single reason it was refused.</param>
public sealed record ImportResult(ImportOutcome Outcome, string? Id, IReadOnlyList<string> Messages, string? OtherFile = null)
{
    public bool Ok => Outcome is ImportOutcome.Imported or ImportOutcome.Replaced or ImportOutcome.AlreadyImported;
}

/// <summary>
/// Copies a remote file into the user's remotes folder after it validated. The bytes are copied untouched (the approval is
/// pinned to their hash) and the file is named after the remote's id, so importing a newer version replaces the old one.
/// A remote never becomes runnable here: if it has sensitive or dangerous actions it still needs approval.
/// </summary>
public static class RemoteImporter
{
    public const int MaxBytes = 256 * 1024;

    public static ImportResult ImportFile(string sourcePath, string userFolder, IRemoteCatalog catalog)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Length > MaxBytes) return new(ImportOutcome.Invalid, null, [$"$: file is {info.Length} bytes, the limit is {MaxBytes}"]);
            bytes = File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ImportOutcome.ReadFailed, null, [ex.Message]);
        }
        return Import(bytes, userFolder, catalog);
    }

    public static ImportResult Import(byte[] bytes, string userFolder, IRemoteCatalog catalog)
    {
        var validation = RemoteValidator.Validate(bytes);
        if (!validation.IsValid) return new(ImportOutcome.Invalid, null, validation.Errors);

        var id = validation.Remote!.Id;
        var hash = RemoteHash.Of(bytes);
        var target = Path.Combine(userFolder, id + ".json");
        var targetFull = Path.GetFullPath(target);

        foreach (var e in catalog.Entries.Where(e => e.Definition?.Id == id))
        {
            if (e.Source == RemoteSource.BuiltIn) return new(ImportOutcome.Rejected, id, []);
            if (!string.Equals(Path.GetFullPath(e.FilePath), targetFull, StringComparison.OrdinalIgnoreCase))
                return new(ImportOutcome.Rejected, id, [], Path.GetFileName(e.FilePath));
            if (e.Hash == hash) return new(ImportOutcome.AlreadyImported, id, []);
        }

        var replacing = File.Exists(target);
        Directory.CreateDirectory(userFolder);
        var tmp = target + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, target, overwrite: true);
        catalog.Reload();
        return new(replacing ? ImportOutcome.Replaced : ImportOutcome.Imported, id, []);
    }
}
