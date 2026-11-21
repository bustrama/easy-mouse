using System.Text.Json;
using EasyMouse.Core.Crypto;

namespace EasyMouse.Core.Devices;

/// <summary>
/// JSON file of devices; each secret is passed through an <see cref="ISecretProtector"/> before it is
/// written. Writes are atomic (temp file + replace). A corrupt file or an undecryptable secret throws on
/// load instead of silently starting empty, which would unpair every phone without telling anyone.
/// </summary>
public sealed class FileDeviceStore : IDeviceStore
{
    private const int FormatVersion = 1;

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);

    public FileDeviceStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        Load();
    }

    public IReadOnlyList<Device> GetAll() { lock (_gate) return [.. _devices.Values]; }

    public Device? Get(string id) { lock (_gate) return _devices.GetValueOrDefault(id); }

    public void Add(Device device)
    {
        lock (_gate)
        {
            _devices[device.Id] = device;
            Save();
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (!_devices.Remove(id)) return false;
            Save();
            return true;
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            int count = _devices.Count;
            if (count == 0) return 0;
            _devices.Clear();
            Save();
            return count;
        }
    }

    public bool Touch(string id, DateTimeOffset lastSeenUtc)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var device)) return false;
            _devices[id] = device with { LastSeenUtc = lastSeenUtc };
            Save();
            return true;
        }
    }

    private sealed class FileModel
    {
        public int Version { get; set; }
        public List<DeviceModel> Devices { get; set; } = [];
    }

    private sealed class DeviceModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Secret { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(_path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Device store '{_path}' is corrupt.", ex);
        }
        if (model is null || model.Version != FormatVersion)
            throw new InvalidDataException($"Device store '{_path}' has an unsupported format.");

        foreach (var d in model.Devices)
        {
            byte[] protectedBytes;
            try { protectedBytes = Convert.FromBase64String(d.Secret); }
            catch (FormatException ex) { throw new InvalidDataException($"Device store '{_path}' contains an invalid secret.", ex); }
            var secret = _protector.Unprotect(protectedBytes);
            if (secret.Length != HandshakeCrypto.DeviceSecretLength)
                throw new InvalidDataException($"Device store '{_path}' contains a secret of the wrong length.");
            _devices[d.Id] = new Device(d.Id, d.Name, secret, d.CreatedUtc, d.LastSeenUtc);
        }
    }

    private void Save()
    {
        var model = new FileModel
        {
            Version = FormatVersion,
            Devices = [.. _devices.Values.Select(d => new DeviceModel
            {
                Id = d.Id,
                Name = d.Name,
                Secret = Convert.ToBase64String(_protector.Protect(d.Secret)),
                CreatedUtc = d.CreatedUtc,
                LastSeenUtc = d.LastSeenUtc,
            })],
        };
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(model, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
