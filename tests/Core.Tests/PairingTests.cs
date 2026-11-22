using EasyMouse.Core.Crypto;
using EasyMouse.Core.Devices;
using EasyMouse.Core.Pairing;
using EasyMouse.Core.Sessions;
using EasyMouse.Core.Tests.Support;

namespace EasyMouse.Core.Tests;

public class PairingServiceTests
{
    private readonly ManualTimeProvider _time = new();

    private (PairingService Service, DeviceManager Devices, InMemoryDeviceStore Store) Create(PairingOptions? options = null)
    {
        var store = new InMemoryDeviceStore();
        var hub = new ConnectionHub(_time, options);
        var devices = new DeviceManager(store, hub, _time);
        return (new PairingService(devices, _time, options ?? new PairingOptions { AllowDelay = TimeSpan.Zero }), devices, store);
    }

    private static (byte[] Nc, byte[] Mac) Hello(byte[] k, string name = "Phone")
    {
        var nc = HandshakeCrypto.Random(16);
        return (nc, HandshakeCrypto.PairMac(k, nc, name));
    }

    [Fact]
    public void Closed_window_refuses()
    {
        var (svc, _, _) = Create();
        Assert.Equal(PairingState.Closed, svc.State);
        var (nc, mac) = Hello(new byte[16]);
        Assert.Equal(PairingError.WindowClosed, svc.Begin("Phone", nc, mac).Error);
    }

    [Fact]
    public async Task Happy_path_issues_a_device_and_a_matching_code()
    {
        var (svc, _, store) = Create();
        svc.OpenWindow();
        var k = svc.CurrentSecret!.Secret;
        Assert.Equal(16, k.Length);
        var (nc, mac) = Hello(k, "Dana's iPhone");

        var begin = svc.Begin("Dana's iPhone", nc, mac);
        Assert.True(begin.Ok);
        Assert.Equal(PairingState.Pending, svc.State);
        Assert.Equal(HandshakeCrypto.PairCode(k, nc, begin.Ns!), begin.Request!.Code);
        Assert.Matches("^[0-9]{4}$", begin.Request.Code!);
        Assert.Equal("Dana's iPhone", svc.PendingRequest!.DeviceName);

        Assert.True(begin.Request.Allow());
        var outcome = await begin.Outcome!;
        Assert.Equal(PairingResultKind.Approved, outcome.Kind);
        Assert.True(HandshakeCrypto.TryUnwrap(k, nc, begin.Ns!, outcome.Wrap!, out var secret));
        Assert.Equal(outcome.Device!.Secret, secret);
        Assert.Equal(32, secret.Length);
        Assert.Equal(secret, store.Get(outcome.Device.Id)!.Secret);
        Assert.Equal(PairingState.Open, svc.State);
    }

    [Fact]
    public async Task The_secret_is_spent_by_use_whatever_the_user_decides()
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var k = svc.CurrentSecret!.Secret;
        var (nc, mac) = Hello(k);
        var begin = svc.Begin("Phone", nc, mac);
        begin.Request!.Deny();
        Assert.Equal(PairingResultKind.Denied, (await begin.Outcome!).Kind);

