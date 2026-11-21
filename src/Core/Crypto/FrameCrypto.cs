using System.Buffers.Binary;
using System.Security.Cryptography;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Crypto;

public enum OpenResult { Ok, Malformed, Replay, AuthFailed }

/// <summary>
/// Encrypted frame: <c>counter (u64 BE) | ciphertext | tag(16)</c>. AEAD nonce is
/// <c>dir | 000 | counter</c>; no associated data. Stateless helpers; see <see cref="SecureChannel"/> for counters.
/// </summary>
public static class FrameCrypto
{
    public const int HeaderLength = 8;
    public const int TagLength = 16;
    public const int Overhead = HeaderLength + TagLength;

    public static byte[] Seal(byte[] key, FrameDirection direction, ulong counter, ReadOnlySpan<byte> plaintext)
    {
        var frame = new byte[HeaderLength + plaintext.Length + TagLength];
        BinaryPrimitives.WriteUInt64BigEndian(frame, counter);
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(direction, counter, nonce);
        using var aead = new ChaCha20Poly1305(key);
        aead.Encrypt(nonce, plaintext, frame.AsSpan(HeaderLength, plaintext.Length), frame.AsSpan(HeaderLength + plaintext.Length, TagLength));
        return frame;
    }

    /// <summary>Authenticates and decrypts. Does not check counter ordering.</summary>
    public static OpenResult TryOpen(byte[] key, FrameDirection direction, ReadOnlySpan<byte> frame, out ulong counter, out byte[] plaintext)
    {
        counter = 0;
        plaintext = [];
        if (frame.Length < Overhead + 1) return OpenResult.Malformed;
        counter = BinaryPrimitives.ReadUInt64BigEndian(frame);
        int ctLength = frame.Length - Overhead;
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(direction, counter, nonce);
        var plain = new byte[ctLength];
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            aead.Decrypt(nonce, frame.Slice(HeaderLength, ctLength), frame.Slice(HeaderLength + ctLength, TagLength), plain);
        }
        catch (CryptographicException)
        {
            return OpenResult.AuthFailed;
        }
        plaintext = plain;
        return OpenResult.Ok;
    }

    public static void MakeNonce(FrameDirection direction, ulong counter, Span<byte> nonce)
    {
        nonce.Clear();
        nonce[0] = (byte)direction;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }
}
