using NewsDesk.Core.Model;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Desktop.ViewModels;

/// <summary>One collection/briefing run in the history list.</summary>
public sealed class RunRowViewModel(RunRecord run)
{
    public RunRecord Run { get; } = run;
    public string When => Run.StartedUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm");
    public string Status => Run.Status.ToString();
    public bool IsProblem => Run.Status is RunStatus.Partial or RunStatus.Failed;
    public bool IsRunning => Run.Status == RunStatus.Running;
    public string Trigger => Run.Trigger;
    public string Duration => Run.CompletedUtc is { } done ? $"{Math.Max(0, (done - Run.StartedUtc).TotalSeconds):0.#}s" : "";
    public string Subline => string.Join(" · ", new[] { Trigger, Duration }.Where(s => s.Length > 0));
}

/// <summary>A saved highlight in the reading pane.</summary>
public sealed class HighlightViewModel(HighlightRow row)
{
    public HighlightRow Row { get; } = row;
    public Guid Id => Row.Id;
    public string Quote => Row.Quote;
    public string Comment => Row.Comment;
    public bool HasComment => Row.Comment.Length > 0;
    public int? CurrentStart => Row.CurrentStart;
    public int Length => Row.Quote.Length;
    public bool IsStale => Row.IsStale;
    public string QuoteLabel => $"“{Row.Quote}”";
    public string Meta => Row.IsStale
        ? $"From an earlier version of this article · saved {Row.CreatedUtc.ToLocalTime():d MMM yyyy}"
        : $"Saved {Row.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}";
}

public sealed record SourceOutcomeRow(string Source, string Outcome, string Detail, bool IsProblem);
