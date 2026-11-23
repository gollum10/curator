using CommunityToolkit.Mvvm.ComponentModel;
using NewsDesk.Connectors.Feeds;
using NewsDesk.Core.Model;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Desktop.ViewModels;

public sealed partial class SourceRowViewModel : ObservableObject
{
    private readonly NewsDeskStore _store;
    private readonly Guid _projectId;
    private bool _loading;

    public SourceRowViewModel(NewsDeskStore store, Guid projectId, Source source, ProjectSource link)
    {
        _store = store;
        _projectId = projectId;
        Source = source;
        _loading = true;
        Priority = link.Priority;
        IsPrimary = link.IsPrimary;
        _loading = false;
    }

    public Source Source { get; private set; }
    public string Title => Source.Title;
    public string FeedUrl => Source.FeedUrl;
    public string HealthLabel => Source.Health switch
    {
        SourceHealth.Unknown => "Not checked yet",
        SourceHealth.AuthenticationRequired => "Authentication required",
        SourceHealth.ExtractionBroken => "Feed missing or broken",
        var h => h.ToString(),
    };
    public bool IsHealthy => Source.Health == SourceHealth.Healthy;
    public bool IsProblem => Source.Health is not (SourceHealth.Healthy or SourceHealth.Unknown);
    public string Details
    {
        get
        {
            var parts = new List<string>
            {
                "Last success: " + (Source.LastSuccessUtc?.ToLocalTime().ToString("g") ?? "never"),
                "Last attempt: " + (Source.LastAttemptUtc?.ToLocalTime().ToString("g") ?? "never"),
            };
            if (Source.NextRetryUtc is { } nr) parts.Add($"Next retry: {nr.ToLocalTime():g}");
            return string.Join(" · ", parts);
        }
    }
    public string? Error => Source.LastError;
    public bool HasError => !string.IsNullOrEmpty(Source.LastError);

    [ObservableProperty] private int priority;
    [ObservableProperty] private bool isPrimary;

    partial void OnPriorityChanged(int value) => SaveLink();
    partial void OnIsPrimaryChanged(bool value) => SaveLink();

    private void SaveLink()
    {
        if (_loading) return;
        _store.Subscribe(new ProjectSource(_projectId, Source.Id, Math.Clamp(Priority, 0, 100), IsPrimary));
    }

    public void Reload()
    {
        Source = _store.GetSource(Source.Id) ?? Source;
        OnPropertyChanged(string.Empty);
    }
}

public sealed class FeedOptionViewModel(DiscoveredFeed feed)
{
    public DiscoveredFeed Feed { get; } = feed;
    public string Title => Feed.Title ?? "(untitled feed)";
    public string FeedUrl => Feed.FeedUrl;
    public IReadOnlyList<string> Preview { get; } = feed.Preview.Take(5)
        .Select(i => $"{i.Title}  ·  {i.Published?.ToLocalTime().ToString("yyyy-MM-dd") ?? "date unknown"}").ToList();
    public string EntryCount => $"{Feed.Preview.Count} recent entries";
}
