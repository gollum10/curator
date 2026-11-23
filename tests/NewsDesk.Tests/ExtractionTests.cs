using System.Net;
using System.Text;
using NewsDesk.Connectors.Extraction;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Model;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

/// <summary>Serves canned pages and records every request.</summary>
public sealed class FakeFetcher : IFetcher
{
    public Dictionary<string, (HttpStatusCode Status, string Body)> Pages { get; } = new(StringComparer.Ordinal);
    public List<string> Requests { get; } = [];

    public Task<FetchResponse> GetAsync(Uri uri, string? etag, string? lastModified, CancellationToken ct)
    {
        var key = uri.GetLeftPart(UriPartial.Path);
        Requests.Add(key);
        var (status, body) = Pages.TryGetValue(key, out var p) ? p : (HttpStatusCode.NotFound, "");
        return Task.FromResult(new FetchResponse(status, uri, Encoding.UTF8.GetBytes(body), "text/html", null, null, null, false));
    }
}

public class RobotsTests
{
    private static readonly Uri Page = new("https://site.example/news/story");

    [Fact]
    public void Generic_disallow_all_blocks_us_but_named_crawler_rules_do_not_apply()
    {
        var p = RobotsPolicy.Parse("User-agent: ClaudeBot\nDisallow: /\n\nUser-agent: *\nDisallow: /\n");
        Assert.False(p.IsAllowed(Page));
        var q = RobotsPolicy.Parse("User-agent: GPTBot\nDisallow: /\n\nUser-agent: *\nDisallow: /admin\n");
        Assert.True(q.IsAllowed(Page));
        Assert.False(q.IsAllowed(new Uri("https://site.example/admin/x")));
    }

    [Fact]
    public void Our_specific_group_wins_over_star()
    {
        var p = RobotsPolicy.Parse("User-agent: *\nDisallow: /\n\nUser-agent: PersonalNewsDesk\nAllow: /news/\nDisallow: /\n");
        Assert.True(p.IsAllowed(Page));
        Assert.False(p.IsAllowed(new Uri("https://site.example/shop")));
    }

    [Theory]
    [InlineData("Disallow: /news\nAllow: /news/story", true)]   // longer match wins
    [InlineData("Disallow: /*.pdf$\n", true)]
    [InlineData("Disallow: /news/*y\n", false)]
    [InlineData("Disallow:\n", true)]                          // empty disallow allows everything
    [InlineData("Allow: /news/story\nDisallow: /news/story", true)] // tie goes to allow
    public void Longest_match_and_wildcards(string rules, bool allowed) =>
        Assert.Equal(allowed, RobotsPolicy.Parse("User-agent: *\n" + rules).IsAllowed(Page));

    [Fact]
    public async Task Missing_robots_allows_and_unreachable_robots_disallows()
    {
        var f = new FakeFetcher();
        Assert.True(await new RobotsCache(f).IsAllowedAsync(Page, default)); // 404
        f.Pages["https://site.example/robots.txt"] = (HttpStatusCode.ServiceUnavailable, "");
        Assert.False(await new RobotsCache(f).IsAllowedAsync(Page, default));
    }
}

public class PageExtractorTests
{
    private static readonly Uri ListUri = new("https://www.site.example/press");

    private const string ListHtml = """
        <html><body>
          <a href="/press-releases/">All press releases</a>
          <a href="/press-releases/alpha-launch"><img src="a.jpg"></a>
          <a href="/press-releases/alpha-launch">Alpha Distillery Launches Barrel Proof</a>
          <a href="https://site.example/press-releases/beta-release?utm_source=x">Beta Release Announced</a>
          <a href="https://other.example/press-releases/gamma">Elsewhere</a>
          <a href="/review/one">One</a><a href="/review/two">Two</a><a href="/review/three">Three</a>
          <a href="/review/four">Four</a><a href="/review/five">Five</a>
          <a href="javascript:void(0)">x</a>
        </body></html>
        """;

    [Fact]
    public void Links_matching_the_prefix_are_same_site_ordered_and_deduplicated()
    {
        var links = PageExtractor.ExtractLinks(ListHtml, ListUri, "/press-releases/");
        Assert.Equal(["Alpha Distillery Launches Barrel Proof", "Beta Release Announced"], links.Select(l => l.AnchorText));
        Assert.Equal("https://site.example/press-releases/beta-release", links[1].CanonicalUrl);
    }

