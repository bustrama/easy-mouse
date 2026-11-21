namespace EasyMouse.Core.Sessions;

/// <summary>What a <see cref="ServerSession"/> needs from the socket. Sends are serialised by the session.</summary>
public interface ISessionTransport
{
    Task SendTextAsync(string text, CancellationToken cancellationToken);
    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    /// <summary>Sends a close frame with the given code and drops the connection. Must not wait indefinitely for the peer.</summary>
    Task CloseAsync(int code, string reason);
}
