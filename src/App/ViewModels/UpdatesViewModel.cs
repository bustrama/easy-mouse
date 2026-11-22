using System.Windows.Input;
using EasyMouse.App.Updates;

namespace EasyMouse.App.ViewModels;

/// <summary>The "Updates" page of the settings window: the toggles, the status line, release notes and the two buttons.</summary>
public sealed class UpdatesViewModel : ViewModelBase
{
    private readonly AppController _app;
    private readonly Func<string, bool> _confirm;
    private readonly RelayCommand _checkNow;
    private readonly RelayCommand _install;

    /// <param name="confirm">Asks the user to confirm the download and install (text of the question); true means go ahead.</param>
    public UpdatesViewModel(AppController app, Func<string, bool> confirm)
    {
        _app = app;
        _confirm = confirm;
        _checkNow = new RelayCommand(async () => await Service.CheckAsync(manual: true), () => !Service.Busy);
        _install = new RelayCommand(async () => await InstallAsync(), () => HasUpdate && !Service.Busy);
    }

    private UpdateService Service => _app.Updates;

    public ICommand CheckNowCommand => _checkNow;
    public ICommand InstallCommand => _install;

    public bool CheckEnabled
    {
        get => _app.Settings.CheckForUpdates;
        set => _ = _app.UpdateSettingsAsync(s => s with { CheckForUpdates = value });
    }

    public bool IncludePrereleases
    {
        get => _app.Settings.IncludePrereleases;
        set => _ = _app.UpdateSettingsAsync(s => s with { IncludePrereleases = value });
    }

    public string CurrentVersionText => Service.Current.ToString();

    public string LastCheckedText => Service.LastChecked is { } t
        ? t.ToLocalTime().ToString("g", Loc.Instance.Culture)
        : Loc.Instance["Upd_Never"];

    public bool HasUpdate => Service.Available is not null;
    public bool Busy => Service.Busy;
    public bool NotBusy => !Service.Busy;

    public string InstallLabel => Service.Available is { } r ? Loc.Format("Upd_Install", r.Version) : "";

    public string StatusText => Service.Phase switch
    {
        UpdatePhase.Checking => Loc.Instance["Upd_Checking"],
        UpdatePhase.Downloading => Loc.Format("Upd_Downloading", (int)Math.Round(Service.Progress * 100)),
        UpdatePhase.Verifying => Loc.Instance["Upd_Verifying"],
        UpdatePhase.Launching => Loc.Instance["Upd_Launching"],
        _ when Service.InstallerRunning => Loc.Instance["Upd_Launching"],
        _ when Service.LastError is { } e => ErrorText(e),
        _ when Service.Available is { } r => Loc.Format("Upd_Available", r.Version),
        _ when Service.UpToDate => Loc.Instance["Upd_UpToDate"],
        _ => "",
    };

    public bool HasStatus => StatusText.Length > 0;
    public bool StatusIsProblem => Service.Phase == UpdatePhase.Idle && Service.LastError is not null;
    public bool StatusIsUpdate => Service.Phase == UpdatePhase.Idle && Service.LastError is null && Service.Available is not null;

    public string NotesTitle => Service.Available is { } r ? Loc.Format("Upd_Notes", r.Version) : "";
    public string Notes => Service.Available?.Notes ?? "";
    public bool HasNotes => Notes.Length > 0;

    public static string ErrorText(UpdateException e) => e.Kind switch
    {
        UpdateErrorKind.Offline => Loc.Instance["Upd_Err_Offline"],
        UpdateErrorKind.RateLimited => Loc.Format("Upd_Err_RateLimited",
            (e.RetryAfter ?? DateTimeOffset.UtcNow.AddHours(1)).ToLocalTime().ToString("t", Loc.Instance.Culture)),
        UpdateErrorKind.Malformed => Loc.Instance["Upd_Err_Malformed"],
        UpdateErrorKind.Untrusted => Loc.Instance["Upd_Err_Untrusted"],
        UpdateErrorKind.TooLarge => Loc.Instance["Upd_Err_TooLarge"],
        UpdateErrorKind.SignatureInvalid => Loc.Instance["Upd_Err_Signature"],
        UpdateErrorKind.HashMismatch => Loc.Instance["Upd_Err_Hash"],
        UpdateErrorKind.Downgrade => Loc.Instance["Upd_Err_Downgrade"],
        UpdateErrorKind.InstallerFailed => Loc.Instance["Upd_Err_Installer"],
        _ => Loc.Instance["Upd_Err_Http"],
    };

    private async Task InstallAsync()
    {
        if (Service.Available is not { } release) return; // the one the user is asked about below is the one installed
        if (!_confirm(Loc.Format("Upd_Confirm", release.Version))) return;
        await Service.InstallAsync(release);
    }

    /// <summary>Re-read everything from the service (it changes on background threads; call on the UI thread).</summary>
    public void Refresh()
    {
        foreach (var name in new[]
        {
            nameof(CheckEnabled), nameof(IncludePrereleases), nameof(CurrentVersionText), nameof(LastCheckedText), nameof(HasUpdate),
            nameof(Busy), nameof(NotBusy), nameof(InstallLabel), nameof(StatusText), nameof(HasStatus), nameof(StatusIsProblem),
            nameof(StatusIsUpdate), nameof(NotesTitle), nameof(Notes), nameof(HasNotes),
        })
            Raise(name);
        _checkNow.RaiseCanExecuteChanged();
        _install.RaiseCanExecuteChanged();
    }
}
