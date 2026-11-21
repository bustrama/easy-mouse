using System.Buffers.Text;

namespace EasyMouse.Core.Crypto;

/// <summary>base64url without padding (docs/protocol.md "b64"), strict decoding.</summary>
public static class B64
{
    public static string Encode(ReadOnlySpan<byte> data) => Base64Url.EncodeToString(data);

    /// <summary>Decodes to exactly <paramref name="expectedLength"/> bytes; rejects padding, standard-alphabet characters, non-canonical trailing bits and other lengths.</summary>
    public static bool TryDecode(string? text, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        if (text is null) return false;
        int expectedChars = (expectedLength * 4 + 2) / 3;
        if (text.Length != expectedChars) return false;
        try
        {
            var decoded = Base64Url.DecodeFromChars(text);
            if (decoded.Length != expectedLength) return false;
            if (!string.Equals(Base64Url.EncodeToString(decoded), text, StringComparison.Ordinal)) return false;
            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
