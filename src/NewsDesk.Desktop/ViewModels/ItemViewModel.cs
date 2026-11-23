using CommunityToolkit.Mvvm.ComponentModel;
using NewsDesk.Exports;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Desktop.ViewModels;

/// <summary>An article within one project, as shown in lists and the reading pane.</summary>
public sealed partial class ItemViewModel : ObservableObject
{
    public ItemViewModel(ItemRow row, BriefingRow? briefing = null)
    {
        Row = row;
        Rank = briefing?.Rank;
        Reason = briefing?.Reason ?? (row.MatchReason.Length > 0 ? $"{row.MatchReason} · score {row.Score}" : "");
        Supporting = briefing?.Supporting ?? [];
        GroupKey = briefing?.GroupKey?.Replace(",", ", ");
        isRead = row.IsRead;
        isKept = row.IsKept;
        isDismissed = row.IsDismissed;
        note = row.Notes;
        tags = string.Join(", ", row.Tags);
        SavedNote = note;
        SavedTags = tags;
    }

    public ItemRow Row { get; }
    public Guid ArticleId => Row.ArticleId;
    public int? Rank { get; }
    public string Reason { get; }
    public IReadOnlyList<ItemRow> Supporting { get; }
    public string? GroupKey { get; }

    public string Title => Row.Title;
    public string SourceTitle => Row.SourceTitle;
    public string Url => Row.Url;
    public string DateLabel => Exporters.DateLabel(Row);
    public string Subline => $"{Row.SourceTitle} · {DateLabel}";
    public string Excerpt => Row.Excerpt;
    public string Coverage => Exporters.CoverageLabel(Row.CaptureLevel);
    /// <summary>Same text highlights are anchored to (full text, else excerpt), so selection offsets line up.</summary>
    public string ReaderText => Row.AnchorText.Length > 0 ? Row.AnchorText : "This source supplied only a headline and link.";
    public bool CanHighlight => Row.AnchorText.Length > 0;
    public string CapturedLabel => $"Captured {Row.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    public bool HasRank => Rank is not null;
    public string RankLabel => Rank?.ToString() ?? "";
    public bool HasSupporting => Supporting.Count > 0;
    public string SupportingLabel => Supporting.Count switch { 0 => "", 1 => "+1 related report", var n => $"+{n} related reports" };
    public bool HasGroupKey => GroupKey is not null;
    public bool IsUpdate => Reason.Contains("updated since last briefing", StringComparison.Ordinal);
    public bool HasReason => Reason.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private bool isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(KeepLabel))]
    private bool isKept;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private bool isDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedAnnotations))]
    private string note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedAnnotations))]
    private string tags;

    public string SavedNote { get; private set; }
    public string SavedTags { get; private set; }
    public bool HasUnsavedAnnotations => Note != SavedNote || Tags != SavedTags;

    public void MarkAnnotationsSaved()
    {
        SavedNote = Note;
        SavedTags = Tags;
        OnPropertyChanged(nameof(HasUnsavedAnnotations));
    }

    public string KeepLabel => IsKept ? "★ Kept" : "☆ Keep";
    public string StateLabel => IsDismissed ? "Dismissed" : IsKept ? "Kept" : IsRead ? "Read" : "New";
}
