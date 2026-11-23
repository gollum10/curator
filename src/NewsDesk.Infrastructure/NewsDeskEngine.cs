using System.Collections.Concurrent;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Curation;
using NewsDesk.Core.Model;
using NewsDesk.Core.Scheduling;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Infrastructure;

public sealed record RunReport(RunRecord Run, IReadOnlyList<SourceOutcome> Sources, CurationResult? Curation, bool Skipped = false);

/// <summary>
/// Application service for collection and briefing runs. Shared by the desktop UI and the headless worker.
/// </summary>
public sealed class NewsDeskEngine(NewsDeskStore store, IEnumerable<ISourceCollector> collectors, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IReadOnlyList<ISourceCollector> _collectors = collectors.ToList();
    private readonly SemaphoreSlim _global = new(4);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Lazy<Task<SourceOutcome>>> _inFlight = new();

    /// <summary>Automatic runs do not refetch a source more often than this.</summary>
    public static readonly TimeSpan MinimumRefresh = TimeSpan.FromMinutes(Schedule.MinimumIntervalMinutes);

    /// <summary>Even a manual refresh reuses a fetch this recent, so projects sharing a source fetch it once.</summary>
    public static readonly TimeSpan ManualReuseWindow = TimeSpan.FromMinutes(2);

    /// <summary>Runs every enabled project whose schedule is due. Missed slots produce a single catch-up run.</summary>
    public async Task<IReadOnlyList<RunReport>> RunDueAsync(CancellationToken ct, IProgress<string>? progress = null)
    {
        var now = _clock.GetUtcNow();
        var reports = new List<RunReport>();
        foreach (var project in store.GetProjects())
        {
            ct.ThrowIfCancellationRequested();
            if (!project.Enabled || project.Schedule.Kind == ScheduleKind.Manual) continue;
            if (project.NextDueUtc is null)
            {
                project.NextDueUtc = ScheduleCalculator.NextDue(project.Schedule, project.TimeZoneId, now);
                store.UpdateProject(project);
                continue;
            }
            if (project.NextDueUtc > now) continue;

            var due = project.NextDueUtc.Value;
            var late = now - due;
            var trigger = late > TimeSpan.FromMinutes(30) ? $"catch-up (scheduled {due:u}, {FormatSpan(late)} late)" : "scheduled";
            progress?.Report($"{project.Name}: {trigger}");
            reports.Add(await RunProjectAsync(project.Id, trigger, $"{project.Id}:sched:{due:O}", force: false, ct, progress));
        }
        return reports;
    }

    /// <summary>Manual refresh. Forces a fetch of every source except those that denied access or asked us to wait.</summary>
    public Task<RunReport> RefreshProjectAsync(Guid projectId, CancellationToken ct, IProgress<string>? progress = null) =>
        RunProjectAsync(projectId, "manual", $"{projectId}:manual:{_clock.GetUtcNow():O}", force: true, ct, progress);

    private async Task<RunReport> RunProjectAsync(Guid projectId, string trigger, string runKey, bool force, CancellationToken ct, IProgress<string>? progress)
    {
        var project = store.GetProject(projectId) ?? throw new InvalidOperationException("Project not found.");
        var started = _clock.GetUtcNow();
        var run = new RunRecord { Id = Guid.NewGuid(), ProjectId = projectId, RunKey = runKey, RuleVersionId = project.RuleVersionId, Trigger = trigger, StartedUtc = started };
        if (!store.TryStartRun(run))
            return new RunReport(run, [], null, Skipped: true);

        // Advance the schedule from now (not from the missed slot) so there is no backlog of identical runs.
        project.NextDueUtc = ScheduleCalculator.NextDue(project.Schedule, project.TimeZoneId, started);
        project.LastRunUtc = started;
        store.UpdateProject(project);

        var sources = store.GetProjectSources(projectId);
        IReadOnlyList<SourceOutcome> outcomes = [];
        CurationResult? curation = null;
        RunStatus status;
        string summary;
        try
        {
            outcomes = await CollectAsync(sources.Select(s => s.Source).ToList(), force, ct, progress);
            var rules = store.GetRules(project.RuleVersionId);
            var now = _clock.GetUtcNow();
            var candidates = store.GetCandidates(projectId, now.AddHours(-Math.Max(1, rules.WindowHours)));
            curation = Curator.Curate(candidates, rules, now);

            var failed = outcomes.Count(o => o.Outcome is not ("Success" or "NotModified" or "Skipped (fresh)"));
            status = sources.Count == 0 ? RunStatus.Failed
                : failed == 0 ? RunStatus.Complete
                : failed == outcomes.Count ? RunStatus.Failed
                : RunStatus.Partial;
            summary = sources.Count == 0 ? "No sources configured." : BuildSummary(curation, outcomes, failed);
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Failed;
            summary = "Cancelled.";
            store.CompleteRun(run.Id, projectId, status, summary, outcomes, null, _clock.GetUtcNow());
            throw;
        }

        store.CompleteRun(run.Id, projectId, status, summary, outcomes, curation, _clock.GetUtcNow());
        progress?.Report($"{project.Name}: {status} — {summary}");
        return new RunReport(run with { Status = status, Summary = summary, CompletedUtc = _clock.GetUtcNow(), SourceOutcomes = outcomes }, outcomes, curation);
    }

    private static string BuildSummary(CurationResult curation, IReadOnlyList<SourceOutcome> outcomes, int failed)
    {
        var parts = new List<string> { $"{curation.Selections.Count} of {curation.Requested} selected from {curation.Matches.Count} matches" };
        if (failed > 0) parts.Add($"{failed} of {outcomes.Count} sources unavailable");
        if (curation.Shortfall is not null) parts.Add(curation.Shortfall);
        return string.Join(". ", parts);
    }

    /// <summary>Collects a set of sources concurrently: at most four at once and one per host. Each source is fetched at most once even if requested concurrently.</summary>
    public async Task<IReadOnlyList<SourceOutcome>> CollectAsync(IReadOnlyList<Source> sources, bool force, CancellationToken ct, IProgress<string>? progress = null)
    {
        return await Task.WhenAll(sources.Select(s => CollectSharedAsync(s, force, ct, progress)));
    }

    private async Task<SourceOutcome> CollectSharedAsync(Source source, bool force, CancellationToken ct, IProgress<string>? progress)
    {
        var mine = new Lazy<Task<SourceOutcome>>(() => CollectOneAsync(source, force, ct, progress));
        var actual = _inFlight.GetOrAdd(source.Id, mine);
        if (!ReferenceEquals(actual, mine)) return await actual.Value;
        try { return await mine.Value; }
        finally { _inFlight.TryRemove(KeyValuePair.Create(source.Id, mine)); }
    }

    private async Task<SourceOutcome> CollectOneAsync(Source source, bool force, CancellationToken ct, IProgress<string>? progress)
    {
        // Re-read: another project's run may have just refreshed this shared source.
        source = store.GetSource(source.Id) ?? source;
        var now = _clock.GetUtcNow();

        if (source.Health == SourceHealth.Disabled)
            return Outcome(source, "Skipped (disabled)", source.LastError);
        if (source.Health is SourceHealth.Blocked or SourceHealth.AuthenticationRequired && !force)
            return Outcome(source, "Skipped (access denied)", source.LastError);
        if (source.NextRetryUtc is { } retry && retry > now)
            return Outcome(source, $"Waiting until {retry:u}", source.LastError);
        if (source.LastSuccessUtc is { } last && now - last < (force ? ManualReuseWindow : MinimumRefresh))
            return Outcome(source, "Skipped (fresh)", null);

        var collector = _collectors.FirstOrDefault(c => c.CanHandle(source));
        if (collector is null)
            return Outcome(source, "No connector", $"No connector for '{source.Connector}'.");

        var host = Uri.TryCreate(source.FeedUrl, UriKind.Absolute, out var u) ? u.Host : source.FeedUrl;
        var hostGate = _perHost.GetOrAdd(host, _ => new SemaphoreSlim(1));
        await _global.WaitAsync(ct);
        try
        {
            await hostGate.WaitAsync(ct);
            try
            {
                progress?.Report($"Fetching {source.Title}");
                var result = await collector.CollectAsync(source, ct);
                return Apply(source, result);
            }
            finally { hostGate.Release(); }
        }
        finally { _global.Release(); }
    }

    private SourceOutcome Apply(Source source, CollectResult result)
    {
        var now = _clock.GetUtcNow();
        source.LastAttemptUtc = now;
        IngestStats stats = new(0, 0, 0, 0);
        var warnings = result.Warnings.ToList();

        switch (result.Outcome)
        {
            case CollectOutcome.Success:
                var normalized = result.Items.Select(Normalizer.Normalize).ToList();
                var skipped = normalized.Count(n => n is null);
                if (skipped > 0) warnings.Add($"{skipped} entries had no usable link and were skipped.");
                stats = store.Ingest(source.Id, normalized.OfType<NormalizedItem>(), now);
                if (result.Items.Count == 0 && source.LastSuccessUtc is not null)
                    warnings.Add("Feed returned no entries; check whether the source has changed.");
                source.Health = SourceHealth.Healthy;
                source.LastSuccessUtc = now;
                source.LastError = null;
                source.NextRetryUtc = null;
                source.ETag = result.ETag;
                source.LastModified = result.LastModified;
                if (string.IsNullOrWhiteSpace(source.Title) || source.Title == new Uri(source.FeedUrl).Host)
                    source.Title = result.FeedTitle ?? source.Title;
                source.SiteUrl ??= result.SiteUrl;
                break;
            case CollectOutcome.NotModified:
                source.Health = SourceHealth.Healthy;
                source.LastSuccessUtc = now;
                source.LastError = null;
                source.NextRetryUtc = null;
                break;
            case CollectOutcome.RateLimited:
                source.Health = SourceHealth.Delayed;
                source.LastError = result.Error;
                var wait = result.RetryAfter is { } ra ? TimeSpan.FromSeconds(Math.Clamp(ra.TotalSeconds, 60, 86400)) : TimeSpan.FromMinutes(30);
                source.NextRetryUtc = now + wait;
                break;
            case CollectOutcome.AccessDenied:
                // Stop automatic retries; the user must test or repair the source.
                source.Health = SourceHealth.Blocked;
                source.LastError = result.Error;
                source.NextRetryUtc = null;
                break;
            case CollectOutcome.NotFound:
                source.Health = SourceHealth.ExtractionBroken;
                source.LastError = result.Error;
                source.NextRetryUtc = now.AddHours(6);
                break;
            default:
                source.Health = SourceHealth.Delayed;
                source.LastError = result.Error;
                source.NextRetryUtc = now + (result.RetryAfter is { } r2 ? TimeSpan.FromSeconds(Math.Clamp(r2.TotalSeconds, 60, 86400)) : MinimumRefresh);
                break;
        }
        store.UpdateSource(source);
        return new SourceOutcome(source.Id, source.Title, result.Outcome.ToString(), stats.New, stats.Updated, result.Error, source.LastSuccessUtc, warnings);
    }

    /// <summary>
    /// Weekly automatic backup (first, so cleanup is always recoverable), then retention at most once a day.
    /// Returns a short description of what was done, or null if nothing was due.
    /// </summary>
    public string? RunMaintenanceIfDue(string backupFolder, string? settingsPath)
    {
        var now = _clock.GetUtcNow();
        var parts = new List<string>();
        if (Maintenance.BackupService.AutoBackupIfDue(store, store.Database, backupFolder, settingsPath, now) is { } path)
            parts.Add($"automatic backup saved to {path}");
        if (store.GetMetaTime("last_retention_utc") is not { } last || now - last >= TimeSpan.FromHours(20))
            parts.Add("cleanup: " + store.ApplyRetention(now).Describe());
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static SourceOutcome Outcome(Source s, string outcome, string? error) =>
        new(s.Id, s.Title, outcome, 0, 0, error, s.LastSuccessUtc, []);

    private static string FormatSpan(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0.#}h" : $"{t.TotalMinutes:0}m";
}
