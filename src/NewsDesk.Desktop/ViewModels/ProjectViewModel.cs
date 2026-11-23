using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewsDesk.Core.Model;
using NewsDesk.Desktop.Services;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Desktop.ViewModels;

public enum DeskView { Briefing, Inbox, AllMatches, Kept, Dismissed, Sources, History, Collection }

/// <summary>An entry in the project sidebar: a fixed view or one of the project's collections.</summary>
public sealed partial class DeskViewOption(DeskView view, string label, Guid? collectionId = null, int count = 0) : ObservableObject
{
    public DeskView View { get; } = view;
    public Guid? CollectionId { get; } = collectionId;
    [ObservableProperty] private string label = label;
    [ObservableProperty] private int count = count;
}

/// <summary>A checkbox in the reading pane: whether the selected item is in a given collection.</summary>
public sealed partial class CollectionMembership(Guid collectionId, string name, bool isMember, Action<CollectionMembership> toggled) : ObservableObject
{
    public Guid CollectionId { get; } = collectionId;
    public string Name { get; } = name;
    [ObservableProperty] private bool isMember = isMember;
    partial void OnIsMemberChanged(bool value) => toggled(this);
}

/// <summary>The project desk: briefing, inbox and other views, reading pane, and source manager.</summary>
public sealed partial class ProjectViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;
    private readonly Action _changed;
    private readonly Stack<ItemViewModel> _undo = new();
    private CancellationTokenSource? _refreshCts;

    public ProjectViewModel(AppServices services, IDialogService dialogs, Guid projectId, Action changed, DeskView initial = DeskView.Briefing)
    {
        _s = services;
        _dialogs = dialogs;
        _changed = changed;
        Project = services.Store.GetProject(projectId) ?? throw new InvalidOperationException("Project not found.");
        Views =
        [
            new(DeskView.Briefing, "Briefing"), new(DeskView.Inbox, "Inbox"), new(DeskView.AllMatches, "All matches"),
            new(DeskView.Kept, "Kept"), new(DeskView.Dismissed, "Dismissed"), new(DeskView.Sources, "Sources"),
            new(DeskView.History, "History"),
        ];
        _selectedView = Views.First(v => v.View == initial);
        LoadHeader();
        ReloadCollections();
        Load();
    }

    public Project Project { get; private set; }
    public IReadOnlyList<DeskViewOption> Views { get; }
    public ObservableCollection<DeskViewOption> Collections { get; } = [];
    public ObservableCollection<CollectionMembership> ItemCollections { get; } = [];
    [ObservableProperty] private bool hasCollections;
    public ObservableCollection<ItemViewModel> Items { get; } = [];
    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    public ObservableCollection<FeedOptionViewModel> DiscoveredFeeds { get; } = [];
    public ObservableCollection<PatternOptionViewModel> ExtractionOptions { get; } = [];
    public ObservableCollection<string> BriefingWarnings { get; } = [];

    [ObservableProperty] private string name = "";
    [ObservableProperty] private string headerDetails = "";

    private DeskViewOption _selectedView;

    /// <summary>
    /// Shared by the views list and the collections list. Null assignments (from the list that lost its selection) are ignored.
    /// </summary>
    public DeskViewOption? SelectedView
    {
        get => _selectedView;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedView)) return;
            _selectedView = value;
            OnPropertyChanged();
            foreach (var p in new[] { nameof(IsSourcesView), nameof(IsHistoryView), nameof(IsArticlesView), nameof(IsBriefingView), nameof(ShowSearch), nameof(IsCollectionView),
                         nameof(SelectedCollectionName), nameof(SelectedFixedView), nameof(SelectedCollectionOption) })
                OnPropertyChanged(p);
            Load();
        }
    }

    /// <summary>Selection for the fixed views list; null while a collection is open so that list shows no highlight.</summary>
    public DeskViewOption? SelectedFixedView
    {
        get => IsCollectionView ? null : _selectedView;
        set => SelectedView = value;
    }

    /// <summary>Selection for the collections list; null unless a collection is open.</summary>
    public DeskViewOption? SelectedCollectionOption
    {
        get => IsCollectionView ? _selectedView : null;
        set => SelectedView = value;
    }

    private DeskViewOption View => _selectedView;
    public bool IsSourcesView => View.View == DeskView.Sources;
    public bool IsHistoryView => View.View == DeskView.History;
    public bool IsArticlesView => !IsSourcesView && !IsHistoryView;
    public bool IsBriefingView => View.View == DeskView.Briefing;
    public bool IsCollectionView => View.View == DeskView.Collection;
    public string SelectedCollectionName => IsCollectionView ? View.Label : "";
    public bool ShowSearch => IsArticlesView && !IsBriefingView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleKeepCommand), nameof(DismissCommand), nameof(ToggleReadCommand), nameof(OpenOriginalCommand),
        nameof(SaveAnnotationsCommand), nameof(ExportSelectedCommand), nameof(RestoreCommand), nameof(NewCollectionWithItemCommand))]
    private ItemViewModel? selectedItem;

    public bool HasSelection => SelectedItem is not null;

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string emptyText = "";
    [ObservableProperty] private bool isEmpty;

    [ObservableProperty] private string briefingTitle = "";
    [ObservableProperty] private string briefingMeta = "";
    [ObservableProperty] private string briefingSummary = "";
    [ObservableProperty] private bool hasBriefingWarnings;
    [ObservableProperty] private bool isBriefingProblem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool isRefreshing;

    [ObservableProperty] private string progressText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool canUndo;

    [ObservableProperty] private string newSourceUrl = "";
    [ObservableProperty] private string discoveryMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCommand))]
    private bool isDiscovering;

    partial void OnSearchTextChanged(string value)
    {
        if (ShowSearch) Load();
    }

    partial void OnSelectedItemChanged(ItemViewModel? oldValue, ItemViewModel? newValue)
    {
        if (oldValue is { HasUnsavedAnnotations: true }) SaveAnnotationsFor(oldValue);
        if (newValue is { IsRead: false })
        {
            newValue.IsRead = true;
            _s.Store.SetRead(Project.Id, newValue.ArticleId, true);
            _changed();
        }
        LoadItemCollections();
    }

    // ------------------------------------------------------------ highlights

    public ObservableCollection<HighlightViewModel> ItemHighlights { get; } = [];
    [ObservableProperty] private bool hasHighlights;

    private void LoadItemHighlights()
    {
        ItemHighlights.Clear();
        if (SelectedItem is { } item)
            foreach (var h in _s.Store.GetHighlights(Project.Id, item.ArticleId)) ItemHighlights.Add(new HighlightViewModel(h));
        HasHighlights = ItemHighlights.Count > 0;
    }

    /// <summary>Called by the view with the reader's current selection.</summary>
    public void AddHighlight((int Start, int Length)? selection)
    {
        if (SelectedItem is not { CanHighlight: true } item) return;
        if (selection is not { } sel)
        {
            ProgressText = "Select some text in the article first, then choose Highlight.";
            return;
        }
        var saved = _s.Store.AddHighlight(Project.Id, item.ArticleId, sel.Start, sel.Length, "", DateTimeOffset.UtcNow);
        if (saved is null)
        {
            ProgressText = $"That selection can't be highlighted (empty, or longer than {Core.Text.HighlightAnchor.MaxQuoteLength} characters).";
            return;
        }
        LoadItemHighlights();
        ProgressText = "Highlight saved.";
        _changed();
    }

    [RelayCommand]
    private void CommentHighlight(HighlightViewModel? h)
    {
        if (h is null) return;
        var text = _dialogs.Prompt("Highlight comment", $"Comment on {Shorten(h.QuoteLabel)}", h.Comment);
        if (text is null) return;
        _s.Store.SetHighlightComment(Project.Id, h.Id, text, DateTimeOffset.UtcNow);
        LoadItemHighlights();
    }

    [RelayCommand]
    private void DeleteHighlight(HighlightViewModel? h)
    {
        if (h is null) return;
        _s.Store.DeleteHighlight(Project.Id, h.Id);
        LoadItemHighlights();
        ProgressText = "Highlight removed.";
    }

    private void LoadItemCollections()
    {
        LoadItemHighlights();
        ItemCollections.Clear();
        if (SelectedItem is not { } item) return;
        var memberOf = _s.Store.GetCollectionIdsForItem(Project.Id, item.ArticleId);
        foreach (var c in Collections)
            ItemCollections.Add(new CollectionMembership(c.CollectionId!.Value, c.Label, memberOf.Contains(c.CollectionId.Value), OnMembershipToggled));
    }

    private void OnMembershipToggled(CollectionMembership m)
    {
        if (SelectedItem is not { } item) return;
        if (m.IsMember)
        {
            _s.Store.AddToCollection(Project.Id, m.CollectionId, item.ArticleId, DateTimeOffset.UtcNow);
            ProgressText = $"Added to {m.Name}.";
        }
        else
        {
            _s.Store.RemoveFromCollection(m.CollectionId, item.ArticleId);
            ProgressText = $"Removed from {m.Name}. The item itself is unchanged.";
            if (View.CollectionId == m.CollectionId) RemoveFromList(item);
        }
        ReloadCollections();
    }

    /// <summary>Syncs the sidebar collection entries in place so the current selection survives.</summary>
    private void ReloadCollections()
    {
        var rows = _s.Store.GetCollections(Project.Id);
        foreach (var gone in Collections.Where(o => rows.All(r => r.Id != o.CollectionId)).ToList())
            Collections.Remove(gone);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var existing = Collections.FirstOrDefault(o => o.CollectionId == row.Id);
            if (existing is null)
            {
                Collections.Insert(i, new DeskViewOption(DeskView.Collection, row.Name, row.Id, row.Count));
                continue;
            }
            existing.Label = row.Name;
            existing.Count = row.Count;
            var at = Collections.IndexOf(existing);
            if (at != i) Collections.Move(at, i);
        }
        HasCollections = Collections.Count > 0;
        if (IsCollectionView && !Collections.Contains(View)) SelectedView = Views[0];
        OnPropertyChanged(nameof(SelectedCollectionName));
    }

    // ------------------------------------------------------------ collections

    [RelayCommand]
    private void NewCollection() => CreateCollection(addSelected: false);

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void NewCollectionWithItem() => CreateCollection(addSelected: true);

    private void CreateCollection(bool addSelected)
    {
        var name = _dialogs.Prompt("New collection", "Name the collection, for example “Episode 12” or “Weekly Leadership Briefing”.", "");
        if (name is null) return;
        try
        {
            var row = _s.Store.CreateCollection(Project.Id, name, DateTimeOffset.UtcNow);
            if (addSelected && SelectedItem is { } item)
                _s.Store.AddToCollection(Project.Id, row.Id, item.ArticleId, DateTimeOffset.UtcNow);
            ReloadCollections();
            LoadItemCollections();
            ProgressText = addSelected ? $"Created {row.Name} and added this item." : $"Created {row.Name}.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _dialogs.Info(ex.Message);
        }
    }

    [RelayCommand]
    private void RenameCollection()
    {
        if (View.CollectionId is not { } id) return;
        var name = _dialogs.Prompt("Rename collection", "New name:", View.Label);
        if (name is null || name.Trim() == View.Label) return;
        try
        {
            _s.Store.RenameCollection(id, name);
            ReloadCollections();
            LoadItemCollections();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _dialogs.Info(ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteCollection()
    {
        if (View.CollectionId is not { } id) return;
        if (!_dialogs.Confirm($"Delete the collection “{View.Label}”? The {View.Count} item(s) in it, and their notes, stay in the project.", "Delete collection")) return;
        _s.Store.DeleteCollection(id);
        SelectedView = Views[0];
        ReloadCollections();
    }

    private void LoadHeader()
    {
        Project = _s.Store.GetProject(Project.Id) ?? Project;
        Name = Project.Name;
        var r = Project.Rules;
        var schedule = Project.Schedule.Kind switch
        {
            ScheduleKind.Manual => "Manual refresh",
            ScheduleKind.Interval => $"Every {Project.Schedule.IntervalMinutes} min",
            ScheduleKind.Daily => $"Daily at {Project.Schedule.TimeOfDay:HH\\:mm}",
            _ => $"{string.Join(", ", Project.Schedule.Days.Select(d => d.ToString()[..3]))} at {Project.Schedule.TimeOfDay:HH\\:mm}",
        };
        var next = Project.Enabled && Project.NextDueUtc is { } due ? $" · next {due.ToLocalTime():g}" : Project.Enabled ? "" : " · paused";
        HeaderDetails = $"Top {r.TopN} {(r.Mode == CurationMode.Stories ? "distinct stories" : "articles")} from the last {FormatWindow(r.WindowHours)} · {schedule}{next}";
    }

    private static string FormatWindow(int hours) => hours % 24 == 0 && hours >= 48 ? $"{hours / 24} days" : $"{hours} hours";

    public void Load()
    {
        var keepSelection = SelectedItem?.ArticleId;
        Items.Clear();
        BriefingWarnings.Clear();
        HasBriefingWarnings = false;
        IsBriefingProblem = false;

        switch (View.View)
        {
            case DeskView.Sources:
                LoadSources();
                IsEmpty = false;
                return;
            case DeskView.History:
                LoadHistory();
                IsEmpty = false;
                return;
            case DeskView.Briefing:
                LoadBriefing();
                break;
            case DeskView.Collection:
                foreach (var row in _s.Store.GetCollectionItems(Project.Id, View.CollectionId!.Value, SearchText))
                    Items.Add(new ItemViewModel(row));
                EmptyText = SearchText.Length > 0 ? "No items in this collection match your search."
                    : "This collection is empty. Open any item and tick this collection under COLLECTIONS in the reading pane.";
                break;
            default:
                var view = View.View switch
                {
                    DeskView.Inbox => ItemView.Inbox,
                    DeskView.Kept => ItemView.Kept,
                    DeskView.Dismissed => ItemView.Dismissed,
                    _ => ItemView.AllMatches,
                };
                foreach (var row in _s.Store.GetItems(Project.Id, view, SearchText))
                    Items.Add(new ItemViewModel(row));
                EmptyText = SearchText.Length > 0 ? "No items match your search."
                    : view switch
                    {
                        ItemView.Inbox => _s.Store.GetProjectSources(Project.Id).Count == 0
                            ? "No sources configured. Open Sources to add a website or feed."
                            : "Inbox is empty. Refresh to collect, or everything has been reviewed.",
                        ItemView.Kept => "Nothing kept yet. Keep an item to protect it from cleanup.",
                        ItemView.Dismissed => "No dismissed items.",
                        _ => "No matching items yet. Refresh to collect.",
                    };
                break;
        }
        IsEmpty = Items.Count == 0;
        SelectedItem = Items.FirstOrDefault(i => i.ArticleId == keepSelection);
    }

    private Guid? _briefingRunId;
    private bool _briefingIsStories;

    private void LoadBriefing()
    {
        var run = _s.Store.GetLatestBriefingRun(Project.Id);
        _briefingRunId = run?.Id;
        _briefingIsStories = run is not null && _s.Store.GetRules(run.RuleVersionId).Mode == CurationMode.Stories;
        if (run is null)
        {
            BriefingTitle = "No briefing yet";
            BriefingMeta = "";
            BriefingSummary = "";
            EmptyText = _s.Store.GetProjectSources(Project.Id).Count == 0
                ? "Add sources, then refresh to create the first briefing."
                : "Refresh to collect and create the first briefing.";
            return;
        }

        var rules = _s.Store.GetRules(run.RuleVersionId);
        BriefingTitle = $"Briefing · {run.StartedUtc.ToLocalTime():dddd d MMMM, HH:mm}";
        BriefingMeta = $"{run.Status} · last {FormatWindow(rules.WindowHours)} · top {rules.TopN} {(rules.Mode == CurationMode.Stories ? "distinct stories" : "articles")} · " +
                       $"{run.Trigger} · AI not used";
        BriefingSummary = run.Summary;
        IsBriefingProblem = run.Status != RunStatus.Complete;

        foreach (var o in run.SourceOutcomes.Where(o => o.Outcome is not ("Success" or "NotModified" or "Skipped (fresh)")))
            BriefingWarnings.Add($"{o.SourceTitle}: {o.Outcome}{(o.Error is null ? "" : $" ({o.Error})")} — data as of {o.LastSuccessUtc?.ToLocalTime().ToString("g") ?? "never"}");
        foreach (var o in run.SourceOutcomes)
            foreach (var w in o.Warnings) BriefingWarnings.Add($"{o.SourceTitle}: {w}");
        HasBriefingWarnings = BriefingWarnings.Count > 0;

        foreach (var entry in _s.Store.GetBriefing(Project.Id, run.Id))
            if (!entry.Lead.IsDismissed) Items.Add(new ItemViewModel(entry.Lead, entry));

        EmptyText = run.Status == RunStatus.Failed
            ? "Collection failed and no items qualified. See the source problems above."
            : "No new items qualified in this briefing. Previously delivered items are in the Inbox.";
    }

    // ------------------------------------------------------------ history

    public ObservableCollection<RunRowViewModel> Runs { get; } = [];
    public ObservableCollection<SourceOutcomeRow> RunOutcomes { get; } = [];
    public ObservableCollection<ItemViewModel> RunEntries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun))]
    private RunRowViewModel? selectedRun;

    [ObservableProperty] private string runTitle = "";
    [ObservableProperty] private string runSummary = "";
    [ObservableProperty] private string runRules = "";
    [ObservableProperty] private string runEntriesNote = "";
    [ObservableProperty] private bool hasNoRuns;
    public bool HasSelectedRun => SelectedRun is not null;

    private void LoadHistory()
    {
        var keep = SelectedRun?.Run.Id;
        Runs.Clear();
        foreach (var r in _s.Store.GetRuns(Project.Id, 200)) Runs.Add(new RunRowViewModel(r));
        HasNoRuns = Runs.Count == 0;
        SelectedRun = Runs.FirstOrDefault(r => r.Run.Id == keep) ?? Runs.FirstOrDefault();
    }

    partial void OnSelectedRunChanged(RunRowViewModel? value)
    {
        RunOutcomes.Clear();
        RunEntries.Clear();
        if (value is null) return;
        var run = value.Run;
        RunTitle = $"{run.StartedUtc.ToLocalTime():dddd d MMMM yyyy, HH:mm} — {run.Status}";
        RunSummary = run.Summary;
        var rules = _s.Store.GetRules(run.RuleVersionId);
        RunRules = $"Rules used: top {rules.TopN} {(rules.Mode == CurationMode.Stories ? "distinct stories" : "articles")}, last {FormatWindow(rules.WindowHours)}, " +
                   $"minimum score {rules.ScoreFloor}" +
                   (rules.IncludeTerms.Count > 0 ? $", topics: {string.Join("; ", rules.IncludeTerms)}" : ", all items from sources") +
                   (rules.ExcludeTerms.Count > 0 ? $", excluding: {string.Join("; ", rules.ExcludeTerms)}" : "");
        foreach (var o in run.SourceOutcomes)
        {
            var ok = o.Outcome is "Success" or "NotModified" or "Skipped (fresh)";
            var detail = o.Outcome == "Success" ? $"{o.NewItems} new, {o.UpdatedItems} updated" : o.Error ?? "";
            if (o.Warnings.Count > 0) detail = string.Join(" ", new[] { detail }.Concat(o.Warnings).Where(s => s.Length > 0));
            RunOutcomes.Add(new SourceOutcomeRow(o.SourceTitle, o.Outcome, detail, !ok));
        }
        foreach (var entry in _s.Store.GetBriefing(Project.Id, run.Id))
            RunEntries.Add(new ItemViewModel(entry.Lead, entry));
        RunEntriesNote = RunEntries.Count == 0
            ? "This run selected no items (or they have since been removed by cleanup)."
            : "Items selected in this run. Select one to open the original.";
    }

    [RelayCommand]
    private void OpenRunEntry(ItemViewModel? item)
    {
        if (item is not null) _dialogs.OpenUrl(item.Url);
    }

    private void LoadSources()
    {
        Sources.Clear();
        foreach (var (source, link) in _s.Store.GetProjectSources(Project.Id))
            Sources.Add(new SourceRowViewModel(_s.Store, Project.Id, source, link));
        HasNoSources = Sources.Count == 0;
    }

    [ObservableProperty] private bool hasNoSources;

    // ------------------------------------------------------------ refresh

    private bool CanRefresh() => !IsRefreshing;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        ProgressText = "Starting…";
        _refreshCts = new CancellationTokenSource();
        var progress = new Progress<string>(m => ProgressText = m);
        try
        {
            var report = await Task.Run(() => _s.Engine.RefreshProjectAsync(Project.Id, _refreshCts.Token, progress));
            ProgressText = "";
            _s.Log($"manual refresh '{Project.Name}': {report.Run.Status} — {report.Run.Summary}");
            LoadHeader();
            if (View.View is not (DeskView.Briefing or DeskView.Sources or DeskView.Collection or DeskView.History)) SelectedView = Views[0];
            else Load();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Refresh cancelled. Sources already collected were kept.";
            Load();
        }
        finally
        {
            IsRefreshing = false;
            _refreshCts.Dispose();
            _refreshCts = null;
            _changed();
        }
    }

    [RelayCommand]
    private void CancelRefresh() => _refreshCts?.Cancel();

    // ------------------------------------------------------------ item actions

    private bool HasItem() => SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void ToggleKeep()
    {
        var item = SelectedItem!;
        item.IsKept = !item.IsKept;
        _s.Store.SetKept(Project.Id, item.ArticleId, item.IsKept);
        if (!item.IsKept && View.View == DeskView.Kept) RemoveFromList(item);
        _changed();
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void ToggleRead()
    {
        var item = SelectedItem!;
        item.IsRead = !item.IsRead;
        _s.Store.SetRead(Project.Id, item.ArticleId, item.IsRead);
        _changed();
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void Dismiss()
    {
        var item = SelectedItem!;
        if (item.IsDismissed) return;
        _s.Store.Dismiss(Project.Id, item.ArticleId);
        item.IsDismissed = true;
        _undo.Push(item);
        CanUndo = true;
        ProgressText = $"Dismissed “{Shorten(item.Title)}”. Ctrl+Z to undo.";
        if (View.View != DeskView.Dismissed) RemoveFromList(item);
        _changed();
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void Restore()
    {
        var item = SelectedItem!;
        _s.Store.Restore(Project.Id, item.ArticleId);
        item.IsDismissed = false;
        if (View.View == DeskView.Dismissed) RemoveFromList(item);
        _changed();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!_undo.TryPop(out var item)) return;
        _s.Store.Restore(Project.Id, item.ArticleId);
        CanUndo = _undo.Count > 0;
        ProgressText = $"Restored “{Shorten(item.Title)}”.";
        Load();
        SelectedItem = Items.FirstOrDefault(i => i.ArticleId == item.ArticleId);
        _changed();
    }

    private void RemoveFromList(ItemViewModel item)
    {
        var index = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void OpenOriginal() => _dialogs.OpenUrl(SelectedItem!.Url);

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (url is not null) _dialogs.OpenUrl(url);
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void SaveAnnotations()
    {
        SaveAnnotationsFor(SelectedItem!);
        ProgressText = "Notes and tags saved.";
    }

    private void SaveAnnotationsFor(ItemViewModel item)
    {
        _s.Store.SetNote(Project.Id, item.ArticleId, item.Note, DateTimeOffset.UtcNow);
        _s.Store.SetTags(Project.Id, item.ArticleId, item.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        item.MarkAnnotationsSaved();
    }

    /// <summary>Saves pending note edits; called when leaving the project.</summary>
    public void Flush()
    {
        if (SelectedItem is { HasUnsavedAnnotations: true } item) SaveAnnotationsFor(item);
    }

    // ------------------------------------------------------------ export

    [RelayCommand]
    private void ExportView()
    {
        Flush();
        var rows = Items.Select(i => ReloadRow(i)).OfType<ItemRow>().ToList();
        if (rows.Count == 0) { _dialogs.Info("There is nothing in this view to export."); return; }
        if (IsCollectionView)
        {
            _dialogs.ShowExport(new ExportViewModel(_dialogs, rows, Project.Name, View.Label, collectionName: View.Label));
            return;
        }
        // A distinct-stories briefing exports to Excel as one risk per row plus a supporting-sources sheet.
        var risks = IsBriefingView && _briefingIsStories && _briefingRunId is { } runId
            ? _s.Store.GetBriefing(Project.Id, runId).Where(b => !b.Lead.IsDismissed).ToList()
            : null;
        _dialogs.ShowExport(new ExportViewModel(_dialogs, rows, Project.Name, $"{Project.Name} — {View.Label}", riskBriefing: risks));
    }

    [RelayCommand(CanExecute = nameof(HasItem))]
    private void ExportSelected()
    {
        Flush();
        var item = SelectedItem!;
        var rows = new[] { item }.Select(ReloadRow).OfType<ItemRow>().ToList();
        _dialogs.ShowExport(new ExportViewModel(_dialogs, rows, Project.Name, item.Title));
    }

    private ItemRow? ReloadRow(ItemViewModel i) => _s.Store.GetItem(Project.Id, i.ArticleId);

    // ------------------------------------------------------------ project settings

    [RelayCommand]
    private void EditSettings()
    {
        var editor = ProjectEditorViewModel.ForExisting(_s, Project);
        if (!_dialogs.EditProject(editor)) return;
        LoadHeader();
        Load();
        _changed();
    }

    // ------------------------------------------------------------ sources

    private bool CanDiscover() => !IsDiscovering;

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSourceUrl)) { DiscoveryMessage = "Enter a website or feed address."; return; }
        IsDiscovering = true;
        DiscoveredFeeds.Clear();
        ExtractionOptions.Clear();
        DiscoveryMessage = "Looking for feeds…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await Task.Run(() => _s.Discovery.DiscoverAsync(NewSourceUrl, cts.Token));
            foreach (var f in result.Feeds) DiscoveredFeeds.Add(new FeedOptionViewModel(f));
            if (result.ExtractionPage is { } extractPage)
                foreach (var pattern in result.ExtractionSuggestions) ExtractionOptions.Add(new PatternOptionViewModel(pattern, extractPage));
            DiscoveryMessage = result.Feeds.Count > 0
                ? result.Message + " Feeds usually include headlines and excerpts; full articles may need the original site."
                : result.Message;
        }
        catch (OperationCanceledException)
        {
            DiscoveryMessage = "Timed out while looking for feeds.";
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand]
    private void AddFeed(FeedOptionViewModel? option)
    {
        if (option is null) return;
        var source = _s.Store.GetOrAddSource(option.FeedUrl, option.Feed.Title, option.Feed.SiteUrl, option.Feed.Connector);
        _s.Store.Subscribe(new ProjectSource(Project.Id, source.Id));
        DiscoveredFeeds.Clear();
        NewSourceUrl = "";
        DiscoveryMessage = $"Added {source.Title}. Refresh to collect from it.";
        LoadSources();
        _changed();
    }

    [RelayCommand]
    private void FollowPattern(PatternOptionViewModel? option)
    {
        if (option is null) return;
        var template = new NewsDesk.Connectors.Extraction.ExtractionTemplate { ListUrl = option.Page, LinkPrefix = option.Pattern.Prefix };
        var host = Uri.TryCreate(option.Page, UriKind.Absolute, out var u) ? u.Host : option.Page;
        var source = _s.Store.GetOrAddSource(NewsDesk.Connectors.Extraction.ExtractionCollector.SourceKey(template), $"{host} · {option.Pattern.Prefix}",
            option.Page, NewsDesk.Connectors.Extraction.ExtractionCollector.ConnectorName,
            NewsDesk.Connectors.Extraction.ExtractionCollector.SerializeTemplate(template));
        _s.Store.Subscribe(new ProjectSource(Project.Id, source.Id));
        ExtractionOptions.Clear();
        DiscoveredFeeds.Clear();
        NewSourceUrl = "";
        DiscoveryMessage = $"Following {source.Title} with website extraction. Refresh to collect; the first run takes the newest items and marks older ones as seen.";
        LoadSources();
        _changed();
    }

    [RelayCommand]
    private void RemoveSource(SourceRowViewModel? row)
    {
        if (row is null) return;
        if (!_dialogs.Confirm($"Stop following “{row.Title}” in {Project.Name}? Items already collected stay available.", "Remove source")) return;
        _s.Store.Unsubscribe(Project.Id, row.Source.Id);
        LoadSources();
        _changed();
    }

    [RelayCommand]
    private async Task TestSourceAsync(SourceRowViewModel? row)
    {
        if (row is null) return;
        DiscoveryMessage = $"Testing {row.Title}…";
        var outcome = (await Task.Run(() => _s.Engine.CollectAsync([row.Source], force: true, CancellationToken.None)))[0];
        row.Reload();
        DiscoveryMessage = outcome.Outcome switch
        {
            "Success" => $"{row.Title}: OK — {outcome.NewItems} new, {outcome.UpdatedItems} updated.",
            "NotModified" => $"{row.Title}: OK — no changes since last check.",
            "Skipped (fresh)" => $"{row.Title}: checked within the last two minutes.",
            _ => $"{row.Title}: {outcome.Outcome}{(outcome.Error is null ? "" : $" — {outcome.Error}")}",
        };
        _changed();
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..57] + "…" : s;
}
