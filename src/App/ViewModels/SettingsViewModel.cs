using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;
using EasyMouse.App.Logic;
using EasyMouse.App.Platform;
using EasyMouse.Platform.Windows.Broker;

namespace EasyMouse.App.ViewModels;

public enum SettingsPage { General, Network, Security, Devices, Updates, About }

public sealed record NavItem(SettingsPage Page, string Label, bool Selected);

public sealed record AdapterChoice(string? Id, string Display);

public sealed record LanguageChoice(string Code, string Display);

public sealed class DeviceRowViewModel
{
    public DeviceRowViewModel(DeviceRowInfo info, ICommand remove)
    {
        Info = info;
        Name = info.Name;
        Meta = info.Kind switch
        {
            DeviceRowKind.Connected => Loc.Format("Dev_MetaConnected", Ago(info.PairedDaysAgo), Core.Devices.DeviceManager.DefaultExpiry.Days),
            DeviceRowKind.ExpiresSoon => Loc.Format("Dev_MetaSoon", info.LastSeenDaysAgo, info.DaysUntilExpiry),
            _ => Loc.Format("Dev_MetaSeen", Ago(info.LastSeenDaysAgo), info.DaysUntilExpiry),
        };
        Status = Loc.Instance[info.Connected ? "Dev_Connected" : "Dev_Offline"];
        (Background, Foreground) = info.Kind switch
        {
            DeviceRowKind.Connected => (Frozen("#12261A"), Frozen("#86EFAC")),
            DeviceRowKind.ExpiresSoon => (Frozen("#2A2112"), Frozen("#FCD34D")),
            _ => (Frozen("#2A323E"), Frozen("#C9D1DC")),
        };
        RemoveCommand = remove;
    }

    public DeviceRowInfo Info { get; }
    public string Name { get; }
    public string Meta { get; }
    public string Status { get; }
    public Brush Background { get; }
    public Brush Foreground { get; }
    public ICommand RemoveCommand { get; }

    private static string Ago(int days) => days switch
    {
        0 => Loc.Instance["Dev_Today"],
        1 => Loc.Instance["Dev_Yesterday"],
        _ => Loc.Format("Dev_DaysAgo", days),
    };

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>Settings window: General, Network, Security, Devices and About pages over <see cref="AppController"/>.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppController _app;
    private readonly Func<DeviceRowViewModel, bool> _confirmRemove;
    private SettingsPage _page = SettingsPage.General;
    private string _portText;
    private string _portError = "";
    private string _hotkeyText;
    private string _hotkeyMessage = "";
    private bool _capturing;
    private string _signature = "";
    private string _adminMessage = "";
    private readonly Func<bool> _confirmAdmin;

    public SettingsViewModel(AppController app, Func<DeviceRowViewModel, bool>? confirmRemove = null, Func<string, bool>? confirmUpdate = null, Func<bool>? confirmAdmin = null)
    {
        _confirmAdmin = confirmAdmin ?? (() => true);
        _app = app;
        Updates = new UpdatesViewModel(app, confirmUpdate ?? (_ => true));
        _confirmRemove = confirmRemove ?? (_ => true);
        _portText = app.Settings.Port.ToString();
        _hotkeyText = app.Settings.Hotkey;
                ApplyPortCommand = new RelayCommand(async () => await ApplyPortAsync());
        DisconnectAllCommand = new RelayCommand(async () => await _app.EmergencyStopAsync());
        RemoveCommand = new RelayCommand(async p =>
        {
            if (p is DeviceRowViewModel row && _confirmRemove(row)) await _app.RemoveDeviceAsync(row.Info.Id);
        });
        SelectPageCommand = new RelayCommand(p => { if (p is SettingsPage page) Page = page; });
        Loc.Instance.LanguageChanged += (_, _) => Refresh(force: true);
        Refresh(force: true);
    }

    public ICommand ApplyPortCommand { get; }
    public ICommand DisconnectAllCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SelectPageCommand { get; }
    public UpdatesViewModel Updates { get; }

