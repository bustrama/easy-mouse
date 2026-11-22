using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Pairing;

namespace EasyMouse.Core.Sessions;

/// <summary>Everything a session needs, wired together. Create with <see cref="Create"/>.</summary>
public sealed class CoreServices
{
    public required DeviceManager Devices { get; init; }
    public required PairingService Pairing { get; init; }
    public required ConnectionHub Hub { get; init; }
    public required IInputSink Input { get; init; }
    public required IMediaController Media { get; init; }
    public required IRemoteActionHandler Actions { get; init; }
    public IRemoteLayoutSource Layouts { get; init; } = new NoRemoteLayouts();
    public required HostStatus Status { get; init; }
    public required SessionLimits Limits { get; init; }
    public required TimeProvider Time { get; init; }
    /// <summary>Receives failures that are handled by closing a socket but must not go unnoticed (the host logs them).</summary>
    public Action<string, Exception>? OnError { get; set; }

    public static CoreServices Create(
        IDeviceStore store,
        IInputSink input,
        IMediaController media,
        IRemoteActionHandler? actions = null,
        IRemoteLayoutSource? layouts = null,
        TimeProvider? time = null,
        PairingOptions? pairingOptions = null,
        SessionLimits? limits = null,
        TimeSpan? deviceExpiry = null)
    {
        time ??= TimeProvider.System;
        var hub = new ConnectionHub(time, pairingOptions);
        var devices = new DeviceManager(store, hub, time, deviceExpiry);
        return new CoreServices
        {
            Devices = devices,
            Pairing = new PairingService(devices, time, pairingOptions),
            Hub = hub,
            Input = input,
            Media = media,
            Actions = actions ?? new NoRemoteActions(),
            Layouts = layouts ?? new NoRemoteLayouts(),
            Status = new HostStatus(),
            Limits = limits ?? new SessionLimits(),
            Time = time,
        };
    }
}
