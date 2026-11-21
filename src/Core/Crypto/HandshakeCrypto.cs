using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EasyMouse.Core.Crypto;

/// <summary>
/// Pure functions for the pairing and connect handshakes (docs/protocol.md). Only BCL primitives:
/// HKDF-SHA256 (<c>ikm, salt = nc|ns, info = ASCII label</c>), HMAC-SHA256, ChaCha20-Poly1305.
/// </summary>
public static class HandshakeCrypto
{
    public const int PairSecretLength = 16;
    public const int NonceLength = 16;
    public const int DeviceIdLength = 8;
    public const int DeviceSecretLength = 32;
    public const int MacLength = 32;
    public const int KeyLength = 32;

    public static byte[] Random(int length) => RandomNumberGenerator.GetBytes(length);

    public static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }

    public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length = KeyLength)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, Encoding.ASCII.GetBytes(info));

    public static byte[] Hmac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    public static bool MacEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary><c>HMAC(k, "hello" | nc | name)</c>; the name is its UTF-8 bytes exactly as sent.</summary>
    public static byte[] PairMac(byte[] k, byte[] nc, string name)
        => Hmac(k, Concat(Encoding.ASCII.GetBytes("hello"), nc, Encoding.UTF8.GetBytes(name)));

    /// <summary>4-digit confirmation code: <c>u32be(HKDF(k, nc|ns, "code", 4)) mod 10000</c>, zero padded.</summary>
    public static string PairCode(byte[] k, byte[] nc, byte[] ns)
    {
        var bytes = Hkdf(k, Concat(nc, ns), "code", 4);
        return (BinaryPrimitives.ReadUInt32BigEndian(bytes) % 10000).ToString("D4", CultureInfo.InvariantCulture);
    }

    public static byte[] WrapKey(byte[] k, byte[] nc, byte[] ns) => Hkdf(k, Concat(nc, ns), "wrap");

    /// <summary>AEAD(key = WrapKey, nonce = 12 zero bytes, no AAD, plaintext = S) as <c>ciphertext | tag</c> (48 bytes).</summary>
    public static byte[] Wrap(byte[] k, byte[] nc, byte[] ns, byte[] deviceSecret)
    {
        var result = new byte[deviceSecret.Length + FrameCrypto.TagLength];
        using var aead = new ChaCha20Poly1305(WrapKey(k, nc, ns));
        aead.Encrypt(new byte[12], deviceSecret, result.AsSpan(0, deviceSecret.Length), result.AsSpan(deviceSecret.Length));
        return result;
    }

    public static bool TryUnwrap(byte[] k, byte[] nc, byte[] ns, byte[] wrapped, out byte[] deviceSecret)
    {
        deviceSecret = [];
        if (wrapped.Length != DeviceSecretLength + FrameCrypto.TagLength) return false;
        var plain = new byte[DeviceSecretLength];
        try
        {
            using var aead = new ChaCha20Poly1305(WrapKey(k, nc, ns));
            aead.Decrypt(new byte[12], wrapped.AsSpan(0, DeviceSecretLength), wrapped.AsSpan(DeviceSecretLength), plain);
        }
        catch (CryptographicException) { return false; }
        deviceSecret = plain;
        return true;
    }

    /// <summary><c>HMAC(S, "c" | nc | ns)</c> for the client, <c>HMAC(S, "s" | nc | ns)</c> for the server.</summary>
    public static byte[] ConnectMac(byte[] deviceSecret, bool server, byte[] nc, byte[] ns)
        => Hmac(deviceSecret, Concat(Encoding.ASCII.GetBytes(server ? "s" : "c"), nc, ns));

    public static (byte[] ClientToServer, byte[] ServerToClient) DeriveSessionKeys(byte[] deviceSecret, byte[] nc, byte[] ns)
    {
        var salt = Concat(nc, ns);
        return (Hkdf(deviceSecret, salt, "c2s"), Hkdf(deviceSecret, salt, "s2c"));
    }
}
