using System.ComponentModel;
using System.Windows.Threading;
using EasyMouse.App.ViewModels;
using EasyMouse.Core.Pairing;

namespace EasyMouse.App.Views;

/// <summary>"Allow this phone?" for one request. Closing the window without an answer denies.</summary>
public partial class ApproveWindow : AppWindow
{
    private readonly ApproveViewModel _vm;
    private readonly ApprovalRequest _request;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public ApproveWindow(ApprovalRequest request)
    {
        InitializeComponent();
        _request = request;
        _vm = new ApproveViewModel(request);
        DataContext = _vm;
        _timer.Tick += (_, _) => { _vm.Tick(); if (_vm.IsDecided) Close(); };
        _timer.Start();
        _ = request.Decision.ContinueWith(_ => UiThread.Post(Close), TaskScheduler.Default);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _timer.Stop();
        _request.Deny(); // no-op if already decided
        base.OnClosing(e);
    }
}
