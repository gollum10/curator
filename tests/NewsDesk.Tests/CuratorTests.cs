using NewsDesk.Core.Collection;
using NewsDesk.Core.Curation;
using NewsDesk.Core.Model;

namespace NewsDesk.Tests;

public class CuratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static Candidate C(string title, string body = "", double hoursAgo = 1, int priority = 50, bool primary = false,
        ProjectItem? state = null, string? url = null, bool noDate = false)
    {
        var raw = Items.Raw(Guid.NewGuid().ToString("N"), title, body, noDate ? null : Now.AddHours(-hoursAgo), url);
        var n = Normalizer.Normalize(raw)!;
        var article = new Article
        {
            Url = n.Url, CanonicalUrl = n.CanonicalUrl, Title = n.Title, PublishedUtc = n.PublishedUtc,
            DiscoveredUtc = Now.AddHours(-hoursAgo),
        };
        return new Candidate
        {
            Article = article,
            Revision = new ArticleRevision { ArticleId = article.Id, ContentHash = n.ContentHash, Text = n.Text, Excerpt = n.Excerpt },
            SourcePriority = priority, SourceIsPrimary = primary, State = state,
        };
    }

    [Fact]
    public void A04_returns_fewer_than_requested_and_explains_shortfall()
    {
        var rules = new ProjectRules { IncludeTerms = ["Arcade1Up | A1Up"], TopN = 5 };
        var result = Curator.Curate([C("Arcade1Up reveals cabinet"), C("A1Up price cut"), C("Unrelated console news")], rules, Now);

        Assert.Equal(2, result.Selections.Count);
        Assert.NotNull(result.Shortfall);
        Assert.Contains("Requested 5 articles; 2 qualified", result.Shortfall);
        Assert.Equal(1, result.RejectedByRules);
    }

    [Fact]
    public void A03_distinct_risk_mode_groups_by_cve()
    {
        var cves = new[] { "CVE-2026-1001", "CVE-2026-2002", "CVE-2026-3003" };
        var candidates = Enumerable.Range(0, 12)
            .Select(i => C($"Report {i} on {cves[i % 3]} exploitation", "vulnerability details", hoursAgo: i + 1))
            .ToList();
        var rules = new ProjectRules { IncludeTerms = ["vulnerability", "exploitation"], Mode = CurationMode.Stories, TopN = 5 };

        var result = Curator.Curate(candidates, rules, Now);

        Assert.Equal(3, result.Selections.Count);
        Assert.All(result.Selections, s => Assert.Equal(3, s.Supporting.Count));
        Assert.Equal(cves.Order(), result.Selections.Select(s => s.GroupKey!).Order());
    }

    [Fact]
    public void Match_reason_names_the_alias_that_matched()
    {
        var rules = new ProjectRules { IncludeTerms = ["arcade | retro"] };
        var sel = Assert.Single(Curator.Curate([C("Retro Recap of the week")], rules, Now).Selections);
        Assert.Contains("\"Retro\" (title)", sel.Reason);
    }

    [Fact]
    public void Similar_titles_without_identifiers_are_not_merged()
    {
        var rules = new ProjectRules { IncludeTerms = ["Arcade1Up"], Mode = CurationMode.Stories };
        var result = Curator.Curate([C("Arcade1Up announces new cabinet"), C("Arcade1Up announces new cabinet line")], rules, Now);
        Assert.Equal(2, result.Selections.Count);
    }

    [Fact]
    public void Exclusions_and_window_are_hard_filters()
    {
        var rules = new ProjectRules { IncludeTerms = ["Arcade1Up"], ExcludeTerms = ["rumor"], WindowHours = 24 };
        var result = Curator.Curate([C("Arcade1Up rumor roundup"), C("Arcade1Up launch", hoursAgo: 30), C("Arcade1Up launch today")], rules, Now);
        Assert.Single(result.Selections);
        Assert.Equal(1, result.OutsideWindow);
        Assert.Equal(1, result.RejectedByRules);
    }

    [Fact]
    public void All_mode_requires_every_term()
    {
        var rules = new ProjectRules { IncludeTerms = ["Arcade1Up", "Pac-Man"], IncludeMode = MatchMode.All };
        var result = Curator.Curate([C("Arcade1Up Pac-Man cabinet"), C("Arcade1Up Galaga cabinet")], rules, Now);
        Assert.Equal("Arcade1Up Pac-Man cabinet", Assert.Single(result.Selections).Lead.Candidate.Article.Title);
    }

    [Fact]
    public void Terms_match_on_word_boundaries()
    {
        var rules = new ProjectRules { IncludeTerms = ["NAS"] };
        var result = Curator.Curate([C("Dynastic history"), C("Synology NAS flaw")], rules, Now);
        Assert.Equal("Synology NAS flaw", Assert.Single(result.Selections).Lead.Candidate.Article.Title);
    }

    [Fact]
    public void Dismissed_items_stay_hidden_until_content_changes()
    {
        var item = C("Arcade1Up launch");
        var rules = new ProjectRules { IncludeTerms = ["Arcade1Up"] };
        var dismissedUnchanged = item with { State = new ProjectItem { IsDismissed = true, DismissedHash = item.Revision.ContentHash } };
        var dismissedChanged = item with { State = new ProjectItem { IsDismissed = true, DismissedHash = "older-hash" } };

        Assert.Empty(Curator.Curate([dismissedUnchanged], rules, Now).Selections);
        Assert.Single(Curator.Curate([dismissedChanged], rules, Now).Selections);
    }

    [Fact]
    public void Previously_delivered_unchanged_items_are_suppressed_unless_requested()
    {
        var item = C("Arcade1Up launch");
        var delivered = item with { State = new ProjectItem { LastDeliveredHash = item.Revision.ContentHash } };
        Assert.Empty(Curator.Curate([delivered], new ProjectRules { IncludeTerms = ["Arcade1Up"] }, Now).Selections);
        Assert.Single(Curator.Curate([delivered], new ProjectRules { IncludeTerms = ["Arcade1Up"], IncludePreviouslySeen = true }, Now).Selections);
    }

    [Fact]
    public void Exact_duplicates_across_sources_are_removed()
    {
        var a = C("Arcade1Up launch", url: "https://site.example/story?utm_source=feed");
        var b = C("Arcade1Up launch (syndicated)", url: "https://site.example/story");
        var result = Curator.Curate([a, b], new ProjectRules { IncludeTerms = ["Arcade1Up"] }, Now);
        Assert.Single(result.Selections);
        Assert.Equal(1, result.DuplicatesRemoved);
    }

    [Fact]
    public void Score_uses_documented_weights_and_floor()
    {
        // Title match (relevance 1.0), brand new (freshness ~1), priority 100, primary source → 100.
        var top = Curator.Curate([C("Arcade1Up", hoursAgo: 0, priority: 100, primary: true)], new ProjectRules { IncludeTerms = ["Arcade1Up"] }, Now);
        Assert.Equal(100, top.Selections[0].Lead.Score.Total);

        // Body-only match, old, low priority → below the 50 floor.
        var weak = Curator.Curate([C("Cabinet news", "mentions Arcade1Up once", hoursAgo: 23, priority: 0)], new ProjectRules { IncludeTerms = ["Arcade1Up"] }, Now);
        Assert.Empty(weak.Selections);
        Assert.Equal(1, weak.BelowFloor);
    }

    [Fact]
    public void Unknown_dates_rank_by_discovery_and_are_labelled()
    {
        var result = Curator.Curate([C("Arcade1Up undated", noDate: true)], new ProjectRules { IncludeTerms = ["Arcade1Up"] }, Now);
        var sel = Assert.Single(result.Selections);
        Assert.True(sel.Lead.DateUnknown);
        Assert.Contains("publication date unknown", sel.Reason);
    }

    [Fact]
    public void Ties_break_by_newer_date()
    {
        var older = C("Arcade1Up a", hoursAgo: 0.5);
        var newer = C("Arcade1Up b", hoursAgo: 0.4);
        var rules = new ProjectRules { IncludeTerms = ["Arcade1Up"], WindowHours = 100000 };
        var result = Curator.Curate([older, newer], rules, Now);
        Assert.Equal(result.Selections[0].Lead.Score.Total, result.Selections[1].Lead.Score.Total);
        Assert.Equal("Arcade1Up b", result.Selections[0].Lead.Candidate.Article.Title);
    }
}
