using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EasyMouse.Core.Crypto;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Tests;

/// <summary>
/// tests/vectors.json holds fixed vectors for the web client's tests. The "rfc" section comes from the RFCs
/// (verified below against the BCL, independent of our wrappers); the rest is generated from this
/// implementation and was cross-checked with a separate implementation (Node's crypto module, see the PR).
/// Regenerate with EM_UPDATE_VECTORS=1.
/// </summary>
public class VectorsTests
{
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);
    private static byte[] Unhex(string s) => Convert.FromHexString(s);
    private static byte[] Seq(int start, int length) => [.. Enumerable.Range(start, length).Select(i => (byte)i)];

    private static JsonObject Rfc() => new()
    {
        ["hkdf_rfc5869_a1"] = new JsonObject
        {
            ["hash"] = "SHA-256",
            ["ikm"] = Hex([.. Enumerable.Repeat((byte)0x0b, 22)]),
            ["salt"] = Hex(Seq(0x00, 13)),
            ["info"] = Hex(Seq(0xf0, 10)),
            ["length"] = 42,
            ["okm"] = "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
        },
        ["hmac_rfc4231_case1"] = new JsonObject
        {
            ["key"] = Hex([.. Enumerable.Repeat((byte)0x0b, 20)]),
            ["data"] = Hex(Encoding.ASCII.GetBytes("Hi There")),
            ["mac"] = "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
        },
        ["chacha20poly1305_rfc8439_2_8_2"] = new JsonObject
        {
            ["key"] = Hex(Seq(0x80, 32)),
            ["nonce"] = "070000004041424344454647",
            ["aad"] = "50515253c0c1c2c3c4c5c6c7",
            ["plaintext"] = Hex(Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.")),
            ["ciphertext"] = "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b6116",
            ["tag"] = "1ae10b594f09e26a7e902ecbd0600691",
        },
    };

    private static JsonObject Frame(string dir, ulong counter, byte[] key, byte[] plaintext, string description)
    {
        var d = dir == "c2s" ? FrameDirection.ClientToServer : FrameDirection.ServerToClient;
        return new JsonObject
        {
            ["description"] = description,
            ["dir"] = dir,
            ["counter"] = counter,
            ["plaintext"] = Hex(plaintext),
            ["frame"] = Hex(FrameCrypto.Seal(key, d, counter, plaintext)),
        };
    }

    public static JsonObject Compute()
    {
        var k = Seq(0x10, 16);
        var nc = Seq(0x20, 16);
        var ns = Seq(0x30, 16);
        var secret = Seq(0x40, 32);
        var id = Seq(0x50, 8);
        const string name = "Dana's iPhone";
        const string nameHe = "הטלפון של דנה";

        var pairMac = HandshakeCrypto.PairMac(k, nc, name);
        var pairMacHe = HandshakeCrypto.PairMac(k, nc, nameHe);
        var wrapKey = HandshakeCrypto.WrapKey(k, nc, ns);
        var wrap = HandshakeCrypto.Wrap(k, nc, ns, secret);
        var (c2s, s2c) = HandshakeCrypto.DeriveSessionKeys(secret, nc, ns);

        var frames = new JsonArray
        {
            Frame("c2s", 1, c2s, FrameCodec.Encode(new MoveMessage(12, -7)), "move dx=12 dy=-7"),
            Frame("c2s", 2, c2s, FrameCodec.Encode(new ButtonMessage(MouseButton.Left, true)), "button left down"),
            Frame("c2s", 3, c2s, FrameCodec.Encode(new WheelMessage(0, -120)), "wheel dy=-120"),
            Frame("c2s", 4, c2s, FrameCodec.Encode(new TextMessage("שלום 👋")), "text (Hebrew and emoji, UTF-8)"),
            Frame("c2s", 5, c2s, FrameCodec.Encode(new KeyMessage(0x41, KeyAction.Press)), "key A press"),
            Frame("c2s", 6, c2s, FrameCodec.Encode(new MediaMessage(MediaCommand.Mute)), "media mute"),
            Frame("c2s", 7, c2s, FrameCodec.Encode(new PingMessage()), "ping"),
            Frame("s2c", 1, s2c, FrameCodec.Encode(new StateMessage("{\"t\":\"state\",\"media\":null,\"volume\":{\"level\":40,\"muted\":false},\"host\":{\"locked\":false,\"elevated\":false}}")), "state"),
            Frame("s2c", 2, s2c, FrameCodec.Encode(new PingMessage()), "ping reply"),
            Frame("s2c", 3, s2c, FrameCodec.Encode(new ArtMessage("a1b2c3", ArtworkType.Jpeg, [0xFF, 0xD8, 0xFF, 0xD9])), "art id a1b2c3 (jpeg, 4 bytes)"),
        };

        // Receiver side checks (receiver = server, key c2s). Counter must be strictly increasing; a frame that
        // fails authentication does not move the counter.
        var move = FrameCodec.Encode(new MoveMessage(1, 1));
        byte[] Seal(ulong c) => FrameCrypto.Seal(c2s, FrameDirection.ClientToServer, c, move);
        var tampered = Seal(4);
        tampered[^1] ^= 0x01;
        var sequence = new JsonArray
        {
            new JsonObject { ["description"] = "first frame", ["frame"] = Hex(Seal(1)), ["expect"] = "accept" },
            new JsonObject { ["description"] = "replay of counter 1", ["frame"] = Hex(Seal(1)), ["expect"] = "drop" },
            new JsonObject { ["description"] = "gap is fine", ["frame"] = Hex(Seal(3)), ["expect"] = "accept" },
            new JsonObject { ["description"] = "reordered (2 after 3)", ["frame"] = Hex(Seal(2)), ["expect"] = "drop" },
            new JsonObject { ["description"] = "tag bit flipped", ["frame"] = Hex(tampered), ["expect"] = "drop" },
            new JsonObject { ["description"] = "valid counter 4 after the tampered one", ["frame"] = Hex(Seal(4)), ["expect"] = "accept" },
            new JsonObject { ["description"] = "counter 0 is never valid", ["frame"] = Hex(Seal(0)), ["expect"] = "drop" },
        };

        return new JsonObject
        {
            ["version"] = 1,
            ["notes"] = "All byte values are lowercase hex unless the key ends in B64 (base64url, no padding). Generated by tests/Core.Tests/VectorsTests.cs.",
            ["rfc"] = Rfc(),
            ["pairing"] = new JsonObject
            {
                ["k"] = Hex(k), ["kB64"] = B64.Encode(k),
                ["nc"] = Hex(nc), ["ncB64"] = B64.Encode(nc),
                ["ns"] = Hex(ns), ["nsB64"] = B64.Encode(ns),
                ["name"] = name,
                ["mac"] = Hex(pairMac), ["macB64"] = B64.Encode(pairMac),
                ["nameHe"] = nameHe,
                ["macHe"] = Hex(pairMacHe),
                ["code"] = HandshakeCrypto.PairCode(k, nc, ns),
                ["wrapKey"] = Hex(wrapKey),
                ["deviceSecret"] = Hex(secret),
                ["wrap"] = Hex(wrap), ["wrapB64"] = B64.Encode(wrap),
            },
            ["connect"] = new JsonObject
            {
                ["deviceSecret"] = Hex(secret),
                ["id"] = Hex(id), ["idB64"] = B64.Encode(id),
                ["nc"] = Hex(nc), ["ns"] = Hex(ns),
                ["clientMac"] = Hex(HandshakeCrypto.ConnectMac(secret, false, nc, ns)),
                ["serverMac"] = Hex(HandshakeCrypto.ConnectMac(secret, true, nc, ns)),
                ["keyC2S"] = Hex(c2s),
                ["keyS2C"] = Hex(s2c),
            },
            ["frames"] = frames,
            ["receiveSequenceC2S"] = sequence,
        };
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void Generate_when_requested()
    {
        if (Environment.GetEnvironmentVariable("EM_UPDATE_VECTORS") != "1") return;
        File.WriteAllText(VectorsPath(), Compute().ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
    }

    private static string VectorsPath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "vectors.json"));

    [Fact]
    public void Vectors_file_is_up_to_date_with_the_implementation()
    {
        var onDisk = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")))!;
        Assert.Equal(Compute().ToJsonString(), onDisk.ToJsonString());
    }

    // The RFC vectors are checked against the BCL directly, so they validate the file's constants independently.

    [Fact]
    public void Rfc_vectors_hold_for_the_bcl()
    {
        var rfc = Rfc();

        var h = rfc["hkdf_rfc5869_a1"]!;
        Assert.Equal((string)h["okm"]!, Hex(HKDF.DeriveKey(HashAlgorithmName.SHA256, Unhex((string)h["ikm"]!), 42, Unhex((string)h["salt"]!), Unhex((string)h["info"]!))));

        var m = rfc["hmac_rfc4231_case1"]!;
        Assert.Equal((string)m["mac"]!, Hex(HMACSHA256.HashData(Unhex((string)m["key"]!), Unhex((string)m["data"]!))));

        var a = rfc["chacha20poly1305_rfc8439_2_8_2"]!;
        var plain = Unhex((string)a["plaintext"]!);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var aead = new ChaCha20Poly1305(Unhex((string)a["key"]!)))
            aead.Encrypt(Unhex((string)a["nonce"]!), plain, ct, tag, Unhex((string)a["aad"]!));
        Assert.Equal((string)a["ciphertext"]!, Hex(ct));
        Assert.Equal((string)a["tag"]!, Hex(tag));
    }

    [Fact]
    public void Our_helpers_agree_with_the_rfc_primitives()
    {
        // HandshakeCrypto.Hkdf must be plain RFC 5869 HKDF-SHA256 with ASCII info.
        var ikm = Seq(1, 22);
        var salt = Seq(100, 32);
        Assert.Equal(
            Hex(HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes("c2s"))),
            Hex(HandshakeCrypto.Hkdf(ikm, salt, "c2s")));
    }

    [Fact]
    public void Receive_sequence_behaves_as_documented()
    {
        var root = Compute();
        var c2s = Unhex((string)root["connect"]!["keyC2S"]!);
        var s2cKey = Unhex((string)root["connect"]!["keyS2C"]!);
        var server = new SecureChannel(s2cKey, c2s, FrameDirection.ServerToClient);
        foreach (var step in root["receiveSequenceC2S"]!.AsArray())
        {
            var result = server.TryOpen(Unhex((string)step!["frame"]!), out _);
            Assert.Equal((string)step["expect"]! == "accept", result == OpenResult.Ok);
        }
    }
}
