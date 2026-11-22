using System.Net;
using System.Net.Http;
using System.Text.Json;
using EasyMouse.App.Logic;
using EasyMouse.App.ViewModels;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Fakes;
using EasyMouse.Core.Pairing;
using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.App.Tests;

/// <summary>
/// The App's non-window pieces against the real Kestrel host: listener controller, approval queue and the approval
/// view model, driven from outside by the Core protocol client over a real WebSocket.
/// </summary>
public class ListenerAndApprovalTests
{
    private static readonly ListenerPlan Loopback = new(IPAddress.Loopback, 0, 8, ["127.0.0.1"], StopReason.None);

    private static CoreServices NewCore(TimeSpan? allowDelay = null)
        => CoreServices.Create(new InMemoryDeviceStore(), new RecordingInputSink(), new RecordingMediaController(),
            pairingOptions: new PairingOptions { AllowDelay = allowDelay ?? TimeSpan.Zero });

    private static Uri Ws(ListenerController l) => new($"ws://127.0.0.1:{l.EndPoint!.Port}/ws");

    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task Pairing_end_to_end_through_the_approval_dialog_with_the_one_second_delay()
    {
        var core = NewCore(allowDelay: TimeSpan.FromSeconds(1));
        await using var listener = new ListenerController(core, "TestPC");
        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(core.Pairing, core.Hub);
        await listener.ApplyAsync(Loopback);
        Assert.True(listener.Running);

        core.Pairing.OpenWindow(); // what showing the QR window does
        var secret = core.Pairing.CurrentSecret!;
        await using var phone = await TestClient.ConnectAsync(Ws(listener));
        var paired = phone.PairAsync(secret.Secret, "Dana's iPhone");

        await Eventually(() => coordinator.Current is not null);
        var vm = new ApproveViewModel(coordinator.Current!);
        Assert.Equal("Dana's iPhone", vm.DeviceName);
        Assert.Matches("^[0-9]{4}$", vm.Code!);
        Assert.False(vm.IsTakeover);

        // Allow is off for the first second: the button, its command and the request itself all refuse.
        Assert.False(vm.AllowEnabled);
        Assert.False(vm.AllowCommand.CanExecute(null));
        Assert.False(coordinator.Current!.Allow());
        Assert.False(coordinator.Current.IsDecided);

        await Task.Delay(1100);
        vm.Tick();
        Assert.True(vm.AllowEnabled);
        vm.AllowCommand.Execute(null);

        var device = await paired;
        Assert.Equal("Dana's iPhone", core.Devices.List().Single().Name);
        await Eventually(() => coordinator.Current is null);

        // The paired phone can now connect and drive the PC.
        await using var phone2 = await TestClient.ConnectAsync(Ws(listener));
        await phone2.AuthenticateAsync(device);
        await Eventually(() => core.Hub.ConnectedDeviceIds.Contains(device.Id));
    }

    [Fact]
    public async Task Deny_is_reported_to_the_phone_and_nothing_is_stored()
    {
        var core = NewCore();
        await using var listener = new ListenerController(core, "TestPC");
        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(core.Pairing, core.Hub);
        await listener.ApplyAsync(Loopback);
        core.Pairing.OpenWindow();
        await using var phone = await TestClient.ConnectAsync(Ws(listener));

        await phone.SendTextAsync(JsonSerializer.Serialize(new
        {
            t = "pair.hello", name = "Intruder",
            nc = EasyMouse.Core.Crypto.B64.Encode(new byte[16]),
            mac = EasyMouse.Core.Crypto.B64.Encode(EasyMouse.Core.Crypto.HandshakeCrypto.PairMac(core.Pairing.CurrentSecret!.Secret, new byte[16], "Intruder")),
        }));
        await Eventually(() => coordinator.Current is not null);
        new ApproveViewModel(coordinator.Current!).DenyCommand.Execute(null);

        Assert.Empty(core.Devices.List());
        await Eventually(() => core.Pairing.State == PairingState.Open);
    }

    [Fact]
    public async Task One_dialog_at_a_time_a_takeover_waits_behind_the_pairing_request()
    {
        var core = NewCore();
        await using var listener = new ListenerController(core, "TestPC");
        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(core.Pairing, core.Hub);
        await listener.ApplyAsync(Loopback);

        // Two paired phones; A is connected and controlling, B is waiting.
        var devices = new List<DeviceCredentials>();
        foreach (var name in new[] { "Phone A", "Phone B" })
        {
            core.Pairing.OpenWindow();
            await using var p = await TestClient.ConnectAsync(Ws(listener));
            var task = p.PairAsync(core.Pairing.CurrentSecret!.Secret, name);
            await Eventually(() => coordinator.Current is not null);
            coordinator.Current!.Allow();
            devices.Add(await task);
            await Eventually(() => coordinator.Current is null);
        }
        await using var a = await TestClient.ConnectAsync(Ws(listener));
        await a.AuthenticateAsync(devices[0]);
        await a.ReceiveAsync(); // state snapshot
        await using var b = await TestClient.ConnectAsync(Ws(listener));
        await b.AuthenticateAsync(devices[1]);
        Assert.Equal("busy", (await b.ReceiveTextAsync()).Type);

        await b.SendTextAsync("""{"t":"takeover"}""");
        await Eventually(() => coordinator.Current is not null);
        var takeover = coordinator.Current!;
        Assert.Equal(ApprovalKind.Takeover, takeover.Kind);
        var takeoverVm = new ApproveViewModel(takeover);
        Assert.True(takeoverVm.IsTakeover);
        Assert.False(takeoverVm.HasCode);

        // A new phone scans the QR while the takeover question is open: it queues, it does not replace it.
        core.Pairing.OpenWindow();
        await using var c = await TestClient.ConnectAsync(Ws(listener));
        var cPaired = c.PairAsync(core.Pairing.CurrentSecret!.Secret, "Phone C");
        await Eventually(() => core.Pairing.PendingRequest is not null);
        Assert.Same(takeover, coordinator.Current);

        takeoverVm.DenyCommand.Execute(null);
        Assert.Equal("takeover.denied", (await b.ReceiveTextAsync()).Type);
        await Eventually(() => coordinator.Current is { Kind: ApprovalKind.Pair, DeviceName: "Phone C" });
        coordinator.Current!.Allow();
        await cPaired;
    }

