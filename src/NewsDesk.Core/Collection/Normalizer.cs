using NewsDesk.Core.Model;
using NewsDesk.Core.Text;

namespace NewsDesk.Core.Collection;

/// <summary>Canonical metadata and content for one source entry, ready for storage.</summary>
public sealed record NormalizedItem
{
    public string? PublisherId { get; init; }
    public required string Url { get; init; }
    public required string CanonicalUrl { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset? PublishedUtc { get; init; }
    public DateTimeOffset? UpdatedUtc { get; init; }
    public required string ContentHash { get; init; }
    public CaptureLevel CaptureLevel { get; init; }
    public string Excerpt { get; init; } = "";
    public string Text { get; init; } = "";
    public string? StructuredJson { get; init; }
}

public static class Normalizer
{
    private const int FullTextThreshold = 600;

    /// <summary>Returns null when the entry has no usable http(s) link.</summary>
    public static NormalizedItem? Normalize(RawItem raw)
    {
        if (raw.Link is null || !Uri.TryCreate(raw.Link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;

        var summary = TextUtil.HtmlToText(raw.SummaryHtml);
        var content = TextUtil.HtmlToText(raw.ContentHtml);
        var text = content.Length > summary.Length ? content : summary;
        var level = text.Length == 0 ? CaptureLevel.LinkOnly
            : raw.IsCompleteRecord || (content.Length >= FullTextThreshold && content.Length >= summary.Length) ? CaptureLevel.FullText
            : CaptureLevel.Excerpt;

        return new NormalizedItem
        {
            PublisherId = raw.PublisherId,
            Url = uri.ToString(),
            CanonicalUrl = UrlNormalizer.Canonicalize(uri),
            Title = raw.Title,
            PublishedUtc = raw.Published?.ToUniversalTime(),
            UpdatedUtc = raw.Updated?.ToUniversalTime(),
            ContentHash = TextUtil.ContentHash(raw.Title, text),
            CaptureLevel = level,
            Excerpt = TextUtil.Excerpt(summary.Length > 0 ? summary : content),
            Text = text,
            StructuredJson = raw.StructuredJson,
        };
    }
}
