namespace EasyMouse.Core.Devices;

/// <summary>Persistence for paired devices. Implementations must be thread-safe.</summary>
public interface IDeviceStore
{
    IReadOnlyList<Device> GetAll();
    Device? Get(string id);
    void Add(Device device);
    /// <summary>Returns false if the id was not present.</summary>
    bool Remove(string id);
    /// <summary>Removes every device; returns how many.</summary>
    int Clear();
    /// <summary>Updates <see cref="Device.LastSeenUtc"/>; returns false if the id is unknown.</summary>
    bool Touch(string id, DateTimeOffset lastSeenUtc);
}

/// <summary>Protects device secrets at rest. The Windows app plugs in DPAPI; tests plug in anything reversible.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the data cannot be unprotected.</summary>
    byte[] Unprotect(byte[] protectedData);
}
