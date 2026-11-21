using System.Net.WebSockets;
using System.Text;
using EasyMouse.Core.Sessions;

namespace EasyMouse.Server;

/// <summary>Adapts a <see cref="WebSocket"/> to <see cref="ISessionTransport"/>. WebSocket allows one send at a time; the session serialises its sends.</summary>
internal sealed class WebSocketTransport(WebSocket socket) : ISessionTransport
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
        => socket.State == WebSocketState.Open
            ? socket.SendAsync(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellationToken).AsTask()
            : Task.CompletedTask;

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => socket.State == WebSocketState.Open
            ? socket.SendAsync(data, WebSocketMessageType.Binary, true, cancellationToken).AsTask()
            : Task.CompletedTask;

    public async Task CloseAsync(int code, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var cts = new CancellationTokenSource(CloseGrace);
        try
        {
            await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, cts.Token).ConfigureAwait(false);
            // A peer that never answers with its own close frame must not hold the connection open.
            _ = Task.Delay(CloseGrace).ContinueWith(_ => { if (socket.State != WebSocketState.Closed) socket.Abort(); }, TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            socket.Abort();
        }
    }
}
