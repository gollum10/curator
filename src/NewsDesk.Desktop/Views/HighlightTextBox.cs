using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NewsDesk.Desktop.ViewModels;

namespace NewsDesk.Desktop.Views;

/// <summary>
/// Read-only, selectable article text with highlighted ranges. The document is built from plain text only (publisher
/// HTML is never rendered), and selections are reported as offsets into that same plain text.
/// </summary>
public sealed class HighlightTextBox : RichTextBox
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HighlightTextBox), new PropertyMetadata("", (d, _) => ((HighlightTextBox)d).Rebuild()));

    public static readonly DependencyProperty HighlightsProperty = DependencyProperty.Register(
        nameof(Highlights), typeof(IEnumerable), typeof(HighlightTextBox), new PropertyMetadata(null, OnHighlightsChanged));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(HighlightTextBox),
        new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xD5, 0x4F)), (d, _) => ((HighlightTextBox)d).Rebuild()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IEnumerable? Highlights { get => (IEnumerable?)GetValue(HighlightsProperty); set => SetValue(HighlightsProperty, value); }
    public Brush HighlightBrush { get => (Brush)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    public HighlightTextBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsDocumentEnabled = false;
        AcceptsReturn = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = new FlowDocument { PagePadding = new Thickness(0) };
    }

    private static void OnHighlightsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (HighlightTextBox)d;
        if (e.OldValue is INotifyCollectionChanged oldList) oldList.CollectionChanged -= box.OnHighlightListChanged;
        if (e.NewValue is INotifyCollectionChanged newList) newList.CollectionChanged += box.OnHighlightListChanged;
        box.Rebuild();
    }

    private void OnHighlightListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var text = Text ?? "";
        var ranges = (Highlights?.OfType<HighlightViewModel>() ?? [])
            .Where(h => h.CurrentStart is { } s && s >= 0 && s + h.Length <= text.Length)
            .Select(h => (Start: h.CurrentStart!.Value, End: h.CurrentStart!.Value + h.Length))
            .OrderBy(r => r.Start).ToList();

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var pos = 0;
        foreach (var (start, end) in ranges)
        {
            var s = Math.Max(start, pos);
            if (end <= s) continue; // overlaps an earlier highlight entirely
            if (s > pos) paragraph.Inlines.Add(new Run(text[pos..s]));
            paragraph.Inlines.Add(new Run(text[s..end]) { Background = HighlightBrush });
            pos = end;
        }
        if (pos < text.Length) paragraph.Inlines.Add(new Run(text[pos..]));

        Document.Blocks.Clear();
        Document.Blocks.Add(paragraph);
        Document.FontFamily = FontFamily;
        Document.FontSize = FontSize;
        Document.Foreground = Foreground;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty && Document is not null) Document.Foreground = Foreground;
    }

    /// <summary>Selection as (start, length) in <see cref="Text"/>, or null if nothing is selected.</summary>
    public (int Start, int Length)? GetSelectionRange()
    {
        if (Selection.IsEmpty || Document.Blocks.FirstBlock is not Paragraph p) return null;
        // TextRange reports line breaks as \r\n; the source text uses \n only.
        var start = new TextRange(p.ContentStart, Selection.Start).Text.Replace("\r\n", "\n").Length;
        var selected = Selection.Text.Replace("\r\n", "\n");
        var text = Text ?? "";
        if (start + selected.Length <= text.Length && string.CompareOrdinal(text, start, selected, 0, selected.Length) == 0)
            return (start, selected.Length);
        // Fall back to the nearest exact occurrence if offsets drifted.
        var index = text.IndexOf(selected, Math.Max(0, start - 50), StringComparison.Ordinal);
        return index >= 0 ? (index, selected.Length) : null;
    }
}
