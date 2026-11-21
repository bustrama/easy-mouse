namespace EasyMouse.Core.Devices;

public sealed class InMemoryDeviceStore : IDeviceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);

    public IReadOnlyList<Device> GetAll() { lock (_gate) return [.. _devices.Values]; }

    public Device? Get(string id) { lock (_gate) return _devices.GetValueOrDefault(id); }

    public void Add(Device device) { lock (_gate) _devices[device.Id] = device; }

    public bool Remove(string id) { lock (_gate) return _devices.Remove(id); }

    public int Clear()
    {
        lock (_gate)
        {
            int count = _devices.Count;
            _devices.Clear();
            return count;
        }
    }

    public bool Touch(string id, DateTimeOffset lastSeenUtc)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var device)) return false;
            _devices[id] = device with { LastSeenUtc = lastSeenUtc };
            return true;
        }
    }
}
