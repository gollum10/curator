using NewsDesk.Core.Collection;
using NewsDesk.Core.Model;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public class EngineTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCollector _collector = new();
    private NewsDeskStore Store => _db.Store;

    public void Dispose() => _db.Dispose();

    private NewsDeskEngine Engine() => new(Store, [_collector], _clock);

    private Project AddProject(string name, ProjectRules rules, Schedule? schedule = null)
    {
        var p = new Project { Name = name, Schedule = schedule ?? new Schedule(), TimeZoneId = "UTC" };
        return Store.CreateProject(p, rules, _clock.Now);
    }

    private Source AddSource(Project p, string url, int priority = 50)
    {
        var s = Store.GetOrAddSource(url, url, null);
        Store.Subscribe(new ProjectSource(p.Id, s.Id, priority));
        return s;
    }

    [Fact]
    public async Task A01_shared_source_is_fetched_once_and_dismissal_is_per_project()
    {
        var gaming = AddProject("Gaming", new ProjectRules { IncludeTerms = ["Arcade1Up"] });
        var other = AddProject("Other", new ProjectRules { IncludeTerms = ["cabinet"] });
        AddSource(gaming, "https://feed.example/rss");
        AddSource(other, "https://feed.example/rss");
        _collector.Responses["https://feed.example/rss"] = () => FakeCollector.Feed(
            Items.Raw("1", "Arcade1Up cabinet revealed", "details", _clock.Now.AddHours(-1)));
        var engine = Engine();

        await Task.WhenAll(engine.RefreshProjectAsync(gaming.Id, default), engine.RefreshProjectAsync(other.Id, default));
        Assert.Equal(1, _collector.Calls["https://feed.example/rss"]);

        var gamingItem = Assert.Single(Store.GetItems(gaming.Id, ItemView.Inbox));
        Assert.Single(Store.GetItems(other.Id, ItemView.Inbox));

        Store.Dismiss(gaming.Id, gamingItem.ArticleId);
        Assert.Empty(Store.GetItems(gaming.Id, ItemView.Inbox));
        Assert.Single(Store.GetItems(other.Id, ItemView.Inbox));
    }

    [Fact]
    public async Task A02_rerunning_same_input_creates_no_duplicates_and_changes_become_revisions()
    {
        var p = AddProject("P", new ProjectRules { IncludeTerms = ["Arcade1Up"], IncludePreviouslySeen = true });
        AddSource(p, "https://feed.example/rss");
        var body = "original";
        _collector.Responses["https://feed.example/rss"] = () => FakeCollector.Feed(
            Items.Raw("1", "Arcade1Up news", body, _clock.Now.AddHours(-1)),
            Items.Raw("1", "Arcade1Up news", body, _clock.Now.AddHours(-1))); // duplicate within a run too
        var engine = Engine();

        var first = await engine.RefreshProjectAsync(p.Id, default);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await engine.RefreshProjectAsync(p.Id, default);

        Assert.Equal(1, first.Sources[0].NewItems);
        Assert.Equal(0, second.Sources[0].NewItems);
        Assert.Single(Store.GetItems(p.Id, ItemView.AllMatches));
        Assert.Single(Store.GetBriefing(p.Id, second.Run.Id));

        body = "corrected text";
        _clock.Advance(TimeSpan.FromMinutes(5));
        var third = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(1, third.Sources[0].UpdatedItems);
        var item = Assert.Single(Store.GetItems(p.Id, ItemView.AllMatches));
        Assert.Equal("corrected text", item.Text);
    }

    [Fact]
    public async Task Unchanged_items_are_not_redelivered_but_material_updates_are_labelled()
    {
        var p = AddProject("P", new ProjectRules { IncludeTerms = ["Arcade1Up"] });
        AddSource(p, "https://feed.example/rss");
        var body = "v1";
        _collector.Responses["https://feed.example/rss"] = () => FakeCollector.Feed(Items.Raw("1", "Arcade1Up news", body, _clock.Now.AddHours(-1)));
        var engine = Engine();

        Assert.Single((await engine.RefreshProjectAsync(p.Id, default)).Curation!.Selections);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty((await engine.RefreshProjectAsync(p.Id, default)).Curation!.Selections);

        body = "v2 with new details";
        _clock.Advance(TimeSpan.FromMinutes(5));
        var updated = await engine.RefreshProjectAsync(p.Id, default);
        var sel = Assert.Single(updated.Curation!.Selections);
        Assert.Contains("updated since last briefing", sel.Reason);
    }

    [Fact]
    public async Task A05_missed_schedules_produce_one_catch_up_run()
    {
        var schedule = new Schedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(7, 0) };
        var p = AddProject("Daily", new ProjectRules { IncludeTerms = ["Arcade1Up"], WindowHours = 24 * 7 }, schedule);
        AddSource(p, "https://feed.example/rss");
        _collector.Responses["https://feed.example/rss"] = () => FakeCollector.Feed(Items.Raw("1", "Arcade1Up", "", _clock.Now.AddHours(-1)));
        var engine = Engine();

        await engine.RunDueAsync(default); // first call sets next due
        var project = Store.GetProject(p.Id)!;
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 7, 0, 0, TimeSpan.Zero), project.NextDueUtc);

        _clock.Now = new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero); // asleep for three scheduled slots
        var reports = await engine.RunDueAsync(default);
        var run = Assert.Single(reports);
        Assert.StartsWith("catch-up", run.Run.Trigger);
        Assert.Empty(await engine.RunDueAsync(default));
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 7, 0, 0, TimeSpan.Zero), Store.GetProject(p.Id)!.NextDueUtc);
        Assert.Single(Store.GetRuns(p.Id));
    }

    [Fact]
    public async Task A06_rate_limited_and_failed_sources_make_a_partial_briefing_and_keep_prior_content()
    {
        var p = AddProject("P", new ProjectRules { IncludeTerms = ["Arcade1Up"], WindowHours = 48 });
        var good = AddSource(p, "https://good.example/rss");
        var limited = AddSource(p, "https://limited.example/rss");
        AddSource(p, "https://broken.example/rss");

        _collector.Responses[good.FeedUrl] = () => FakeCollector.Feed(Items.Raw("g", "Arcade1Up good", "", _clock.Now.AddHours(-1)));
        _collector.Responses[limited.FeedUrl] = () => FakeCollector.Feed(Items.Raw("l", "Arcade1Up earlier", "", _clock.Now.AddHours(-1), "https://limited.example/l"));
        _collector.Responses["https://broken.example/rss"] = () => new CollectResult { Outcome = CollectOutcome.Failed, Error = "boom" };
        var engine = Engine();
        await engine.RefreshProjectAsync(p.Id, default);

        _collector.Responses[limited.FeedUrl] = () => new CollectResult { Outcome = CollectOutcome.RateLimited, RetryAfter = TimeSpan.FromMinutes(10), Error = "HTTP 429" };
        _clock.Advance(TimeSpan.FromMinutes(20));
        var report = await engine.RefreshProjectAsync(p.Id, default);

        Assert.Equal(RunStatus.Partial, report.Run.Status);
        var limitedSource = Store.GetSource(limited.Id)!;
        Assert.Equal(SourceHealth.Delayed, limitedSource.Health);
        Assert.Equal(_clock.Now.AddMinutes(10), limitedSource.NextRetryUtc);
        Assert.Contains(Store.GetItems(p.Id, ItemView.AllMatches), i => i.Title == "Arcade1Up earlier");

        // Honors Retry-After: no request before the retry time.
        var callsBefore = _collector.Calls[limited.FeedUrl];
        _clock.Advance(TimeSpan.FromMinutes(5));
        await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(callsBefore, _collector.Calls[limited.FeedUrl]);
    }

    [Fact]
    public async Task Access_denied_stops_automatic_retries()
    {
        var p = AddProject("P", new ProjectRules(), new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = 15 });
        var s = AddSource(p, "https://denied.example/rss");
        _collector.Responses[s.FeedUrl] = () => new CollectResult { Outcome = CollectOutcome.AccessDenied, Error = "HTTP 403" };
        var engine = Engine();

        await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(SourceHealth.Blocked, Store.GetSource(s.Id)!.Health);

        _clock.Advance(TimeSpan.FromHours(1));
        await engine.RunDueAsync(default);
        await engine.RunDueAsync(default);
        Assert.Equal(1, _collector.Calls[s.FeedUrl]);
    }

    [Fact]
    public async Task A07_kept_items_and_notes_survive_rule_changes()
    {
        var p = AddProject("P", new ProjectRules { IncludeTerms = ["Arcade1Up"] });
        AddSource(p, "https://feed.example/rss");
        _collector.Responses["https://feed.example/rss"] = () => FakeCollector.Feed(Items.Raw("1", "Arcade1Up news", "", _clock.Now.AddHours(-1)));
        await Engine().RefreshProjectAsync(p.Id, default);
        var item = Assert.Single(Store.GetItems(p.Id, ItemView.Inbox));

        Store.SetKept(p.Id, item.ArticleId, true);
        Store.SetNote(p.Id, item.ArticleId, "Use in episode 12", _clock.Now);
        Store.SaveRules(p.Id, new ProjectRules { IncludeTerms = ["something else"] }, _clock.Now);
        _clock.Advance(TimeSpan.FromHours(1));
        await Engine().RefreshProjectAsync(p.Id, default);

        var kept = Assert.Single(Store.GetItems(p.Id, ItemView.Kept));
        Assert.Equal("Use in episode 12", kept.Notes);
    }

    [Fact]
    public async Task Search_finds_items_by_text_and_tolerates_fts_syntax()
    {
        var p = AddProject("P", new ProjectRules());
        AddSource(p, "https://feed.example/rss");
        _collector.Responses["https://feed.example/rss"] = () => FakeCollector.Feed(
            Items.Raw("1", "Cabinet review", "The Galaga edition is great", _clock.Now.AddHours(-1)),
            Items.Raw("2", "Other", "nothing here", _clock.Now.AddHours(-1)));
        await Engine().RefreshProjectAsync(p.Id, default);

        Assert.Equal("Cabinet review", Assert.Single(Store.GetItems(p.Id, ItemView.AllMatches, "galaga")).Title);
        Assert.Empty(Store.GetItems(p.Id, ItemView.AllMatches, "\"unbalanced AND OR ("));
    }

    [Fact]
    public async Task Project_without_sources_fails_visibly()
    {
        var p = AddProject("Empty", new ProjectRules());
        var report = await Engine().RefreshProjectAsync(p.Id, default);
        Assert.Equal(RunStatus.Failed, report.Run.Status);
        Assert.Equal("No sources configured.", report.Run.Summary);
    }
}
