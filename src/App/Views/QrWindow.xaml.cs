using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasyMouse.App.ViewModels;

namespace EasyMouse.App.Views;

/// <summary>The QR window. Closing it hides it (the app stays in the tray) and closes the pairing window (pairing is open only while the QR is shown).</summary>
public partial class QrWindow : AppWindow
{
    private readonly AppController _app;
    private readonly QrViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public QrWindow(AppController app, Action openSettings)
    {
        InitializeComponent();
        _app = app;
        _vm = new QrViewModel(app, openSettings);
        DataContext = _vm;
        _timer.Tick += (_, _) => _vm.Refresh();
        _app.StateChanged += OnAppStateChanged;
        IsVisibleChanged += OnVisibleChanged;
        StateChanged += (_, _) => SyncShown();
        Loaded += (_, _) => StartRingAnimation();
    }

    public bool AllowClose { get; set; }

    /// <summary>Visible AND not minimized: a minimized window is still "visible" to WPF, but the QR is not on any screen.</summary>
    private bool IsShown => IsVisible && WindowState != WindowState.Minimized;
    private bool _shown;

    private void OnAppStateChanged(object? sender, EventArgs e) { if (_shown) _vm.Refresh(); }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => SyncShown();

    private void SyncShown()
    {
        var shown = IsShown;
        if (shown == _shown) return;
        _shown = shown;
        if (shown)
        {
            _app.SetQrWindowVisible(true);
            _vm.Refresh(force: true);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _app.SetQrWindowVisible(false); // closes pairing and discards the one-time secret
        }
    }

    /// <summary>Brings the window up (from the tray, or from a second launch).</summary>
    public void ShowAndActivate()
    {
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

    private void StartRingAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation) return; // honour "show animations" being off
        var duration = new Duration(TimeSpan.FromSeconds(2));
        var scale = new DoubleAnimation(0.9, 1.18, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.7, 0, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        RingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
        RingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale);
        Ring.BeginAnimation(OpacityProperty, fade);
    }
}
