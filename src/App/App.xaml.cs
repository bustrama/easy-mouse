using System.Windows;
using EasyMouse.App.Logic;
using EasyMouse.App.Platform;
using EasyMouse.App.ViewModels;
using EasyMouse.App.Views;

namespace EasyMouse.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppController? _controller;
    private TrayIcon? _tray;
    private QrWindow? _qr;
    private SettingsWindow? _settings;
    private ApproveWindow? _approve;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = SingleInstance.TryAcquire("EasyMouse.App");
        if (_instance is null)
        {
            Shutdown(); // another copy is running; it has been asked to show its window
            return;
        }

        try
        {
            _controller = new AppController(AppPaths.Default());
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(Loc.Format("Err_StoreBody", ex.Message), Loc.Instance["Err_StoreTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _tray = new TrayIcon();
        _tray.ShowQrClicked += ShowQr;
        _tray.SettingsClicked += () => ShowSettings(SettingsPage.General);
        _tray.DevicesClicked += () => ShowSettings(SettingsPage.Devices);
        _tray.DisconnectAllClicked += () => _ = _controller.EmergencyStopAsync(); // closes every socket AND stops listening (docs/security.md)
        _tray.ResumeClicked += () => _ = _controller.ResumeAsync();
        _tray.QuitClicked += Quit;
        Loc.Instance.LanguageChanged += (_, _) => RefreshTray();

        _qr = new QrWindow(_controller, () => ShowSettings(SettingsPage.Network));
        _settings = new SettingsWindow(_controller);

        _controller.StateChanged += (_, _) => { RefreshTray(); ShowPendingApproval(); };
        _controller.DeviceConnected += name => _tray.Toast(Loc.Format("Toast_Connected", name), Loc.Instance["Toast_ConnectedBody"]);
        _controller.EmergencyStopRan += (_, _) => _tray.Toast(Loc.Instance["Toast_Emergency"], Loc.Instance["Toast_EmergencyBody"]);
        _instance.ListenForShow(() => Dispatcher.BeginInvoke(ShowQr));

        RefreshTray();
        await _controller.StartAsync();
        if (_controller.SettingsLoadError is not null)
            _tray.Toast(Loc.Instance["App_Name"], _controller.SettingsLoadError);
        if (!_controller.HotkeyRegistered)
            _tray.Toast(Loc.Instance["Err_HotkeyTitle"], Loc.Instance["Set_HotkeyInUse"]);

        if (AutostartCommand.ShouldShowWindowAtLaunch(AutostartCommand.IsAutostart(e.Args), _controller.Settings.StartMinimised)) ShowQr();
    }

    private void ShowQr() => _qr?.ShowAndActivate();

    private void ShowSettings(SettingsPage page) => _settings?.ShowPage(page);

    private void RefreshTray()
    {
        if (_tray is null || _controller is null) return;
        _tray.SetTexts(Loc.Instance["Tray_ShowQr"], Loc.Instance["Tray_Settings"], Loc.Instance["Tray_Devices"],
            Loc.Instance["Tray_DisconnectAll"], Loc.Instance["Tray_Resume"], Loc.Instance["Tray_Quit"], Loc.Instance.FlowDirection == FlowDirection.RightToLeft);
        _tray.SetResumeVisible(_controller.EmergencyStopped);
        var (state, tip) = _controller.ConnectedCount > 0 ? (TrayState.Connected, Loc.Instance["Tray_TipConnected"])
            : !_controller.IsListening ? (TrayState.Stopped, Loc.Instance["Tray_TipStopped"])
            : (TrayState.Idle, Loc.Instance["Tray_Tip"]);
        _tray.SetState(state, tip);
    }

    /// <summary>One approval dialog at a time: show the coordinator's current request, close it when that is decided.</summary>
    private void ShowPendingApproval()
    {
        var current = _controller?.Approvals.Current;
        if (current is null || current.IsDecided) return;
        if (_approve is { IsLoaded: true } existing && existing.IsVisible) return;
        _approve = new ApproveWindow(current);
        _approve.Closed += (_, _) => { _approve = null; ShowPendingApproval(); };
        _approve.Show();
        _approve.Activate();
    }

    private async void Quit()
    {
        if (_controller is null) { Shutdown(); return; }
        _qr!.AllowClose = true;
        _settings!.AllowClose = true;
        _qr.Close();
        _settings.Close();
        _tray?.Dispose();
        await _controller.DisposeAsync();
        _instance?.Dispose();
        Shutdown();
    }
}
