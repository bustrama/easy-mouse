using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using EasyMouse.App.Logic;
using EasyMouse.Remotes.Catalog;

namespace EasyMouse.App.ViewModels;

/// <summary>The file pickers (the window shows real dialogs; tests answer with fixed paths).</summary>
public interface IRemotesDialogs
{
    /// <summary>Path of a remote file the user picked, or null when cancelled.</summary>
    string? PickRemoteFile();
    /// <summary>Full path of a program (.exe) the user picked, or null when cancelled.</summary>
    string? PickProgram();
}

public enum RemoteRowState { Ready, NeedsReview, Rejected, Invalid }

public sealed class SlotChoiceViewModel : ViewModelBase
{
    private string? _path;

    public SlotChoiceViewModel(string slot, string? path, Action<SlotChoiceViewModel> choose)
    {
        Slot = slot;
        _path = path;
        Label = Loc.Format("Rm_Slot", slot);
        ChooseCommand = new RelayCommand(() => choose(this));
    }

    public string Slot { get; }
    public string Label { get; }
    public string? Path
    {
        get => _path;
        set { if (Set(ref _path, value)) Raise(nameof(PathText)); }
    }
    public string PathText => Path ?? Loc.Instance["Rm_NoneChosen"];
    public ICommand ChooseCommand { get; }
}

/// <summary>One remote in the list. Built from a catalog entry; the hash shown (and approved) is the one captured here.</summary>
public sealed class RemoteRowViewModel : ViewModelBase
{
    private readonly RemotesViewModel _owner;

    internal RemoteRowViewModel(RemotesViewModel owner, RemoteEntry entry, bool rejected, IReadOnlyDictionary<string, string> picks)
    {
        _owner = owner;
        Id = entry.Id;
        Name = entry.Definition?.Name ?? entry.Id;
        Description = entry.Definition?.Description;
        SourceText = Loc.Instance[entry.Source == RemoteSource.BuiltIn ? "Rm_BuiltIn" : "Rm_User"];
        ReviewedHash = entry.Hash; // what the user is shown is what is approved, even if the file is edited meanwhile
        HashShort = entry.Hash.Length >= 12 ? entry.Hash[..12] : entry.Hash;
        Errors = entry.Errors;
        Lines = ReviewText.For(entry);
        HasDangerous = Lines.Any(l => l.Dangerous);
        HostsText = entry.ExternalHosts.Count == 0 ? null : Loc.Format("Rm_Hosts", string.Join(", ", entry.ExternalHosts));
        Slots = [.. entry.LaunchSlots.Select(s => new SlotChoiceViewModel(s, picks.GetValueOrDefault(s), owner.ChooseProgram))];
        foreach (var s in Slots) s.PropertyChanged += (_, _) => ApproveCommand?.RaiseCanExecuteChanged();

        State = entry.State switch
        {
            RemoteState.Invalid => RemoteRowState.Invalid,
            RemoteState.NeedsReview => rejected ? RemoteRowState.Rejected : RemoteRowState.NeedsReview,
            _ => RemoteRowState.Ready,
        };
        IsApproved = entry.State == RemoteState.Ready && entry.Approval is not null;
        ApprovedText = IsApproved ? Loc.Format("Rm_ApprovedHash", HashShort) : null;
        HashText = Loc.Format("Rm_Hash", HashShort);
        (StateText, ChipBackground, ChipForeground) = State switch
        {
            RemoteRowState.Ready => (Loc.Instance["Rm_StateReady"], Frozen("#DCFCE7"), Frozen("#14532D")),
            RemoteRowState.NeedsReview => (Loc.Instance["Rm_StateReview"], Frozen("#FEF3C7"), Frozen("#78350F")),
            RemoteRowState.Rejected => (Loc.Instance["Rm_StateRejected"], Frozen("#2A323E"), Frozen("#C9D1DC")),
            _ => (Loc.Instance["Rm_StateInvalid"], Frozen("#FEE2E2"), Frozen("#7F1D1D")),
        };

        ApproveCommand = new RelayCommand(() => owner.Approve(this), () => State == RemoteRowState.NeedsReview && Slots.All(s => s.Path is not null));
        RejectCommand = new RelayCommand(() => owner.Reject(this));
        ReviewAgainCommand = new RelayCommand(() => owner.ReviewAgain(this));
        RevokeCommand = new RelayCommand(() => owner.Revoke(this));
    }

