using ClosedXML.Excel;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Exports;

/// <summary>
/// Excel workbooks. All text is written as text values (never formulas), links are real hyperlinks only for http(s),
/// and dates are date cells in local time.
/// </summary>
public static class XlsxExporter
{
    private const int MaxCellChars = 32_000; // Excel's hard limit is 32,767

    /// <summary>One article per row, same columns as CSV.</summary>
    public static void WriteItems(IReadOnlyList<ItemRow> items, ExportOptions options, Stream output)
    {
        using var wb = NewWorkbook(options);
        var ws = wb.Worksheets.Add("Articles");
        string[] headers = ["Project", "Collection", "Title", "Publisher", "URL", "Published", "Captured", "Coverage", "Tags", "Summary or excerpt", "Notes", "State", "Highlights"];
        WriteHeader(ws, headers);
        var row = 2;
        foreach (var i in items)
        {
            Text(ws.Cell(row, 1), options.ProjectName);
            Text(ws.Cell(row, 2), options.CollectionName ?? "");
            Text(ws.Cell(row, 3), i.Title);
            Text(ws.Cell(row, 4), i.SourceTitle);
            Link(ws.Cell(row, 5), i.Url);
            Date(ws.Cell(row, 6), i.PublishedUtc);
            Date(ws.Cell(row, 7), i.CapturedUtc);
            Text(ws.Cell(row, 8), Exporters.CoverageLabel(i.CaptureLevel));
            Text(ws.Cell(row, 9), string.Join("; ", i.Tags));
            Text(ws.Cell(row, 10), Exporters.Body(i, options.Scope));
            Text(ws.Cell(row, 11), Exporters.IncludesNotes(options.Scope) ? i.Notes : "");
            Text(ws.Cell(row, 12), Exporters.State(i));
            Text(ws.Cell(row, 13), Exporters.IncludesNotes(options.Scope) ? string.Join("\n", Exporters.HighlightLines(i)) : "");
            row++;
        }
        Finish(ws, headers.Length, row - 1, wrapColumns: [3, 10, 11, 13], widths: new() { [3] = 50, [5] = 40, [10] = 70, [11] = 40, [13] = 50 });
        wb.SaveAs(output);
    }

    /// <summary>
    /// Distinct-story briefing: one risk (story group) per row, plus a "Supporting sources" sheet listing every report
    /// with its group's rank so each claim can be traced to its source.
    /// </summary>
    public static void WriteBriefing(IReadOnlyList<BriefingRow> entries, ExportOptions options, Stream output)
    {
        using var wb = NewWorkbook(options);
        var risks = wb.Worksheets.Add("Risks");
        string[] h1 = ["Rank", "Lead report", "Identifiers", "Score", "Why selected", "Publisher", "URL", "Published", "Supporting reports", "Summary or excerpt", "Notes"];
        WriteHeader(risks, h1);
        var r = 2;
        foreach (var e in entries)
        {
            risks.Cell(r, 1).Value = e.Rank;
            Text(risks.Cell(r, 2), e.Lead.Title);
            Text(risks.Cell(r, 3), e.GroupKey?.Replace(",", ", ") ?? "");
            risks.Cell(r, 4).Value = e.Score;
            Text(risks.Cell(r, 5), e.Reason);
            Text(risks.Cell(r, 6), e.Lead.SourceTitle);
            Link(risks.Cell(r, 7), e.Lead.Url);
            Date(risks.Cell(r, 8), e.Lead.PublishedUtc);
            risks.Cell(r, 9).Value = e.Supporting.Count;
            Text(risks.Cell(r, 10), Exporters.Body(e.Lead, options.Scope));
            Text(risks.Cell(r, 11), Exporters.IncludesNotes(options.Scope) ? e.Lead.Notes : "");
            r++;
        }
        Finish(risks, h1.Length, r - 1, wrapColumns: [2, 5, 10, 11], widths: new() { [2] = 50, [3] = 30, [5] = 45, [7] = 40, [10] = 70, [11] = 40 });

        var support = wb.Worksheets.Add("Supporting sources");
        string[] h2 = ["Rank", "Role", "Identifiers", "Title", "Publisher", "URL", "Published", "Captured", "Coverage"];
        WriteHeader(support, h2);
        var s = 2;
        foreach (var e in entries)
        {
            foreach (var (item, role) in e.Supporting.Select(x => (x, "Supporting")).Prepend((e.Lead, "Lead")))
            {
                support.Cell(s, 1).Value = e.Rank;
                Text(support.Cell(s, 2), role);
                Text(support.Cell(s, 3), e.GroupKey?.Replace(",", ", ") ?? "");
                Text(support.Cell(s, 4), item.Title);
                Text(support.Cell(s, 5), item.SourceTitle);
                Link(support.Cell(s, 6), item.Url);
                Date(support.Cell(s, 7), item.PublishedUtc);
                Date(support.Cell(s, 8), item.CapturedUtc);
                Text(support.Cell(s, 9), Exporters.CoverageLabel(item.CaptureLevel));
                s++;
            }
        }
        Finish(support, h2.Length, s - 1, wrapColumns: [4], widths: new() { [3] = 30, [4] = 55, [6] = 40 });
        wb.SaveAs(output);
    }

    private static XLWorkbook NewWorkbook(ExportOptions options)
    {
        var wb = new XLWorkbook();
        wb.Properties.Title = options.Title;
        wb.Properties.Author = "Personal News Desk";
        return wb;
    }

    private static void WriteHeader(IXLWorksheet ws, string[] headers)
    {
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var header = ws.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EAF8");
        ws.SheetView.FreezeRows(1);
    }

    private static void Finish(IXLWorksheet ws, int columns, int lastRow, int[] wrapColumns, Dictionary<int, double> widths)
    {
        if (lastRow >= 1) ws.Range(1, 1, Math.Max(1, lastRow), columns).SetAutoFilter();
        ws.Columns(1, columns).AdjustToContents(1, Math.Min(lastRow, 200), 8, 30);
        foreach (var (col, width) in widths) ws.Column(col).Width = width;
        foreach (var col in wrapColumns) ws.Column(col).Style.Alignment.WrapText = true;
        ws.Range(1, 1, Math.Max(1, lastRow), columns).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }

    /// <summary>Always a text value; text that looks like a formula stays inert text.</summary>
    private static void Text(IXLCell cell, string value)
    {
        if (value.Length > MaxCellChars) value = value[..MaxCellChars] + "…";
        cell.Value = value;
        cell.Style.NumberFormat.Format = "@";
    }

    private static void Link(IXLCell cell, string url)
    {
        Text(cell, url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            cell.SetHyperlink(new XLHyperlink(uri));
            cell.Style.Font.FontColor = XLColor.FromHtml("#2F64C9");
            cell.Style.Font.Underline = XLFontUnderlineValues.Single;
        }
    }

    private static void Date(IXLCell cell, DateTimeOffset? value)
    {
        if (value is not { } d) { Text(cell, "unknown"); return; }
        cell.Value = d.ToLocalTime().DateTime;
        cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
    }
}
