using System.Text;
using NewsDesk.Connectors.Kev;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Curation;
using NewsDesk.Core.Model;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public class KevTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCollector _collector = new();
    private NewsDeskStore Store => _db.Store;

    public void Dispose() => _db.Dispose();

    private const string CatalogJson = """
        {
          "title": "CISA Catalog of Known Exploited Vulnerabilities",
          "catalogVersion": "2026.11.20",
          "dateReleased": "2026-11-20T15:00:00.0000Z",
          "count": 3,
          "vulnerabilities": [
            { "cveID": "CVE-2026-1001", "vendorProject": "Acme", "product": "Gateway", "vulnerabilityName": "Acme Gateway <Auth> Bypass",
              "dateAdded": "2026-11-20", "shortDescription": "Allows unauthenticated access.", "requiredAction": "Apply mitigations per vendor instructions.",
              "dueDate": "2026-12-11", "knownRansomwareCampaignUse": "Known", "notes": "https://acme.example/advisory", "cwes": ["CWE-287"] },
            { "cveID": "CVE-2026-1002", "vendorProject": "Acme", "product": "Mail", "vulnerabilityName": "Acme Mail Deserialization",
              "dateAdded": "2026-11-21", "shortDescription": "Remote code execution.", "requiredAction": "Patch.",
              "dueDate": "2026-12-12", "knownRansomwareCampaignUse": "Unknown", "notes": "", "cwes": [] },
            { "cveID": "not-a-cve", "vendorProject": "x", "product": "y", "vulnerabilityName": "junk", "dateAdded": "2026-11-21" }
          ]
        }
        """;

    [Fact]
    public void Catalog_parses_to_items_with_evidence_newest_first()
    {
        var catalog = KevCollector.Parse(Encoding.UTF8.GetBytes(CatalogJson), _clock.Now);

        Assert.Equal("2026.11.20", catalog.Version);
        Assert.Equal(["CVE-2026-1002", "CVE-2026-1001"], catalog.Items.Select(i => i.PublisherId));
        var gw = catalog.Items[1];
        Assert.Equal("CVE-2026-1001: Acme Gateway <Auth> Bypass", gw.Title);
        Assert.StartsWith("https://www.cisa.gov/known-exploited-vulnerabilities-catalog?search_api_fulltext=CVE-2026-1001", gw.Link);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), gw.Published); // date-only, anchored at midday UTC
        Assert.Equal(new DateOnly(2026, 11, 20), DateOnly.FromDateTime(gw.Published!.Value.ToOffset(TimeSpan.FromHours(-5)).DateTime));
        Assert.Contains("\"dueDate\":\"2026-12-11\"", gw.StructuredJson);
        Assert.Contains("\"catalogVersion\":\"2026.11.20\"", gw.StructuredJson);

        var normalized = Normalizer.Normalize(gw)!;
        Assert.Equal(CaptureLevel.FullText, normalized.CaptureLevel);
        Assert.Contains("Acme Gateway <Auth> Bypass", normalized.Title);
        Assert.Contains("Federal patch deadline (BOD 22-01): 2026-12-11", normalized.Text);
        Assert.Contains("Known ransomware campaign use: Known", normalized.Text);
    }

    [Theory]
    [InlineData("https://www.cisa.gov/known-exploited-vulnerabilities-catalog", true)]
    [InlineData("https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json", true)]
    [InlineData("https://www.cisa.gov/news-events/cybersecurity-advisories", false)]
    [InlineData("https://evil.example/known-exploited-vulnerabilities", false)]
    public void Kev_urls_are_recognized(string url, bool expected) => Assert.Equal(expected, KevCollector.IsKevUrl(new Uri(url)));

    private async Task<CurationResult> RunWith(params (string Feed, string Connector, int Priority, RawItem[] Items)[] sources)
    {
        var p = Store.CreateProject(new Project { Name = "Cyber", TimeZoneId = "UTC" }, new ProjectRules { Mode = CurationMode.Stories, WindowHours = 168, TopN = 10 }, _clock.Now);
        foreach (var (feed, connector, priority, items) in sources)
        {
            var s = Store.GetOrAddSource(feed, feed, null, connector);
            Store.Subscribe(new ProjectSource(p.Id, s.Id, priority, connector == KevCollector.ConnectorName));
            _collector.Responses[feed] = () => FakeCollector.Feed(items);
        }
        var report = await new NewsDeskEngine(Store, [_collector], _clock).RefreshProjectAsync(p.Id, default);
        return report.Curation!;
    }

    private RawItem[] KevItems() => KevCollector.Parse(Encoding.UTF8.GetBytes(CatalogJson), _clock.Now).Items.ToArray();

    [Fact]
    public async Task News_about_a_kev_listed_cve_is_confirmed_and_led_by_the_news_report()
    {
        var result = await RunWith(
            ("https://kev.example/catalog.json", KevCollector.ConnectorName, 50, KevItems()),
            ("https://news.example/rss", "feed", 60, [Items.Raw("n1", "Attackers hit Acme gateways via CVE-2026-1001", "Details.", _clock.Now.AddHours(-2))]));

        var gateway = result.Selections.Single(s => s.GroupKey == "CVE-2026-1001");
        Assert.Equal("Attackers hit Acme gateways via CVE-2026-1001", gateway.Lead.Candidate.Article.Title);
        Assert.Single(gateway.Supporting); // the KEV record
        Assert.Equal(ExploitationLevel.Confirmed, gateway.Evidence!.Level);
        Assert.Contains("Confirmed exploited — CISA KEV (added 2026-11-20; federal patch deadline 2026-12-11; known ransomware use)", gateway.Reason);
        Assert.Equal(ExploitationLevel.Confirmed, result.Selections.Single(s => s.GroupKey == "CVE-2026-1002").Evidence!.Level);
    }

    [Fact]
    public async Task A_delivered_story_returns_when_cisa_later_confirms_exploitation()
    {
        var p = Store.CreateProject(new Project { Name = "Cyber", TimeZoneId = "UTC" }, new ProjectRules { Mode = CurationMode.Stories, WindowHours = 168 }, _clock.Now);
        var news = Store.GetOrAddSource("https://news.example/rss", "News", null);
        Store.Subscribe(new ProjectSource(p.Id, news.Id, 60));
        _collector.Responses[news.FeedUrl] = () => FakeCollector.Feed(
            Items.Raw("n1", "Attackers hit Acme gateways via CVE-2026-1001", "Details.", _clock.Now.AddHours(-30)),
            Items.Raw("n2", "Unrelated breach story", "No CVE.", _clock.Now.AddHours(-30)));
        var engine = new NewsDeskEngine(Store, [_collector], _clock);
        var first = await engine.RefreshProjectAsync(p.Id, default);
        Assert.Equal(2, first.Curation!.Selections.Count);

        // A day later CISA adds the CVE to KEV.
        _clock.Advance(TimeSpan.FromDays(1));
        var kev = Store.GetOrAddSource("https://kev.example/catalog.json", "KEV", null, KevCollector.ConnectorName);
        Store.Subscribe(new ProjectSource(p.Id, kev.Id, 50, true));
        _collector.Responses[kev.FeedUrl] = () => FakeCollector.Feed(KevItems().Where(i => i.PublisherId == "CVE-2026-1001").ToArray());
        var second = await engine.RefreshProjectAsync(p.Id, default);

        var story = Assert.Single(second.Curation!.Selections); // the unrelated breach story stays suppressed
        Assert.Equal("Attackers hit Acme gateways via CVE-2026-1001", story.Lead.Candidate.Article.Title);
        Assert.Equal(ExploitationLevel.Confirmed, story.Evidence!.Level);
        Assert.Contains("new since last briefing: CISA KEV confirmation", story.Reason);

        // Nothing new a minute later: the whole story is suppressed again.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty((await engine.RefreshProjectAsync(p.Id, default)).Curation!.Selections);
    }

    [Fact]
    public async Task Digest_posts_do_not_merge_distinct_cves()
    {
        var digest = Items.Raw("d1", "CISA Adds Four Known Exploited Vulnerabilities to Catalog",
            "CVE-2026-1001 Acme Gateway. CVE-2026-1002 Acme Mail. CVE-2026-1003 Foo. CVE-2026-1004 Bar.", _clock.Now.AddHours(-1));
        var result = await RunWith(
            ("https://kev.example/catalog.json", KevCollector.ConnectorName, 50, KevItems()),
            ("https://cisa.example/rss", "feed", 90, [digest]));

        // Two KEV CVEs become two separate risks, each with the digest as supporting coverage; no merged mega-group.
        Assert.Equal(2, result.Selections.Count);
        Assert.All(result.Selections, s => Assert.Contains(s.Supporting, x => x.Candidate.Article.Title.StartsWith("CISA Adds Four", StringComparison.Ordinal)));
        Assert.Equal(new HashSet<string> { "CVE-2026-1001", "CVE-2026-1002" }, result.Selections.Select(s => s.GroupKey!).ToHashSet());
    }

    [Fact]
    public async Task Digest_without_matching_groups_stands_alone()
    {
        var digest = Items.Raw("d1", "Patch roundup", "CVE-2026-2001 CVE-2026-2002 CVE-2026-2003 CVE-2026-2004", _clock.Now.AddHours(-1));
        var result = await RunWith(("https://news.example/rss", "feed", 60, [digest]));
        var only = Assert.Single(result.Selections);
        Assert.Equal("Patch roundup", only.Lead.Candidate.Article.Title);
        Assert.Equal(ExploitationLevel.None, only.Evidence!.Level);
    }

    [Fact]
    public async Task Exploitation_claims_without_kev_are_reported_not_confirmed_and_rank_below_confirmed()
    {
        var result = await RunWith(
            ("https://kev.example/catalog.json", KevCollector.ConnectorName, 50, KevItems()),
            ("https://news.example/rss", "feed", 60,
            [
                Items.Raw("a", "Zero-day in Widget exploited in the wild (CVE-2026-3001)", "Vendor says attacks are ongoing.", _clock.Now.AddHours(-1)),
                Items.Raw("b", "Breach at retailer exposes data", "No CVE involved.", _clock.Now.AddHours(-1)),
            ]));

        var reported = result.Selections.Single(s => s.GroupKey == "CVE-2026-3001");
        Assert.Equal(ExploitationLevel.Reported, reported.Evidence!.Level);
        Assert.Contains("not confirmed by CISA KEV", reported.Reason);
        Assert.Equal(ExploitationLevel.None, result.Selections.Single(s => s.GroupKey is null).Evidence!.Level);
        Assert.True(result.Selections.Single(s => s.GroupKey == "CVE-2026-1002").GroupScore > 0);
        Assert.Equal(result.Selections.OrderByDescending(s => s.GroupScore).Select(s => s.Rank), result.Selections.Select(s => s.Rank));
    }
}
