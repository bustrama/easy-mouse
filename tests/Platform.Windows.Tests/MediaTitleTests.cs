using System.Net;
using System.Text;
using EasyMouse.Platform.Windows;

namespace EasyMouse.Platform.Windows.Tests;

public class ReleaseNameCleanerTests
{
    [Theory]
    // anime (fansub groups, CRC)
    [InlineData("[SubsPlease] Yuusha-kei ni Shosu - 09 (1080p) [AFD2CAE7].mkv", "Yuusha-kei ni Shosu - 09")]
    [InlineData("[Erai-raws] Kimetsu no Yaiba - 11 [1080p][Multiple Subtitle][ABCD1234].mkv", "Kimetsu no Yaiba - 11")]
    [InlineData("[Judas] Attack on Titan - S04E28 [BD 1080p][HEVC x265 10bit][Dual-Audio].mkv", "Attack on Titan - S04E28")]
    // series
    [InlineData("The.Bear.S02E05.1080p.WEB-DL.DDP5.1.H.264-NTb.mkv", "The Bear S02E05")]
    [InlineData("Better_Call_Saul_S06E13_720p_HDTV_x264-GRP.mp4", "Better Call Saul S06E13")]
    [InlineData("Severance.S01E03.2160p.WEB-DL.DV.HDR10+.HEVC.Atmos-FLUX", "Severance S01E03")]
    // movies with year
    [InlineData("Dune.Part.Two.2024.2160p.BluRay.REMUX.HDR.HEVC.TrueHD.Atmos-FraMeSToR.mkv", "Dune Part Two 2024")]
    [InlineData("Spirited Away (2001) 1080p BluRay x265.mkv", "Spirited Away (2001)")]
    [InlineData("Mr.Robot.2015.S01E01.720p.BluRay.x264.mkv", "Mr Robot 2015 S01E01")]
    // already readable: dots in a normal title survive, nothing to cut
    [InlineData("Mr. Robot - S01E01 - eps1.0_hellofriend.mov", "Mr. Robot - S01E01 - eps1.0 hellofriend")]
    [InlineData("Plain Title", "Plain Title")]
    // non-Latin
    [InlineData("[Group] 鬼滅の刃 - 05 [1080p].mkv", "鬼滅の刃 - 05")]
    [InlineData("שם הסדרה S02E05 1080p WEB-DL x264.mkv", "שם הסדרה S02E05")]
    [InlineData("Фильм.2019.BDRip.1080p.x264.mkv", "Фильм 2019")]
    // tags only, nothing left: fall back to the sanitised input rather than an empty title
    [InlineData("1080p.x264.mkv", "1080p.x264")]
    public void Release_names_become_readable_titles(string input, string expected) => Assert.Equal(expected, ReleaseNameCleaner.Clean(input));

    [Fact]
    public void Control_and_bidi_override_characters_never_reach_the_output()
    {
        var clean = ReleaseNameCleaner.Clean("Evil\u202EgnirtS\u0000\u001b[31m.1080p.mkv\u2066");
        Assert.DoesNotContain(clean, c => char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069');
    }

    [Fact]
    public void Very_long_and_hostile_inputs_are_bounded_and_fast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var hostile in new[]
        {
            new string('[', 200_000), new string('(', 200_000) + "1080p", "[" + new string('a', 100_000) + "]",
            string.Concat(Enumerable.Repeat("a.", 100_000)), string.Concat(Enumerable.Repeat("1080p ", 50_000)),
            new string('9', 300_000) + "x" + new string('9', 300_000), new string('\u0301', 100_000),
        })
        {
            var cleaned = ReleaseNameCleaner.Clean(hostile);
            Assert.True(cleaned.Length <= ReleaseNameCleaner.MaxOutput);
            _ = ReleaseNameCleaner.LooksLikeReleaseName(hostile);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 500));
        var cleaned = ReleaseNameCleaner.Clean(emoji);
        Assert.True(cleaned.Length <= ReleaseNameCleaner.MaxOutput);
        Assert.Equal(cleaned, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cleaned))); // round-trips: no lone surrogate
    }

    [Theory]
    [InlineData("[SubsPlease] Yuusha-kei ni Shosu - 09 (1080p) [AFD2CAE7].mkv", true)]
    [InlineData("The.Bear.S02E05.1080p.WEB-DL.x264-NTb", true)]
    [InlineData("Movie 1080p BluRay x264", true)]
    [InlineData("Some Song.mp3", true)]
    [InlineData("Get Me Going", false)]
    [InlineData("Nature Relaxation 4K HDR", false)]
    [InlineData("Mr. Robot", false)]
    [InlineData("Song (1080p)", false)]
    [InlineData("", false)]
    public void Only_release_style_names_are_detected(string input, bool expected) => Assert.Equal(expected, ReleaseNameCleaner.LooksLikeReleaseName(input));
}

public class MediaTitleResolverTests
{
    private const string Hash = "5e490a786665701f6bed3a931abf4f53f5d46c54";