    [Fact]
    public async Task Closing_or_denying_all_clears_the_queue()
    {
        var core = NewCore();
        await using var listener = new ListenerController(core, "TestPC");
        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(core.Pairing, core.Hub);
        await listener.ApplyAsync(Loopback);
        core.Pairing.OpenWindow();
        await using var phone = await TestClient.ConnectAsync(Ws(listener));
        var task = phone.PairAsync(core.Pairing.CurrentSecret!.Secret, "Late");
        await Eventually(() => coordinator.Current is not null);
        core.Pairing.CloseWindow(); // closing the QR window denies a pending request
        await Eventually(() => coordinator.Current is null);
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    [Fact]
    public async Task Stopping_the_listener_closes_every_phone_and_the_port_stops_answering()
    {
        var core = NewCore();
        await using var listener = new ListenerController(core, "TestPC");
        await listener.ApplyAsync(Loopback);
        var port = listener.EndPoint!.Port;

        // Pair + connect one phone.
        core.Pairing.OpenWindow();
        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(core.Pairing, core.Hub);
        await using var p = await TestClient.ConnectAsync(Ws(listener));
        var task = p.PairAsync(core.Pairing.CurrentSecret!.Secret, "Phone");
        await Eventually(() => coordinator.Current is not null);
        coordinator.Current!.Allow();
        var device = await task;
        await using var phone = await TestClient.ConnectAsync(Ws(listener));
        await phone.AuthenticateAsync(device);
        await Eventually(() => core.Hub.ConnectedDeviceIds.Count == 1);

        // Public network / emergency stop: the plan says "do not listen".
        var closing = phone.WaitForCloseAsync(); // a browser answers the close frame by itself; this client needs a read pending
        await listener.ApplyAsync(new ListenerPlan(null, port, 0, [], StopReason.PublicNetwork));
        Assert.False(listener.Running);
        Assert.Equal(1000, await closing);
        await Assert.ThrowsAnyAsync<Exception>(async () => { await using var _ = await TestClient.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws")); });

        // Back to Private: listening again.
        await listener.ApplyAsync(Loopback with { Port = port });
        Assert.True(listener.Running);
    }

    [Fact]
    public async Task A_busy_port_is_reported_as_such_and_retried_later()
    {
        var core = NewCore();
        await using var first = new ListenerController(core, "TestPC");
        await first.ApplyAsync(Loopback);
        var port = first.EndPoint!.Port;

        await using var second = new ListenerController(NewCore(), "TestPC");
        await second.ApplyAsync(Loopback with { Port = port });
        Assert.False(second.Running);
        Assert.Equal(ListenerError.PortInUse, second.Error);

        await first.ApplyAsync(new ListenerPlan(null, port, 0, [], StopReason.EmergencyStop)); // frees the port
        await second.ApplyAsync(Loopback with { Port = port }); // same plan again: a failed start is retried
        Assert.True(second.Running);
        Assert.Equal(ListenerError.None, second.Error);
    }

    [Fact]
    public async Task Requests_reaching_the_listener_are_reported_with_their_peer()
    {
        var core = NewCore();
        await using var listener = new ListenerController(core, "TestPC");
        IPAddress? seen = null;
        listener.RequestReceived += p => seen = p;
        await listener.ApplyAsync(Loopback);
        using var http = new HttpClient();
        using var response = await http.GetAsync($"http://127.0.0.1:{listener.EndPoint!.Port}/api/info");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(IPAddress.Loopback, seen);

        var advisor = new HelpAdvisor(TimeProvider.System, TimeSpan.Zero);
        advisor.WindowOpened();
        advisor.RequestSeen(seen); // loopback: the phone has not been heard from
        Assert.True(advisor.ShouldShowHelp);
    }

    [Fact]
    public async Task The_listener_never_binds_to_all_interfaces()
    {
        var core = NewCore();
        await using var listener = new ListenerController(core, "TestPC");
        await listener.ApplyAsync(new ListenerPlan(IPAddress.Any, 0, 0, ["x"], StopReason.None));
        Assert.False(listener.Running);
        Assert.Equal(ListenerError.Other, listener.Error);
    }
}
