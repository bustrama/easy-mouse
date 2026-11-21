namespace EasyMouse.Core.Protocol;

/// <summary>WebSocket close codes used by the server (docs/protocol.md, "Errors").</summary>
public static class CloseCode
{
    public const int Normal = 1000;
    public const int InternalError = 1011;
    public const int Malformed = 4400;
    public const int Unauthorized = 4401;
    public const int PairingClosed = 4408;
    public const int Replaced = 4409;
    public const int RateLimited = 4429;
}
