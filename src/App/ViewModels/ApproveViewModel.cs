using System.Windows.Input;
using EasyMouse.Core.Pairing;

namespace EasyMouse.App.ViewModels;

/// <summary>
/// The approval dialog. Allow stays disabled until the request's own delay has passed (the rule itself is enforced by
/// <see cref="ApprovalRequest.Allow"/>); Deny is always possible and is the default button.
/// </summary>
public sealed class ApproveViewModel : ViewModelBase
{
    private readonly ApprovalRequest _request;
    private bool _allowEnabled;

    public ApproveViewModel(ApprovalRequest request)
    {
        _request = request;
        AllowCommand = new RelayCommand(() => { _request.Allow(); Tick(); }, () => AllowEnabled);
        DenyCommand = new RelayCommand(() => _request.Deny());
        Tick();
    }

    public ICommand AllowCommand { get; }
    public ICommand DenyCommand { get; }

    public bool IsTakeover => _request.Kind == ApprovalKind.Takeover;
    public string DeviceName => _request.DeviceName;
    public string? Code => _request.Code;
    public bool HasCode => Code is not null;
    public string Title => Loc.Instance[IsTakeover ? "Ap_TakeTitle" : "Ap_Title"];
    public string Body => Loc.Instance[IsTakeover ? "Ap_TakeBody" : "Ap_Body"];
    public string Foot => Loc.Instance[IsTakeover ? "Ap_TakeFoot" : "Ap_Foot"];
    public bool IsDecided => _request.IsDecided;
    public Task<bool> Decision => _request.Decision;

    public bool AllowEnabled { get => _allowEnabled; private set { if (Set(ref _allowEnabled, value)) ((RelayCommand)AllowCommand).RaiseCanExecuteChanged(); } }

    /// <summary>Called by a short timer so the button turns on when the delay is over.</summary>
    public void Tick() => AllowEnabled = _request.CanAllow;
}
