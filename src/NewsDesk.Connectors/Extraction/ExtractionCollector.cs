using System.Net;
using System.Text;
using System.Text.Json;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Model;

namespace NewsDesk.Connectors.Extraction;

/// <summary>
/// Website extraction for sites without feeds. Reads the list page, keeps links matching the rule, and fetches only
/// articles not seen before (a few per run, one at a time, with a pause between requests). robots.txt is obeyed for
/// every request. Plain HTTP only — no scripts are run.
/// </summary>
public sealed class ExtractionCollector(IFetcher http, RobotsCache robots, TimeSpan? politeDelay = null) : IIncrementalCollector
{
    public const string ConnectorName = "extract";
    private readonly TimeSpan _delay = politeDelay ?? TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool CanHandle(Source source) => source.Connector == ConnectorName;

    public static string SerializeTemplate(ExtractionTemplate t) => JsonSerializer.Serialize(t, Json);

    public static ExtractionTemplate? ReadTemplate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ExtractionTemplate>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Unique source address for a rule, so one page can host several rules (e.g. reviews and press releases).</summary>
    public static string SourceKey(ExtractionTemplate t) => $"{t.ListUrl}#extract={Uri.EscapeDataString(t.LinkPrefix)}";

    public Task<CollectResult> CollectAsync(Source source, CancellationToken ct) =>
        CollectAsync(source, new HashSet<string>(), ct);

    public async Task<CollectResult> CollectAsync(Source source, IReadOnlySet<string> known, CancellationToken ct)
    {
        if (ReadTemplate(source.ConfigJson) is not { } template || !Uri.TryCreate(template.ListUrl, UriKind.Absolute, out var listUri))
            return new CollectResult { Outcome = CollectOutcome.Failed, Error = "This source has no valid extraction rule." };

        if (!await robots.IsAllowedAsync(listUri, ct))
            return new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = $"{listUri.Host}'s robots.txt does not allow automated reading of this page." };

        FetchResponse page;
        try { page = await http.GetAsync(listUri, null, null, ct); }
        catch (BlockedAddressException ex) { return new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = ex.Message }; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new CollectResult { Outcome = CollectOutcome.Failed, Error = "Timed out." }; }
        catch (HttpRequestException ex) { return new CollectResult { Outcome = CollectOutcome.Failed, Error = ex.Message }; }

        if (Status(page) is { } failure) return failure;

        var links = PageExtractor.ExtractLinks(Encoding.UTF8.GetString(page.Body), page.FinalUri, template.LinkPrefix);
        var warnings = new List<string>();
        if (links.Count == 0)
            return new CollectResult { Outcome = CollectOutcome.Success, ListedCount = 0, Warnings = ["The extraction rule found no matching links; the page layout may have changed."] };

        static string Id(ListedLink l) => "pid:" + l.CanonicalUrl;
        var fresh = links.Where(l => !known.Contains(Id(l))).ToList();
        var baseline = new List<string>();
        if (known.Count == 0 && fresh.Count > template.FirstRunLimit)
        {
            baseline = fresh.Skip(template.FirstRunLimit).Select(Id).ToList();
            fresh = fresh.Take(template.FirstRunLimit).ToList();
            warnings.Add($"First run: collected the newest {fresh.Count} items; {baseline.Count} older items on the page were marked as already seen.");
        }

        var batch = fresh.Take(Math.Max(1, template.MaxNewPerRun)).ToList();
        if (fresh.Count > batch.Count) warnings.Add($"{fresh.Count - batch.Count} more new items will be collected on the next run.");

        var items = new List<RawItem>();
        int undated = 0, failed = 0, disallowed = 0;
        foreach (var link in batch)
        {
            var uri = new Uri(link.Url);
            if (!await robots.IsAllowedAsync(uri, ct)) { disallowed++; continue; }
            if (items.Count + failed > 0) await Task.Delay(_delay, ct);

            ArticleMetadata? meta = null;
            try
            {
                var article = await http.GetAsync(uri, null, null, ct);
                if ((int)article.Status is >= 200 and < 300 && !article.Truncated)
                    meta = PageExtractor.ReadMetadata(Encoding.UTF8.GetString(article.Body), template.DateSelector);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }

            // An article we could not read is retried next run rather than stored half-empty.
            if (meta is null) { failed++; continue; }
            // Prefer the page's declared headline, then the link text on the list page, then the cleaned <title>.
            var title = meta.Title ?? (link.AnchorText.Length >= 8 ? link.AnchorText : null) ?? meta.PageTitle ?? link.AnchorText;
            if (string.IsNullOrWhiteSpace(title)) { failed++; continue; }
            if (meta.Published is null && meta.Updated is null) undated++;
            items.Add(new RawItem
            {
                PublisherId = link.CanonicalUrl,
                Title = title.Trim(),
                Link = link.Url,
                SummaryHtml = meta.Description is null ? null : WebUtility.HtmlEncode(meta.Description),
                Published = meta.Published,
                Updated = meta.Updated,
            });
        }

        if (failed > 0) warnings.Add($"{failed} article page(s) could not be read and will be retried next run.");
        if (disallowed > 0) warnings.Add($"{disallowed} article page(s) are disallowed by robots.txt and were skipped.");
        if (undated > 0) warnings.Add($"{undated} article(s) have no machine-readable publication date.");

        return new CollectResult
        {
            Outcome = CollectOutcome.Success,
            Items = items,
            ListedCount = links.Count,
            BaselineIdentities = baseline,
            Warnings = warnings,
            FeedTitle = null,
            SiteUrl = listUri.GetLeftPart(UriPartial.Authority),
        };
    }

    private static CollectResult? Status(FetchResponse r) => r.Status switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable =>
            new CollectResult { Outcome = CollectOutcome.RateLimited, RetryAfter = r.RetryAfter, Error = $"HTTP {(int)r.Status}" },
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = $"HTTP {(int)r.Status}: access denied" },
        HttpStatusCode.NotFound or HttpStatusCode.Gone =>
            new CollectResult { Outcome = CollectOutcome.NotFound, Error = $"HTTP {(int)r.Status}: page not found" },
        _ when (int)r.Status >= 400 => new CollectResult { Outcome = CollectOutcome.Failed, Error = $"HTTP {(int)r.Status}" },
        _ when r.Truncated => new CollectResult { Outcome = CollectOutcome.Failed, Error = "Page exceeds the size limit." },
        _ => null,
    };
}
