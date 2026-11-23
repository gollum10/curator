using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewsDesk.Exports;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Desktop.ViewModels;

public sealed record Choice<T>(T Value, string Label);

public enum ExportFormat { Markdown, Html, Csv, PlainText }

/// <summary>Export dialog: choose scope and format, preview, then save locally or copy to the clipboard.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<ItemRow> _items;
    private readonly string _projectName;

    private readonly string? _collectionName;

    public ExportViewModel(IDialogService dialogs, IReadOnlyList<ItemRow> items, string projectName, string title, string? collectionName = null)
    {
        _dialogs = dialogs;
        _items = items;
        _projectName = projectName;
        _collectionName = collectionName;
        Title = title;
        selectedFormat = Formats[0];
        selectedScope = Scopes[2];
        UpdatePreview();
    }

    public string Title { get; }
    public string CountLabel => _items.Count == 1 ? "1 item" : $"{_items.Count} items";
    public string Disclosure => "Saved to a file you choose or copied to the clipboard. Nothing is sent to any external service.";

    public IReadOnlyList<Choice<ExportFormat>> Formats { get; } =
    [
        new(ExportFormat.Markdown, "Markdown (.md)"), new(ExportFormat.Html, "HTML page (.html)"),
        new(ExportFormat.Csv, "Spreadsheet (.csv)"), new(ExportFormat.PlainText, "Plain text (.txt)"),
    ];

    public IReadOnlyList<Choice<ContentScope>> Scopes { get; } =
    [
        new(ContentScope.LinkOnly, "Links only"), new(ContentScope.Excerpt, "Excerpts"),
        new(ContentScope.NotesAndExcerpt, "Excerpts, notes and tags"), new(ContentScope.FullText, "Full text where available, with notes"),
    ];

    [ObservableProperty] private Choice<ExportFormat> selectedFormat;
    [ObservableProperty] private Choice<ContentScope> selectedScope;
    [ObservableProperty] private string preview = "";
    [ObservableProperty] private string status = "";

    partial void OnSelectedFormatChanged(Choice<ExportFormat> value) => UpdatePreview();
    partial void OnSelectedScopeChanged(Choice<ContentScope> value) => UpdatePreview();

    private ExportOptions Options => new() { ProjectName = _projectName, CollectionName = _collectionName, Title = Title, Scope = SelectedScope.Value };

    private string Render(ExportFormat format) => format switch
    {
        ExportFormat.Html => Exporters.ToHtml(_items, Options),
        ExportFormat.Csv => Exporters.ToCsv(_items, Options),
        ExportFormat.PlainText => Exporters.ToPlainText(_items, Options),
        _ => Exporters.ToMarkdown(_items, Options),
    };

    private void UpdatePreview()
    {
        var text = Render(SelectedFormat.Value);
        Preview = text.Length > 20000 ? text[..20000] + "\n…" : text;
        Status = "";
    }

    [RelayCommand]
    private void Save()
    {
        var (ext, filter) = SelectedFormat.Value switch
        {
            ExportFormat.Html => (".html", "HTML page|*.html"),
            ExportFormat.Csv => (".csv", "CSV spreadsheet|*.csv"),
            ExportFormat.PlainText => (".txt", "Text file|*.txt"),
            _ => (".md", "Markdown|*.md"),
        };
        var safeName = string.Concat(Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safeName.Length > 80) safeName = safeName[..80];
        var path = _dialogs.SaveFile(safeName + ext, filter);
        if (path is null) return;
        // CSV gets a BOM so Excel detects UTF-8; other formats do not need one.
        File.WriteAllText(path, Render(SelectedFormat.Value), new UTF8Encoding(SelectedFormat.Value == ExportFormat.Csv));
        Status = $"Saved {CountLabel} to {path}";
    }

    [RelayCommand]
    private void Copy()
    {
        var data = new DataObject();
        var plain = Exporters.ToPlainText(_items, Options);
        data.SetText(plain, TextDataFormat.UnicodeText);
        data.SetData(DataFormats.Html, ClipboardHtml.Wrap(Exporters.ToHtml(_items, Options)));
        Clipboard.SetDataObject(data, copy: true);
        Status = $"Copied {CountLabel} as formatted text and plain text.";
    }
}

/// <summary>Builds the CF_HTML clipboard format (header with byte offsets around the fragment).</summary>
public static class ClipboardHtml
{
    public static string Wrap(string htmlDocument)
    {
        var bodyStart = htmlDocument.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
        var bodyEnd = htmlDocument.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        var fragment = bodyStart >= 0 && bodyEnd > bodyStart ? htmlDocument[(bodyStart + 6)..bodyEnd] : htmlDocument;
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";
        var headerLength = string.Format(header, 0, 0, 0, 0).Length;
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(pre);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(post);
        return string.Format(header, startHtml, endHtml, startFragment, endFragment) + pre + fragment + post;
    }
}
