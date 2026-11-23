using System.Net;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Model;

namespace NewsDesk.Connectors.Feeds;

public sealed class FeedCollector(IFetcher http, int maxItemsPerRun = 200, RobotsCache? robots = null) : ISourceCollector
{
    public bool CanHandle(Source source) => source.Connector == "feed";

    public async Task<CollectResult> CollectAsync(Source source, CancellationToken ct)
    {
        if (!Uri.TryCreate(source.FeedUrl, UriKind.Absolute, out var uri) || !SafeHttp.IsAllowedScheme(uri))
            return new CollectResult { Outcome = CollectOutcome.Failed, Error = "Invalid feed URL." };

        FetchResponse r;
        try
        {
            r = await http.GetAsync(uri, source.ETag, source.LastModified, ct);
        }
        catch (BlockedAddressException ex)
        {
            return new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = ex.Message };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CollectResult { Outcome = CollectOutcome.Failed, Error = "Timed out." };
        }
        catch (HttpRequestException ex)
        {
            return new CollectResult { Outcome = CollectOutcome.Failed, Error = ex.Message };
        }

        switch (r.Status)
        {
            case HttpStatusCode.NotModified:
                return new CollectResult { Outcome = CollectOutcome.NotModified, ETag = source.ETag, LastModified = source.LastModified };
            case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                return new CollectResult { Outcome = CollectOutcome.RateLimited, RetryAfter = r.RetryAfter, Error = $"HTTP {(int)r.Status}" };
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = $"HTTP {(int)r.Status}: access denied" };
            case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                return new CollectResult { Outcome = CollectOutcome.NotFound, Error = $"HTTP {(int)r.Status}: feed not found" };
        }
        if ((int)r.Status >= 400)
            return new CollectResult { Outcome = CollectOutcome.Failed, Error = $"HTTP {(int)r.Status}", RetryAfter = r.RetryAfter };
        if (r.Truncated)
            return new CollectResult { Outcome = CollectOutcome.Failed, Error = "Feed exceeds the 10 MB size limit." };

        ParsedFeed feed;
        try { feed = FeedParser.Parse(r.Body, r.FinalUri); }
        catch (FeedFormatException ex)
        {
            return new CollectResult { Outcome = CollectOutcome.Failed, Error = ex.Message };
        }

        var warnings = new List<string>();
        var capped = feed.Items.Count > maxItemsPerRun;
        if (capped) warnings.Add($"Feed listed {feed.Items.Count} entries; only the first {maxItemsPerRun} were collected.");
        var undated = feed.Items.Count(i => i.Published is null && i.Updated is null);
        if (undated > 0) warnings.Add($"{undated} entries have no publication date.");
        // Feeds are published for readers, so this is a notice rather than a block; the user decides whether to keep the source.
        if (robots is not null && !await robots.IsAllowedAsync(uri, ct))
            warnings.Add($"{uri.Host}'s robots.txt asks automated agents not to fetch this address. Consider removing this source if you want to follow that preference.");

        return new CollectResult
        {
            Outcome = CollectOutcome.Success,
            Items = feed.Items.Take(maxItemsPerRun).ToList(),
            FeedTitle = feed.Title,
            SiteUrl = feed.SiteUrl,
            ETag = r.ETag,
            LastModified = r.LastModified,
            Warnings = warnings,
            Capped = capped,
        };
    }
}
