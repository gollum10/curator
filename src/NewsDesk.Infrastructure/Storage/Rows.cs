using NewsDesk.Core.Model;

namespace NewsDesk.Infrastructure.Storage;

public enum ItemView { Inbox, AllMatches, Kept, Dismissed }

/// <summary>An article as seen within one project, for lists and the reading pane.</summary>
public sealed record ItemRow
{
    public required Guid ArticleId { get; init; }
    public required Guid RevisionId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string SourceTitle { get; init; }
    public DateTimeOffset? PublishedUtc { get; init; }
    public DateTimeOffset? UpdatedUtc { get; init; }
    public DateTimeOffset DiscoveredUtc { get; init; }
    public DateTimeOffset CapturedUtc { get; init; }
    public CaptureLevel CaptureLevel { get; init; }
    public string Excerpt { get; init; } = "";
    public string Text { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public bool IsRead { get; init; }
    public bool IsKept { get; init; }
    public bool IsDismissed { get; init; }
    public bool InInbox { get; init; }
    public string MatchReason { get; init; } = "";
    public int Score { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Notes { get; init; } = "";
    public IReadOnlyList<ExportHighlight> Highlights { get; init; } = [];

    /// <summary>The text highlights are anchored to: full text if captured, else the excerpt.</summary>
    public string AnchorText => Text.Length > 0 ? Text : Excerpt;
}

public sealed record SourceOutcome(Guid SourceId, string SourceTitle, string Outcome, int NewItems, int UpdatedItems, string? Error,
    DateTimeOffset? LastSuccessUtc, IReadOnlyList<string> Warnings);

public sealed record RunRecord
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string RunKey { get; init; }
    public required Guid RuleVersionId { get; init; }
    public required string Trigger { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public RunStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<SourceOutcome> SourceOutcomes { get; init; } = [];
}

public sealed record BriefingRow(int Rank, ItemRow Lead, IReadOnlyList<ItemRow> Supporting, string? GroupKey, int Score, string Reason, string ComponentsJson);

public sealed record RetentionStats(int ItemsRemoved, int ArticlesRemoved, int RunsRemoved, int TombstonesPruned)
{
    public string Describe() => ItemsRemoved + ArticlesRemoved + RunsRemoved == 0
        ? "Nothing needed cleaning up."
        : $"Removed {ItemsRemoved} old unreviewed item(s), {ArticlesRemoved} stored article(s), and {RunsRemoved} old briefing(s).";
}

public sealed record StorageStats(int Projects, int Sources, int Articles, int KeptItems, int Collections, int Notes, long DatabaseBytes);

/// <summary>A saved highlight. <see cref="CurrentStart"/> is null when the quote no longer occurs in the current text.</summary>
public sealed record HighlightRow(Guid Id, Guid RevisionId, string Quote, int OriginalStart, string Comment, DateTimeOffset CreatedUtc, int? CurrentStart)
{
    public bool IsStale => CurrentStart is null;
}

/// <summary>Highlight as carried into exports.</summary>
public sealed record ExportHighlight(string Quote, string Comment);

public sealed record CollectionRow(Guid Id, Guid ProjectId, string Name, int Count, DateTimeOffset CreatedUtc);

public sealed record ProjectStats(int Unread, int Inbox, int Kept, int Sources, int SourceWarnings);

public sealed record IngestStats(int New, int Updated, int Unchanged, int Skipped);
