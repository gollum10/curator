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

public sealed record SourceOutcomeRow(string Source, string Outcome, string Detail, bool IsProblem);
