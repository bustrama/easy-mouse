using System.Text.Json;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.Core.Tests;

/// <summary>docs/protocol.md, "0x10 json, remote layouts": pushed to the controller on connect and whenever the source changes.</summary>
public class LayoutPushTests
{
    private sealed class FakeLayouts : IRemoteLayoutSource
    {
        public string? CurrentJson { get; private set; }
        public event EventHandler? Changed;
        public void Set(string? json) { CurrentJson = json; Changed?.Invoke(this, EventArgs.Empty); }
    }

    private const string One = "{\"t\":\"remotes\",\"remotes\":[{\"id\":\"a\"}]}";
    private const string Two = "{\"t\":\"remotes\",\"remotes\":[{\"id\":\"a\"},{\"id\":\"b\"}]}";

    private static async Task<string> NextJsonAsync(TestClient client)
    {
        while (true)
        {
            var m = await client.ReceiveFrameAsync();
            if (m is JsonMessage j) return j.Json;
        }
    }

    private static async Task<bool> NothingMoreAsync(TestClient client)
    {
        await client.SendFrameAsync(new PingMessage());
        while (true)
        {
            var m = await client.ReceiveFrameAsync();
            if (m is PingMessage) return true; // nothing arrived before the ping reply
            if (m is JsonMessage) return false;
        }
    }

    [Fact]
    public async Task Controller_gets_the_layouts_after_connecting_and_again_on_each_change_but_not_for_identical_text()
    {
        var layouts = new FakeLayouts();
        layouts.Set(One);
        await using var host = await TestHost.StartAsync(layouts: layouts);
        var device = await host.PairAsync();

        await using var client = await host.ConnectDeviceAsync(device);
        Assert.Equal(One, await NextJsonAsync(client));

        layouts.Set(One); // a reload that changed nothing
        layouts.Set(Two);
        Assert.Equal(Two, await NextJsonAsync(client));

        layouts.Set(null); // all remotes gone: the phone must be told, it replaces its list
        Assert.Equal("{\"t\":\"remotes\",\"remotes\":[]}", await NextJsonAsync(client));
        Assert.True(await NothingMoreAsync(client));
    }

    [Fact]
    public async Task Nothing_is_sent_at_connect_when_there_are_no_remotes()
    {
        var layouts = new FakeLayouts();
        await using var host = await TestHost.StartAsync(layouts: layouts);
        var device = await host.PairAsync();
        await using var client = await host.ConnectDeviceAsync(device);
        Assert.True(await NothingMoreAsync(client));
    }

    [Fact]
    public async Task A_device_that_is_only_waiting_does_not_get_layouts()
    {
        var layouts = new FakeLayouts();
        layouts.Set(One);
        await using var host = await TestHost.StartAsync(layouts: layouts);
        var first = await host.PairAsync("one");
        var second = await host.PairAsync("two");

        await using var controller = await host.ConnectDeviceAsync(first);
        Assert.Equal(One, await NextJsonAsync(controller));
        await using var waiting = await host.ConnectDeviceAsync(second);
        layouts.Set(Two);
        Assert.Equal(Two, await NextJsonAsync(controller));
        Assert.Equal("busy", (await waiting.ReceiveTextAsync()).Type); // and nothing else: layouts go to the controller only
    }
}
