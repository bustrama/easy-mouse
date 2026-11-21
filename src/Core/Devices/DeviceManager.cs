using EasyMouse.Core.Protocol;
using EasyMouse.Core.Sessions;

namespace EasyMouse.Core.Devices;

/// <summary>
/// Paired devices plus their live sockets. Revocation removes the device from the store and closes its
/// sockets in one call; devices unused for <see cref="Expiry"/> (default 90 days) are refused and purged.
/// </summary>
public sealed class DeviceManager
{
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromDays(90);

    private readonly TimeProvider _time;

    public DeviceManager(IDeviceStore store, ConnectionHub hub, TimeProvider time, TimeSpan? expiry = null)
    {
        Store = store;
        Hub = hub;
        _time = time;
        Expiry = expiry ?? DefaultExpiry;
    }

    public IDeviceStore Store { get; }
    public ConnectionHub Hub { get; }
    public TimeSpan Expiry { get; }

    public IReadOnlyList<Device> List() => Store.GetAll();

    /// <summary>The device if it exists and has not expired. An expired device is removed on the spot.</summary>
    public Device? FindActive(string id)
    {
        var device = Store.Get(id);
        if (device is null) return null;
        if (_time.GetUtcNow() - device.LastSeenUtc > Expiry)
        {
            Store.Remove(id);
            return null;
        }
        return device;
    }

    public void MarkSeen(string id) => Store.Touch(id, _time.GetUtcNow());

    /// <summary>Removes expired devices and closes their sockets; returns how many.</summary>
    public async Task<int> PurgeExpiredAsync()
    {
        int count = 0;
        var now = _time.GetUtcNow();
        foreach (var device in Store.GetAll())
        {
            if (now - device.LastSeenUtc <= Expiry) continue;
            if (Store.Remove(device.Id)) count++;
            await Hub.CloseDeviceAsync(device.Id, CloseCode.Unauthorized, "expired").ConfigureAwait(false);
        }
        return count;
    }

    /// <summary>Takes effect immediately: the device is forgotten first, then its live sockets are closed with 4401.</summary>
    public async Task<bool> RevokeAsync(string id)
    {
        bool existed = Store.Remove(id);
        await Hub.CloseDeviceAsync(id, CloseCode.Unauthorized, "revoked").ConfigureAwait(false);
        return existed;
    }

    public async Task<int> RevokeAllAsync()
    {
        int count = Store.Clear();
        await Hub.CloseAllAsync(CloseCode.Unauthorized, "revoked").ConfigureAwait(false);
        return count;
    }
}
