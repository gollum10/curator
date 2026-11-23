using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using NewsDesk.Core.Text;

namespace NewsDesk.Connectors.Extraction;

/// <summary>
/// A website extraction rule: collect article links on <see cref="ListUrl"/> whose path starts with <see cref="LinkPrefix"/>.
/// Dates come from machine-readable page metadata, or from <see cref="DateSelector"/> (CSS) on the article page when set.
/// </summary>
public sealed record ExtractionTemplate
{
    public required string ListUrl { get; init; }
    public required string LinkPrefix { get; init; }
    public string? DateSelector { get; init; }
    /// <summary>New articles fetched per run (each is one polite request). The rest wait for the next run.</summary>
    public int MaxNewPerRun { get; init; } = 10;
    /// <summary>On the first run only the newest this-many items are collected; older ones are recorded as already seen.</summary>
    public int FirstRunLimit { get; init; } = 8;
}

public sealed record ListedLink(string Url, string CanonicalUrl, string AnchorText);

public sealed record LinkPattern(string Prefix, int Count, IReadOnlyList<string> Samples);

/// <param name="Title">Headline from og:title / twitter:title, if the page declares one.</param>
/// <param name="PageTitle">The &lt;title&gt; with a trailing site-name segment removed (see <see cref="PageExtractor.StripSiteSuffix"/>).</param>
public sealed record ArticleMetadata(string? Title, string? Description, DateTimeOffset? Published, DateTimeOffset? Updated, string? PageTitle = null);

public static partial class PageExtractor
{
    private static readonly HtmlParser Parser = new(new HtmlParserOptions { IsScripting = false });

