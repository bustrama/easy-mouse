using System.Windows.Input;
using System.Windows.Media;
using EasyMouse.App.Logic;
using EasyMouse.Core.Pairing;

namespace EasyMouse.App.ViewModels;

public enum QrStatusKind { Waiting, Approve, Connected, Stopped }

public sealed record HelpItem(string Title, string Body, string Tag, bool Found);

/// <summary>State of the QR window: the code, its countdown, the status pill and the "phone cannot connect" help.</summary>
public sealed class QrViewModel : ViewModelBase
{
    private static readonly Brush BlueBg = Frozen("#172033"), BlueFg = Frozen("#9DB8FF"), BlueDot = Frozen("#5B8CFF");
    private static readonly Brush GreenBg = Frozen("#12261A"), GreenFg = Frozen("#86EFAC"), GreenDot = Frozen("#4ADE80");
    private static readonly Brush AmberBg = Frozen("#2A2112"), AmberFg = Frozen("#FCD34D"), AmberDot = Frozen("#F59E0B");

    private readonly AppController _app;
    private string? _lastUrl;
    private bool[,]? _matrix;
    private string _origin = "";
    private string _countdownText = "";
    private bool _isExpired;
    private string _statusText = "";
    private QrStatusKind _statusKind;
    private bool _showHelp;
    private bool _showConnected;
    private bool _hasConnected;
    private DateTimeOffset? _copiedAt;
    private bool _helpDismissed;
    private bool _wasPublicStop;
    private string _helpIntro = "";
    private IReadOnlyList<HelpItem> _helpItems = [];
    private bool _canMakePrivate;
    private string _privateMessage = "";
    private string _notListeningText = "";
    private string _helpSignature = "";

    public QrViewModel(AppController app, Action openSettings)
    {
        _app = app;
        NewCodeCommand = new RelayCommand(() => { _app.NewCode(); Refresh(); });
        SettingsCommand = new RelayCommand(openSettings);
        ShowTroubleCommand = new RelayCommand(() => { _helpDismissed = false; ShowHelp = true; Refresh(); });
        ShowQrAgainCommand = new RelayCommand(() =>
        {
            ShowHelp = false;
            _helpDismissed = true;
            PrivateMessage = "";
            _app.Help.WindowOpened(); // the "nothing connected" clock starts over
            Refresh();
        });
        ShowConnectionQrCommand = new RelayCommand(() => { _app.ShowQr(); Refresh(force: true); });
        HideQrCommand = new RelayCommand(() => { _app.HideQr(); Refresh(force: true); });
        CopyLinkCommand = new RelayCommand(CopyLink);
        MakePrivateCommand = new RelayCommand(async () =>
        {
            bool ok = await _app.MakeNetworkPrivateAsync();
            PrivateMessage = ok ? "" : Loc.Instance["Help_PrivateFailed"];
            Refresh();
        });
        Loc.Instance.LanguageChanged += (_, _) => { Raise(nameof(CopyLinkText)); Refresh(force: true); };
        Refresh(force: true);
    }

    public ICommand NewCodeCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ShowTroubleCommand { get; }
    public ICommand ShowQrAgainCommand { get; }
    public ICommand MakePrivateCommand { get; }
    public ICommand ShowConnectionQrCommand { get; }
    public ICommand HideQrCommand { get; }
    public ICommand CopyLinkCommand { get; }

    /// <summary>Puts text on the clipboard; replaced in tests. Returns false when the clipboard is busy.</summary>
    public Func<string, bool> CopyToClipboard { get; set; } = ClipboardText.TrySet;

    public bool[,]? Matrix { get => _matrix; private set => Set(ref _matrix, value); }
    public string Origin { get => _origin; private set => Set(ref _origin, value); }
    public string CountdownText { get => _countdownText; private set => Set(ref _countdownText, value); }
    public bool IsExpired { get => _isExpired; private set { if (Set(ref _isExpired, value)) Raise(nameof(ShowExpiredOverlay)); } }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string NotListeningText { get => _notListeningText; private set => Set(ref _notListeningText, value); }
    public bool ShowHelp { get => _showHelp; private set { if (Set(ref _showHelp, value)) Raise(nameof(ShowQr)); } }
    public bool ShowQr => !_showHelp && !_showConnected;
    /// <summary>A phone is connected and the QR is hidden (pairing closed).</summary>
    public bool ShowConnected { get => _showConnected; private set { if (Set(ref _showConnected, value)) Raise(nameof(ShowQr)); } }
    /// <summary>At least one phone is connected (the QR can then be hidden again).</summary>
    public bool HasConnected { get => _hasConnected; private set { if (Set(ref _hasConnected, value)) Raise(nameof(NoneConnected)); } }
    public bool NoneConnected => !_hasConnected;
    public string CopyLinkText => Loc.Instance[_copiedAt is null ? "Btn_CopyLink" : "Btn_LinkCopied"];
    public string HelpIntro { get => _helpIntro; private set => Set(ref _helpIntro, value); }
    public IReadOnlyList<HelpItem> HelpItems { get => _helpItems; private set => Set(ref _helpItems, value); }
    public bool CanMakePrivate { get => _canMakePrivate; private set => Set(ref _canMakePrivate, value); }
    public string PrivateMessage { get => _privateMessage; private set { if (Set(ref _privateMessage, value)) Raise(nameof(HasPrivateMessage)); } }
    public bool HasQr => _matrix is not null;
    public bool NoQr => _matrix is null;
    public bool ShowExpiredOverlay => _matrix is not null && _isExpired;
    public bool HasPrivateMessage => _privateMessage.Length > 0;
    public QrStatusKind StatusKind { get => _statusKind; private set { if (Set(ref _statusKind, value)) { Raise(nameof(ChipBackground)); Raise(nameof(ChipForeground)); Raise(nameof(ChipDot)); } } }

