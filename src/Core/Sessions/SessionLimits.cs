namespace EasyMouse.Core.Sessions;

/// <summary>Server-side limits (docs/protocol.md, "Limits").</summary>
public sealed record SessionLimits
{
    /// <summary>Largest WebSocket message accepted, in bytes (binary frame or handshake text).</summary>
    public int MaxFrameBytes { get; init; } = 4096;
    /// <summary>Largest text payload of a 0x04 frame, in UTF-8 bytes.</summary>
    public int MaxTextBytes { get; init; } = 1024;
    /// <summary>Sustained frames per second from the phone (touch at 120 Hz plus buttons fits well inside).</summary>
    public double FramesPerSecond { get; init; } = 200;
    /// <summary>Burst allowance in frames.</summary>
    public int FrameBurst { get; init; } = 200;
    /// <summary>Time allowed for hello/auth (not for waiting on the PC user).</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Consecutive-or-not frames that fail authentication or replay checks before the socket is closed.</summary>
    public int MaxInvalidFrames { get; init; } = 32;
}
