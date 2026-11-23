using System.IO.Compression;
using NewsDesk.Core.Model;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Maintenance;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public class MaintenanceTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCollector _collector = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "newsdesk-tests", "m" + Guid.NewGuid().ToString("N"));
    private NewsDeskStore Store => _db.Store;

    public MaintenanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Feed = "https://feed.example/rss";

    /// <summary>Project with 6 items collected "now": a=kept, b=noted, c=tagged, d=in collection, e=dismissed, f=untouched.</summary>
    private async Task<(Project P, Dictionary<string, Guid> Ids)> Seed(int retentionDays = 30)
    {
        var p = Store.CreateProject(new Project { Name = "P", TimeZoneId = "UTC", RetentionDays = retentionDays }, new ProjectRules { WindowHours = 48 }, _clock.Now);
        var s = Store.GetOrAddSource(Feed, "Feed", null);
        Store.Subscribe(new ProjectSource(p.Id, s.Id));
        var keys = new[] { "a", "b", "c", "d", "e", "f" };
        _collector.Responses[Feed] = () => FakeCollector.Feed(keys.Select(k => Items.Raw(k, $"Story {k}", "", _clock.Now.AddHours(-1), $"{Feed}/{k}")).ToArray());
        await new NewsDeskEngine(Store, [_collector], _clock).RefreshProjectAsync(p.Id, default);

        var ids = Store.GetItems(p.Id, ItemView.AllMatches).ToDictionary(i => i.Title[^1..], i => i.ArticleId);
        Store.SetKept(p.Id, ids["a"], true);
        Store.SetNote(p.Id, ids["b"], "note", _clock.Now);
        Store.SetTags(p.Id, ids["c"], ["tag"]);
        var col = Store.CreateCollection(p.Id, "Episode", _clock.Now);
        Store.AddToCollection(p.Id, col.Id, ids["d"], _clock.Now);
        Store.Dismiss(p.Id, ids["e"]);
        return (p, ids);
    }

    [Fact]
    public async Task Retention_removes_only_unprotected_old_items()
    {
        var (p, ids) = await Seed();
        Assert.Equal(0, Store.ApplyRetention(_clock.Now).ItemsRemoved); // nothing old yet

        _clock.Advance(TimeSpan.FromDays(31));
        var stats = Store.ApplyRetention(_clock.Now);

        Assert.Equal(1, stats.ItemsRemoved);
        var remaining = Store.GetItems(p.Id, ItemView.AllMatches).Select(i => i.Title).Concat(Store.GetItems(p.Id, ItemView.Dismissed).Select(i => i.Title)).ToHashSet();
        Assert.Equal(new HashSet<string> { "Story a", "Story b", "Story c", "Story d", "Story e" }, remaining);
        Assert.Equal("note", Store.GetItem(p.Id, ids["b"])!.Notes);
    }

    [Fact]
    public async Task Removed_articles_are_not_re_added_by_the_next_refresh()
    {
        var (p, _) = await Seed();
        // "g" was collected but never matched any project (and so is in no briefing).
        var source = Store.GetSources().Single();
        Store.Ingest(source.Id, [NewsDesk.Core.Collection.Normalizer.Normalize(Items.Raw("g", "Story g", "", null, $"{Feed}/g"))!], _clock.Now);

        _clock.Advance(TimeSpan.FromDays(31));
        var stats = Store.ApplyRetention(_clock.Now);
        // f lost its project item but stays stored because the latest briefing still references it; g is removed.
        Assert.Equal(1, stats.ArticlesRemoved);

        // The feed still lists everything, undated — without a tombstone, g would look brand new.
        _collector.Responses[Feed] = () => FakeCollector.Feed(new[] { "a", "b", "c", "d", "e", "f", "g" }
            .Select(k => Items.Raw(k, $"Story {k}", "", null, $"{Feed}/{k}")).ToArray());
        var report = await new NewsDeskEngine(Store, [_collector], _clock).RefreshProjectAsync(p.Id, default);

        Assert.Equal(0, report.Sources[0].NewItems);
        Assert.DoesNotContain(Store.GetItems(p.Id, ItemView.AllMatches), i => i.Title is "Story f" or "Story g");
    }

    [Fact]
    public async Task Old_briefings_are_trimmed_but_recent_ones_kept()
    {
        var (p, _) = await Seed();
        var engine = new NewsDeskEngine(Store, [_collector], _clock);
        for (var i = 0; i < 12; i++)
        {
            _clock.Advance(TimeSpan.FromDays(3));
            await engine.RefreshProjectAsync(p.Id, default);
        }
        Assert.Equal(13, Store.GetRuns(p.Id, 100).Count);

        Store.ApplyRetention(_clock.Now);
        var runs = Store.GetRuns(p.Id, 100);
        Assert.True(runs.Count >= NewsDeskStore.KeepRecentRunsPerProject);
        Assert.All(runs.Skip(NewsDeskStore.KeepRecentRunsPerProject), r => Assert.True(r.StartedUtc >= _clock.Now.AddDays(-30)));
    }

    [Fact]
    public async Task A10_backup_restores_projects_notes_and_collections_on_a_clean_profile()
    {
        var (p, ids) = await Seed();
        var settings = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settings, "{\"DarkTheme\":true}");
        var zip = Path.Combine(_dir, "backup.zip");
        var manifest = BackupService.Create(Store.Database, zip, settings);
        Assert.Equal(1, manifest.Projects);

        using var clean = new TempDb();
        var cleanSettings = Path.Combine(_dir, "clean-settings.json");
        var (ok, message, safety) = BackupService.Restore(zip, clean.Database, cleanSettings, Path.Combine(_dir, "safety"));
        Assert.True(ok, message);
        Assert.True(File.Exists(safety));

        var restored = new NewsDeskStore(new Database(clean.Database.Path));
        var rp = Assert.Single(restored.GetProjects());
        Assert.Equal(p.Id, rp.Id);
        Assert.Equal("note", restored.GetItem(p.Id, ids["b"])!.Notes);
        Assert.Equal(1, Assert.Single(restored.GetCollections(p.Id)).Count);
        Assert.True(restored.GetItem(p.Id, ids["a"])!.IsKept);
        Assert.Contains("DarkTheme", File.ReadAllText(cleanSettings));
    }

    [Fact]
    public void Inspect_rejects_files_that_are_not_our_backups()
    {
        var notZip = Path.Combine(_dir, "x.zip");
        File.WriteAllText(notZip, "hello");
        Assert.False(BackupService.Inspect(notZip).Ok);

        var evil = Path.Combine(_dir, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("../../outside.txt").Open());
            w.Write("x");
        }
        var check = BackupService.Inspect(evil);
        Assert.False(check.Ok);
        Assert.Contains("Unexpected file", check.Message);
    }

    [Fact]
    public async Task Maintenance_backs_up_weekly_and_cleans_daily()
    {
        await Seed();
        var engine = new NewsDeskEngine(Store, [_collector], _clock);
        var folder = Path.Combine(_dir, "auto");

        Assert.Contains("automatic backup", engine.RunMaintenanceIfDue(folder, null));
        Assert.Null(engine.RunMaintenanceIfDue(folder, null)); // nothing due again immediately

        _clock.Advance(TimeSpan.FromDays(1));
        var second = engine.RunMaintenanceIfDue(folder, null)!;
        Assert.Contains("cleanup", second);
        Assert.DoesNotContain("automatic backup", second);

        for (var i = 0; i < 6; i++) { _clock.Advance(TimeSpan.FromDays(7)); engine.RunMaintenanceIfDue(folder, null); }
        Assert.Equal(4, Directory.GetFiles(folder, "auto-*.zip").Length);
    }
}
