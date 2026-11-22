using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyMouse.App.Logic;
using EasyMouse.App.Updates;
using EasyMouse.Core.Tests.Support;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace EasyMouse.App.Tests;

/// <summary>Test keys and a fake GitHub: nothing here touches the network.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public static (byte[] Private, byte[] Public) NewKey()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        return (((Ed25519PrivateKeyParameters)pair.Private).GetEncoded(), ((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
    }

    public static byte[] Sign(byte[] privateKey, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public const string Base = "https://github.com/bustrama/easy-mouse/releases/download/";

    public static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, string body = "Notes", long installerSize = 1000, string? installerUrl = null)
    {
        var v = tag[1..];
        var name = $"EasyMouse-Setup-{v}.exe";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            body,
            published_at = "2026-01-01T00:00:00Z",
            assets = new object[]
            {
                new { name, size = installerSize, browser_download_url = installerUrl ?? $"{Base}{tag}/{name}" },
                new { name = "SHA256SUMS", size = 120, browser_download_url = $"{Base}{tag}/SHA256SUMS" },
                new { name = "SHA256SUMS.sig", size = 64, browser_download_url = $"{Base}{tag}/SHA256SUMS.sig" },
            },
        });
    }

    public List<HttpRequestMessage> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    public static HttpResponseMessage Ok(string body) => Ok(Encoding.UTF8.GetBytes(body));
    public static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }
}

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.0+build5", 0)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.9", 1)]
    public void Compares_like_semver(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(SemVer.Parse(a).CompareTo(SemVer.Parse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    [InlineData("a.b.c")]
    [InlineData("99999999999.0.0")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3-a..b")]
    public void Rejects_garbage(string text) => Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void Rejects_overlong_input() => Assert.False(SemVer.TryParse("1.2.3-" + new string('a', 100), out _));

    [Fact]
    public void Prerelease_flag_and_round_trip()
    {
        var v = SemVer.Parse("1.2.3-rc.1+abc");
        Assert.True(v.IsPrerelease);
        Assert.Equal("1.2.3-rc.1", v.ToString());
        Assert.False(SemVer.Parse("1.2.3").IsPrerelease);
    }
}

public class ReleaseParserTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Parses_a_good_release()
    {
        var r = ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", body: "Fixes")), false);
        Assert.NotNull(r);
        Assert.Equal("1.2.0", r.Version.ToString());
        Assert.Equal("Fixes", r.Notes);
        Assert.Equal("EasyMouse-Setup-1.2.0.exe", r.Installer.Name);
        Assert.Equal(new Uri(FakeGitHub.Base + "v1.2.0/SHA256SUMS.sig"), r.Signature.Url);
    }

    [Fact]
    public void Ignores_drafts_and_prereleases_unless_asked()
    {
        Assert.Null(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", draft: true)), true));
        Assert.Null(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", prerelease: true)), false));
        Assert.Null(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0-rc.1")), false)); // pre-release by version even if not flagged
        Assert.NotNull(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", prerelease: true)), true));
    }

    [Fact]
    public void List_picks_the_highest_usable_release()
    {
        var json = "[" + string.Join(",", FakeGitHub.ReleaseJson("v1.1.0"), FakeGitHub.ReleaseJson("v1.3.0", draft: true),
            FakeGitHub.ReleaseJson("v1.2.0"), FakeGitHub.ReleaseJson("v1.4.0-beta.1", prerelease: true)) + "]";
        Assert.Equal("1.2.0", ReleaseParser.SelectBest(B(json), false)!.Version.ToString());
        Assert.Equal("1.4.0-beta.1", ReleaseParser.SelectBest(B(json), true)!.Version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"tag_name\":")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Malformed_or_wrong_shape_is_an_error(string json)
    {
        var ex = Assert.Throws<UpdateException>(() => ReleaseParser.SelectBest(B(json), false));
        Assert.Equal(UpdateErrorKind.Malformed, ex.Kind);
    }

    [Fact]
    public void Deeply_nested_json_is_rejected()
    {
        var json = new string('[', 5000) + new string(']', 5000);
        Assert.Equal(UpdateErrorKind.Malformed, Assert.Throws<UpdateException>(() => ReleaseParser.SelectBest(B(json), false)).Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[1,\"x\",null,[]]")]
    [InlineData("{\"tag_name\":5,\"draft\":false,\"prerelease\":false,\"assets\":[]}")]
    [InlineData("{\"tag_name\":\"v1.2.0\",\"draft\":\"no\",\"prerelease\":false,\"assets\":[]}")]
    [InlineData("{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false,\"assets\":\"x\"}")]
    [InlineData("{\"tag_name\":\"v1.2.0\",\"draft\":false,\"prerelease\":false,\"assets\":[]}")]
    public void Unusable_releases_are_skipped_without_crashing(string json) => Assert.Null(ReleaseParser.SelectBest(B(json), true));

    [Theory]
    [InlineData("1.2.0")]            // no leading v
    [InlineData("v1.2")]
    [InlineData("v1.2.0+meta")]
    [InlineData("vv1.2.0")]
    [InlineData("v1.2.0/../../x")]
    public void Odd_tags_are_skipped(string tag)
    {
        var json = FakeGitHub.ReleaseJson("v1.2.0").Replace("\"tag_name\":\"v1.2.0\"", $"\"tag_name\":{JsonSerializer.Serialize(tag)}");
        Assert.Null(ReleaseParser.SelectBest(B(json), true));
    }

    [Theory]
    [InlineData("http://github.com/bustrama/easy-mouse/releases/download/v1.2.0/EasyMouse-Setup-1.2.0.exe")]
    [InlineData("https://evil.example/bustrama/easy-mouse/releases/download/v1.2.0/EasyMouse-Setup-1.2.0.exe")]
    [InlineData("https://github.com/someone-else/easy-mouse/releases/download/v1.2.0/EasyMouse-Setup-1.2.0.exe")]
    [InlineData("https://github.com/bustrama/easy-mouse/releases/download/v1.1.0/EasyMouse-Setup-1.2.0.exe")]
    [InlineData("https://github.com.evil.example/bustrama/easy-mouse/releases/download/v1.2.0/EasyMouse-Setup-1.2.0.exe")]
    [InlineData("https://user@github.com/bustrama/easy-mouse/releases/download/v1.2.0/EasyMouse-Setup-1.2.0.exe")]
    [InlineData("https://github.com/bustrama/easy-mouse/releases/download/v1.2.0/EasyMouse-Setup-1.2.0.exe?x=1")]
    public void Asset_urls_that_are_not_canonical_make_the_release_unusable(string url)
        => Assert.Null(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", installerUrl: url)), false));

    [Fact]
    public void Missing_or_oversized_assets_make_the_release_unusable()
    {
        Assert.Null(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0").Replace("SHA256SUMS.sig", "other.sig")), false));
        Assert.Null(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", installerSize: UpdateTrust.MaxInstallerBytes + 1)), false));
        Assert.Null(ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", installerSize: 0)), false));
    }

    [Fact]
    public void Hostile_release_notes_are_bounded_and_stripped_of_control_and_bidi_characters()
    {
        var evil = "ok\u0007\u001b[31m" + "‮evil" + new string('x', 50_000);
        var notes = ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0", body: evil)), false)!.Notes;
        Assert.True(notes.Length <= UpdateTrust.MaxNotesChars + 1);
        Assert.DoesNotContain('\u0007', notes);
        Assert.DoesNotContain('\u001b', notes);
        Assert.DoesNotContain('‮', notes);
        Assert.StartsWith("ok", notes);
    }

    [Fact]
    public void Null_body_gives_empty_notes() =>
        Assert.Equal("", ReleaseParser.SelectBest(B(FakeGitHub.ReleaseJson("v1.2.0").Replace("\"body\":\"Notes\"", "\"body\":null")), false)!.Notes);
}

public class UpdateVerifierTests
{
    private const string Name = "EasyMouse-Setup-1.2.0.exe";
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("pretend installer bytes");

    private static byte[] Sums(byte[] installer, string name = Name) =>
        Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(installer))}  {name}\n{new string('a', 64)}  EasyMouse-1.2.0-win-x64-portable.zip\n");

    [Fact]
    public void Good_signature_and_hash_pass()
    {
        var (priv, pub) = FakeGitHub.NewKey();
        var sums = Sums(Installer);
        var verifier = new UpdateVerifier(pub);
        var hash = verifier.SignedHashFor(sums, FakeGitHub.Sign(priv, sums), Name);
        using var ms = new MemoryStream(Installer);
        UpdateVerifier.VerifyHash(ms, hash); // does not throw
    }

    [Fact]
    public void Tampered_checksum_list_fails_the_signature()
    {
        var (priv, pub) = FakeGitHub.NewKey();
        var sums = Sums(Installer);
        var sig = FakeGitHub.Sign(priv, sums);
        var tampered = Sums(Encoding.ASCII.GetBytes("evil installer"));
        var ex = Assert.Throws<UpdateException>(() => new UpdateVerifier(pub).SignedHashFor(tampered, sig, Name));
        Assert.Equal(UpdateErrorKind.SignatureInvalid, ex.Kind);
    }

    [Fact]
    public void Wrong_key_fails_the_signature()
    {
        var (priv, _) = FakeGitHub.NewKey();
        var (_, otherPub) = FakeGitHub.NewKey();
        var sums = Sums(Installer);
        var ex = Assert.Throws<UpdateException>(() => new UpdateVerifier(otherPub).SignedHashFor(sums, FakeGitHub.Sign(priv, sums), Name));
        Assert.Equal(UpdateErrorKind.SignatureInvalid, ex.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public void Signatures_of_the_wrong_length_are_invalid(int length)
    {
        var (_, pub) = FakeGitHub.NewKey();
        Assert.False(new UpdateVerifier(pub).IsSignatureValid(Sums(Installer), new byte[length]));
    }

    [Fact]
    public void Flipped_signature_bit_is_invalid()
    {
        var (priv, pub) = FakeGitHub.NewKey();
        var sums = Sums(Installer);
        var sig = FakeGitHub.Sign(priv, sums);
        sig[10] ^= 1;
        Assert.False(new UpdateVerifier(pub).IsSignatureValid(sums, sig));
    }

    [Fact]
    public void Installer_that_does_not_match_the_signed_hash_is_rejected()
    {
        var (priv, pub) = FakeGitHub.NewKey();
        var sums = Sums(Installer);
        var hash = new UpdateVerifier(pub).SignedHashFor(sums, FakeGitHub.Sign(priv, sums), Name);
        using var tampered = new MemoryStream(Encoding.ASCII.GetBytes("pretend installer byteS"));
        Assert.Equal(UpdateErrorKind.HashMismatch, Assert.Throws<UpdateException>(() => UpdateVerifier.VerifyHash(tampered, hash)).Kind);
    }

    [Fact]
    public void The_embedded_key_is_a_valid_32_byte_public_key_and_rejects_a_foreign_signature()
    {
        Assert.Equal(32, UpdateTrust.PublicKey.Length);
        var (priv, _) = FakeGitHub.NewKey();
        var sums = Sums(Installer);
        Assert.False(new UpdateVerifier().IsSignatureValid(sums, FakeGitHub.Sign(priv, sums)));
    }

    [Theory]
    [InlineData("zz{0}  EasyMouse-Setup-1.2.0.exe\n")]                 // not hex
    [InlineData("{0} EasyMouse-Setup-1.2.0.exe\n")]                    // single space
    [InlineData("{0}  ../EasyMouse-Setup-1.2.0.exe\n")]                // path
    [InlineData("{0}  EasyMouse-Setup-1.2.0.exe\n{0}  EasyMouse-Setup-1.2.0.exe\n")] // duplicate
    [InlineData("{0}  \n")]                                            // empty name
    public void Malformed_checksum_lists_are_rejected_even_when_signed(string template)
    {
        var (priv, pub) = FakeGitHub.NewKey();
        var sums = Encoding.UTF8.GetBytes(template.Replace("{0}", new string('a', 64)));
        var ex = Assert.Throws<UpdateException>(() => new UpdateVerifier(pub).SignedHashFor(sums, FakeGitHub.Sign(priv, sums), Name));
        Assert.Equal(UpdateErrorKind.Malformed, ex.Kind);
    }

    [Fact]
    public void A_signed_list_without_an_entry_for_the_installer_is_rejected()
    {
        var (priv, pub) = FakeGitHub.NewKey();
        var sums = Sums(Installer, name: "EasyMouse-Setup-1.1.0.exe"); // an older, genuinely signed list replayed under a newer tag
        var ex = Assert.Throws<UpdateException>(() => new UpdateVerifier(pub).SignedHashFor(sums, FakeGitHub.Sign(priv, sums), Name));
        Assert.Equal(UpdateErrorKind.Malformed, ex.Kind);
    }

    [Fact]
    public void Non_utf8_and_oversized_lists_are_rejected()
    {
        Assert.Equal(UpdateErrorKind.Malformed, Assert.Throws<UpdateException>(() => UpdateVerifier.ParseChecksums([0xff, 0xfe, 0xfd], Name)).Kind);
        Assert.Equal(UpdateErrorKind.TooLarge, Assert.Throws<UpdateException>(() => UpdateVerifier.ParseChecksums(new byte[70_000], Name)).Kind);
    }

    [Fact]
    public void Accepts_the_binary_mode_marker_and_crlf()
    {
        var hash = new string('b', 64);
        var found = UpdateVerifier.ParseChecksums(Encoding.UTF8.GetBytes($"{hash} *{Name}\r\n"), Name);
        Assert.Equal(Convert.FromHexString(hash), found);
    }
}

public class GitHubHttpTests
{
    private static readonly Uri Api = UpdateTrust.LatestReleaseUrl;

    private static Task<byte[]> Get(FakeGitHub fake, Uri url, IReadOnlySet<string>? hosts = null, long max = 1000)
    {
        using var http = new GitHubHttp(fake);
        return http.GetBytesAsync(url, hosts ?? UpdateTrust.ApiHosts, max, TimeSpan.FromSeconds(5), "application/json", CancellationToken.None);
    }

    [Fact]
    public async Task Sends_only_a_generic_user_agent_and_no_credentials_or_cookies()
    {
        var fake = new FakeGitHub { Respond = _ => FakeGitHub.Ok("{}") };
        await Get(fake, Api);
        var req = Assert.Single(fake.Requests);
        Assert.Equal("EasyMouse-Updater", req.Headers.UserAgent.ToString());
        Assert.Null(req.Headers.Authorization);
        Assert.False(req.Headers.Contains("Cookie"));
        Assert.False(req.Headers.Contains("Referer"));
        Assert.Equal(Api, req.RequestUri);
    }

    [Theory]
    [InlineData("http://api.github.com/repos/bustrama/easy-mouse/releases/latest")]
    [InlineData("https://api.github.com.evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("https://api.github.com:8443/x")]
    [InlineData("https://user:pw@api.github.com/x")]
    [InlineData("ftp://api.github.com/x")]
    public async Task Requests_to_untrusted_addresses_are_refused_before_anything_is_sent(string url)
    {
        var fake = new FakeGitHub { Respond = _ => FakeGitHub.Ok("{}") };
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Get(fake, new Uri(url)));
        Assert.Equal(UpdateErrorKind.Untrusted, ex.Kind);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Redirects_to_an_allowed_asset_host_are_followed_and_checked_per_hop()
    {
        var fake = new FakeGitHub
        {
            Respond = r => r.RequestUri!.Host == "github.com"
                ? FakeGitHub.Redirect("https://release-assets.githubusercontent.com/blob/1")
                : FakeGitHub.Ok("payload"),
        };
        var bytes = await Get(fake, new Uri(FakeGitHub.Base + "v1.2.0/SHA256SUMS"), UpdateTrust.AssetHosts);
        Assert.Equal("payload", Encoding.UTF8.GetString(bytes));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://release-assets.githubusercontent.com/blob/1")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/blob/1")]
    [InlineData("https://api.github.com/other")] // allowed for the API but not for an asset download
    public async Task Redirects_to_other_hosts_or_plain_http_are_refused(string target)
    {
        var fake = new FakeGitHub { Respond = r => r.RequestUri!.Host == "github.com" ? FakeGitHub.Redirect(target) : FakeGitHub.Ok("evil") };
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Get(fake, new Uri(FakeGitHub.Base + "v1.2.0/SHA256SUMS"), UpdateTrust.AssetHosts));
        Assert.Equal(UpdateErrorKind.Untrusted, ex.Kind);
        Assert.Single(fake.Requests); // the hostile target was never contacted
    }

    [Fact]
    public async Task The_api_itself_may_not_redirect_anywhere_else()
    {
        var fake = new FakeGitHub { Respond = _ => FakeGitHub.Redirect("https://github.com/bustrama/easy-mouse/releases/latest") };
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Get(fake, Api));
        Assert.Equal(UpdateErrorKind.Untrusted, ex.Kind);
    }

    [Fact]
    public async Task Redirect_loops_stop()
    {
        var fake = new FakeGitHub { Respond = _ => FakeGitHub.Redirect("https://github.com/loop") };
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Get(fake, new Uri("https://github.com/a"), UpdateTrust.AssetHosts));
        Assert.Equal(UpdateErrorKind.Untrusted, ex.Kind);
        Assert.True(fake.Requests.Count <= UpdateTrust.MaxRedirects + 1);
    }

    [Fact]
    public async Task Relative_redirects_resolve_against_the_current_host_and_are_checked()
    {
        var calls = 0;
        var fake = new FakeGitHub { Respond = _ => ++calls == 1 ? FakeGitHub.Redirect("/elsewhere") : FakeGitHub.Ok("ok") };
        Assert.Equal("ok", Encoding.UTF8.GetString(await Get(fake, new Uri("https://github.com/a"), UpdateTrust.AssetHosts)));
        Assert.Equal("https://github.com/elsewhere", fake.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Responses_over_the_limit_are_refused_by_header_and_by_streaming()
    {
        var big = new byte[5000];
        var withLength = new FakeGitHub { Respond = _ => FakeGitHub.Ok(big) };
        Assert.Equal(UpdateErrorKind.TooLarge, (await Assert.ThrowsAsync<UpdateException>(() => Get(withLength, Api, max: 1000))).Kind);

        var noLength = new FakeGitHub { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(big)) } };
        Assert.Equal(UpdateErrorKind.TooLarge, (await Assert.ThrowsAsync<UpdateException>(() => Get(noLength, Api, max: 1000))).Kind);
    }

    [Fact]
    public async Task Rate_limit_answers_carry_the_reset_time()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var fake = new FakeGitHub
        {
            Respond = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
                r.Headers.Add("X-RateLimit-Remaining", "0");
                r.Headers.Add("X-RateLimit-Reset", reset.ToString());
                return r;
            },
        };
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Get(fake, Api));
        Assert.Equal(UpdateErrorKind.RateLimited, ex.Kind);
        Assert.Equal(reset, ex.RetryAfter!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Status_429_is_a_rate_limit_and_other_errors_are_plain_http_errors()
    {
        Assert.Equal(UpdateErrorKind.RateLimited, (await Assert.ThrowsAsync<UpdateException>(() =>
            Get(new FakeGitHub { Respond = _ => new HttpResponseMessage((HttpStatusCode)429) }, Api))).Kind);
        Assert.Equal(UpdateErrorKind.Http, (await Assert.ThrowsAsync<UpdateException>(() =>
            Get(new FakeGitHub { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) }, Api))).Kind);
        Assert.Equal(UpdateErrorKind.Http, (await Assert.ThrowsAsync<UpdateException>(() =>
            Get(new FakeGitHub { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) }, Api))).Kind); // plain 403, not a rate limit
    }

    [Fact]
    public async Task Network_failure_is_reported_as_offline()
    {
        var fake = new FakeGitHub { Respond = _ => throw new HttpRequestException("no route") };
        Assert.Equal(UpdateErrorKind.Offline, (await Assert.ThrowsAsync<UpdateException>(() => Get(fake, Api))).Kind);
    }

    private sealed class UnknownLengthStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "em-upd-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly FakeGitHub _github = new();
    private readonly (byte[] Private, byte[] Public) _key = FakeGitHub.NewKey();
    private AppSettings _settings = new();
    private readonly byte[] _installer = Encoding.ASCII.GetBytes("MZ-installer-1.2.0");

    public UpdateServiceTests() => Directory.CreateDirectory(_work);

    public void Dispose() { try { Directory.Delete(_work, true); } catch (IOException) { } }

    private sealed class FakeLauncher : IInstallerLauncher
    {
        public List<string> Launched { get; } = [];
        public bool Decline { get; set; }
        public TaskCompletionSource<int>? Exit { get; set; }
        public bool FileWasLockedAgainstWrites { get; private set; }
        public Task<int>? Launch(string installerPath)
        {
            if (Decline) throw new UpdateException(UpdateErrorKind.Cancelled, "declined");
            // While the installer starts, the verified file must not be replaceable.
            try { using var _ = new FileStream(installerPath, FileMode.Open, FileAccess.Write, FileShare.Read); }
            catch (IOException) { FileWasLockedAgainstWrites = true; }
            Launched.Add(installerPath);
            return Exit?.Task;
        }
    }

    private readonly FakeLauncher _launcher = new();

    private UpdateService Service(string current = "1.1.0", Func<string, string?>? installerVersion = null)
    {
        var s = new UpdateService(new GitHubHttp(_github), new UpdateVerifier(_key.Public), _launcher, new NullUpdateCache(),
            () => _settings, async change => { _settings = change(_settings); await Task.CompletedTask; }, SemVer.Parse(current), _time,
            _work, installerVersion ?? (_ => "1.2.0"));
        return s;
    }

    private void Serve(string tag = "v1.2.0", byte[]? installer = null, byte[]? sums = null, byte[]? sig = null, bool prerelease = false, string? list = null)
    {
        var v = tag[1..];
        var name = $"EasyMouse-Setup-{v}.exe";
        installer ??= _installer;
        sums ??= Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(_installer))}  {name}\n");
        sig ??= FakeGitHub.Sign(_key.Private, sums);
        var json = list ?? FakeGitHub.ReleaseJson(tag, prerelease: prerelease);
        _github.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/releases/latest") || p.EndsWith("/releases") => FakeGitHub.Ok(json),
            var p when p.EndsWith("/SHA256SUMS") => FakeGitHub.Ok(sums),
            var p when p.EndsWith("/SHA256SUMS.sig") => FakeGitHub.Ok(sig),
            var p when p.EndsWith(".exe") => FakeGitHub.Ok(installer),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    [Fact]
    public async Task Check_finds_a_newer_release_and_records_the_time_without_downloading_anything()
    {
        Serve();
        var svc = Service();
        await svc.CheckAsync(manual: true);
        Assert.Equal("1.2.0", svc.Available!.Version.ToString());
        Assert.Null(svc.LastError);
        Assert.Equal(_time.GetUtcNow(), _settings.LastUpdateCheckUtc);
        Assert.All(_github.Requests, r => Assert.Equal(UpdateTrust.ApiHost, r.RequestUri!.Host));
        Assert.Single(_github.Requests);
        Assert.Empty(_launcher.Launched);
    }

    [Theory]
    [InlineData("1.2.0")]   // same version
    [InlineData("1.3.0")]   // we are newer than the release: never a downgrade
    [InlineData("2.0.0")]
    public async Task No_update_is_offered_unless_the_release_is_strictly_newer(string current)
    {
        Serve();
        var svc = Service(current);
        await svc.CheckAsync(manual: true);
        Assert.Null(svc.Available);
        Assert.True(svc.UpToDate);
    }

    [Fact]
    public async Task Prerelease_of_the_same_version_is_older_than_the_final()
    {
        Serve("v1.2.0-rc.1", prerelease: true);
        _settings = _settings with { IncludePrereleases = true };
        var svc = Service("1.2.0");
        await svc.CheckAsync(manual: true);
        Assert.Null(svc.Available);
    }

    [Fact]
    public async Task Prereleases_use_the_list_endpoint_only_when_enabled()
    {
        Serve("v1.2.0-beta.1", prerelease: true, list: "[" + FakeGitHub.ReleaseJson("v1.2.0-beta.1", prerelease: true) + "]");
        var svc = Service();
        await svc.CheckAsync(manual: true);
        Assert.Null(svc.Available);
        Assert.EndsWith("/releases/latest", _github.Requests[0].RequestUri!.AbsolutePath);

        _settings = _settings with { IncludePrereleases = true };
        await svc.CheckAsync(manual: true);
        Assert.Equal("1.2.0-beta.1", svc.Available!.Version.ToString());
        Assert.Contains("/releases", _github.Requests[1].RequestUri!.AbsolutePath);
        Assert.DoesNotContain("latest", _github.Requests[1].RequestUri!.AbsolutePath);

        _settings = _settings with { IncludePrereleases = false };
        svc.Reevaluate();
        Assert.Null(svc.Available);
    }

    [Fact]
    public async Task Automatic_check_runs_at_most_once_a_day_and_only_when_enabled()
    {
        Serve();
        var svc = Service();
        Assert.True(svc.IsDue());
        await svc.CheckAsync(manual: false);
        Assert.False(svc.IsDue());
        _time.Advance(TimeSpan.FromHours(23));
        Assert.False(svc.IsDue());
        _time.Advance(TimeSpan.FromHours(2));
        Assert.True(svc.IsDue());
        _settings = _settings with { CheckForUpdates = false };
        Assert.False(svc.IsDue());
    }

    [Fact]
    public async Task Offline_automatic_checks_stay_quiet_and_retry_after_an_hour()
    {
        _github.Respond = _ => throw new HttpRequestException("offline");
        var svc = Service();
        await svc.CheckAsync(manual: false);
        Assert.Equal(UpdateErrorKind.Offline, svc.LastError!.Kind);
        Assert.Null(_settings.LastUpdateCheckUtc); // a failed check is not a check
        Assert.False(svc.IsDue());                 // no hammering
        _time.Advance(TimeSpan.FromMinutes(61));
        Assert.True(svc.IsDue());
        Assert.Single(_github.Requests);
    }

    [Fact]
    public async Task Rate_limit_blocks_further_requests_until_the_reset_time()
    {
        var reset = _time.GetUtcNow().AddMinutes(40);
        _github.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            r.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            return r;
        };
        var svc = Service();
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateErrorKind.RateLimited, svc.LastError!.Kind);
        Assert.False(svc.IsDue());
        await svc.CheckAsync(manual: true);   // even a manual click does not call GitHub again while limited
        Assert.Single(_github.Requests);
        _time.Advance(TimeSpan.FromMinutes(41));
        Assert.True(svc.IsDue());
    }

    [Fact]
    public async Task Garbage_from_the_server_is_a_clean_error_and_leaves_state_unchanged()
    {
        Serve(list: "<html>captive portal</html>");
        var svc = Service();
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateErrorKind.Malformed, svc.LastError!.Kind);
        Assert.Null(svc.Available);
    }

    [Fact]
    public async Task Install_downloads_verifies_and_only_then_launches()
    {
        Serve();
        var svc = Service();
        var started = false;
        svc.InstallerStarted += (_, _) => started = true;
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);

        Assert.Null(svc.LastError);
        Assert.True(started);
        Assert.True(svc.InstallerRunning);
        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal(_installer, await File.ReadAllBytesAsync(launched));
        Assert.True(_launcher.FileWasLockedAgainstWrites, "the verified installer must be held open while it is started");
        Assert.All(_github.Requests, r => Assert.Equal("https", r.RequestUri!.Scheme));
        Assert.All(_github.Requests, r => Assert.Contains(r.RequestUri!.Host, UpdateTrust.AssetHosts.Concat(UpdateTrust.ApiHosts)));
    }

    [Fact]
    public async Task Tampered_installer_is_never_launched_and_is_deleted()
    {
        Serve(installer: Encoding.ASCII.GetBytes("MZ-evil"));
        var svc = Service();
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);
        Assert.Equal(UpdateErrorKind.HashMismatch, svc.LastError!.Kind);
        Assert.Empty(_launcher.Launched);
        Assert.Empty(Directory.GetDirectories(_work, "EasyMouse-update-*"));
    }

    [Fact]
    public async Task Checksums_signed_with_another_key_are_never_trusted()
    {
        var (otherPriv, _) = FakeGitHub.NewKey();
        var sums = Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(_installer))}  EasyMouse-Setup-1.2.0.exe\n");
        Serve(sums: sums, sig: FakeGitHub.Sign(otherPriv, sums));
        var svc = Service();
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);
        Assert.Equal(UpdateErrorKind.SignatureInvalid, svc.LastError!.Kind);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task A_missing_or_short_signature_fails_closed()
    {
        Serve(sig: new byte[10]);
        var svc = Service();
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);
        Assert.Equal(UpdateErrorKind.SignatureInvalid, svc.LastError!.Kind);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task Installer_whose_own_version_differs_from_the_release_is_refused()
    {
        Serve();
        var svc = Service(installerVersion: _ => "1.0.0");
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);
        Assert.Equal(UpdateErrorKind.Downgrade, svc.LastError!.Kind);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task Declining_the_windows_permission_prompt_is_not_an_error()
    {
        Serve();
        _launcher.Decline = true;
        var svc = Service();
        var started = false;
        svc.InstallerStarted += (_, _) => started = true;
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);
        Assert.Null(svc.LastError);
        Assert.False(started);
        Assert.False(svc.InstallerRunning);
        Assert.NotNull(svc.Available); // still offered
        Assert.Equal(UpdatePhase.Idle, svc.Phase);
    }

    [Fact]
    public async Task Install_refuses_a_release_that_is_not_newer_than_the_running_version()
    {
        Serve();
        var older = Service("1.1.0");
        await older.CheckAsync(manual: true);
        var release = older.Available!;
        _github.Requests.Clear();

        var current = Service("1.2.0");
        await current.InstallAsync(release);
        Assert.Equal(UpdateErrorKind.Downgrade, current.LastError!.Kind);
        Assert.Empty(_github.Requests);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task A_cached_answer_that_is_not_newer_is_not_offered_after_the_app_updated()
    {
        var cache = new MemCache(Encoding.UTF8.GetBytes(FakeGitHub.ReleaseJson("v1.2.0")));
        var svc = new UpdateService(new GitHubHttp(_github), new UpdateVerifier(_key.Public), _launcher, cache,
            () => _settings, change => { _settings = change(_settings); return Task.CompletedTask; }, SemVer.Parse("1.2.0"), _time, _work);
        Assert.Null(svc.Available);
        var newer = new UpdateService(new GitHubHttp(_github), new UpdateVerifier(_key.Public), _launcher, cache,
            () => _settings, change => { _settings = change(_settings); return Task.CompletedTask; }, SemVer.Parse("1.1.0"), _time, _work);
        Assert.Equal("1.2.0", newer.Available!.Version.ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_corrupt_cache_is_ignored()
    {
        var svc = new UpdateService(new GitHubHttp(_github), new UpdateVerifier(_key.Public), _launcher, new MemCache("garbage"u8.ToArray()),
            () => _settings, change => { _settings = change(_settings); return Task.CompletedTask; }, SemVer.Parse("1.1.0"), _time, _work);
        Assert.Null(svc.Available);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failure_that_is_not_an_update_error_is_recorded_and_does_not_escape_the_check()
    {
        Serve();
        var svc = new UpdateService(new GitHubHttp(_github), new UpdateVerifier(_key.Public), _launcher, new NullUpdateCache(),
            () => _settings, _ => throw new IOException("settings.json is locked"), SemVer.Parse("1.1.0"), _time, _work, _ => "1.2.0");
        await svc.CheckAsync(manual: true); // an escaping exception would crash the process from an async-void command
        Assert.NotNull(svc.LastError);
        Assert.False(svc.Busy);
    }

    [Fact]
    public async Task A_release_string_with_a_lone_surrogate_is_a_clean_result_not_an_exception()
    {
        Serve(list: "{\"tag_name\":\"v1.2.0\",\"name\":\"\\ud800\",\"body\":\"\\ud800\",\"draft\":false,\"prerelease\":false,\"assets\":[]}");
        var svc = Service();
        await svc.CheckAsync(manual: true);
        Assert.False(svc.Busy);
        Assert.Null(svc.Available); // no usable assets either way
    }

    [Fact]
    public async Task The_installer_file_stays_locked_until_the_installer_process_has_ended()
    {
        Serve();
        _launcher.Exit = new TaskCompletionSource<int>();
        var svc = Service();
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);
        var path = Assert.Single(_launcher.Launched);

        // Launch returned long ago (the UAC prompt may still be open): the file must still not be replaceable.
        Assert.Throws<IOException>(() => { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); });

        _launcher.Exit.SetResult(0);
        for (var i = 0; i < 100; i++)
        {
            try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); return; }
            catch (IOException) { await Task.Delay(20); }
        }
        Assert.Fail("the installer file was never released");
    }

    [Fact]
    public async Task Install_uses_the_release_the_user_confirmed_even_if_a_later_check_found_another()
    {
        Serve();
        var svc = Service();
        await svc.CheckAsync(manual: true);
        var confirmed = svc.Available!;

        // A background check now sees 1.3.0, while the assets of 1.2.0 are still what was confirmed.
        Serve(list: FakeGitHub.ReleaseJson("v1.3.0"));
        _github.Respond = Chain(_github.Respond!, _installer);
        await svc.CheckAsync(manual: true);
        Assert.Equal("1.3.0", svc.Available!.Version.ToString());

        await svc.InstallAsync(confirmed);
        Assert.Null(svc.LastError);
        Assert.Equal("EasyMouse-Setup-1.2.0.exe", Path.GetFileName(Assert.Single(_launcher.Launched)));
    }

    private Func<HttpRequestMessage, HttpResponseMessage> Chain(Func<HttpRequestMessage, HttpResponseMessage> listServer, byte[] installer)
    {
        var sums = Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(installer))}  EasyMouse-Setup-1.2.0.exe\n");
        var sig = FakeGitHub.Sign(_key.Private, sums);
        return r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/SHA256SUMS") => FakeGitHub.Ok(sums),
            var p when p.EndsWith("/SHA256SUMS.sig") => FakeGitHub.Ok(sig),
            var p when p.EndsWith(".exe") => FakeGitHub.Ok(installer),
            _ => listServer(r),
        };
    }

    private sealed class MemCache(byte[]? raw) : IUpdateCache
    {
        public byte[]? Load() => raw;
        public void Save(byte[] value) => raw = value;
    }

    [Fact]
    public async Task No_release_published_yet_is_up_to_date_not_an_error()
    {
        // GitHub answers 404 for /releases/latest until the first release is published.
        _github.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var svc = Service();
        await svc.CheckAsync(manual: true);
        Assert.Null(svc.LastError);
        Assert.Null(svc.Available);
        Assert.True(svc.UpToDate);
        Assert.Equal(_time.GetUtcNow(), _settings.LastUpdateCheckUtc); // recorded, so no hourly retry loop
        Assert.False(svc.IsDue());
    }

    [Fact]
    public async Task A_404_for_an_asset_is_an_error_not_a_silent_success()
    {
        Serve();
        var svc = Service();
        await svc.CheckAsync(manual: true);
        var inner = _github.Respond;
        _github.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/SHA256SUMS") ? new HttpResponseMessage(HttpStatusCode.NotFound) : inner(r);
        await svc.InstallAsync(svc.Available!);
        Assert.NotNull(svc.LastError);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task Installer_that_ends_with_an_error_after_starting_clears_the_running_status()
    {
        Serve();
        _launcher.Exit = new TaskCompletionSource<int>();
        var svc = Service();
        await svc.CheckAsync(manual: true);
        await svc.InstallAsync(svc.Available!);
        Assert.True(svc.InstallerRunning);
        _launcher.Exit.SetResult(5); // e.g. the user declined the permission prompt late
        await Task.Delay(100);
        Assert.False(svc.InstallerRunning);
        Assert.Equal(UpdateErrorKind.InstallerFailed, svc.LastError!.Kind);
    }

    [Fact]
    public void Settings_default_to_checking_daily_without_prereleases()
    {
        var s = new AppSettings();
        Assert.True(s.CheckForUpdates);
        Assert.False(s.IncludePrereleases);
        Assert.Null(s.LastUpdateCheckUtc);
    }

    [Fact]
    public void Update_settings_survive_a_save_and_load()
    {
        var path = Path.Combine(_work, "settings.json");
        var store = new SettingsStore(path);
        var stamp = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        store.Save(new AppSettings { CheckForUpdates = false, IncludePrereleases = true, LastUpdateCheckUtc = stamp });
        var loaded = new SettingsStore(path).Load();
        Assert.False(loaded.CheckForUpdates);
        Assert.True(loaded.IncludePrereleases);
        Assert.Equal(stamp, loaded.LastUpdateCheckUtc);
    }
}
