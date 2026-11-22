namespace EasyMouse.Core.Tests.Support;

// TestClient.cs is linked from Core.Tests; this is the one small record it needs from TestHost.cs.
public sealed record DeviceCredentials(string Id, byte[] Secret);
