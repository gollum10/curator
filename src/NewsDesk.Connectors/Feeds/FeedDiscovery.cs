using System.Text;
using System.Text.RegularExpressions;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Text;

namespace NewsDesk.Connectors.Feeds;

public sealed record DiscoveredFeed(string FeedUrl, string? Title, string? SiteUrl, IReadOnlyList<RawItem> Preview);

public sealed record DiscoveryResult(IReadOnlyList<DiscoveredFeed> Feeds, string Message);

/// <summary>
/// Turns a user-entered URL into feeds the app can actually collect. Reports what was found; finding a feed does not
/// imply full article text is available.
/// </summary>
public sealed partial class FeedDiscovery(SafeHttp http)
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

        var message = feeds.Count switch
        {
            0 => $"No RSS or Atom feed found for {uri.Host}. Website extraction is a planned expansion feature.",
            1 => "Found 1 feed.",
            _ => $"Found {feeds.Count} feeds; choose which to follow.",
        };
        return new(feeds, message);
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