        Assert.NotEqual(k, svc.CurrentSecret!.Secret);
        var (nc2, mac2) = Hello(k); // replay of the spent QR
        Assert.Equal(PairingError.BadMac, svc.Begin("Phone", nc2, mac2).Error);
    }

    [Fact]
    public void Secret_expires_after_five_minutes()
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var k = svc.CurrentSecret!.Secret;
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(5), svc.CurrentSecret.ExpiresAt);
        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var (nc, mac) = Hello(k);
        Assert.Equal(PairingError.None, svc.Begin("Phone", nc, mac).Error);
    }

    [Fact]
    public void Expired_secret_fails_until_regenerated()
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var k = svc.CurrentSecret!.Secret;
        _time.Advance(TimeSpan.FromMinutes(5));
        var (nc, mac) = Hello(k);
        Assert.Equal(PairingError.Expired, svc.Begin("Phone", nc, mac).Error);
        svc.Regenerate();
        Assert.NotEqual(k, svc.CurrentSecret!.Secret);
    }

    [Fact]
    public void Five_failed_attempts_regenerate_the_secret()
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var k = svc.CurrentSecret!.Secret;
        int changes = 0;
        svc.Changed += (_, _) => changes++;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(PairingError.BadMac, svc.Begin("Phone", HandshakeCrypto.Random(16), new byte[32]).Error);
            Assert.Equal(k, svc.CurrentSecret!.Secret);
        }
        Assert.Equal(PairingError.BadMac, svc.Begin("Phone", HandshakeCrypto.Random(16), new byte[32]).Error);
        Assert.NotEqual(k, svc.CurrentSecret!.Secret);
        Assert.Equal(0, svc.FailedAttempts);
        Assert.True(changes >= 1);

        // The correct old secret no longer works.
        var (nc, mac) = Hello(k);
        Assert.Equal(PairingError.BadMac, svc.Begin("Phone", nc, mac).Error);
    }

    [Fact]
    public void Only_one_request_is_pending_at_a_time()
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var (nc, mac) = Hello(svc.CurrentSecret!.Secret);
        Assert.True(svc.Begin("Phone", nc, mac).Ok);
        var (nc2, mac2) = Hello(svc.CurrentSecret!.Secret); // the fresh QR
        Assert.Equal(PairingError.Busy, svc.Begin("Phone", nc2, mac2).Error);
    }

    [Fact]
    public void Allow_is_refused_for_one_second()
    {
        var (svc, _, _) = Create(new PairingOptions());
        svc.OpenWindow();
        var (nc, mac) = Hello(svc.CurrentSecret!.Secret);
        var request = svc.Begin("Phone", nc, mac).Request!;
        Assert.False(request.CanAllow);
        Assert.False(request.Allow());
        Assert.False(request.IsDecided);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(request.Allow());
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(request.CanAllow);
        Assert.True(request.Allow());
        Assert.True(request.IsDecided);
        Assert.False(request.CanAllow);
    }

    [Fact]
    public async Task Unanswered_request_is_denied_after_the_timeout()
    {
        var (svc, _, store) = Create(new PairingOptions { AllowDelay = TimeSpan.Zero, ApprovalTimeout = TimeSpan.FromSeconds(60) });
        svc.OpenWindow();
        var (nc, mac) = Hello(svc.CurrentSecret!.Secret);
        var begin = svc.Begin("Phone", nc, mac);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(PairingResultKind.Denied, (await begin.Outcome!).Kind);
        Assert.Empty(store.GetAll());
        Assert.Null(svc.PendingRequest);
    }

    [Fact]
    public async Task Closing_the_window_denies_the_pending_request()
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var (nc, mac) = Hello(svc.CurrentSecret!.Secret);
        var begin = svc.Begin("Phone", nc, mac);
        svc.CloseWindow();
        Assert.Equal(PairingResultKind.Denied, (await begin.Outcome!).Kind);
        Assert.Equal(PairingState.Closed, svc.State);
        Assert.Null(svc.CurrentSecret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData("‮evil")]
    public void Unsafe_device_names_are_refused(string name)
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var (nc, mac) = Hello(svc.CurrentSecret!.Secret, name);
        Assert.Equal(PairingError.BadName, svc.Begin(name, nc, mac).Error);
    }

    [Fact]
    public void Name_is_bound_into_the_mac()
    {
        var (svc, _, _) = Create();
        svc.OpenWindow();
        var (nc, mac) = Hello(svc.CurrentSecret!.Secret, "Dana");
        Assert.Equal(PairingError.BadMac, svc.Begin("Mallory", nc, mac).Error);
    }
}

