using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewsDesk.Core.Curation;
using NewsDesk.Core.Model;
using NewsDesk.Core.Scheduling;
using NewsDesk.Desktop.Services;

namespace NewsDesk.Desktop.ViewModels;

public sealed partial class DayOption(DayOfWeek day, bool selected) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string Label => Day.ToString()[..3];
    [ObservableProperty] private bool isSelected = selected;
}

/// <summary>Create or edit a project: purpose, matching, curation, schedule, retention, and a rule preview.</summary>
public sealed partial class ProjectEditorViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Project? _existing;

    private ProjectEditorViewModel(AppServices services, Project? existing)
    {
        _s = services;
        _existing = existing;
        var rules = existing?.Rules ?? new ProjectRules();
        var schedule = existing?.Schedule ?? new Schedule { Kind = ScheduleKind.Daily };
        Title = existing is null ? "New project" : $"Project settings — {existing.Name}";
        name = existing?.Name ?? "";
        description = existing?.Description ?? "";
        includeTerms = string.Join(Environment.NewLine, rules.IncludeTerms);
        excludeTerms = string.Join(Environment.NewLine, rules.ExcludeTerms);
        matchAll = rules.IncludeMode == MatchMode.All;
        windowHours = rules.WindowHours;
        topN = rules.TopN;
        storiesMode = rules.Mode == CurationMode.Stories;
        scoreFloor = rules.ScoreFloor;
        includePreviouslySeen = rules.IncludePreviouslySeen;
        scheduleKind = schedule.Kind;
        timeOfDay = schedule.TimeOfDay.ToString("HH:mm");
        intervalMinutes = schedule.IntervalMinutes;
        retentionDays = existing?.RetentionDays ?? 30;
        enabled = existing?.Enabled ?? true;
        Days = new(Enum.GetValues<DayOfWeek>().Skip(1).Append(DayOfWeek.Sunday)
            .Select(d => new DayOption(d, schedule.Days.Count == 0 ? d is not (DayOfWeek.Saturday or DayOfWeek.Sunday) : schedule.Days.Contains(d))));
        CanPreview = existing is not null;
        previewText = CanPreview ? "Preview applies these rules to items already collected, without saving." : "Preview is available after the project has sources and has been refreshed.";
    }

    public static ProjectEditorViewModel ForNew(AppServices s) => new(s, null);
    public static ProjectEditorViewModel ForExisting(AppServices s, Project p) => new(s, p);

    public string Title { get; }
    public bool IsNew => _existing is null;
    public Guid? SavedProjectId { get; private set; }
    public IReadOnlyList<ScheduleKind> ScheduleKinds { get; } = Enum.GetValues<ScheduleKind>();
    public ObservableCollection<DayOption> Days { get; }
    public bool CanPreview { get; }
    public ObservableCollection<string> PreviewItems { get; } = [];

    [ObservableProperty] private string name;
    [ObservableProperty] private string description;
    [ObservableProperty] private string includeTerms;
    [ObservableProperty] private string excludeTerms;
    [ObservableProperty] private bool matchAll;
    [ObservableProperty] private int windowHours;
    [ObservableProperty] private int topN;
    [ObservableProperty] private bool storiesMode;
    [ObservableProperty] private int scoreFloor;
    [ObservableProperty] private bool includePreviouslySeen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTime), nameof(ShowDays), nameof(ShowInterval))]
    private ScheduleKind scheduleKind;

    [ObservableProperty] private string timeOfDay;
    [ObservableProperty] private int intervalMinutes;
    [ObservableProperty] private int retentionDays;
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string previewText;

    public bool ShowTime => ScheduleKind is ScheduleKind.Daily or ScheduleKind.Weekly;
    public bool ShowDays => ScheduleKind == ScheduleKind.Weekly;
    public bool ShowInterval => ScheduleKind == ScheduleKind.Interval;

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private ProjectRules BuildRules() => new()
    {
        IncludeTerms = Lines(IncludeTerms),
        ExcludeTerms = Lines(ExcludeTerms),
        IncludeMode = MatchAll ? MatchMode.All : MatchMode.Any,
        WindowHours = Math.Clamp(WindowHours, 1, 24 * 365),
        TopN = Math.Clamp(TopN, 1, 100),
        Mode = StoriesMode ? CurationMode.Stories : CurationMode.Articles,
        ScoreFloor = Math.Clamp(ScoreFloor, 0, 100),
        IncludePreviouslySeen = IncludePreviouslySeen,
    };

    private Schedule? BuildSchedule()
    {
        if (!TimeOnly.TryParse(TimeOfDay, out var time)) { Error = "Enter the time as HH:MM, for example 07:30."; return null; }
        var days = Days.Where(d => d.IsSelected).Select(d => d.Day).ToList();
        if (ScheduleKind == ScheduleKind.Weekly && days.Count == 0) { Error = "Choose at least one day."; return null; }
        return new Schedule
        {
            Kind = ScheduleKind,
            TimeOfDay = time,
            Days = ScheduleKind == ScheduleKind.Weekly ? days : [],
            IntervalMinutes = Math.Max(Schedule.MinimumIntervalMinutes, IntervalMinutes),
        };
    }

    [RelayCommand]
    private void Preview()
    {
        if (_existing is null) return;
        var rules = BuildRules() with { IncludePreviouslySeen = true };
        var now = DateTimeOffset.UtcNow;
        var candidates = _s.Store.GetCandidates(_existing.Id, now.AddHours(-rules.WindowHours));
        // Preview ignores earlier dismissals and deliveries so the effect of the rules alone is visible.
        var result = Curator.Curate(candidates.Select(c => c with { State = null }), rules, now);
        PreviewItems.Clear();
        foreach (var s in result.Selections)
            PreviewItems.Add($"{s.Rank}. {s.Lead.Candidate.Article.Title}  —  {s.Reason}");
        PreviewText = $"{result.Considered} items in the window · {result.Matches.Count} match · {result.Selections.Count} would be selected." +
                      (result.Shortfall is null ? "" : " " + result.Shortfall);
    }

    /// <summary>Validates and saves. Returns true on success.</summary>
    public bool Save()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Give the project a name."; return false; }
        var other = _s.Store.FindProject(Name);
        if (other is not null && other.Id != _existing?.Id) { Error = "Another project already uses that name."; return false; }
        var schedule = BuildSchedule();
        if (schedule is null) return false;
        var rules = BuildRules();
        var now = DateTimeOffset.UtcNow;

        if (_existing is null)
        {
            var p = new Project
            {
                Name = Name.Trim(), Description = Description.Trim(), Schedule = schedule, RetentionDays = Math.Max(1, RetentionDays), Enabled = Enabled,
            };
            p.NextDueUtc = ScheduleCalculator.NextDue(schedule, p.TimeZoneId, now);
            _s.Store.CreateProject(p, rules, now);
            SavedProjectId = p.Id;
            return true;
        }

        var project = _s.Store.GetProject(_existing.Id)!;
        var old = project.Schedule;
        var scheduleChanged = old.Kind != schedule.Kind || old.TimeOfDay != schedule.TimeOfDay
                              || old.IntervalMinutes != schedule.IntervalMinutes || !old.Days.SequenceEqual(schedule.Days);
        project.Name = Name.Trim();
        project.Description = Description.Trim();
        project.Schedule = schedule;
        project.RetentionDays = Math.Max(1, RetentionDays);
        project.Enabled = Enabled;
        if (scheduleChanged || project.NextDueUtc is null)
            project.NextDueUtc = ScheduleCalculator.NextDue(schedule, project.TimeZoneId, now);
        _s.Store.UpdateProject(project);
        if (!RulesEqual(project.Rules, rules)) _s.Store.SaveRules(project.Id, rules, now);
        SavedProjectId = project.Id;
        return true;
    }

    private static bool RulesEqual(ProjectRules a, ProjectRules b) =>
        a.IncludeTerms.SequenceEqual(b.IncludeTerms) && a.ExcludeTerms.SequenceEqual(b.ExcludeTerms) && a.IncludeMode == b.IncludeMode
        && a.WindowHours == b.WindowHours && a.TopN == b.TopN && a.Mode == b.Mode && a.ScoreFloor == b.ScoreFloor
        && a.IncludePreviouslySeen == b.IncludePreviouslySeen;
}
