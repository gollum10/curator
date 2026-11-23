using NewsDesk.Core.Model;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public class CollectionTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCollector _collector = new();
    private NewsDeskStore Store => _db.Store;

    public void Dispose() => _db.Dispose();

    private async Task<(Project Project, IReadOnlyList<ItemRow> Items)> ProjectWithItems(string name, string feed)
    {
        var p = Store.CreateProject(new Project { Name = name, TimeZoneId = "UTC" }, new ProjectRules(), _clock.Now);
        var s = Store.GetOrAddSource(feed, feed, null);
        Store.Subscribe(new ProjectSource(p.Id, s.Id));
        _collector.Responses[feed] = () => FakeCollector.Feed(
            Items.Raw("a", "First story", "", _clock.Now.AddHours(-2), feed + "/a"),
            Items.Raw("b", "Second story", "galaga", _clock.Now.AddHours(-1), feed + "/b"));
        await new NewsDeskEngine(Store, [_collector], _clock).RefreshProjectAsync(p.Id, default);
        return (p, Store.GetItems(p.Id, ItemView.AllMatches));
    }

    [Fact]
    public async Task Items_can_belong_to_several_collections_and_keep_insertion_order()
    {
        var (p, items) = await ProjectWithItems("P", "https://feed.example/rss");
        var episode = Store.CreateCollection(p.Id, "Episode 12", _clock.Now);
        var shortlist = Store.CreateCollection(p.Id, "Shortlist", _clock.Now);

        Store.AddToCollection(p.Id, episode.Id, items[0].ArticleId, _clock.Now);
        Store.AddToCollection(p.Id, episode.Id, items[1].ArticleId, _clock.Now.AddMinutes(1));
        Store.AddToCollection(p.Id, episode.Id, items[1].ArticleId, _clock.Now.AddMinutes(2)); // idempotent
        Store.AddToCollection(p.Id, shortlist.Id, items[1].ArticleId, _clock.Now);

        Assert.Equal([items[0].ArticleId, items[1].ArticleId], Store.GetCollectionItems(p.Id, episode.Id).Select(i => i.ArticleId));
        Assert.Equal(2, Store.GetCollections(p.Id).Single(c => c.Name == "Episode 12").Count);
        Assert.Equal(new HashSet<Guid> { episode.Id, shortlist.Id }, Store.GetCollectionIdsForItem(p.Id, items[1].ArticleId));
        Assert.Equal("Second story", Assert.Single(Store.GetCollectionItems(p.Id, episode.Id, "galaga")).Title);
    }

    [Fact]
    public async Task Names_are_unique_per_project_ignoring_case_but_reusable_across_projects()
    {
        var (a, _) = await ProjectWithItems("A", "https://a.example/rss");
        var (b, _) = await ProjectWithItems("B", "https://b.example/rss");
        Store.CreateCollection(a.Id, "Weekly Leadership Briefing", _clock.Now);

        Assert.Throws<InvalidOperationException>(() => Store.CreateCollection(a.Id, "weekly leadership briefing", _clock.Now));
        Store.CreateCollection(b.Id, "Weekly Leadership Briefing", _clock.Now);
        Assert.Throws<ArgumentException>(() => Store.CreateCollection(a.Id, "   ", _clock.Now));
    }

    [Fact]
    public async Task Rename_checks_for_clashes()
    {
        var (p, _) = await ProjectWithItems("P", "https://feed.example/rss");
        var one = Store.CreateCollection(p.Id, "One", _clock.Now);
        Store.CreateCollection(p.Id, "Two", _clock.Now);

        Assert.Throws<InvalidOperationException>(() => Store.RenameCollection(one.Id, "TWO"));
        Store.RenameCollection(one.Id, "one"); // changing case of its own name is fine
        Assert.Contains(Store.GetCollections(p.Id), c => c.Name == "one");
    }

    [Fact]
    public async Task Cannot_add_items_across_projects()
    {
        var (a, aItems) = await ProjectWithItems("A", "https://a.example/rss");
        var (b, _) = await ProjectWithItems("B", "https://b.example/rss");
        var bCollection = Store.CreateCollection(b.Id, "B list", _clock.Now);

        Assert.Throws<InvalidOperationException>(() => Store.AddToCollection(a.Id, bCollection.Id, aItems[0].ArticleId, _clock.Now));
        Assert.Throws<InvalidOperationException>(() => Store.AddToCollection(b.Id, bCollection.Id, aItems[0].ArticleId, _clock.Now));
    }

    [Fact]
    public async Task Deleting_a_collection_or_removing_an_item_keeps_the_research()
    {
        var (p, items) = await ProjectWithItems("P", "https://feed.example/rss");
        var col = Store.CreateCollection(p.Id, "Episode 12", _clock.Now);
        Store.AddToCollection(p.Id, col.Id, items[0].ArticleId, _clock.Now);
        Store.AddToCollection(p.Id, col.Id, items[1].ArticleId, _clock.Now);
        Store.SetNote(p.Id, items[0].ArticleId, "intro segment", _clock.Now);

        Store.RemoveFromCollection(col.Id, items[1].ArticleId);
        Assert.Single(Store.GetCollectionItems(p.Id, col.Id));

        Store.DeleteCollection(col.Id);
        Assert.Empty(Store.GetCollections(p.Id));
        Assert.Equal(2, Store.GetItems(p.Id, ItemView.AllMatches).Count);
        Assert.Equal("intro segment", Store.GetItem(p.Id, items[0].ArticleId)!.Notes);
    }

    [Fact]
    public async Task Dismissed_items_remain_visible_in_their_collections()
    {
        var (p, items) = await ProjectWithItems("P", "https://feed.example/rss");
        var col = Store.CreateCollection(p.Id, "Episode 12", _clock.Now);
        Store.AddToCollection(p.Id, col.Id, items[0].ArticleId, _clock.Now);
        Store.Dismiss(p.Id, items[0].ArticleId);

        Assert.True(Assert.Single(Store.GetCollectionItems(p.Id, col.Id)).IsDismissed);
    }
}