public class DeviceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "easymouse-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>Reversible protector for tests (AES-GCM with a throwaway key): stands in for DPAPI.</summary>
    private sealed class TestProtector : ISecretProtector
    {
        private readonly byte[] _key = HandshakeCrypto.Random(32);
        public int ProtectCalls;

        public byte[] Protect(byte[] plain)
        {
            ProtectCalls++;
            var nonce = HandshakeCrypto.Random(12);
            var ct = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new System.Security.Cryptography.AesGcm(_key, 16);
            aes.Encrypt(nonce, plain, ct, tag);
            return HandshakeCrypto.Concat(nonce, tag, ct);
        }

        public byte[] Unprotect(byte[] data)
        {
            var plain = new byte[data.Length - 28];
            using var aes = new System.Security.Cryptography.AesGcm(_key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return plain;
        }
    }

    private Device NewDevice(string id = "dev1") => new(id, "Phone", HandshakeCrypto.Random(32), _time.GetUtcNow(), _time.GetUtcNow());

    [Fact]
    public void In_memory_store_basics()
    {
        var store = new InMemoryDeviceStore();
        var d = NewDevice();
        store.Add(d);
        Assert.Equal(d, store.Get("dev1"));
        Assert.Null(store.Get("nope"));
        Assert.True(store.Touch("dev1", _time.GetUtcNow().AddDays(1)));
        Assert.Equal(_time.GetUtcNow().AddDays(1), store.Get("dev1")!.LastSeenUtc);
        Assert.False(store.Touch("nope", _time.GetUtcNow()));
        Assert.True(store.Remove("dev1"));
        Assert.False(store.Remove("dev1"));
        store.Add(NewDevice("a")); store.Add(NewDevice("b"));
        Assert.Equal(2, store.Clear());
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void File_store_persists_across_instances_and_never_writes_the_raw_secret()
    {
        var path = Path.Combine(_dir, "devices.json");
        var protector = new TestProtector();
        var d = NewDevice();
        var store = new FileDeviceStore(path, protector);
        store.Add(d);
        store.Touch(d.Id, _time.GetUtcNow().AddHours(5));

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(Convert.ToBase64String(d.Secret), text);
        Assert.DoesNotContain(Convert.ToHexString(d.Secret), text, StringComparison.OrdinalIgnoreCase);
        Assert.True(protector.ProtectCalls >= 1);

        var reloaded = new FileDeviceStore(path, protector);
        var back = reloaded.Get(d.Id)!;
        Assert.Equal(d.Secret, back.Secret);
        Assert.Equal(d.Name, back.Name);
        Assert.Equal(_time.GetUtcNow().AddHours(5), back.LastSeenUtc);

        Assert.True(reloaded.Remove(d.Id));
        Assert.Empty(new FileDeviceStore(path, protector).GetAll());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void File_store_fails_loudly_on_corruption_or_a_wrong_protector()
    {
        var path = Path.Combine(_dir, "devices.json");
        var store = new FileDeviceStore(path, new TestProtector());
        store.Add(NewDevice());

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new FileDeviceStore(path, new TestProtector()));

        File.WriteAllText(path, "{ not json");
        Assert.Throws<InvalidDataException>(() => new FileDeviceStore(path, new TestProtector()));
        File.WriteAllText(path, "{\"version\":99,\"devices\":[]}");
        Assert.Throws<InvalidDataException>(() => new FileDeviceStore(path, new TestProtector()));
    }

    private DeviceManager Manager(IDeviceStore store, TimeSpan? expiry = null)
        => new(store, new ConnectionHub(_time), _time, expiry);

    [Fact]
    public void Devices_expire_after_90_days_unused()
    {
        var store = new InMemoryDeviceStore();
        var manager = Manager(store);
        Assert.Equal(TimeSpan.FromDays(90), manager.Expiry);
        store.Add(NewDevice());

        _time.Advance(TimeSpan.FromDays(90));
        Assert.NotNull(manager.FindActive("dev1")); // exactly 90 days is still fine
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(manager.FindActive("dev1"));
        Assert.Null(store.Get("dev1")); // and it is purged
    }

    [Fact]
    public void Using_a_device_resets_its_expiry()
    {
        var store = new InMemoryDeviceStore();
        var manager = Manager(store);
        store.Add(NewDevice());
        _time.Advance(TimeSpan.FromDays(80));
        manager.MarkSeen("dev1");
        _time.Advance(TimeSpan.FromDays(80));
        Assert.NotNull(manager.FindActive("dev1"));
    }

    [Fact]
    public async Task Expiry_is_configurable_and_purge_removes_expired_devices()
    {
        var store = new InMemoryDeviceStore();
        var manager = Manager(store, TimeSpan.FromDays(7));
        store.Add(NewDevice("old"));
        _time.Advance(TimeSpan.FromDays(6));
        store.Add(NewDevice("fresh"));
        _time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await manager.PurgeExpiredAsync());
        Assert.Null(store.Get("old"));
        Assert.NotNull(store.Get("fresh"));
    }
}

