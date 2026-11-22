using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace EasyMouse.App.Updates;

/// <summary>
/// Fail-closed verification of a downloaded update: an Ed25519 signature over <c>SHA256SUMS</c> made with the release key,
/// then the installer's SHA-256 against the signed list. Nothing is run before both checks pass.
/// </summary>
public sealed class UpdateVerifier
{
    private readonly byte[] _publicKey;

    public UpdateVerifier(byte[]? publicKey = null)
    {
        _publicKey = publicKey ?? UpdateTrust.PublicKey;
        if (_publicKey.Length != 32) throw new ArgumentException("An Ed25519 public key is 32 bytes.", nameof(publicKey));
    }

    /// <summary>Raw 64-byte Ed25519 signature check. Any malformed input is simply "not valid".</summary>
    public bool IsSignatureValid(byte[] message, byte[] signature)
    {
        if (signature.Length != UpdateTrust.SignatureBytes) return false;
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(_publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Org.BouncyCastle.Crypto.CryptoException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies <c>SHA256SUMS</c> (signature first, so the text is not parsed unless it is genuine) and returns the signed hash
    /// for <paramref name="fileName"/>.
    /// </summary>
    /// <exception cref="UpdateException"><see cref="UpdateErrorKind.SignatureInvalid"/>, or <see cref="UpdateErrorKind.Malformed"/> when the list has no valid entry for the file.</exception>
    public byte[] SignedHashFor(byte[] checksums, byte[] signature, string fileName)
    {
        if (!IsSignatureValid(checksums, signature))
            throw new UpdateException(UpdateErrorKind.SignatureInvalid, "The signature of SHA256SUMS is not valid.");
        return ParseChecksums(checksums, fileName);
    }

    /// <summary>Parses <c>sha256sum</c> output (<c>hex  name</c> or <c>hex *name</c>); duplicates or junk make the whole list invalid.</summary>
    public static byte[] ParseChecksums(byte[] checksums, string fileName)
    {
        if (checksums.Length > UpdateTrust.MaxChecksumsBytes) throw new UpdateException(UpdateErrorKind.TooLarge, "SHA256SUMS is too large.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(checksums); }
        catch (ArgumentException ex) { throw new UpdateException(UpdateErrorKind.Malformed, "SHA256SUMS is not text.", ex); }

        byte[]? found = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length < 67 || line[64] != ' ' || line[65] is not (' ' or '*'))
                throw new UpdateException(UpdateErrorKind.Malformed, "SHA256SUMS has a malformed line.");
            var hex = line[..64];
            var name = line[66..];
            if (!hex.All(char.IsAsciiHexDigit) || name.Length == 0 || name.Contains('/') || name.Contains('\\'))
                throw new UpdateException(UpdateErrorKind.Malformed, "SHA256SUMS has a malformed line.");
            if (!seen.Add(name)) throw new UpdateException(UpdateErrorKind.Malformed, "SHA256SUMS lists a file twice.");
            if (name == fileName) found = Convert.FromHexString(hex);
        }
        return found ?? throw new UpdateException(UpdateErrorKind.Malformed, $"SHA256SUMS has no entry for {fileName}.");
    }

    /// <summary>Hashes <paramref name="installer"/> from the start and compares in constant time.</summary>
    public static void VerifyHash(Stream installer, byte[] expectedSha256)
    {
        installer.Position = 0;
        var actual = SHA256.HashData(installer);
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedSha256))
            throw new UpdateException(UpdateErrorKind.HashMismatch, "The installer does not match the signed checksum.");
    }
}
