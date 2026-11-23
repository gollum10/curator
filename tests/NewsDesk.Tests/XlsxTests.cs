using ClosedXML.Excel;
using NewsDesk.Core.Model;
using NewsDesk.Exports;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public class XlsxTests
{
    private static ItemRow Row(string title, string url = "https://example.com/a", string excerpt = "", string notes = "", DateTimeOffset? published = null) => new()
    {
        ArticleId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), Title = title, Url = url, SourceTitle = "Example",
        PublishedUtc = published, CapturedUtc = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero),
        CaptureLevel = CaptureLevel.Excerpt, Excerpt = excerpt, Notes = notes, Tags = ["a", "b"],
    };

    private static XLWorkbook Roundtrip(Action<Stream> write)
    {
        var ms = new MemoryStream();
        write(ms);
        ms.Position = 0;
        return new XLWorkbook(ms);
    }

    [Fact]
    public void A08_items_sheet_keeps_formula_like_and_unicode_text_inert()
    {
        var items = new[]
        {
            Row("=HYPERLINK(\"http://evil\",\"click\")", notes: "+cmd|' /C calc'!A0", excerpt: "line1\nline2, \"quoted\""),
            Row("Ünïcødé ✓ 日本", url: "javascript:alert(1)", published: new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero)),
        };
        using var wb = Roundtrip(s => XlsxExporter.WriteItems(items, new ExportOptions { ProjectName = "P", CollectionName = "Episode 12" }, s));
        var ws = wb.Worksheet("Articles");

        Assert.Equal("Title", ws.Cell(1, 3).GetString());
        var title = ws.Cell(2, 3);
        Assert.False(title.HasFormula);
        Assert.Equal("=HYPERLINK(\"http://evil\",\"click\")", title.GetString());
        Assert.False(ws.Cell(2, 11).HasFormula);
        Assert.Equal("line1\nline2, \"quoted\"", ws.Cell(2, 10).GetString());
        Assert.Equal("Episode 12", ws.Cell(2, 2).GetString());
        Assert.Equal("a; b", ws.Cell(2, 9).GetString());

        Assert.Equal("Ünïcødé ✓ 日本", ws.Cell(3, 3).GetString());
        Assert.True(ws.Cell(2, 5).HasHyperlink);
        Assert.False(ws.Cell(3, 5).HasHyperlink); // non-web schemes are never links
        Assert.Equal("unknown", ws.Cell(2, 6).GetString());
        Assert.Equal(XLDataType.DateTime, ws.Cell(3, 6).DataType);
    }

    [Fact]
    public void Very_long_text_is_truncated_to_fit_excel()
    {
        using var wb = Roundtrip(s => XlsxExporter.WriteItems([Row("t", excerpt: new string('x', 40_000))], new ExportOptions(), s));
        Assert.True(wb.Worksheet(1).Cell(2, 10).GetString().Length <= 32_767);
    }

    [Fact]
    public void Risk_briefing_has_one_row_per_risk_and_a_supporting_sources_sheet()
    {
        var entries = new[]
        {
            new BriefingRow(1, Row("Lead A"), [Row("Support A1"), Row("Support A2")], "CVE-2026-1001", 90, "Matched", "{}"),
            new BriefingRow(2, Row("Lead B"), [], "CVE-2026-2002,CVE-2026-2003", 80, "Matched", "{}"),
        };
        using var wb = Roundtrip(s => XlsxExporter.WriteBriefing(entries, new ExportOptions { ProjectName = "Cyber" }, s));

        var risks = wb.Worksheet("Risks");
        Assert.Equal(3, risks.LastRowUsed()!.RowNumber()); // header + 2 risks
        Assert.Equal("Lead A", risks.Cell(2, 2).GetString());
        Assert.Equal(2, risks.Cell(2, 9).GetValue<int>());
        Assert.Equal("CVE-2026-2002, CVE-2026-2003", risks.Cell(3, 3).GetString());

        var support = wb.Worksheet("Supporting sources");
        Assert.Equal(5, support.LastRowUsed()!.RowNumber()); // header + 3 for A + 1 for B
        Assert.Equal(["Lead", "Supporting", "Supporting", "Lead"], Enumerable.Range(2, 4).Select(r => support.Cell(r, 2).GetString()));
        Assert.All(Enumerable.Range(2, 3), r => Assert.Equal(1, support.Cell(r, 1).GetValue<int>()));
    }
}