    [Fact]
    public void Patterns_with_enough_links_are_suggested()
    {
        var s = Assert.Single(PageExtractor.SuggestPatterns(ListHtml, ListUri));
        Assert.Equal("/review/", s.Prefix);
        Assert.Equal(5, s.Count);
    }

    [Fact]
    public void Metadata_prefers_declared_values_and_never_guesses_dates_from_text()
    {
        var withLd = """
            <html><head><title>Headline | The Site</title>
            <meta property="og:description" content="Short summary.">
            <script type="application/ld+json">{"@context":"https://schema.org","@graph":[{"@type":"NewsArticle","datePublished":"2026-11-20T09:30:00-05:00"}]}</script>
            </head><body><p>Posted November 1, 2026</p></body></html>
            """;
        var m = PageExtractor.ReadMetadata(withLd);
        Assert.Null(m.Title);
        Assert.Equal("Headline", m.PageTitle);
        Assert.Equal("Short summary.", m.Description);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 14, 30, 0, TimeSpan.Zero), m.Published);

        var noDate = PageExtractor.ReadMetadata("<html><head><title>X</title></head><body><p>November 19, 2026</p></body></html>");
        Assert.Null(noDate.Published);

        var withSelector = PageExtractor.ReadMetadata("<html><body><div class='published'>November 19, 2026</div></body></html>", ".published");
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero), withSelector.Published);
        Assert.Null(PageExtractor.ReadMetadata("<div class='x'>soon</div>", "<<bad selector").Published);
    }

    [Theory]
    [InlineData("From Westward to Grimoire | Breaking Bourbon", null, "From Westward to Grimoire")]
    [InlineData("Alpha Launch - Whiskey News", "Whiskey News", "Alpha Launch")]
    [InlineData("Alpha Launch - Beta Edition", null, "Alpha Launch - Beta Edition")] // dash kept without a site-name match
    [InlineData("Title Without Suffix", null, "Title Without Suffix")]
    public void Site_suffixes_are_removed(string input, string? site, string expected) =>
        Assert.Equal(expected, PageExtractor.StripSiteSuffix(input, site));

    [Fact]
    public void Declared_headlines_are_cleaned_too()
    {
        var m = PageExtractor.ReadMetadata("<html><head><meta property='og:title' content='Mizunara  Review | Breaking Bourbon'></head></html>");
        Assert.Equal("Mizunara Review", m.Title);
    }
}

