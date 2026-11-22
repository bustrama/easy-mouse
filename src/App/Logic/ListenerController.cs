using System.Net;
using System.Net.Sockets;
using EasyMouse.Core.Sessions;
using EasyMouse.Server;

namespace EasyMouse.App.Logic;

public enum ListenerError { None, PortInUse, Other }

/// <summary>
/// Owns the one <see cref="EasyMouseServer"/>: starts it for a <see cref="ListenerPlan"/>, stops it when the plan says
/// not to listen (Public network, emergency stop, no network) and restarts it when the address or port changes.
/// Stopping closes every phone socket (the server does that on stop).
/// </summary>
public sealed class ListenerController : IAsyncDisposable
{
    private readonly CoreServices _core;
    private readonly string _pcName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EasyMouseServer? _server;

    public ListenerController(CoreServices core, string pcName)
    {
        _core = core;
        _pcName = pcName;
    }

    /// <summary>The plan last applied (what the user is told we are doing).</summary>
    public ListenerPlan? Plan { get; private set; }
    public bool Running => _server is not null;
    public ListenerError Error { get; private set; }
    public string? ErrorDetail { get; private set; }
    public IPEndPoint? EndPoint => _server?.EndPoint;

    /// <summary>Raised (any thread) after the listener started, stopped or failed.</summary>
    public event EventHandler? Changed;
    /// <summary>Every HTTP request that reached the listener, with its peer address.</summary>
    public event Action<IPAddress?>? RequestReceived;

    public async Task ApplyAsync(ListenerPlan plan)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Same target and in the state it asks for: nothing to do. A failed start is not "running", so it is retried.
            if (plan.SameTarget(Plan) && Running == plan.Listening) return;

            await StopServerAsync().ConfigureAwait(false);
            Plan = plan;
            Error = ListenerError.None;
            ErrorDetail = null;
            if (plan.Listening) await StartServerAsync(plan).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task StartServerAsync(ListenerPlan plan)
    {
        EasyMouseServer? server = null;
        try
        {
            server = new EasyMouseServer(new ServerOptions
            {
                BindAddress = plan.Address!,
                Port = plan.Port,
                AllowedHosts = plan.AllowedHosts,
                PeerPrefixLength = plan.PrefixLength,
                PcName = _pcName,
            }, _core);
            server.RequestReceived += OnRequest;
            await server.StartAsync().ConfigureAwait(false);
            _server = server;
        }
        catch (Exception ex)
        {
            if (server is not null) server.RequestReceived -= OnRequest;
            Error = IsAddressInUse(ex) ? ListenerError.PortInUse : ListenerError.Other;
            ErrorDetail = ex.Message;
        }
    }

    private void OnRequest(IPAddress? peer) => RequestReceived?.Invoke(peer);

    private static bool IsAddressInUse(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
        return false;
    }

    private async Task StopServerAsync()
    {
        var server = _server;
        _server = null;
        if (server is null) return;
        server.RequestReceived -= OnRequest;
        // Tell every phone first and give them a moment to answer the close, so they see a clean close and not a reset.
        await _core.Hub.CloseAllAsync(EasyMouse.Core.Protocol.CloseCode.Normal, "listener stopping").ConfigureAwait(false);
        for (int i = 0; i < 20 && _core.Hub.SessionCount > 0; i++) await Task.Delay(50).ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Closes every phone socket right now without changing the plan.</summary>
    public Task DisconnectAllAsync() => _core.Hub.CloseAllAsync(EasyMouse.Core.Protocol.CloseCode.Normal, "disconnected");

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopServerAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