    public IReadOnlyList<LanguageChoice> Languages { get; private set; } = [];
    public IReadOnlyList<NavItem> Nav { get; private set; } = [];
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];
    public IReadOnlyList<AdapterChoice> Adapters { get; private set; } = [];

    public SettingsPage Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Raise(nameof(IsGeneral)); Raise(nameof(IsNetwork)); Raise(nameof(IsSecurity)); Raise(nameof(IsDevices)); Raise(nameof(IsUpdates)); Raise(nameof(IsAbout));
            Refresh(force: true);
        }
    }

    public bool IsGeneral => _page == SettingsPage.General;
    public bool IsNetwork => _page == SettingsPage.Network;
    public bool IsSecurity => _page == SettingsPage.Security;
    public bool IsDevices => _page == SettingsPage.Devices;
    public bool IsUpdates => _page == SettingsPage.Updates;
    public bool IsAbout => _page == SettingsPage.About;

    // ---- General
    public bool StartWithWindows
    {
        get => _app.Settings.StartWithWindows;
        set => _ = _app.UpdateSettingsAsync(s => s with { StartWithWindows = value });
    }

    public bool StartMinimised
    {
        get => _app.Settings.StartMinimised;
        set => _ = _app.UpdateSettingsAsync(s => s with { StartMinimised = value });
    }

    public string LanguageCode
    {
        get => _app.Settings.Language;
        set => _ = _app.UpdateSettingsAsync(s => s with { Language = value });
    }

    // ---- Network
    public string PortText { get => _portText; set { if (Set(ref _portText, value)) PortError = ""; } }
    public string PortError { get => _portError; private set { if (Set(ref _portError, value)) Raise(nameof(HasPortError)); } }
    public string StatusText => _app.StatusText;
    public bool StatusIsProblem => _app.StatusKind != ListenerStatusKind.Listening;

    public string? SelectedAdapterId
    {
        get => _app.Settings.NetworkAdapterId;
        set => _ = _app.UpdateSettingsAsync(s => s with { NetworkAdapterId = value });
    }

    private async Task ApplyPortAsync()
    {
        if (!int.TryParse(_portText.Trim(), out var port) || !AppSettings.IsValidPort(port))
        {
            PortError = Loc.Instance["Set_PortInvalid"];
            return;
        }
        await _app.UpdateSettingsAsync(s => s with { Port = port });
        Refresh();
    }

    // ---- Security
    // The switch is the broker service itself (see <see cref="IAdminControl"/>): reading it needs no rights, changing it goes through Windows' consent prompt.
    public bool AdminChecked => _app.Admin.State is BrokerServiceState.On or BrokerServiceState.Faulted;
    /// <summary>Whether the switch can be used at all: the service must be installed and readable.</summary>
    public bool AdminAvailable => _app.Admin.State is BrokerServiceState.Off or BrokerServiceState.On or BrokerServiceState.Faulted;
    public bool AdminStatusIsProblem => _app.Admin.State is BrokerServiceState.NotInstalled or BrokerServiceState.Faulted or BrokerServiceState.Unknown;
    public string AdminStatusText => Loc.Instance[_app.Admin.State switch
    {
        BrokerServiceState.NotInstalled => "Admin_NotInstalled",
        BrokerServiceState.On => "Admin_On",
        BrokerServiceState.Faulted => "Admin_Faulted",
        BrokerServiceState.Unknown => "Admin_Unknown",
        _ => "Admin_Off",
    }];
    public string AdminMessage { get => _adminMessage; private set { if (Set(ref _adminMessage, value)) Raise(nameof(HasAdminMessage)); } }
    public bool HasAdminMessage => _adminMessage.Length > 0;

    /// <summary>
    /// The user flipped the switch. Turning it on first shows the plain-language warning on this PC (the confirmAdmin callback); only
    /// a yes goes on to Windows' own prompt. The phone has no way to reach this: no message of the phone protocol touches settings.
    /// </summary>
    public async Task SetAdminAsync(bool enable)
    {
        AdminMessage = "";
        if (enable && !_confirmAdmin())
        {
            Refresh();
            return;
        }
        var result = await _app.SetAdminControlAsync(enable);
        AdminMessage = result switch
        {
            AdminSwitchResult.Cancelled => Loc.Instance["Admin_Cancelled"],
            AdminSwitchResult.Failed => Loc.Instance["Admin_Failed"],
            _ => "",
        };
        Refresh();
    }

    public string HotkeyText { get => _capturing ? Loc.Instance["Set_HotkeyPress"] : _hotkeyText; private set => Set(ref _hotkeyText, value); }
    public string HotkeyMessage { get => _hotkeyMessage; private set { if (Set(ref _hotkeyMessage, value)) Raise(nameof(HasHotkeyMessage)); } }

    public void BeginHotkeyCapture() { _capturing = true; Raise(nameof(HotkeyText)); }

    public void EndHotkeyCapture() { _capturing = false; Raise(nameof(HotkeyText)); }

    /// <summary>A key combination was pressed while the hotkey box had focus.</summary>
    public async Task CaptureHotkeyAsync(HotkeyModifiers modifiers, uint virtualKey)
    {
        var candidate = new Hotkey(modifiers, virtualKey).ToString();
        if (!candidate.TryParseHotkey(out _)) { HotkeyMessage = Loc.Instance["Set_HotkeyNeed"]; return; }
        var result = await _app.SetHotkeyAsync(candidate);
        HotkeyMessage = result == HotkeyResult.InUse ? Loc.Instance["Set_HotkeyInUse"] : result == HotkeyResult.Invalid ? Loc.Instance["Set_HotkeyNeed"] : "";
        if (result == HotkeyResult.Ok) HotkeyText = candidate;
    }

    // ---- About
    public string VersionLine => Loc.Format("About_Line1", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");

    /// <summary>Re-read everything that depends on app state or language.</summary>
    public void Refresh(bool force = false)
    {
        Nav = [.. new[] { SettingsPage.General, SettingsPage.Network, SettingsPage.Security, SettingsPage.Devices, SettingsPage.Updates, SettingsPage.About }
            .Select(p => new NavItem(p, Loc.Instance["Nav_" + p], p == _page))];
        Raise(nameof(Nav));
        Raise(nameof(WindowTitle));
        Languages = [new("auto", Loc.Instance["Lang_Auto"]), new("en", "English"), new("he", "עברית")];
        Raise(nameof(Languages));
        Raise(nameof(StatusText));
        Raise(nameof(StatusIsProblem));
        Raise(nameof(VersionLine));
        Raise(nameof(AdminChecked)); Raise(nameof(AdminAvailable)); Raise(nameof(AdminStatusText)); Raise(nameof(AdminStatusIsProblem));
        Raise(nameof(HotkeyText));
        Updates.Refresh();

        var rows = _app.Core.Devices.List()
            .OrderBy(d => d.CreatedUtc)
            .Select(d => DeviceRowInfo.From(d, _app.Core.Time.GetUtcNow(), _app.Core.Devices.Expiry, _app.Core.Hub.ConnectedDeviceIds.Contains(d.Id)))
            .ToList();
        var adapters = new List<AdapterChoice>();
        var candidates = NetworkAnalyzer.Candidates(_app.Network.Adapters);
        var auto = NetworkAnalyzer.Choose(_app.Network.Adapters, null);
        adapters.Add(new AdapterChoice(null, auto is null ? Loc.Instance["Set_CardNone"] : Loc.Format("Set_CardAuto", CardName(auto))));
        adapters.AddRange(candidates.Select(a => new AdapterChoice(a.Id, CardName(a))));

        var signature = string.Join('|', rows.Select(r => $"{r.Id}{r.Kind}{r.LastSeenDaysAgo}{r.DaysUntilExpiry}")) + "#" +
                        string.Join('|', adapters.Select(a => a.Id + a.Display)) + Loc.Instance.Culture.Name;
        if (!force && signature == _signature) return;
        _signature = signature;

        Devices.Clear();
        foreach (var r in rows) Devices.Add(new DeviceRowViewModel(r, RemoveCommand));
        Raise(nameof(HasDevices));
        Raise(nameof(NoDevices));
        Adapters = adapters;
        Raise(nameof(Adapters));
        Raise(nameof(SelectedAdapterId));
        Raise(nameof(LanguageCode));
        Raise(nameof(StartWithWindows));
        Raise(nameof(StartMinimised));
    }

    public bool HasDevices => Devices.Count > 0;
    public bool NoDevices => Devices.Count == 0;
    public bool HasPortError => _portError.Length > 0;
    public bool HasHotkeyMessage => _hotkeyMessage.Length > 0;
    public string WindowTitle => Loc.Instance[_page == SettingsPage.Devices ? "Win_Devices" : "Win_Settings"];

    private string CardName(AdapterInfo a)
    {
        var category = _app.Network.CategoryOf(a);
        var cat = Loc.Instance["Cat_" + (category == NetCategory.Unknown ? "Unknown" : category.ToString())];
        return Loc.Format("Set_CardCategory", a.Name, cat);
    }
}
