using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace NewsDesk.Connectors.Http;

public sealed class BlockedAddressException(string message) : HttpRequestException(message);

public sealed record FetchResponse(
    HttpStatusCode Status,
    Uri FinalUri,
    byte[] Body,
    string? ContentType,
    string? ETag,
    string? LastModified,
    TimeSpan? RetryAfter,
    bool Truncated);

/// <summary>
/// HTTP client for untrusted public URLs. Every connection (including each redirect hop) re-resolves DNS and refuses
/// non-public addresses. Responses are capped in size after decompression.
/// </summary>
/// <summary>Fetches untrusted public URLs. Implemented by <see cref="SafeHttp"/>; tests supply fakes.</summary>
public interface IFetcher
{
    Task<FetchResponse> GetAsync(Uri uri, string? etag, string? lastModified, CancellationToken ct);
}

public sealed class SafeHttp : IFetcher, IDisposable
{
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly long _maxBytes;

    public SafeHttp(TimeSpan? timeout = null, long maxBytes = DefaultMaxBytes, bool allowPrivateAddresses = false)
    {
        _maxBytes = maxBytes;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            // A proxy would make the connect callback see the proxy address instead of the target, defeating the guard.
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var allowed = allowPrivateAddresses ? addresses : addresses.Where(AddressGuard.IsPublic).ToArray();
                if (allowed.Length == 0)
                    throw new BlockedAddressException($"Blocked request to non-public address for host '{ctx.DnsEndPoint.Host}'.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PersonalNewsDesk/0.1 (+feed reader)");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, text/html;q=0.8, */*;q=0.5");
    }

    public static bool IsAllowedScheme(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task<FetchResponse> GetAsync(Uri uri, string? etag, string? lastModified, CancellationToken ct)
    {
        if (!IsAllowedScheme(uri)) throw new ArgumentException("Only http and https URLs are supported.", nameof(uri));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);
        if (!string.IsNullOrEmpty(lastModified) && DateTimeOffset.TryParse(lastModified, out var lm))
            request.Headers.IfModifiedSince = lm;

        HttpResponseMessage sent;
        try
        {
            sent = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException blocked)
        {
            throw blocked; // surface the policy decision, not a generic network error
        }
        using var response = sent;
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (!IsAllowedScheme(finalUri)) throw new HttpRequestException("Redirected to a disallowed scheme.");

        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };

        byte[] body = [];
        var truncated = false;
        if (response.StatusCode != HttpStatusCode.NotModified)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _maxBytes) { truncated = true; break; }
                buffer.Write(chunk, 0, read);
            }
            body = buffer.ToArray();
        }

        return new FetchResponse(
            response.StatusCode, finalUri, body,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToString("R"),
            retryAfter, truncated);
    }

    public void Dispose() => _client.Dispose();
}
