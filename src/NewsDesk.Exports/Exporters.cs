using System.Globalization;
using System.Net;
using System.Text;
using NewsDesk.Core.Model;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Exports;

public enum ContentScope { LinkOnly, Excerpt, NotesAndExcerpt, FullText }

public sealed record ExportOptions
{
    public ContentScope Scope { get; init; } = ContentScope.NotesAndExcerpt;
    public string ProjectName { get; init; } = "";
    public string? CollectionName { get; init; }
    public string Title { get; init; } = "Research brief";
}

/// <summary>One common export model; every format preserves source attribution and links.</summary>
public static class Exporters
{
    private static readonly string[] CsvColumns =
        ["project", "collection", "title", "publisher", "url", "published_utc", "captured_utc", "capture_level", "tags", "summary_or_excerpt", "notes", "highlights", "state"];

    /// <summary>Highlights as one line each: “quote” — comment.</summary>
    public static IEnumerable<string> HighlightLines(ItemRow i) =>
        i.Highlights.Select(h => $"“{h.Quote}”" + (h.Comment.Length > 0 ? $" — {h.Comment}" : ""));

    public static string ToCsv(IEnumerable<ItemRow> items, ExportOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', CsvColumns).Append("\r\n");
        foreach (var i in items)
        {
            string[] fields =
            [
                options.ProjectName, options.CollectionName ?? "", i.Title, i.SourceTitle, i.Url,
                Iso(i.PublishedUtc), Iso(i.CapturedUtc), i.CaptureLevel.ToString(), string.Join("; ", i.Tags),
                Body(i, options.Scope), IncludesNotes(options.Scope) ? i.Notes : "",
                IncludesNotes(options.Scope) ? string.Join(" | ", HighlightLines(i)) : "", State(i),
            ];
            sb.AppendJoin(',', fields.Select(CsvField)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// RFC 4180 quoting plus neutralization of spreadsheet formulas: values beginning with = + - @ or a control character
    /// are prefixed with an apostrophe so they open as text.
    /// </summary>
    public static string CsvField(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value != value.Trim()
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string ToMarkdown(IReadOnlyList<ItemRow> items, ExportOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(MdEscape(options.Title));
        sb.AppendLine();
        sb.AppendLine($"Project: {MdEscape(options.ProjectName)}" + (options.CollectionName is { } c ? $" · Collection: {MdEscape(c)}" : ""));
        sb.AppendLine($"Exported {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC · {items.Count} items");
        sb.AppendLine();
        for (var n = 0; n < items.Count; n++)
        {
            var i = items[n];
            sb.AppendLine($"## {n + 1}. {MdEscape(i.Title)}");
            sb.AppendLine();
            sb.AppendLine($"{MdEscape(i.SourceTitle)} · {DateLabel(i)} · {CoverageLabel(i.CaptureLevel)} · [source {n + 1}]");
            sb.AppendLine();
            var body = Body(i, options.Scope);
            if (body.Length > 0)
            {
                foreach (var line in body.Split('\n')) sb.Append("> ").AppendLine(MdEscape(line));
                sb.AppendLine();
            }
            if (IncludesNotes(options.Scope) && i.Highlights.Count > 0)
            {
                sb.AppendLine("**Highlights:**");
                sb.AppendLine();
                foreach (var line in HighlightLines(i)) sb.AppendLine("- " + MdEscape(line.Replace('\n', ' ')));
                sb.AppendLine();
            }
            if (IncludesNotes(options.Scope) && i.Notes.Length > 0)
            {
                sb.AppendLine("**Notes:** " + MdEscape(i.Notes));
                sb.AppendLine();
            }
            if (i.Tags.Count > 0) { sb.AppendLine("Tags: " + MdEscape(string.Join(", ", i.Tags))); sb.AppendLine(); }
        }
        sb.AppendLine("## Sources");
        sb.AppendLine();
        for (var n = 0; n < items.Count; n++)
            sb.AppendLine($"{n + 1}. {MdEscape(items[n].SourceTitle)}: <{items[n].Url}>");
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<ItemRow> items, ExportOptions options)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        sb.AppendLine($"<title>{E(options.Title)}</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:46rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#1b1b1b}" +
                      "h2{font-size:1.15rem;margin-top:2rem}.meta{color:#555;font-size:.9rem}blockquote{margin:.5rem 0;padding-left:1rem;border-left:3px solid #ccc;white-space:pre-wrap}" +
                      ".notes{background:#f6f6f0;padding:.5rem .75rem;border-radius:4px}</style></head><body>");
        sb.AppendLine($"<h1>{E(options.Title)}</h1>");
        sb.AppendLine($"<p class=\"meta\">Project: {E(options.ProjectName)}" + (options.CollectionName is { } c ? $" · Collection: {E(c)}" : "") +
                      $" · Exported {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC · {items.Count} items</p>");
        for (var n = 0; n < items.Count; n++)
        {
            var i = items[n];
            sb.AppendLine($"<h2>{n + 1}. {E(i.Title)}</h2>");
            sb.AppendLine($"<p class=\"meta\">{E(i.SourceTitle)} · {E(DateLabel(i))} · {E(CoverageLabel(i.CaptureLevel))} · <a href=\"#src{n + 1}\">[{n + 1}]</a></p>");
            var body = Body(i, options.Scope);
            if (body.Length > 0) sb.AppendLine($"<blockquote>{E(body)}</blockquote>");
            if (IncludesNotes(options.Scope) && i.Highlights.Count > 0)
            {
                sb.AppendLine("<p class=\"meta\"><strong>Highlights</strong></p><ul>");
                foreach (var h in i.Highlights)
                    sb.AppendLine($"<li><mark>{E(h.Quote)}</mark>{(h.Comment.Length > 0 ? " — " + E(h.Comment) : "")}</li>");
                sb.AppendLine("</ul>");
            }
            if (IncludesNotes(options.Scope) && i.Notes.Length > 0)
                sb.AppendLine($"<p class=\"notes\"><strong>Notes:</strong> {E(i.Notes)}</p>");
        }
        sb.AppendLine("<h2>Sources</h2><ol>");
        for (var n = 0; n < items.Count; n++)
        {
            var url = Uri.TryCreate(items[n].Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.ToString() : "";
            sb.AppendLine($"<li id=\"src{n + 1}\">{E(items[n].SourceTitle)}: <a href=\"{E(url)}\" rel=\"noopener noreferrer\">{E(url)}</a></li>");
        }
        sb.AppendLine("</ol></body></html>");
        return sb.ToString();
    }

    /// <summary>Plain-text clipboard variant.</summary>
    public static string ToPlainText(IReadOnlyList<ItemRow> items, ExportOptions options)
    {
        var sb = new StringBuilder();
        foreach (var i in items)
        {
            sb.AppendLine(i.Title);
            sb.AppendLine($"{i.SourceTitle} · {DateLabel(i)}");
            var body = Body(i, options.Scope);
            if (body.Length > 0) sb.AppendLine(body);
            if (IncludesNotes(options.Scope))
                foreach (var line in HighlightLines(i)) sb.AppendLine("Highlight: " + line);
            if (IncludesNotes(options.Scope) && i.Notes.Length > 0) sb.AppendLine("Notes: " + i.Notes);
            sb.AppendLine(i.Url);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string CoverageLabel(CaptureLevel level) => level switch
    {
        CaptureLevel.FullText => "Full text from feed",
        CaptureLevel.Excerpt => "Excerpt only — open original for full article",
        _ => "Link only",
    };

    public static string DateLabel(ItemRow i) =>
        i.PublishedUtc is { } p ? $"Published {p.ToLocalTime():yyyy-MM-dd HH:mm}"
        : i.UpdatedUtc is { } u ? $"Updated {u.ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"Publication date unknown (found {i.DiscoveredUtc.ToLocalTime():yyyy-MM-dd HH:mm})";

    public static bool IncludesNotes(ContentScope scope) => scope is ContentScope.NotesAndExcerpt or ContentScope.FullText;

    public static string Body(ItemRow i, ContentScope scope) => scope switch
    {
        ContentScope.LinkOnly => "",
        ContentScope.FullText => i.Text.Length > 0 ? i.Text : i.Excerpt,
        _ => i.Excerpt,
    };

    public static string State(ItemRow i) =>
        i.IsDismissed ? "dismissed" : i.IsKept ? "kept" : i.IsRead ? "read" : "new";

    private static string Iso(DateTimeOffset? d) => d?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "";

    private static string MdEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
