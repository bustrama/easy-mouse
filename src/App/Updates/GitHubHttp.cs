using System.Net;
using System.Net.Http.Headers;

namespace EasyMouse.App.Updates;

/// <summary>
/// The only code in the app that talks to the internet. HTTPS only, default port only, no credentials, no cookies, a generic
/// User-Agent, redirects followed by hand and only to hosts on the given allow-list, hard size limits and timeouts.
/// The handler is injectable so tests never touch the network.
/// </summary>
public sealed class GitHubHttp : IDisposable
{
    private readonly HttpClient _client;

    /// <param name="handler">Null for the real network. Auto-redirect must be off; this class does the redirects itself.</param>
    public GitHubHttp(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan }; // limits come from the tokens below
    }

    /// <summary>GET into memory, at most <paramref name="maxBytes"/>.</summary>
    public async Task<byte[]> GetBytesAsync(Uri url, IReadOnlySet<string> allowedHosts, long maxBytes, TimeSpan timeout, string accept, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await GetToStreamAsync(url, allowedHosts, maxBytes, timeout, accept, ms, null, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>GET into <paramref name="sink"/>, at most <paramref name="maxBytes"/>; <paramref name="progress"/> gets bytes so far.</summary>
    public async Task GetToStreamAsync(Uri url, IReadOnlySet<string> allowedHosts, long maxBytes, TimeSpan timeout, string accept,
        Stream sink, Action<long, long?>? progress, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await GetCoreAsync(url, allowedHosts, maxBytes, accept, sink, progress, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorKind.Offline, "The request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorKind.Offline, "Could not reach GitHub: " + ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateErrorKind.Offline, "The connection was interrupted: " + ex.Message, ex);
        }
    }

    private async Task GetCoreAsync(Uri url, IReadOnlySet<string> allowedHosts, long maxBytes, string accept, Stream sink, Action<long, long?>? progress, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            CheckUrl(current, allowedHosts);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UpdateTrust.UserAgent, null));
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.AcceptEncoding.Clear();

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;

            if (code is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= UpdateTrust.MaxRedirects) throw new UpdateException(UpdateErrorKind.Untrusted, "Too many redirects.");
                var location = response.Headers.Location
                    ?? throw new UpdateException(UpdateErrorKind.Malformed, "A redirect had no target.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue; // validated at the top of the loop, before anything is sent
            }

            if (code is 403 or 429 && IsRateLimited(response, out var retryAfter))
                throw new UpdateException(UpdateErrorKind.RateLimited, "GitHub rate limit reached.", retryAfter: retryAfter);
            if (code == 404)
                throw new UpdateException(UpdateErrorKind.NoRelease, "GitHub has nothing at this address (404).");
            if (code != 200)
                throw new UpdateException(UpdateErrorKind.Http, $"GitHub answered HTTP {code}.");

            var length = response.Content.Headers.ContentLength;
            if (length > maxBytes) throw new UpdateException(UpdateErrorKind.TooLarge, "The response is larger than allowed.");

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new UpdateException(UpdateErrorKind.TooLarge, "The response is larger than allowed.");
                await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Invoke(total, length);
            }
            return;
        }
    }

    /// <summary>https, no user info, default port, and an allow-listed host. Throws <see cref="UpdateErrorKind.Untrusted"/> otherwise.</summary>
    public static void CheckUrl(Uri url, IReadOnlySet<string> allowedHosts)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            throw new UpdateException(UpdateErrorKind.Untrusted, "Only HTTPS addresses are allowed.");
        if (!string.IsNullOrEmpty(url.UserInfo) || !url.IsDefaultPort)
            throw new UpdateException(UpdateErrorKind.Untrusted, "Credentials and custom ports are not allowed.");
        if (!allowedHosts.Contains(url.IdnHost))
            throw new UpdateException(UpdateErrorKind.Untrusted, $"The host {url.IdnHost} is not allowed.");
    }

    private static bool IsRateLimited(HttpResponseMessage response, out DateTimeOffset? retryAfter)
    {
        retryAfter = null;
        var limited = (int)response.StatusCode == 429;
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0") limited = true;
        if (response.Headers.RetryAfter is { } ra)
        {
            limited = true;
            retryAfter = ra.Date ?? (ra.Delta is { } d ? DateTimeOffset.UtcNow + d : null);
        }
        if (retryAfter is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var epoch) && epoch > 0 && epoch < 4_000_000_000)
            retryAfter = DateTimeOffset.FromUnixTimeSeconds(epoch);
        return limited;
    }

    public void Dispose() => _client.Dispose();
}
