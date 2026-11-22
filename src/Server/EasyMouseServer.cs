using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EasyMouse.Core.Protocol;
using EasyMouse.Core.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasyMouse.Server;

public sealed record ServerOptions
{
    /// <summary>The LAN interface address to bind. Never <c>0.0.0.0</c> or <c>::</c>.</summary>
    public required IPAddress BindAddress { get; init; }
    /// <summary>0 picks a free port (tests); the app uses a fixed port so the origin and firewall rule stay stable.</summary>
    public int Port { get; init; }
    /// <summary>Names that may appear in the Host header: the PC's own IP and <c>&lt;pcname&gt;.local</c>.</summary>
    public required IReadOnlyList<string> AllowedHosts { get; init; }
    /// <summary>When set, only peers inside <c>BindAddress/PeerPrefixLength</c> are served.</summary>
    public int? PeerPrefixLength { get; init; }
    public string PcName { get; init; } = "Easy Mouse";
    public string Version { get; init; } = "0.1.0";
    /// <summary>The web client; defaults to the assets embedded from <c>web/dist</c> (or a stub page).</summary>
    public StaticAssets? Assets { get; init; }
    public ILoggerProvider? LoggerProvider { get; init; }
}

/// <summary>
/// Kestrel host: <c>GET /api/info</c>, the embedded web client, and <c>GET /ws</c>. It binds to one chosen
/// address only, checks Host/Origin/peer on every request, sends strict security headers and no CORS headers.
/// </summary>
public sealed class EasyMouseServer : IAsyncDisposable
{
    public const int ProtocolVersion = 0;

    private readonly ServerOptions _options;
    private readonly CoreServices _services;
    private readonly RequestPolicy _policy;
    private readonly StaticAssets _assets;
    private WebApplication? _app;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public EasyMouseServer(ServerOptions options, CoreServices services)
    {
        if (IPAddress.Any.Equals(options.BindAddress) || IPAddress.IPv6Any.Equals(options.BindAddress))
            throw new ArgumentException("Binding to all interfaces is not allowed; choose the LAN interface address.", nameof(options));
        if (options.BindAddress.AddressFamily != AddressFamily.InterNetwork && !IPAddress.IsLoopback(options.BindAddress))
            throw new ArgumentException("Only IPv4 is supported in v1.", nameof(options));
        _options = options;
        _services = services;
        _policy = new RequestPolicy(options.AllowedHosts, options.BindAddress, options.PeerPrefixLength);
        _assets = options.Assets ?? StaticAssets.FromAssembly(typeof(EasyMouseServer).Assembly);
    }

    public IPEndPoint? EndPoint { get; private set; }

    /// <summary>Raised for every HTTP request that reaches the listener (before any check) with the peer address. The App uses it to tell whether anything from the LAN ever arrived.</summary>
    public event Action<IPAddress?>? RequestReceived;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (_options.LoggerProvider is not null) builder.Logging.AddProvider(_options.LoggerProvider);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 0;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxRequestLineSize = 4 * 1024;
            k.Listen(_options.BindAddress, _options.Port, o => o.Protocols = HttpProtocols.Http1);
        });
        builder.Services.AddSingleton(_services);

        var app = builder.Build();
        _logger = app.Logger;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Use(Guard);
        app.MapGet("/api/info", Info);
        app.Map("/ws", HandleSocket);
        app.Map("/{**path}", ServeStatic); // all methods and all paths (MapFallback skips paths with an extension)

        await app.StartAsync().ConfigureAwait(false);
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToList();
        if (addresses.Count != 1 || !TryParseHttpAddress(addresses[0], out var endpoint) || !endpoint.Address.Equals(_options.BindAddress))
        {
            // For instance an ASPNETCORE_URLS override: never run on an address we were not asked to bind.
            await app.StopAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Unexpected listening addresses: {string.Join(", ", addresses)}");
        }
        EndPoint = endpoint;
        _policy.Port = endpoint.Port;
    }

    private static bool TryParseHttpAddress(string address, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !IPAddress.TryParse(uri.Host, out var ip)) return false;
        endpoint = new IPEndPoint(ip, uri.Port);
        return true;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _services.Hub.CloseAllAsync(CloseCode.Normal, "server stopping").ConfigureAwait(false);
        await _app.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    // ------------------------------------------------------------------ pipeline

    private async Task Guard(HttpContext ctx, RequestDelegate next)
    {
        RequestReceived?.Invoke(ctx.Connection.RemoteIpAddress);
        var headers = ctx.Response.Headers;
        var host = ctx.Request.Headers.Host.ToString();
        // The Host header is validated below; only then is it echoed into the CSP.
        bool hostOk = _policy.IsHostAllowed(host);
        headers.ContentSecurityPolicy = hostOk
            ? $"default-src 'self'; connect-src 'self' ws://{host.ToLowerInvariant()}; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
            : "default-src 'none'; frame-ancestors 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        if (!_policy.IsPeerAllowed(ctx.Connection.RemoteIpAddress)
            || !hostOk
            || !_policy.IsOriginAllowed(ctx.Request.Headers.Origin.Count == 0 ? null : ctx.Request.Headers.Origin.ToString()))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(ctx).ConfigureAwait(false);
    }

    private Task Info(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        var json = JsonSerializer.Serialize(new { name = _options.PcName, version = _options.Version, proto = ProtocolVersion });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength = bytes.Length;
        return ctx.Response.Body.WriteAsync(bytes).AsTask();
    }

    private async Task ServeStatic(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (!_assets.TryGet(ctx.Request.Path.Value ?? "/", out var content, out var contentType))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.ContentLength = content.Length;
        if (HttpMethods.IsGet(ctx.Request.Method)) await ctx.Response.Body.WriteAsync(content).ConfigureAwait(false);
    }

    private async Task HandleSocket(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var session = new ServerSession(_services, new WebSocketTransport(socket));
        session.Start();
        var limits = _services.Limits;
        var buffer = new byte[limits.MaxFrameBytes + 1];
        try
        {
            // Keep reading after we sent a close frame (state CloseSent) until the peer answers with its own,
            // so the close code is delivered instead of the connection being reset under unread data.
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                int total = 0;
                bool oversize = false;
                ValueWebSocketReceiveResult result;
                do
                {
                    var slice = oversize ? buffer.AsMemory() : buffer.AsMemory(total);
                    result = await socket.ReceiveAsync(slice, ctx.RequestAborted).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (oversize) continue;
                    total += result.Count;
                    if (total > limits.MaxFrameBytes || (total == buffer.Length && !result.EndOfMessage))
                    {
                        oversize = true;
                        await session.CloseAsync(CloseCode.Malformed, "too large").ConfigureAwait(false);
                    }
                } while (!result.EndOfMessage);

                if (oversize || session.IsClosed) continue; // closing: discard until the peer's close frame arrives
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    await session.OnBinaryAsync(buffer.AsMemory(0, total)).ConfigureAwait(false);
                }
                else
                {
                    string text;
                    try { text = new UTF8Encoding(false, true).GetString(buffer, 0, total); }
                    catch (DecoderFallbackException)
                    {
                        await session.CloseAsync(CloseCode.Malformed, "bad utf-8").ConfigureAwait(false);
                        return;
                    }
                    await session.OnTextAsync(text).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Peer went away.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session failed");
            await session.CloseAsync(CloseCode.InternalError, "internal error").ConfigureAwait(false);
        }
        finally
        {
            session.OnDisconnected();
        }
    }
}
