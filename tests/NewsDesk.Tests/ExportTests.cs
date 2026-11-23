using NewsDesk.Core.Model;
using NewsDesk.Exports;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public class ExportTests
{
    private static ItemRow Row(string title, string excerpt = "", string notes = "") => new()
    {
        ArticleId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), Title = title, Url = "https://example.com/a?x=1&y=2",
        SourceTitle = "Example", PublishedUtc = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero),
        CaptureLevel = CaptureLevel.Excerpt, Excerpt = excerpt, Notes = notes,
    };

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("Ünïcødé ✓ 日本", "Ünïcødé ✓ 日本")]
    public void A08_csv_fields_are_quoted_and_formulas_neutralized(string input, string expected) =>
        Assert.Equal(expected, Exporters.CsvField(input));

    [Fact]
    public void Csv_has_header_and_one_row_per_article()
    {
        var csv = Exporters.ToCsv([Row("Title, with comma", "say \"hi\"", "=note")], new ExportOptions { ProjectName = "P" });
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("project,collection,title,publisher,url", lines[0]);
        Assert.Contains("\"Title, with comma\"", lines[1]);
        Assert.Contains("\"say \"\"hi\"\"\"", lines[1]);
        Assert.Contains("'=note", lines[1]);
    }

    [Fact]
    public void Html_export_encodes_untrusted_text_and_blocks_scripts()
    {
        var html = Exporters.ToHtml([Row("<script>alert(1)</script>", "<img src=x onerror=alert(1)>")], new ExportOptions { ProjectName = "P" });
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("https://example.com/a?x=1&amp;y=2", html);
    }

    [Fact]
    public void Markdown_export_includes_numbered_sources_and_notes()
    {
        var md = Exporters.ToMarkdown([Row("First", "excerpt", "my note")], new ExportOptions { ProjectName = "P", Title = "Brief" });
        Assert.Contains("# Brief", md);
        Assert.Contains("**Notes:** my note", md);
        Assert.Contains("1. Example: <https://example.com/a?x=1&y=2>", md);
    }
}
