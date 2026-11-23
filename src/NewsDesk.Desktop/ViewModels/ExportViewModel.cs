using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewsDesk.Exports;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Desktop.ViewModels;

public sealed record Choice<T>(T Value, string Label);

public enum ExportFormat { Markdown, Html, Excel, Csv, PlainText }

/// <summary>Export dialog: choose scope and format, preview, then save locally or copy to the clipboard.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<ItemRow> _items;
    private readonly string _projectName;

    private readonly string? _collectionName;
    private readonly IReadOnlyList<BriefingRow>? _riskBriefing;

    /// <param name="riskBriefing">For a distinct-stories briefing: the groups, so Excel export can write one risk per row plus supporting sources.</param>
    public ExportViewModel(IDialogService dialogs, IReadOnlyList<ItemRow> items, string projectName, string title, string? collectionName = null,
        IReadOnlyList<BriefingRow>? riskBriefing = null)
    {
        _dialogs = dialogs;
        _items = items;
        _projectName = projectName;
        _collectionName = collectionName;
        _riskBriefing = riskBriefing;
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
        new(ExportFormat.Excel, "Excel workbook (.xlsx)"), new(ExportFormat.Csv, "CSV spreadsheet (.csv)"),
        new(ExportFormat.PlainText, "Plain text (.txt)"),
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
        var text = SelectedFormat.Value == ExportFormat.Excel ? ExcelPreview() : Render(SelectedFormat.Value);
        Preview = text.Length > 20000 ? text[..20000] + "\n…" : text;
        Status = "";
    }

    private string ExcelPreview()
    {
        var sb = new StringBuilder();
        if (_riskBriefing is { Count: > 0 } risks)
        {
            var supporting = risks.Sum(r => r.Supporting.Count + 1);
            sb.AppendLine($"Excel workbook with two sheets:");
            sb.AppendLine($"  • Risks — {risks.Count} row(s), one per distinct risk");
            sb.AppendLine($"  • Supporting sources — {supporting} row(s): each lead and supporting report, traceable by rank");
            sb.AppendLine();
            foreach (var r in risks)
                sb.AppendLine($"{r.Rank}. {r.Lead.Title}  [{r.GroupKey?.Replace(",", ", ") ?? "no identifiers"}]  +{r.Supporting.Count} supporting");
        }
        else
        {
            sb.AppendLine($"Excel workbook, sheet “Articles” — {_items.Count} row(s), one per article.");
            sb.AppendLine("Columns: Project, Collection, Title, Publisher, URL (clickable), Published, Captured, Coverage, Tags, Summary or excerpt, Notes, State.");
            sb.AppendLine();
            foreach (var i in _items.Take(50)) sb.AppendLine($"• {i.Title}  —  {i.SourceTitle}");
        }
        sb.AppendLine();
        sb.AppendLine("Text that looks like a spreadsheet formula is stored as plain text and never runs.");
        return sb.ToString();
    }

    [RelayCommand]
    private void Save()
    {
        var (ext, filter) = SelectedFormat.Value switch
        {
            ExportFormat.Excel => (".xlsx", "Excel workbook|*.xlsx"),
            ExportFormat.Html => (".html", "HTML page|*.html"),
            ExportFormat.Csv => (".csv", "CSV spreadsheet|*.csv"),
            ExportFormat.PlainText => (".txt", "Text file|*.txt"),
            _ => (".md", "Markdown|*.md"),
        };
        var safeName = string.Concat(Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safeName.Length > 80) safeName = safeName[..80];
        var path = _dialogs.SaveFile(safeName + ext, filter);
        if (path is null) return;
        try
        {
            if (SelectedFormat.Value == ExportFormat.Excel)
            {
                using var file = File.Create(path);
                if (_riskBriefing is { Count: > 0 } risks) XlsxExporter.WriteBriefing(risks, Options, file);
                else XlsxExporter.WriteItems(_items, Options, file);
            }
            else
            {
                // CSV gets a BOM so Excel detects UTF-8; other formats do not need one.
                File.WriteAllText(path, Render(SelectedFormat.Value), new UTF8Encoding(SelectedFormat.Value == ExportFormat.Csv));
            }
            Status = $"Saved {CountLabel} to {path}";
        }
        catch (IOException ex)
        {
            Status = $"Could not save: {ex.Message} (Is the file open in another program?)";
        }
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