    [Theory]
    [InlineData("announce&tr=dht:5e490a786665701f6bed3a931abf4f53f5d46c54")]
    [InlineData("magnet:?xt=urn:btih:5e490a786665701f6bed3a931abf4f53f5d46c54&dn=x")]
    [InlineData("udp://tracker.opentrackr.org:1337/announce")]
    [InlineData("https://example.com/watch?v=1")]
    [InlineData("www.example.com")]
    [InlineData("5E490A786665701F6BED3A931ABF4F53F5D46C54")]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")]
    public void Junk_titles_are_recognised(string text) => Assert.True(MediaTitleResolver.IsJunk(text));

    [Theory]
    [InlineData("Get Me Going")]
    [InlineData("Yuusha-kei ni Shosu - 09")]
    [InlineData("Plan 9 from Outer Space")]
    [InlineData("שיר 12345")]
    [InlineData("deadbeef")] // a short hex-looking word is a word
    [InlineData("")]
    public void Normal_titles_are_not_junk(string text) => Assert.False(MediaTitleResolver.IsJunk(text));

    [Fact]
    public void The_infohash_is_found_in_a_tracker_fragment_and_normalised()
    {
        Assert.Equal(Hash, MediaTitleResolver.InfoHashIn("announce&tr=dht:" + Hash));
        Assert.Equal(Hash, MediaTitleResolver.InfoHashIn(Hash.ToUpperInvariant()));
        Assert.Null(MediaTitleResolver.InfoHashIn("abc"));
        Assert.Null(MediaTitleResolver.InfoHashIn(Hash + "00")); // 42 hex: not a hash
    }

