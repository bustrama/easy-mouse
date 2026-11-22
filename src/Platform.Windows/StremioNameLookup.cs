using System.Buffers;
using System.Net;
using System.Text.Json;

namespace EasyMouse.Platform.Windows;

/// <summary>
/// Asks Stremio's local streaming server which file belongs to an infoHash. The address is hard-coded loopback
/// (<c>http://127.0.0.1:11470/stats.json</c>): no configuration, no redirects, one second, a response size cap, defensive
/// parsing and a short per-infoHash cache. It never contacts anything outside this PC (no Cinemeta or other web service).
/// </summary>
internal sealed class StremioNameLookup : IDisposable
{
    public static readonly Uri StatsUri = new("http://127.0.0.1:11470/stats.json");

    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(15);

    private const int MaxCacheEntries = 64;
    private const int MaxFilesScanned = 2000;
    private const int MaxNameLength = 1024;

    private static readonly string[] s_videoExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".ogv"];

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    private sealed record Entry(string? Name, DateTimeOffset ExpiresAt, bool InFlight);

    /// <param name="handler">Tests pass a fake; production leaves it null (a loopback-only handler without redirects or proxy).</param>
    public StremioNameLookup(HttpMessageHandler? handler = null, TimeProvider? time = null, TimeSpan? timeout = null)
    {
        _time = time ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = DefaultTimeout,
            MaxResponseHeadersLength = 16, // KB
        }, disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan }; // the per-call token enforces the real deadline, including the body
    }

    /// <summary>The cached answer: <c>Known</c> false means "not asked yet or expired". A known null name means "asked, nothing found".</summary>
    public (bool Known, string? Name) TryGetCached(string infoHash)
    {
        var key = infoHash.ToLowerInvariant();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var e) && (e.InFlight || e.ExpiresAt > _time.GetUtcNow())) return (!e.InFlight, e.Name);
        }
        return (false, null);
    }

    /// <summary>
    /// Starts one background lookup for the hash unless a fresh answer exists or one is running. Returns the task (for tests);
    /// callers on the SMTC path ignore it. Completion never throws.
    /// </summary>
    public Task BeginLookup(string infoHash, Action<string>? onFound = null)
    {
        var key = infoHash.ToLowerInvariant();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var e) && (e.InFlight || e.ExpiresAt > _time.GetUtcNow())) return Task.CompletedTask;
            if (_cache.Count >= MaxCacheEntries) Prune();
            _cache[key] = new Entry(null, DateTimeOffset.MaxValue, InFlight: true);
        }
        return Task.Run(async () =>
        {
            string? name = null;
            try { name = await LookupAsync(key).ConfigureAwait(false); }
            catch (Exception) { /* local server absent, slow or odd: treated as "not found" */ }
            lock (_gate)
            {
                _cache[key] = new Entry(name, _time.GetUtcNow() + (name is null ? NegativeTtl : PositiveTtl), InFlight: false);
            }
            if (name is not null) onFound?.Invoke(name);
        });
    }

    /// <summary>One request to the stats endpoint. Returns null for anything unexpected; throws only for cancellation/timeouts/IO.</summary>
    public async Task<string?> LookupAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, StatsUri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) return null; // includes 3xx: redirects are never followed
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var body = await ReadCappedAsync(stream, cts.Token).ConfigureAwait(false);
        return body is null ? null : ParseName(body, infoHash);
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream stream, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var ms = new MemoryStream();
            int n;
            while ((n = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > MaxResponseBytes) return null;
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Picks the file name for <paramref name="infoHash"/> out of the <c>stats.json</c> body (an object keyed by infoHash).
    /// Stremio does not say which file of a multi-file torrent is playing, so: the only file, else the only video file,
    /// else the torrent's own name.
    /// </summary>
    internal static string? ParseName(ReadOnlySpan<byte> json, string infoHash)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, infoHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) return null;
                var entry = prop.Value;

                // A key that matches but names a different hash inside would be a lie; ignore such entries.
                if (entry.TryGetProperty("infoHash", out var inner) && inner.ValueKind == JsonValueKind.String
                    && !string.Equals(inner.GetString(), infoHash, StringComparison.OrdinalIgnoreCase)) return null;

                var torrentName = StringOrNull(entry, "name");
                if (entry.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var f in files.EnumerateArray())
                    {
                        if (names.Count >= MaxFilesScanned) break;
                        if (f.ValueKind != JsonValueKind.Object) continue;
                        var n = StringOrNull(f, "name") ?? LastSegment(StringOrNull(f, "path"));
                        if (n is not null) names.Add(n);
                    }
                    if (names.Count == 1) return names[0];
                    var videos = names.Where(IsVideo).ToList();
                    if (videos.Count == 1) return videos[0];
                }
                return torrentName;
            }
            return null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool IsVideo(string name) => s_videoExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static string? LastSegment(string? path)
    {
        if (path is null) return null;
        var i = path.LastIndexOfAny(['/', '\\']);
        var s = i >= 0 ? path[(i + 1)..] : path;
        return s.Length == 0 ? null : s;
    }

    private static string? StringOrNull(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Length > MaxNameLength ? s[..MaxNameLength] : s;
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var k in _cache.Where(kv => !kv.Value.InFlight && kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList()) _cache.Remove(k);
        if (_cache.Count >= MaxCacheEntries)
            foreach (var k in _cache.Where(kv => !kv.Value.InFlight).Select(kv => kv.Key).ToList()) _cache.Remove(k);
    }

    public void Dispose() => _http.Dispose();
}
