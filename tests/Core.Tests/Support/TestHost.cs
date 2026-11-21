using System.Net;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Fakes;
using EasyMouse.Core.Pairing;
using EasyMouse.Core.Sessions;
using EasyMouse.Server;

namespace EasyMouse.Core.Tests.Support;

/// <summary>The real Kestrel host on 127.0.0.1:random with recording fakes behind it.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string PcHostName = "testpc.local";

    private TestHost() { }

    public required CoreServices Services { get; init; }
    public required EasyMouseServer Server { get; init; }
    public required RecordingInputSink Input { get; init; }
    public required RecordingMediaController Media { get; init; }
    public required ManualTimeProvider Time { get; init; }
    public int Port => Server.EndPoint!.Port;
    public Uri WsUri => new($"ws://127.0.0.1:{Port}/ws");
    public Uri HttpUri => new($"http://127.0.0.1:{Port}/");

    /// <summary>When set, pair requests are allowed as soon as they appear (the Allow delay still applies).</summary>
    public bool AutoApprovePairing { get; set; } = true;

    public static async Task<TestHost> StartAsync(
        PairingOptions? pairing = null,
        SessionLimits? limits = null,
        IDeviceStore? store = null,
        TimeSpan? expiry = null,
        StaticAssets? assets = null)
    {
        var time = new ManualTimeProvider();
        var input = new RecordingInputSink();
        var media = new RecordingMediaController();
        var services = CoreServices.Create(
            store ?? new InMemoryDeviceStore(), input, media,
            time: time,
            pairingOptions: pairing ?? new PairingOptions { AllowDelay = TimeSpan.Zero },
            limits: limits,
            deviceExpiry: expiry);
        var server = new EasyMouseServer(new ServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            AllowedHosts = ["127.0.0.1", PcHostName],
            PeerPrefixLength = 8,
            PcName = "Test PC",
            Assets = assets ?? new StaticAssets(new Dictionary<string, byte[]>()),
        }, services);
        await server.StartAsync();

        var host = new TestHost { Services = services, Server = server, Input = input, Media = media, Time = time };
        services.Pairing.Changed += (_, _) =>
        {
            if (host.AutoApprovePairing && services.Pairing.PendingRequest is { } req) req.Allow();
        };
        return host;
    }

    public async Task<TestClient> ConnectAsync(string? origin = null)
        => await TestClient.ConnectAsync(WsUri, origin);

    /// <summary>Opens the QR window and pairs a fresh device, returning its credentials.</summary>
    public async Task<DeviceCredentials> PairAsync(string name = "Test Phone")
    {
        Services.Pairing.OpenWindow();
        var k = Services.Pairing.CurrentSecret!.Secret;
        await using var client = await ConnectAsync();
        return await client.PairAsync(k, name);
    }

    public async Task<TestClient> ConnectDeviceAsync(DeviceCredentials device)
    {
        var client = await ConnectAsync();
        await client.AuthenticateAsync(device);
        return client;
    }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

public sealed record DeviceCredentials(string Id, byte[] Secret);