    [Fact]
    public void Junk_from_an_unknown_app_becomes_the_app_name_and_junk_artists_are_dropped()
    {
        var r = new MediaTitleResolver(null, () => { });
        Assert.Equal(("Spotify", null), r.Resolve("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "magnet:?xt=urn:btih:" + Hash, "udp://x"));
        Assert.Equal(("Get Me Going", "Oh The Larceny"), r.Resolve("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Get Me Going", "Oh The Larceny"));
        Assert.Equal(("Yuusha-kei ni Shosu - 09", null), r.Resolve("mpv.exe", "[SubsPlease] Yuusha-kei ni Shosu - 09 (1080p) [AFD2CAE7].mkv", null));
        Assert.Equal((null, null), r.Resolve("mpv.exe", null, null));
    }

    [Theory]
    [InlineData("stremio-shell-ng.exe", "Stremio")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("C:\\Tools\\my-player.exe", "My player")]
    [InlineData("", "Media")]
    public void App_names_are_friendly(string id, string expected) => Assert.Equal(expected, AppNames.Friendly(id));

    [Fact]
    public async Task Stremio_shows_the_app_name_while_resolving_then_the_cleaned_file_name_without_blocking()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHandler(async (_, ct) => { await gate.Task; return Json(Stats(Hash, "[SubsPlease] Yuusha-kei ni Shosu - 09 (1080p) [AFD2CAE7].mkv")); });
        using var lookup = new StremioNameLookup(handler);
        var resolved = new TaskCompletionSource();
        var resolver = new MediaTitleResolver(lookup, () => resolved.TrySetResult());

        var first = resolver.Resolve("stremio-shell-ng.exe", "announce&tr=dht:" + Hash, null);
        Assert.Equal(("Stremio", null), first); // answered immediately, the server has not replied yet
        resolver.Resolve("stremio-shell-ng.exe", "announce&tr=dht:" + Hash, null);
        gate.SetResult();
        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(("Yuusha-kei ni Shosu - 09", null), resolver.Resolve("stremio-shell-ng.exe", "announce&tr=dht:" + Hash, ""));
        Assert.Equal(1, handler.Calls); // one lookup for repeated refreshes
    }

    [Fact]
    public async Task Stremio_with_the_hash_in_the_artist_field_is_resolved_too()
    {
        using var lookup = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(Stats(Hash, "Movie.2020.1080p.WEB-DL.x264.mkv")))));
        await lookup.BeginLookup(Hash);
        var r = new MediaTitleResolver(lookup, () => { });
        Assert.Equal(("Movie 2020", null), r.Resolve("stremio-shell-ng.exe", "Unknown", Hash));
    }

    [Fact]
    public async Task A_stremio_hash_the_server_does_not_know_falls_back_to_the_app_name_and_is_cached_briefly()
    {
        var time = new FakeTime();
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(Stats("0000000000000000000000000000000000000000", "x.mkv"))));
        using var lookup = new StremioNameLookup(handler, time);
        await lookup.BeginLookup(Hash);
        Assert.Equal((true, (string?)null), lookup.TryGetCached(Hash));

        await lookup.BeginLookup(Hash); // negative answer still fresh: no second request
        Assert.Equal(1, handler.Calls);
        time.Advance(StremioNameLookup.NegativeTtl + TimeSpan.FromSeconds(1));
        Assert.False(lookup.TryGetCached(Hash).Known);
        await lookup.BeginLookup(Hash);
        Assert.Equal(2, handler.Calls);
    }

    // ------------------------------------------------------------------ the lookup itself

    private static string Stats(string hash, string file, string? extraFile = null)
    {
        var files = $$"""{"path":"{{file}}","name":"{{file}}","length":1}""" + (extraFile is null ? "" : $$""",{"path":"{{extraFile}}","name":"{{extraFile}}","length":1}""");
        return $$$"""{"{{{hash}}}":{"infoHash":"{{{hash}}}","name":"{{{file}}}","files":[{{{files}}}]}}""";
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        public Uri? LastUri;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastUri = request.RequestUri;
            return await respond(request, cancellationToken);
        }
    }

    [Fact]
    public async Task The_request_goes_to_exactly_the_loopback_stats_url()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(Stats(Hash, "a.mkv"))));
        using var lookup = new StremioNameLookup(handler);
        Assert.Equal("a.mkv", await lookup.LookupAsync(Hash));
        Assert.Equal("http://127.0.0.1:11470/stats.json", handler.LastUri!.ToString());
    }

    [Fact]
    public async Task The_hash_match_ignores_case_and_a_mismatching_inner_hash_is_ignored()
    {
        using var ok = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(Stats(Hash, "a.mkv")))));
        Assert.Equal("a.mkv", await ok.LookupAsync(Hash.ToUpperInvariant()));

        var lying = $$$"""{"{{{Hash}}}":{"infoHash":"1111111111111111111111111111111111111111","name":"evil.mkv"}}""";
        using var bad = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(lying))));
        Assert.Null(await bad.LookupAsync(Hash));
    }

    [Fact]
    public async Task A_multi_file_torrent_uses_the_only_video_file_else_the_torrent_name()
    {
        using var one = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(Stats(Hash, "Show.S01E01.mkv", "Show.S01E01.srt")))));
        Assert.Equal("Show.S01E01.mkv", await one.LookupAsync(Hash));

        using var many = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(Stats(Hash, "Show.S01E01.mkv", "Show.S01E02.mkv")))));
        Assert.Equal("Show.S01E01.mkv", await many.LookupAsync(Hash)); // torrent name (the first file here) as the best available label
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"5e490a786665701f6bed3a931abf4f53f5d46c54\":5}")]
    [InlineData("{\"5e490a786665701f6bed3a931abf4f53f5d46c54\":{\"name\":42}}")]
    [InlineData("{\"5e490a786665701f6bed3a931abf4f53f5d46c54\":{\"files\":\"x\"}}")]
    [InlineData("")]
    public async Task Bad_json_yields_no_name_instead_of_an_error(string body)
    {
        using var lookup = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(body))));
        Assert.Null(await lookup.LookupAsync(Hash));
    }

    [Fact]
    public async Task Deeply_nested_json_is_rejected()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000));
        using var lookup = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(deep))));
        Assert.Null(await lookup.LookupAsync(Hash));
    }

    [Fact]
    public async Task A_response_over_the_size_cap_is_discarded_unparsed()
    {
        var huge = "{\"" + Hash + "\":{\"name\":\"" + new string('a', StremioNameLookup.MaxResponseBytes + 10) + "\"}}";
        using var lookup = new StremioNameLookup(new FakeHandler((_, _) => Task.FromResult(Json(huge))));
        Assert.Null(await lookup.LookupAsync(Hash));
    }

    [Fact]
    public async Task A_chunked_response_that_never_ends_is_cut_off_by_the_size_cap()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) }));
        using var lookup = new StremioNameLookup(handler, timeout: TimeSpan.FromSeconds(10));
        Assert.Null(await lookup.LookupAsync(Hash));
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Redirects_and_errors_are_never_followed(HttpStatusCode status)
    {
        var handler = new FakeHandler((_, _) =>
        {
            var r = Json(Stats(Hash, "a.mkv"), status);
            r.Headers.Location = new Uri("http://evil.example/stats.json");
            return Task.FromResult(r);
        });
        using var lookup = new StremioNameLookup(handler);
        Assert.Null(await lookup.LookupAsync(Hash));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("127.0.0.1", handler.LastUri!.Host);
    }

    [Fact]
    public async Task A_slow_server_is_given_up_on_at_the_timeout()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); });
        using var lookup = new StremioNameLookup(handler, timeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.LookupAsync(Hash));
        // the background path swallows it and caches a short "not found"
        await lookup.BeginLookup(Hash);
        Assert.Equal((true, (string?)null), lookup.TryGetCached(Hash));
    }

    [Fact]
    public async Task A_refused_connection_is_just_not_found()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var lookup = new StremioNameLookup(handler);
        await lookup.BeginLookup(Hash);
        Assert.Equal((true, (string?)null), lookup.TryGetCached(Hash));
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'a', offset, count); return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
