namespace NewsDesk.Core.Model;

public enum MatchMode { Any, All }

public enum CurationMode { Articles, Stories }

public enum ScheduleKind { Manual, Daily, Weekly, Interval }

public enum SourceHealth { Unknown, Healthy, Delayed, AuthenticationRequired, Blocked, ExtractionBroken, Disabled }

public enum CaptureLevel { LinkOnly, Excerpt, FullText }

public enum RunStatus { Running, Complete, Partial, Failed }

/// <summary>
/// Immutable matching and curation rules. Stored as JSON in a RuleVersion row; every run references the version it used.
/// Each include term may list aliases separated by '|', e.g. "Arcade1Up | A1Up". Terms containing spaces match as exact phrases.
/// </summary>
public sealed record ProjectRules
{
    public IReadOnlyList<string> IncludeTerms { get; init; } = [];
    public MatchMode IncludeMode { get; init; } = MatchMode.Any;
    public IReadOnlyList<string> ExcludeTerms { get; init; } = [];
    public int WindowHours { get; init; } = 24;
    public int TopN { get; init; } = 5;
    public CurationMode Mode { get; init; } = CurationMode.Articles;
    public int ScoreFloor { get; init; } = 50;
    /// <summary>When true, items already delivered in an earlier briefing may appear again even if unchanged.</summary>
    public bool IncludePreviouslySeen { get; init; }
}

public sealed record Schedule
{
    public ScheduleKind Kind { get; init; } = ScheduleKind.Manual;
    /// <summary>Local time of day (project timezone) for Daily and Weekly runs.</summary>
    public TimeOnly TimeOfDay { get; init; } = new(7, 0);
    /// <summary>Days for Weekly runs. Empty means every day.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];
    /// <summary>Minutes between Interval runs. Clamped to at least <see cref="Schedule.MinimumIntervalMinutes"/>.</summary>
    public int IntervalMinutes { get; init; } = 60;

    public const int MinimumIntervalMinutes = 15;
}

public sealed class Project
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool Enabled { get; set; } = true;
    public Schedule Schedule { get; set; } = new();
    public int RetentionDays { get; set; } = 30;
    public Guid RuleVersionId { get; set; }
    public ProjectRules Rules { get; set; } = new();
    public DateTimeOffset? NextDueUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
}

public sealed class Source
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FeedUrl { get; set; }
    public string? SiteUrl { get; set; }
    public string Title { get; set; } = "";
    public string Connector { get; set; } = "feed";
    public SourceHealth Health { get; set; } = SourceHealth.Unknown;
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextRetryUtc { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
}

/// <summary>Project-specific settings for a subscribed source.</summary>
public sealed record ProjectSource(Guid ProjectId, Guid SourceId, int Priority = 50, bool IsPrimary = false);

public sealed class Article
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public string? PublisherId { get; init; }
    public required string Url { get; init; }
    public required string CanonicalUrl { get; init; }
    public required string Title { get; set; }
    public DateTimeOffset? PublishedUtc { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public DateTimeOffset DiscoveredUtc { get; init; }
    public Guid CurrentRevisionId { get; set; }
}

public sealed class ArticleRevision
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ArticleId { get; init; }
    public required string ContentHash { get; init; }
    public CaptureLevel CaptureLevel { get; init; }
    public string Excerpt { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTimeOffset CapturedUtc { get; init; }
    /// <summary>Structured source fields (JSON) when the source is structured, e.g. a CISA KEV record.</summary>
    public string? StructuredJson { get; init; }
}

/// <summary>Per-project decisions about an article. Project ID + Article ID is unique.</summary>
public sealed class ProjectItem
{
    public Guid ProjectId { get; init; }
    public Guid ArticleId { get; init; }
    public bool IsRead { get; set; }
    public bool IsKept { get; set; }
    public bool IsDismissed { get; set; }
    /// <summary>Content hash at dismissal; a different hash later is a material update.</summary>
    public string? DismissedHash { get; set; }
    public bool InInbox { get; set; }
    /// <summary>Content hash last delivered in a briefing; used to suppress unchanged repeats.</summary>
    public string? LastDeliveredHash { get; set; }
    public string MatchReason { get; set; } = "";
    public int Score { get; set; }
    public DateTimeOffset FirstMatchedUtc { get; init; }
}