    public Brush ChipBackground => StatusKind switch { QrStatusKind.Connected => GreenBg, QrStatusKind.Stopped => AmberBg, _ => BlueBg };
    public Brush ChipForeground => StatusKind switch { QrStatusKind.Connected => GreenFg, QrStatusKind.Stopped => AmberFg, _ => BlueFg };
    public Brush ChipDot => StatusKind switch { QrStatusKind.Connected => GreenDot, QrStatusKind.Stopped => AmberDot, _ => BlueDot };

    /// <summary>Re-reads everything from the controller. Called on a timer (countdown) and whenever the app state changes.</summary>
    public void Refresh(bool force = false)
    {
        var now = _app.Core.Time.GetUtcNow();

        var url = _app.QrUrl();
        if (force || url != _lastUrl)
        {
            _lastUrl = url;
            Matrix = url is null ? null : QrMatrix.Encode(url);
            Raise(nameof(HasQr));
            Raise(nameof(NoQr));
            Raise(nameof(ShowExpiredOverlay));
        }
        Origin = _app.Origin();

        var secret = _app.Core.Pairing.CurrentSecret;
        var remaining = secret is null ? TimeSpan.Zero : Countdown.Remaining(secret.ExpiresAt, now);
        IsExpired = secret is not null && remaining == TimeSpan.Zero;
        CountdownText = secret is null ? "" : IsExpired ? Loc.Instance["Qr_Expired"] : Loc.Format("Qr_Valid", Countdown.Format(remaining));

        (StatusKind, StatusText) =
            _app.Core.Pairing.State == PairingState.Pending ? (QrStatusKind.Approve, Loc.Instance["Status_Approve"])
            : _app.ConnectedCount > 0 ? (QrStatusKind.Connected, Loc.Instance["Status_Connected"])
            : !_app.IsListening ? (QrStatusKind.Stopped, Loc.Instance["Status_Stopped"])
            : (QrStatusKind.Waiting, Loc.Instance["Status_Waiting"]);
        NotListeningText = _app.IsListening ? "" : _app.StatusText;

        // Help: shown the moment we stop on a Public network, or after a quiet stretch with no request from the LAN.
        bool publicStop = _app.StatusKind == ListenerStatusKind.Public;
        if (publicStop && !_wasPublicStop) { _helpDismissed = false; ShowHelp = true; }
        _wasPublicStop = publicStop;
        if (!_helpDismissed && _app.Help.ShouldShowHelp && _app.ConnectedCount == 0) ShowHelp = true; // a connected phone proves it can reach us

        HasConnected = _app.ConnectedCount > 0;
        if (HasConnected && !publicStop) ShowHelp = false; // a connected phone proves it can reach us
        ShowConnected = !ShowHelp && HasConnected && !_app.PairingOpen;
        if (_copiedAt is { } at && now - at > TimeSpan.FromSeconds(2)) { _copiedAt = null; Raise(nameof(CopyLinkText)); }

        CanMakePrivate = _app.Category == NetCategory.Public && _app.ChosenAdapter is not null;
        HelpIntro = Loc.Instance[publicStop ? "Help_IntroPublic" : "Help_Intro"];
        RebuildHelp(force);
    }

    private void CopyLink()
    {
        // The origin only: never the pairing secret. A paired phone needs just the address; new phones need the QR.
        var origin = _app.Origin();
        if (origin.Length == 0 || !CopyToClipboard(origin)) return;
        _copiedAt = _app.Core.Time.GetUtcNow();
        Raise(nameof(CopyLinkText));
    }

    private void RebuildHelp(bool force)
    {
        var hints = NetworkAnalyzer.Hints(_app.Network.Adapters, _app.ChosenAdapter, _app.Category, includeGeneric: true);
        var signature = string.Join('|', hints.Select(h => $"{h.Kind}{h.Found}{h.Detail}"));
        if (!force && signature == _helpSignature) return;
        _helpSignature = signature;
        HelpItems = [.. hints.Select(h =>
        {
            var prefix = h.Kind switch
            {
                HintKind.PublicNetwork => "Hint_Public",
                HintKind.GuestWifi => "Hint_Guest",
                HintKind.MobileData => "Hint_Mobile",
                HintKind.Vpn => "Hint_Vpn",
                HintKind.MultipleCards => "Hint_Cards",
                _ => "Hint_NoNet",
            };
            var body = h.Kind == HintKind.MultipleCards ? Loc.Format(prefix + "_Body", h.Detail ?? "") : Loc.Instance[prefix + "_Body"];
            return new HelpItem(Loc.Instance[prefix + "_Title"], body, Loc.Instance[h.Found ? "Tag_Found" : "Tag_Possible"], h.Found);
        })];
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
