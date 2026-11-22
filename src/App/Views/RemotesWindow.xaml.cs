using System.ComponentModel;
using System.Windows;
using EasyMouse.App.ViewModels;
using Microsoft.Win32;

namespace EasyMouse.App.Views;

/// <summary>Remotes: what is on, what needs approval, import. Closing hides it.</summary>
public partial class RemotesWindow : AppWindow, IRemotesDialogs
{
    public RemotesWindow(AppController app)
    {
        InitializeComponent();
        DataContext = new RemotesViewModel(app, this);
    }

    public bool AllowClose { get; set; }

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

    string? IRemotesDialogs.PickRemoteFile()
    {
        var dialog = new OpenFileDialog { Filter = Loc.Instance["Rm_ImportFilter"], CheckFileExists = true, Title = Loc.Instance["Rm_Import"] };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    string? IRemotesDialogs.PickProgram()
    {
        var dialog = new OpenFileDialog { Filter = Loc.Instance["Rm_ProgramFilter"], CheckFileExists = true, Title = Loc.Instance["Rm_Choose"] };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
