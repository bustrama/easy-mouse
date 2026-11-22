using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyMouse.App.Logic;
using EasyMouse.App.ViewModels;

namespace EasyMouse.App.Views;

/// <summary>Settings and Devices (one window, a page each). Closing hides it.</summary>
public partial class SettingsWindow : AppWindow
{
    private readonly AppController _app;
    private readonly SettingsViewModel _vm;

    public SettingsWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        _vm = new SettingsViewModel(app, row => MessageBox.Show(this, Loc.Format("Dev_ConfirmRemove", row.Name), Loc.Instance["App_Name"],
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK,
            text => MessageBox.Show(this, text, Loc.Instance["App_Name"], MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK);
        DataContext = _vm;
        app.StateChanged += (_, _) => { if (IsVisible) _vm.Refresh(); };
    }

    public bool AllowClose { get; set; }

    public void ShowPage(SettingsPage page)
    {
        _vm.Page = page;
        _vm.Refresh(force: true);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ---- combo boxes: act only on a choice the user made, not on the view model re-selecting its own value

    private void OnLanguageChosen(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedValue is string code && code != _vm.LanguageCode) _vm.LanguageCode = code;
    }

    private void OnCardChosen(object sender, SelectionChangedEventArgs e)
    {
        if (CardBox.SelectedItem is not AdapterChoice choice || !CardBox.IsDropDownOpen && !CardBox.IsKeyboardFocusWithin) return;
        if (choice.Id != _vm.SelectedAdapterId) _vm.SelectedAdapterId = choice.Id;
    }

    // ---- hotkey capture

    private void OnHotkeyFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm.BeginHotkeyCapture();

    private void OnHotkeyBlur(object sender, KeyboardFocusChangedEventArgs e) => _vm.EndHotkeyCapture();

    private async void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.DeadCharProcessed or Key.None) return; // wait for the real key
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }

        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        await _vm.CaptureHotkeyAsync(mods, vk);
        Keyboard.ClearFocus();
    }
}
