using NewsDesk.Core.Model;

namespace NewsDesk.Core.Collection;

/// <summary>An entry as supplied by a source, before normalization.</summary>
public sealed record RawItem
{
    public string? PublisherId { get; init; }
    public required string Title { get; init; }
    public string? Link { get; init; }
    public string? SummaryHtml { get; init; }
    public string? ContentHtml { get; init; }
    public DateTimeOffset? Published { get; init; }
    public DateTimeOffset? Updated { get; init; }
    /// <summary>Structured fields from a structured source (JSON), kept verbatim as evidence. Null for ordinary feeds.</summary>
    public string? StructuredJson { get; init; }
    /// <summary>True when the source supplies the complete record (structured data), regardless of text length.</summary>
    public bool IsCompleteRecord { get; init; }
}

public enum CollectOutcome { Success, NotModified, RateLimited, AccessDenied, NotFound, Failed }

public sealed record CollectResult
{
    public required CollectOutcome Outcome { get; init; }
    public IReadOnlyList<RawItem> Items { get; init; } = [];
    public string? FeedTitle { get; init; }
    public string? SiteUrl { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool Capped { get; init; }
}

/// <summary>Collects new items from one source. Implementations must honour cancellation and never throw for ordinary network failures.</summary>
public interface ISourceCollector
{
    bool CanHandle(Source source);
    Task<CollectResult> CollectAsync(Source source, CancellationToken cancellationToken);
}