    public string Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public string SourceText { get; }
    public string ReviewedHash { get; }
    public string HashShort { get; }
    public string HashText { get; }
    public string? ApprovedText { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<ReviewLine> Lines { get; }
    public bool HasDangerous { get; }
    public string? HostsText { get; }
    public IReadOnlyList<SlotChoiceViewModel> Slots { get; }
    public RemoteRowState State { get; }
    public string StateText { get; }
    public Brush ChipBackground { get; }
    public Brush ChipForeground { get; }
    public bool IsApproved { get; }

    public bool IsNeedsReview => State == RemoteRowState.NeedsReview;
    public bool IsRejected => State == RemoteRowState.Rejected;
    public bool IsInvalid => State == RemoteRowState.Invalid;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasHosts => HostsText is not null;
    public bool HasSlots => Slots.Count > 0;

    public RelayCommand? ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand ReviewAgainCommand { get; }
    public ICommand RevokeCommand { get; }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// The Remotes window: every remote with its state, the plain-language review of the ones that need approval (approve or reject,
/// pinned to the file hash shown), import, and the user's folder.
/// </summary>
public sealed class RemotesViewModel : ViewModelBase, IDisposable
{
    private readonly AppController _app;
    private readonly IRemotesDialogs _dialogs;
    private readonly Action<string> _openFolder;
    private readonly Dictionary<string, (string Hash, Dictionary<string, string> Picks)> _picks = new();
    private readonly Dictionary<string, string> _rejected = new(); // id -> hash that was rejected
    private string _notice = "";
    private bool _noticeIsError;
    private IReadOnlyList<string> _noticeDetails = [];

    public RemotesViewModel(AppController app, IRemotesDialogs dialogs, Action<string>? openFolder = null)
    {
        _app = app;
        _dialogs = dialogs;
        _openFolder = openFolder ?? (path => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }));
        ImportCommand = new RelayCommand(Import);
        OpenFolderCommand = new RelayCommand(() => _openFolder(app.Paths.UserRemotes));
        _app.Remotes.Changed += OnCatalogChanged;
        Rebuild();
    }

    public ObservableCollection<RemoteRowViewModel> Rows { get; } = [];
    public ICommand ImportCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public string UserFolder => _app.Paths.UserRemotes;
    public bool HasRows => Rows.Count > 0;
    public bool NoRows => Rows.Count == 0;

    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool NoticeIsError { get => _noticeIsError; private set => Set(ref _noticeIsError, value); }
    public IReadOnlyList<string> NoticeDetails { get => _noticeDetails; private set => Set(ref _noticeDetails, value); }
    public bool HasNotice => Notice.Length > 0;

    /// <summary>Remotes that are on but did not fit in the one message the phone accepts; empty when all fit.</summary>
    public string? LayoutWarning { get; private set; }
    public bool HasLayoutWarning => LayoutWarning is not null;

    /// <summary>Set when the saved approvals could not be read (every remote needing approval then stays off).</summary>
    public string? StoreWarning { get; private set; }
    public bool HasStoreWarning => StoreWarning is not null;

    private void OnCatalogChanged(object? sender, EventArgs e) => UiThread.Post(Rebuild);

    private void Rebuild()
    {
        Rows.Clear();
        // What needs the user first, then broken files, then the ones that are on.
        foreach (var entry in _app.Remotes.Entries.OrderBy(e => e.State switch { RemoteState.NeedsReview => 0, RemoteState.Invalid => 1, _ => 2 }))
        {
            var id = entry.Id;
            var rejected = _rejected.TryGetValue(id, out var h) && h == entry.Hash;
            var picks = _picks.TryGetValue(id, out var p) && p.Hash == entry.Hash ? p.Picks : new Dictionary<string, string>();
            Rows.Add(new RemoteRowViewModel(this, entry, rejected, picks));
        }
        var omitted = _app.LayoutSource.Current.Omitted;
        LayoutWarning = omitted.Count == 0 ? null : Loc.Format("Rm_Omitted", string.Join(", ", omitted));
        StoreWarning = _app.RemoteApprovalStore.LoadError is { } err ? Loc.Format("Rm_ApprovalsError", err) : null;
        Raise(nameof(HasRows));
        Raise(nameof(NoRows));
        Raise(nameof(LayoutWarning));
        Raise(nameof(HasLayoutWarning));
        Raise(nameof(StoreWarning));
        Raise(nameof(HasStoreWarning));
    }