public class ExtractionCollectorTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeFetcher _web = new();
    private const string List = "https://site.example/press";

    public void Dispose() => _db.Dispose();

    private static string ListPage(int count, int first = 1) =>
        "<html><body>" + string.Concat(Enumerable.Range(first, count).Reverse().Select(i => $"<a href='/press-releases/item-{i}'>Release number {i} announced</a>")) + "</body></html>";

    private void Article(int i, string? date = "2026-11-2{0}T10:00:00Z") =>
        _web.Pages[$"https://site.example/press-releases/item-{i}"] = (HttpStatusCode.OK,
            $"<html><head><meta property='og:title' content='Release {i}'>" +
            (date is null ? "" : $"<meta property='article:published_time' content='{string.Format(date, i % 3)}'>") +
            "<meta name='description' content='About the release.'></head><body></body></html>");

    private (Project P, Source S, NewsDeskEngine Engine) Setup(ExtractionTemplate template)
    {
        var p = _db.Store.CreateProject(new Project { Name = "P", TimeZoneId = "UTC" }, new ProjectRules { WindowHours = 24 * 30, TopN = 50 }, _clock.Now);
        var s = _db.Store.GetOrAddSource(ExtractionCollector.SourceKey(template), "Site", List, ExtractionCollector.ConnectorName,
            ExtractionCollector.SerializeTemplate(template));
        _db.Store.Subscribe(new ProjectSource(p.Id, s.Id));
        var engine = new NewsDeskEngine(_db.Store, [new ExtractionCollector(_web, new RobotsCache(_web), TimeSpan.Zero)], _clock);
        return (p, s, engine);
    }

    private int ArticleRequests => _web.Requests.Count(r => r.Contains("/press-releases/"));

    [Fact]
    public async Task First_run_takes_the_newest_and_baselines_the_rest_then_only_new_links_are_fetched()
    {
        for (var i = 1; i <= 20; i++) Article(i);
        _web.Pages[List] = (HttpStatusCode.OK, ListPage(20));
        var (p, s, engine) = Setup(new ExtractionTemplate { ListUrl = List, LinkPrefix = "/press-releases/", FirstRunLimit = 5, MaxNewPerRun = 10 });

        var first = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(5, first.Sources[0].NewItems);
        Assert.Equal(5, ArticleRequests);
        Assert.Contains(first.Sources[0].Warnings, w => w.Contains("15 older items"));
        var titles = _db.Store.GetItems(p.Id, ItemView.AllMatches).Select(i => i.Title).ToHashSet();
        Assert.Equal(new HashSet<string> { "Release 20", "Release 19", "Release 18", "Release 17", "Release 16" }, titles);
        Assert.All(_db.Store.GetItems(p.Id, ItemView.AllMatches), i => Assert.NotNull(i.PublishedUtc));

        // Two new releases appear; only those two article pages are requested.
        Article(21); Article(22);
        _web.Pages[List] = (HttpStatusCode.OK, ListPage(22));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var before = ArticleRequests;
        var second = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(2, second.Sources[0].NewItems);
        Assert.Equal(before + 2, ArticleRequests);
        Assert.Equal(SourceHealth.Healthy, _db.Store.GetSource(s.Id)!.Health);
    }

    [Fact]
    public async Task New_items_beyond_the_per_run_limit_wait_and_unreadable_pages_are_retried()
    {
        for (var i = 1; i <= 6; i++) Article(i);
        _web.Pages.Remove("https://site.example/press-releases/item-5"); // fails this run
        _web.Pages[List] = (HttpStatusCode.OK, ListPage(6));
        var (p, _, engine) = Setup(new ExtractionTemplate { ListUrl = List, LinkPrefix = "/press-releases/", FirstRunLimit = 50, MaxNewPerRun = 4 });

        var first = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(3, first.Sources[0].NewItems); // 6,5(failed),4,3
        Assert.Contains(first.Sources[0].Warnings, w => w.Contains("2 more new items"));
        Assert.Contains(first.Sources[0].Warnings, w => w.Contains("could not be read"));

        Article(5);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(3, second.Sources[0].NewItems); // 5, 2, 1
        Assert.Equal(6, _db.Store.GetItems(p.Id, ItemView.AllMatches).Count);
    }

    [Fact]
    public async Task Robots_disallow_stops_extraction()
    {
        _web.Pages["https://site.example/robots.txt"] = (HttpStatusCode.OK, "User-agent: *\nDisallow: /\n");
        _web.Pages[List] = (HttpStatusCode.OK, ListPage(3));
        var (p, s, engine) = Setup(new ExtractionTemplate { ListUrl = List, LinkPrefix = "/press-releases/" });

        var report = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal("AccessDenied", report.Sources[0].Outcome);
        Assert.Contains("robots.txt", report.Sources[0].Error);
        Assert.DoesNotContain(_web.Requests, r => r == List);
        Assert.Equal(SourceHealth.Blocked, _db.Store.GetSource(s.Id)!.Health);
    }

    [Fact]
    public async Task A_rule_that_stops_matching_marks_the_source_broken()
    {
        for (var i = 1; i <= 3; i++) Article(i);
        _web.Pages[List] = (HttpStatusCode.OK, ListPage(3));
        var (p, s, engine) = Setup(new ExtractionTemplate { ListUrl = List, LinkPrefix = "/press-releases/" });
        await engine.RefreshProjectAsync(p.Id, default);

        _web.Pages[List] = (HttpStatusCode.OK, "<html><body><a href='/news/redesigned-1'>Redesigned</a></body></html>");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var report = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(SourceHealth.ExtractionBroken, _db.Store.GetSource(s.Id)!.Health);
        Assert.Contains(report.Sources[0].Warnings, w => w.Contains("page layout may have changed"));
        Assert.Equal(3, _db.Store.GetItems(p.Id, ItemView.AllMatches).Count); // earlier items kept
    }
}