    /// <summary>Same-site links whose path starts with the prefix (but is not the prefix page itself), in page order, de-duplicated.</summary>
    public static IReadOnlyList<ListedLink> ExtractLinks(string html, Uri pageUri, string prefix)
    {
        var doc = Parser.ParseDocument(html);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<ListedLink>();
        var normalizedPrefix = prefix.StartsWith('/') ? prefix : "/" + prefix;
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            if (UrlNormalizer.Resolve(a.GetAttribute("href"), pageUri) is not { } uri) continue;
            if (!SameSite(uri, pageUri)) continue;
            var path = uri.AbsolutePath;
            if (!path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.TrimEnd('/').Equals(normalizedPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
            var canonical = UrlNormalizer.Canonicalize(new UriBuilder(uri) { Query = "", Fragment = "" }.Uri);
            var text = Clean(a.TextContent);
            if (seen.TryGetValue(canonical, out var index))
            {
                // Keep the most descriptive anchor text for the same link (image links often come first with no text).
                if (text.Length > result[index].AnchorText.Length) result[index] = result[index] with { AnchorText = text };
                continue;
            }
            seen[canonical] = result.Count;
            result.Add(new ListedLink(uri.GetLeftPart(UriPartial.Path), canonical, text));
        }
        return result;
    }

    /// <summary>
    /// Suggests link patterns that look like article lists: same-site path prefixes (first segment) with at least
    /// <paramref name="minimum"/> distinct links, most frequent first.
    /// </summary>
    public static IReadOnlyList<LinkPattern> SuggestPatterns(string html, Uri pageUri, int minimum = 5)
    {
        var doc = Parser.ParseDocument(html);
        var byPrefix = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            if (UrlNormalizer.Resolve(a.GetAttribute("href"), pageUri) is not { } uri || !SameSite(uri, pageUri)) continue;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) continue; // an article lives below a section
            var prefix = "/" + segments[0] + "/";
            var dict = byPrefix.TryGetValue(prefix, out var d) ? d : byPrefix[prefix] = new(StringComparer.OrdinalIgnoreCase);
            var key = uri.AbsolutePath;
            var text = Clean(a.TextContent);
            if (!dict.TryGetValue(key, out var existing) || text.Length > existing.Length) dict[key] = text;
        }
        return byPrefix.Where(kv => kv.Value.Count >= minimum)
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new LinkPattern(kv.Key, kv.Value.Count, kv.Value.Values.Where(t => t.Length > 0).Take(3).ToList()))
            .ToList();
    }

    /// <summary>
    /// Reads title, description and dates from an article page. Dates only come from machine-readable sources
    /// (JSON-LD, article/itemprop meta, &lt;time datetime&gt;) or the template's date selector — never guessed from body text.
    /// </summary>
    public static ArticleMetadata ReadMetadata(string html, string? dateSelector = null)
    {
        var doc = Parser.ParseDocument(html);
        string? Meta(params string[] names)
        {
            foreach (var n in names)
            {
                var el = doc.QuerySelector($"meta[property='{n}'], meta[name='{n}'], meta[itemprop='{n}']");
                var v = el?.GetAttribute("content");
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            return null;
        }

        var siteName = Meta("og:site_name", "application-name");
        var declared = Meta("og:title", "twitter:title");
        var title = declared is null ? null : StripSiteSuffix(Clean(declared), siteName);
        var pageTitle = StripSiteSuffix(Clean(doc.Title ?? ""), siteName);
        var description = Meta("og:description", "description", "twitter:description");
        DateTimeOffset? published = ParseDate(Meta("article:published_time", "datePublished", "pubdate", "date"));
        DateTimeOffset? updated = ParseDate(Meta("article:modified_time", "dateModified", "og:updated_time"));

        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            if (published is not null && updated is not null) break;
            try
            {
                using var json = JsonDocument.Parse(script.TextContent);
                foreach (var node in Flatten(json.RootElement))
                {
                    published ??= ParseDate(Str(node, "datePublished") ?? Str(node, "dateCreated"));
                    updated ??= ParseDate(Str(node, "dateModified"));
                }
            }
            catch (JsonException) { }
        }

        published ??= ParseDate(doc.QuerySelector("article time[datetime], main time[datetime]")?.GetAttribute("datetime"));

        if (published is null && !string.IsNullOrWhiteSpace(dateSelector))
        {
            try { published = ParseDate(doc.QuerySelector(dateSelector)?.TextContent); }
            catch (DomException) { } // invalid selector in a template
        }

        return new ArticleMetadata(string.IsNullOrWhiteSpace(title) ? null : title, description, published, updated,
            string.IsNullOrWhiteSpace(pageTitle) ? null : pageTitle);
    }

    [GeneratedRegex(@"\s+\|\s+[^|]{2,40}$")]
    private static partial Regex PipeSuffix();

    /// <summary>
    /// "Headline | Breaking Bourbon" → "Headline". A trailing "| segment" is removed; a dash-separated segment only when it
    /// matches the declared site name, so headlines like "Alpha – Beta Edition" stay intact.
    /// </summary>
    public static string StripSiteSuffix(string title, string? siteName = null)
    {
        var stripped = title;
        if (!string.IsNullOrWhiteSpace(siteName))
        {
            foreach (var sep in new[] { " | ", " - ", " – ", " — ", " :: " })
                if (stripped.EndsWith(sep + siteName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    stripped = stripped[..^(sep.Length + siteName.Trim().Length)];
                    break;
                }
        }
        if (stripped == title) stripped = PipeSuffix().Replace(title, "");
        return stripped.Trim().Length >= 8 ? stripped.Trim() : title;
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) foreach (var y in Flatten(x)) yield return y; }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            yield return e;
            if (e.TryGetProperty("@graph", out var g)) foreach (var y in Flatten(g)) yield return y;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)\.? \d{1,2}, \d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDayYear();

    /// <summary>ISO 8601 or "Month d, yyyy". Date-only values are anchored at 12:00 UTC so the calendar date shows correctly.</summary>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = Clean(value);
        if (MonthDayYear().IsMatch(s))
        {
            var normalized = s.Replace(".", "").Replace("Sept ", "Sep ");
            foreach (var fmt in new[] { "MMMM d, yyyy", "MMM d, yyyy" })
                if (DateTime.TryParseExact(normalized, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return new DateTimeOffset(d.Date.AddHours(12), TimeSpan.Zero);
            return null;
        }
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$") && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            return new DateTimeOffset(dateOnly.AddHours(12), TimeSpan.Zero);
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}T") &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso))
            return iso;
        return null;
    }

    private static bool SameSite(Uri a, Uri b) => string.Equals(StripWww(a.Host), StripWww(b.Host), StringComparison.OrdinalIgnoreCase);

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