    internal void ChooseProgram(SlotChoiceViewModel slot)
    {
        if (_dialogs.PickProgram() is not { } path) return;
        slot.Path = path;
        var row = Rows.FirstOrDefault(r => r.Slots.Contains(slot));
        if (row is null) return;
        if (!_picks.TryGetValue(row.Id, out var entry) || entry.Hash != row.ReviewedHash)
            _picks[row.Id] = entry = (row.ReviewedHash, new Dictionary<string, string>());
        entry.Picks[slot.Slot] = path;
    }

    internal void Approve(RemoteRowViewModel row)
    {
        var targets = row.Slots.Where(s => s.Path is not null).ToDictionary(s => s.Slot, s => s.Path!);
        // The hash from when the row was drawn, not a fresh read: a file edited since then is refused, never approved unseen.
        var result = _app.Remotes.TryApprove(row.Id, row.ReviewedHash, targets);
        if (result.Ok)
        {
            _rejected.Remove(row.Id);
            _picks.Remove(row.Id);
            SetNotice(Loc.Format("Rm_ApprovedHash", row.HashShort), false);
        }
        else
            SetNotice(result.Failure switch
            {
                ApproveFailure.Changed => Loc.Instance["Rm_ApproveChanged"],
                ApproveFailure.SlotNeeded => Loc.Format("Rm_ApproveSlot", result.Detail ?? ""),
                ApproveFailure.SaveFailed => Loc.Format("Rm_ApproveSaveFail", result.Detail ?? ""),
                _ => Loc.Instance["Rm_ApproveUnknown"],
            }, true);
    }

    internal void Reject(RemoteRowViewModel row)
    {
        _rejected[row.Id] = row.ReviewedHash;
        Rebuild();
    }

    internal void ReviewAgain(RemoteRowViewModel row)
    {
        _rejected.Remove(row.Id);
        Rebuild();
    }

    internal void Revoke(RemoteRowViewModel row) => _app.Remotes.Revoke(row.Id); // Changed rebuilds the list

    private void Import()
    {
        if (_dialogs.PickRemoteFile() is not { } path) return;
        var result = RemoteImporter.ImportFile(path, _app.Paths.UserRemotes, _app.Remotes);
        switch (result.Outcome)
        {
            case ImportOutcome.Imported: SetNotice(Loc.Format("Rm_ImportOk", result.Id!), false); break;
            case ImportOutcome.Replaced: SetNotice(Loc.Format("Rm_ImportReplaced", result.Id!), false); break;
            case ImportOutcome.AlreadyImported: SetNotice(Loc.Instance["Rm_ImportSame"], false); break;
            case ImportOutcome.Invalid: SetNotice(Loc.Instance["Rm_ImportFailed"], true, result.Messages); break;
            case ImportOutcome.ReadFailed: SetNotice(Loc.Format("Rm_ImportReadFail", result.Messages.FirstOrDefault() ?? ""), true); break;
            case ImportOutcome.WriteFailed: SetNotice(Loc.Format("Rm_ImportWriteFail", result.Messages.FirstOrDefault() ?? ""), true); break;
            default:
                SetNotice(result.OtherFile is null
                    ? Loc.Format("Rm_ImportBuiltIn", result.Id!)
                    : Loc.Format("Rm_ImportOtherFile", result.OtherFile, result.Id!), true);
                break;
        }
    }

    private void SetNotice(string text, bool error, IReadOnlyList<string>? details = null)
    {
        Notice = text;
        NoticeIsError = error;
        NoticeDetails = details ?? [];
    }

    public void Dispose() => _app.Remotes.Changed -= OnCatalogChanged;
}
