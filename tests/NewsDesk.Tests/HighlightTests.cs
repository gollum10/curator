using NewsDesk.Core.Model;
using NewsDesk.Core.Text;
using NewsDesk.Exports;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public class HighlightTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCollector _collector = new();
    private NewsDeskStore Store => _db.Store;
    private const string Feed = "https://feed.example/rss";
    private string _body = "Arcade1Up confirmed the new cabinet ships in March. Pricing starts at $499.";

    public void Dispose() => _db.Dispose();

    private async Task<(Project P, Guid ArticleId, NewsDeskEngine Engine)> Seed()
    {
        var p = Store.CreateProject(new Project { Name = "P", TimeZoneId = "UTC" }, new ProjectRules { IncludePreviouslySeen = true }, _clock.Now);
        var s = Store.GetOrAddSource(Feed, "Feed", null);
        Store.Subscribe(new ProjectSource(p.Id, s.Id));
        _collector.Responses[Feed] = () => FakeCollector.Feed(Items.Raw("1", "Cabinet news", _body, _clock.Now.AddHours(-1)));
        var engine = new NewsDeskEngine(Store, [_collector], _clock);
        await engine.RefreshProjectAsync(p.Id, default);
        return (p, Store.GetItems(p.Id, ItemView.AllMatches).Single().ArticleId, engine);
    }

    [Theory]
    [InlineData("abc def abc", "abc", 8, 8)]   // exact at original offset
    [InlineData("xx abc def abc", "abc", 9, 11)] // text shifted: nearest occurrence
    [InlineData("abc def", "ghi", 0, null)]     // gone
    public void Anchor_resolves_to_nearest_exact_occurrence(string text, string quote, int start, int? expected) =>
        Assert.Equal(expected, HighlightAnchor.Resolve(text, quote, start));

    [Fact]
    public void Selections_are_trimmed_and_bounded()
    {
        Assert.Equal((5, "word"), HighlightAnchor.Normalize("one  word  two", 3, 7)); // "  word " → "word" at 5
        Assert.Null(HighlightAnchor.Normalize("abc", 1, 0));
        Assert.Null(HighlightAnchor.Normalize("abc", 2, 5));
        Assert.Null(HighlightAnchor.Normalize("   ", 0, 3));
        Assert.Null(HighlightAnchor.Normalize(new string('a', 3000), 0, 2500));
    }

    [Fact]
    public async Task Highlights_follow_text_changes_and_become_stale_when_the_quote_disappears()
    {
        var (p, id, engine) = await Seed();
        var start = _body.IndexOf("ships in March", StringComparison.Ordinal);
        var h = Store.AddHighlight(p.Id, id, start, "ships in March".Length, "release date", _clock.Now)!;
        Store.AddHighlight(p.Id, id, _body.IndexOf("$499", StringComparison.Ordinal), 4, "", _clock.Now);
        Assert.Equal("ships in March", h.Quote);

        // Publisher adds a sentence at the start: both highlights move.
        _body = "UPDATE: corrected. " + _body;
        _clock.Advance(TimeSpan.FromMinutes(10));
        await engine.RefreshProjectAsync(p.Id, default);
        var moved = Store.GetHighlights(p.Id, id);
        Assert.All(moved, x => Assert.False(x.IsStale));
        Assert.Equal(_body.IndexOf("ships in March", StringComparison.Ordinal), moved.Single(x => x.Quote == "ships in March").CurrentStart);

        // Publisher changes the date: that highlight is kept with its original quote, marked stale.
        _body = _body.Replace("ships in March", "ships in April");
        _clock.Advance(TimeSpan.FromMinutes(10));
        await engine.RefreshProjectAsync(p.Id, default);
        var after = Store.GetHighlights(p.Id, id);
        var stale = after.Single(x => x.Quote == "ships in March");
        Assert.True(stale.IsStale);
        Assert.Equal("release date", stale.Comment);
        Assert.False(after.Single(x => x.Quote == "$499").IsStale);
    }

    [Fact]
    public async Task Highlighted_items_survive_cleanup_and_appear_in_exports()
    {
        var (p, id, _) = await Seed();
        Store.AddHighlight(p.Id, id, 0, "Arcade1Up confirmed".Length, "lead quote", _clock.Now);

        _clock.Advance(TimeSpan.FromDays(60));
        Store.ApplyRetention(_clock.Now);
        var item = Store.GetItem(p.Id, id)!;
        Assert.Equal("Arcade1Up confirmed", Assert.Single(item.Highlights).Quote);

        var md = Exporters.ToMarkdown([item], new ExportOptions { Scope = ContentScope.NotesAndExcerpt });
        Assert.Contains("**Highlights:**", md);
        Assert.Contains("“Arcade1Up confirmed” — lead quote", md);
        Assert.Contains("“Arcade1Up confirmed”", Exporters.ToCsv([item], new ExportOptions { Scope = ContentScope.NotesAndExcerpt }));
        Assert.DoesNotContain("Highlights", Exporters.ToMarkdown([item], new ExportOptions { Scope = ContentScope.Excerpt }));
    }

    [Fact]
    public async Task Highlights_can_be_commented_and_deleted_within_their_project_only()
    {
        var (p, id, _) = await Seed();
        var h = Store.AddHighlight(p.Id, id, 0, 9, "", _clock.Now)!;
        Store.SetHighlightComment(p.Id, h.Id, "check source", _clock.Now);
        Assert.Equal("check source", Store.GetHighlights(p.Id, id).Single().Comment);

        Store.DeleteHighlight(Guid.NewGuid(), h.Id); // wrong project: no effect
        Assert.Single(Store.GetHighlights(p.Id, id));
        Store.DeleteHighlight(p.Id, h.Id);
        Assert.Empty(Store.GetHighlights(p.Id, id));
    }
}
