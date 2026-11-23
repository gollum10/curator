namespace NewsDesk.Core.Text;

public static class UrlNormalizer
{
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "mc_cid", "mc_eid", "yclid", "_hsenc", "_hsmi", "igshid", "ref_src",
    };

    /// <summary>Resolves a possibly relative link against a base URL. Returns null for anything other than http(s).</summary>
    public static Uri? Resolve(string? link, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        link = link.Trim();
        if (Uri.TryCreate(link, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" && !link.StartsWith('/'))
            return abs;
        if (baseUri is null) return null;
        // A relative reference must not carry its own scheme (javascript:, file:, data: ...).
        var colon = link.IndexOf(':');
        var delimiter = link.IndexOfAny(['/', '?', '#']);
        if (colon >= 0 && (delimiter < 0 || colon < delimiter)) return null;
        return Uri.TryCreate(baseUri, link, out var rel) && rel.Scheme is "http" or "https" ? rel : null;
    }

    /// <summary>
    /// Canonical form used for exact duplicate detection: lower-case scheme and host, no default port, no fragment,
    /// tracking parameters (utm_* and known click IDs) removed. Meaningful query parameters are preserved in order.
    /// </summary>
    public static string Canonicalize(Uri uri)
    {
        var b = new UriBuilder(uri) { Fragment = "", Host = uri.Host.ToLowerInvariant() };
        if (uri.IsDefaultPort) b.Port = -1;
        var query = uri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    var key = Uri.UnescapeDataString(p.Split('=', 2)[0]);
                    return !key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !TrackingParams.Contains(key);
                });
            b.Query = string.Join('&', kept);
        }
        var s = b.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return s;
    }
}
