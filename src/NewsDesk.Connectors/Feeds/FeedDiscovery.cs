using System.Text;
using System.Text.RegularExpressions;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Text;

namespace NewsDesk.Connectors.Feeds;

public sealed record DiscoveredFeed(string FeedUrl, string? Title, string? SiteUrl, IReadOnlyList<RawItem> Preview, string Connector = "feed");

public sealed record DiscoveryResult(IReadOnlyList<DiscoveredFeed> Feeds, string Message)
{
    /// <summary>When no feed exists: link patterns that could be followed with website extraction, on the page that was checked.</summary>
    public IReadOnlyList<Extraction.LinkPattern> ExtractionSuggestions { get; init; } = [];
    public string? ExtractionPage { get; init; }
}

/// <summary>
/// Turns a user-entered URL into feeds the app can actually collect. Reports what was found; finding a feed does not
/// imply full article text is available.
/// </summary>
public sealed partial class FeedDiscovery(IFetcher http, RobotsCache? robots = null)
{
    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"\b(rel|type|href|title)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Attr();

    private static readonly string[] CommonPaths = ["/feed", "/rss", "/feed.xml", "/rss.xml", "/atom.xml", "/index.xml"];

    public async Task<DiscoveryResult> DiscoverAsync(string input, CancellationToken ct)
    {
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input.Trim();
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || !SafeHttp.IsAllowedScheme(uri))
            return new([], "Enter an http or https website or feed address.");

        // CISA's exploited-vulnerability catalog is structured data with its own connector.
        if (Kev.KevCollector.IsKevUrl(uri))
        {
            try
            {
                var kev = await http.GetAsync(new Uri(Kev.KevCollector.CatalogUrl), null, null, ct);
                if ((int)kev.Status < 400 && !kev.Truncated)
                {
                    var catalog = Kev.KevCollector.Parse(kev.Body, DateTimeOffset.UtcNow);
                    var feed = new DiscoveredFeed(Kev.KevCollector.CatalogUrl, "CISA Known Exploited Vulnerabilities", Kev.KevCollector.CatalogPage,
                        catalog.Items.Take(10).ToList(), Kev.KevCollector.ConnectorName);
                    return new([feed], $"CISA KEV catalog (structured data, version {catalog.Version}): exploited vulnerabilities with patch deadlines. " +
                                       "Used as confirmed exploitation evidence.");
                }
                return new([], $"The CISA KEV catalog returned HTTP {(int)kev.Status}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                return new([], "Could not read the CISA KEV catalog: " + ex.Message);
            }
        }

        FetchResponse page;
        try { page = await http.GetAsync(uri, null, null, ct); }
        catch (BlockedAddressException ex) { return new([], ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { return new([], $"Could not reach {uri.Host}: {ex.Message}"); }

        if ((int)page.Status >= 400) return new([], $"{uri.Host} returned HTTP {(int)page.Status}.");

        // The URL itself may be a feed.
        if (TryParse(page.Body, page.FinalUri) is { } direct)
            return new([direct], $"This address is a feed with {direct.Preview.Count} recent entries.");

        var html = Encoding.UTF8.GetString(page.Body);
        var candidates = new List<(Uri Url, string? Title)>();
        foreach (Match tag in LinkTag().Matches(html))
        {
            var attrs = Attr().Matches(tag.Value).ToDictionary(
                m => m.Groups[1].Value.ToLowerInvariant(),
                m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value),
                StringComparer.OrdinalIgnoreCase);
            if (!attrs.TryGetValue("rel", out var rel) || !rel.Split(' ').Contains("alternate", StringComparer.OrdinalIgnoreCase)) continue;
            if (!attrs.TryGetValue("type", out var type) || !(type.Contains("rss", StringComparison.OrdinalIgnoreCase) || type.Contains("atom", StringComparison.OrdinalIgnoreCase))) continue;
            if (UrlNormalizer.Resolve(attrs.GetValueOrDefault("href"), page.FinalUri) is { } href && candidates.All(c => c.Url != href))
                candidates.Add((href, attrs.GetValueOrDefault("title")));
        }

        var guessed = false;
        if (candidates.Count == 0)
        {
            guessed = true;
            var root = new Uri(page.FinalUri.GetLeftPart(UriPartial.Authority));
            candidates.AddRange(CommonPaths.Select(p => (new Uri(root, p), (string?)null)));
        }

        var feeds = new List<DiscoveredFeed>();
        foreach (var (url, title) in candidates.Take(8))
        {
            try
            {
                var r = await http.GetAsync(url, null, null, ct);
                if ((int)r.Status < 400 && TryParse(r.Body, r.FinalUri) is { } f && feeds.All(x => x.FeedUrl != f.FeedUrl))
                    feeds.Add(f with { Title = f.Title ?? title });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
            if (guessed && feeds.Count > 0) break;
        }

        if (feeds.Count > 0)
            return new(feeds, feeds.Count == 1 ? "Found 1 feed." : $"Found {feeds.Count} feeds; choose which to follow.");

        // No feed: offer website extraction, but only where robots.txt allows automated reading.
        if (robots is not null && !await robots.IsAllowedAsync(page.FinalUri, ct))
            return new([], $"No RSS or Atom feed found for {uri.Host}, and its robots.txt does not allow automated reading, so website extraction isn't available.");
        var suggestions = Extraction.PageExtractor.SuggestPatterns(html, page.FinalUri);
        return new([], suggestions.Count > 0
            ? $"No RSS or Atom feed found for {uri.Host}. You can follow it with website extraction instead: choose which links to follow below."
            : $"No RSS or Atom feed found for {uri.Host}, and no list of article links was recognized on this page. Try the address of a page that lists articles.")
        {
            ExtractionSuggestions = suggestions,
            ExtractionPage = page.FinalUri.ToString(),
        };
    }

    private static DiscoveredFeed? TryParse(byte[] body, Uri uri)
    {
        try
        {
            var parsed = FeedParser.Parse(body, uri);
            return new DiscoveredFeed(uri.ToString(), parsed.Title, parsed.SiteUrl, parsed.Items.Take(10).ToList());
        }
        catch (FeedFormatException) { return null; }
    }
}
