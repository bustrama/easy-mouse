namespace EasyMouse.Core.Devices;

/// <summary>A paired phone. <see cref="Id"/> is base64url of 8 random bytes; <see cref="Secret"/> is the 256-bit device secret.</summary>
public sealed record Device(string Id, string Name, byte[] Secret, DateTimeOffset CreatedUtc, DateTimeOffset LastSeenUtc);
