using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;
using System.Text.Json;

namespace EasyMouse.Core.Tests;

/// <summary>SR-02 (docs/security-review.md): input held by a vanished phone is released.</summary>
public class HeldInputTests
{
    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static async Task<JsonElement> ReadStateAsync(TestClient client)
    {
        while (true)
        {
            if (await client.ReceiveFrameAsync() is StateMessage s) return JsonDocument.Parse(s.Json).RootElement.Clone();
        }
    }

    // SR-02: a phone that vanishes (battery, Wi-Fi) with a key or button held must not leave it held on the PC.
    [Fact]
    public async Task Held_input_is_released_when_the_controlling_socket_drops()
    {
        await using var host = await TestHost.StartAsync();
        var device = await host.PairAsync();
        var client = await host.ConnectDeviceAsync(device);
        await ReadStateAsync(client);
        await client.SendFrameAsync(new KeyMessage(0xA2, KeyAction.Down)); // Ctrl down
        await client.SendFrameAsync(new ButtonMessage(MouseButton.Left, true));
        await Eventually(() => host.Input.Calls.Count >= 2);

        await client.DisposeAsync(); // abrupt: no key-up, no close handshake

        await Eventually(() => host.Input.ReleaseAllCount == 1);
    }

    [Fact]
    public async Task A_waiting_device_that_disconnects_does_not_release_the_controllers_keys()
    {
        await using var host = await TestHost.StartAsync();
        var a = await host.PairAsync("A");
        var b = await host.PairAsync("B");
        await using var controller = await host.ConnectDeviceAsync(a);
        await ReadStateAsync(controller);
        var waiting = await host.ConnectDeviceAsync(b);
        Assert.Equal("busy", (await waiting.ReceiveTextAsync()).Type);
        await controller.SendFrameAsync(new KeyMessage(0xA2, KeyAction.Down));
        await Eventually(() => host.Input.Calls.Count >= 1);

        await waiting.DisposeAsync();
        await Task.Delay(300);

        Assert.Equal(0, host.Input.ReleaseAllCount);
    }

}
