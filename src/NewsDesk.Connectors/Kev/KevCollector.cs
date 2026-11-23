using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Model;

namespace NewsDesk.Connectors.Kev;

/// <summary>
/// CISA Known Exploited Vulnerabilities catalog (structured JSON). Each catalog entry becomes one item whose publisher ID
/// is the CVE ID and whose structured fields are kept verbatim with the catalog version and retrieval time.
/// </summary>
public sealed class KevCollector(SafeHttp http, int maxItemsPerRun = 200) : ISourceCollector
{
    public const string ConnectorName = "cisa-kev";
    public const string CatalogUrl = "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json";
    public const string CatalogPage = "https://www.cisa.gov/known-exploited-vulnerabilities-catalog";

    public bool CanHandle(Source source) => source.Connector == ConnectorName;

    public static bool IsKevUrl(Uri uri) =>
        uri.Host.EndsWith("cisa.gov", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath.Contains("known_exploited_vulnerabilities", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.Contains("known-exploited-vulnerabilities", StringComparison.OrdinalIgnoreCase));

    public async Task<CollectResult> CollectAsync(Source source, CancellationToken ct)
    {
        FetchResponse r;
        try
        {
            r = await http.GetAsync(new Uri(source.FeedUrl), source.ETag, source.LastModified, ct);
        }
        catch (BlockedAddressException ex) { return new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = ex.Message }; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new CollectResult { Outcome = CollectOutcome.Failed, Error = "Timed out." }; }
        catch (HttpRequestException ex) { return new CollectResult { Outcome = CollectOutcome.Failed, Error = ex.Message }; }

        switch (r.Status)
        {
            case HttpStatusCode.NotModified:
                return new CollectResult { Outcome = CollectOutcome.NotModified, ETag = source.ETag, LastModified = source.LastModified };
            case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                return new CollectResult { Outcome = CollectOutcome.RateLimited, RetryAfter = r.RetryAfter, Error = $"HTTP {(int)r.Status}" };
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = $"HTTP {(int)r.Status}: access denied" };
            case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                return new CollectResult { Outcome = CollectOutcome.NotFound, Error = $"HTTP {(int)r.Status}: catalog not found" };
        }
        if ((int)r.Status >= 400) return new CollectResult { Outcome = CollectOutcome.Failed, Error = $"HTTP {(int)r.Status}" };
        if (r.Truncated) return new CollectResult { Outcome = CollectOutcome.Failed, Error = "Catalog exceeds the size limit." };

        KevCatalog parsed;
        try { parsed = Parse(r.Body, DateTimeOffset.UtcNow); }
        catch (JsonException ex) { return new CollectResult { Outcome = CollectOutcome.Failed, Error = "Unreadable KEV catalog: " + ex.Message }; }

        var warnings = new List<string>();
        if (parsed.Items.Count == 0) warnings.Add("The KEV catalog contained no entries; the format may have changed.");
        var capped = parsed.Items.Count > maxItemsPerRun;
        return new CollectResult
        {
            Outcome = CollectOutcome.Success,
            Items = parsed.Items.Take(maxItemsPerRun).ToList(), // newest additions first
            FeedTitle = "CISA Known Exploited Vulnerabilities",
            SiteUrl = CatalogPage,
            ETag = r.ETag,
            LastModified = r.LastModified,
            Warnings = warnings,
            Capped = capped,
        };
    }

    public sealed record KevCatalog(string? Version, IReadOnlyList<RawItem> Items);

    /// <summary>Parses the catalog; items are sorted newest first by date added.</summary>
    public static KevCatalog Parse(byte[] json, DateTimeOffset retrievedUtc)
    {
        var doc = JsonSerializer.Deserialize<CatalogJson>(json, JsonOptions) ?? throw new JsonException("Empty catalog.");
        var items = (doc.Vulnerabilities ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v.CveId) && v.CveId!.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            .Select(v => ToItem(v, doc.CatalogVersion, retrievedUtc))
            .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue).ThenBy(i => i.PublisherId, StringComparer.Ordinal)
            .ToList();
        return new KevCatalog(doc.CatalogVersion, items);
    }

    private static RawItem ToItem(VulnJson v, string? version, DateTimeOffset retrievedUtc)
    {
        var cve = v.CveId!.Trim().ToUpperInvariant();
        var added = ParseDate(v.DateAdded);
        var lines = new List<string>
        {
            $"{cve} — {v.VendorProject} {v.Product}".Trim(),
            v.ShortDescription ?? "",
            $"Required action: {v.RequiredAction}",
            $"Federal patch deadline (BOD 22-01): {v.DueDate}",
            $"Known ransomware campaign use: {v.KnownRansomwareCampaignUse ?? "Unknown"}",
            $"Added to the KEV catalog: {v.DateAdded} (catalog version {version})",
        };
        if (!string.IsNullOrWhiteSpace(v.Notes)) lines.Add($"Notes: {v.Notes}");
        var text = string.Join("\n", lines.Where(l => l.Length > 0));

        var evidence = new KevEvidence(
            Source: "CISA Known Exploited Vulnerabilities catalog",
            CatalogUrl: CatalogUrl,
            CatalogVersion: version,
            RetrievedUtc: retrievedUtc,
            CveId: cve,
            VendorProject: v.VendorProject,
            Product: v.Product,
            VulnerabilityName: v.VulnerabilityName,
            DateAdded: v.DateAdded,
            DueDate: v.DueDate,
            RequiredAction: v.RequiredAction,
            KnownRansomwareCampaignUse: v.KnownRansomwareCampaignUse,
            Cwes: v.Cwes ?? []);

        return new RawItem
        {
            PublisherId = cve,
            Title = $"{cve}: {v.VulnerabilityName ?? $"{v.VendorProject} {v.Product}"}".Trim(),
            Link = $"{CatalogPage}?search_api_fulltext={Uri.EscapeDataString(cve)}",
            // Encode so text containing "<" survives HTML-to-text conversion unchanged.
            SummaryHtml = WebUtility.HtmlEncode(text).Replace("\n", "<br>"),
            Published = added,
            StructuredJson = JsonSerializer.Serialize(evidence, JsonOptions),
            IsCompleteRecord = true,
        };
    }

    /// <summary>
    /// KEV dates are calendar dates with no time. They are anchored at 12:00 UTC so the same calendar date shows in local
    /// time from UTC-11 to UTC+11 (midnight UTC would display as the previous evening in the Americas).
    /// </summary>
    private static DateTimeOffset? ParseDate(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? new DateTimeOffset(d.AddHours(12), TimeSpan.Zero) : null;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record CatalogJson(string? Title, string? CatalogVersion, string? DateReleased, int? Count, List<VulnJson>? Vulnerabilities);

    private sealed record VulnJson(
        [property: JsonPropertyName("cveID")] string? CveId,
        string? VendorProject, string? Product, string? VulnerabilityName, string? DateAdded, string? ShortDescription,
        string? RequiredAction, string? DueDate, string? KnownRansomwareCampaignUse, string? Notes, List<string>? Cwes);
}

/// <summary>Verbatim KEV fields plus provenance, stored with each record and used as exploitation evidence.</summary>
public sealed record KevEvidence(
    string Source, string CatalogUrl, string? CatalogVersion, DateTimeOffset RetrievedUtc, string CveId,
    string? VendorProject, string? Product, string? VulnerabilityName, string? DateAdded, string? DueDate,
    string? RequiredAction, string? KnownRansomwareCampaignUse, IReadOnlyList<string> Cwes);