public class RequestPolicyTests
{
    private static RequestPolicy Policy(int port = 5000)
        => new(["192.168.1.20", "Office-PC.local"], System.Net.IPAddress.Parse("192.168.1.20"), 24) { Port = port };

    [Theory]
    [InlineData("192.168.1.20:5000", true)]
    [InlineData("office-pc.local:5000", true)]
    [InlineData("OFFICE-PC.LOCAL:5000", true)]
    [InlineData("192.168.1.20", false)]           // missing port, listener is not on 80
    [InlineData("192.168.1.20:5001", false)]      // other port
    [InlineData("evil.example:5000", false)]
    [InlineData("evil.example", false)]
    [InlineData("192.168.1.20.evil.example:5000", false)]
    [InlineData("127.0.0.1:5000", false)]
    [InlineData("[::1]:5000", false)]
    [InlineData("192.168.1.20:5000, evil.example", false)]
    [InlineData("192.168.1.20:", false)]
    [InlineData("192.168.1.20:5000:1", false)]
    [InlineData("192.168.1.20:+5000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Host_header(string? host, bool allowed) => Assert.Equal(allowed, Policy().IsHostAllowed(host));

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://192.168.1.20:5000", true)]
    [InlineData("http://office-pc.local:5000", true)]
    [InlineData("http://evil.example", false)]
    [InlineData("http://evil.example:5000", false)]
    [InlineData("https://192.168.1.20:5000", false)]
    [InlineData("null", false)]
    [InlineData("http://192.168.1.20:5000/", false)]
    [InlineData("http://192.168.1.20:5000@evil.example", false)]
    [InlineData("http://evil.example:5000@192.168.1.20:5000", false)]
    [InlineData("file://", false)]
    [InlineData("", false)]
    public void Origin_header(string? origin, bool allowed) => Assert.Equal(allowed, Policy().IsOriginAllowed(origin));

    [Fact]
    public void Default_port_80_may_be_omitted()
    {
        var policy = Policy(80);
        Assert.True(policy.IsHostAllowed("192.168.1.20"));
        Assert.True(policy.IsOriginAllowed("http://192.168.1.20"));
    }

    [Theory]
    [InlineData("192.168.1.77", true)]
    [InlineData("192.168.2.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("::1", false)]
    public void Peers_must_be_in_the_interface_subnet(string peer, bool allowed)
        => Assert.Equal(allowed, Policy().IsPeerAllowed(System.Net.IPAddress.Parse(peer)));

    [Fact]
    public void Rate_limiter_allows_a_burst_then_refills()
    {
        var time = new ManualTimeProvider();
        var limiter = new RateLimiter(10, 5, time);
        for (int i = 0; i < 5; i++) Assert.True(limiter.TryTake());
        Assert.False(limiter.TryTake());
        time.Advance(TimeSpan.FromMilliseconds(250)); // 2.5 tokens
        Assert.True(limiter.TryTake());
        Assert.True(limiter.TryTake());
        Assert.False(limiter.TryTake());
        time.Advance(TimeSpan.FromSeconds(10)); // refill is capped at the burst
        for (int i = 0; i < 5; i++) Assert.True(limiter.TryTake());
        Assert.False(limiter.TryTake());
    }
}
