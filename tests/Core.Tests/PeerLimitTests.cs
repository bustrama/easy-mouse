using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.Core.Tests;

/// <summary>SR-04 (docs/security-review.md): one LAN host must not be able to hold unlimited sockets open.</summary>
public class PeerLimitTests
{
    [Fact]
    public async Task One_peer_cannot_hold_unlimited_websockets()
    {
        await using var host = await TestHost.StartAsync();
        var open = new List<TestClient>();
        try
        {
            int accepted = 0;
            for (int i = 0; i < 200; i++)
            {
                try { open.Add(await host.ConnectAsync()); accepted++; }
                catch (System.Net.WebSockets.WebSocketException) { break; }
            }
            Assert.InRange(accepted, 1, SessionLimits.MaxSocketsPerPeerDefault);
        }
        finally
        {
            foreach (var c in open) await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_peer_limit_frees_up_when_sockets_close()
    {
        await using var host = await TestHost.StartAsync();
        for (int round = 0; round < 3; round++)
        {
            var open = new List<TestClient>();
            for (int i = 0; i < SessionLimits.MaxSocketsPerPeerDefault; i++) open.Add(await host.ConnectAsync());
            foreach (var c in open) await c.DisposeAsync();
            await Task.Delay(300);
        }
    }
}
