using System.Net;
using System.Text;

namespace EasyMouse.Remotes.Execution;

/// <summary>
/// Sends a <see cref="ResolvedHttp"/>. No redirects (a 3xx is a failure, so an approved host cannot bounce the
/// request, and its secrets, to another host), no proxy, no cookies, a hard timeout. Messages never contain the URL.
/// </summary>
public sealed class HttpClientExecutor : IActionExecutor<ResolvedHttp>, IDisposable
{
    private readonly HttpClient _client;

    public HttpClientExecutor()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task ExecuteAsync(ResolvedHttp action, ActionContext context, CancellationToken cancellationToken)
    {
        if (action.Url.Scheme is not ("http" or "https")) throw new ActionFailedException("denied", "scheme not allowed");
        using var request = new HttpRequestMessage(new HttpMethod(action.Method), action.Url);
        foreach (var (name, value) in action.Headers)
            if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new ActionFailedException("failed", $"header {name} not accepted");
        if (action.Body is not null) request.Content = new StringContent(action.Body, Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(action.Timeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new ActionFailedException("failed", "redirect refused");
            if (status is < 200 or >= 300) throw new ActionFailedException("failed", $"http status {status}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ActionFailedException("failed", "timeout");
        }
        catch (HttpRequestException ex)
        {
            // HttpRequestException messages can contain the host but never the query; keep only the error kind.
            throw new ActionFailedException("failed", $"request failed ({ex.HttpRequestError})");
        }
    }

    public void Dispose() => _client.Dispose();
}
