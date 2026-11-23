using System.Text.RegularExpressions;
using NewsDesk.Core.Model;

namespace NewsDesk.Core.Curation;

/// <summary>Everything the curator needs to know about one article for one project.</summary>
public sealed record Candidate
{
    public required Article Article { get; init; }
    public required ArticleRevision Revision { get; init; }
    public int SourcePriority { get; init; } = 50;
    public bool SourceIsPrimary { get; init; }
    public ProjectItem? State { get; init; }
}

public sealed record ScoreBreakdown(double Relevance, double Freshness, double SourcePriority, double DirectEvidence)
{
    public const double RelevanceWeight = 0.50, FreshnessWeight = 0.25, PriorityWeight = 0.15, EvidenceWeight = 0.10;

    public int Total => (int)Math.Round(100 * (RelevanceWeight * Relevance + FreshnessWeight * Freshness
                                               + PriorityWeight * SourcePriority + EvidenceWeight * DirectEvidence));
}

public sealed record ScoredCandidate(Candidate Candidate, MatchResult Match, ScoreBreakdown Score, DateTimeOffset RankDate, bool DateUnknown)
{
    public bool IsUpdate => Candidate.State?.LastDeliveredHash is { } h && h != Candidate.Revision.ContentHash;
}

public sealed record BriefingSelection(int Rank, ScoredCandidate Lead, IReadOnlyList<ScoredCandidate> Supporting, string? GroupKey)
{
    public string Reason => Lead.Match.Describe() + $" · score {Lead.Score.Total}" + (Lead.DateUnknown ? " · publication date unknown" : "")
                            + (Lead.IsUpdate ? " · updated since last briefing" : "");
}

public sealed record CurationResult
{
    public required IReadOnlyList<BriefingSelection> Selections { get; init; }
    /// <summary>All items that passed hard filters (matched, inside window, not dismissed), ranked.</summary>
    public required IReadOnlyList<ScoredCandidate> Matches { get; init; }
    public int Considered { get; init; }
    public int RejectedByRules { get; init; }
    public int OutsideWindow { get; init; }
    public int Dismissed { get; init; }
    public int BelowFloor { get; init; }
    public int AlreadyDelivered { get; init; }
    public int DuplicatesRemoved { get; init; }
    public int Requested { get; init; }
    public string? Shortfall { get; init; }
}

/// <summary>
/// Deterministic curation: hard filters → exact duplicate removal → optional grouping → ranking → top N.
/// Scores are a heuristic (0-100), not a probability or quality guarantee.
/// </summary>
public static partial class Curator
{
    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,7}\b", RegexOptions.IgnoreCase)]
    private static partial Regex CveId();

    public static CurationResult Curate(IEnumerable<Candidate> candidates, ProjectRules rules, DateTimeOffset nowUtc)
    {
        var matcher = new RuleMatcher(rules);
        var window = TimeSpan.FromHours(Math.Max(1, rules.WindowHours));
        int considered = 0, rejected = 0, outside = 0, dismissed = 0, belowFloor = 0, delivered = 0;
        var passed = new List<ScoredCandidate>();

        foreach (var c in candidates)
        {
            considered++;
            var dateUnknown = c.Article.PublishedUtc is null && c.Article.UpdatedUtc is null;
            var rankDate = c.Article.PublishedUtc ?? c.Article.UpdatedUtc ?? c.Article.DiscoveredUtc;
            var age = nowUtc - rankDate;
            if (age > window) { outside++; continue; }

            if (c.State is { IsDismissed: true } s && s.DismissedHash == c.Revision.ContentHash) { dismissed++; continue; }

            var match = matcher.Match(c.Article.Title, c.Revision.Text);
            if (!match.Passed) { rejected++; continue; }

            var freshness = Math.Clamp(1 - Math.Max(0, age.TotalSeconds) / window.TotalSeconds, 0, 1);
            var score = new ScoreBreakdown(match.Relevance, freshness, Math.Clamp(c.SourcePriority, 0, 100) / 100.0, c.SourceIsPrimary ? 1 : 0);
            passed.Add(new ScoredCandidate(c, match, score, rankDate, dateUnknown));
        }

        // Exact duplicates across sources: same canonical URL or identical content. Keep the best-scored copy.
        var ranked = passed.OrderByDescending(p => p.Score.Total).ThenByDescending(p => p.RankDate).ThenBy(p => p.Candidate.Article.Id).ToList();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<ScoredCandidate>();
        foreach (var p in ranked)
        {
            var urlNew = seenUrls.Add(p.Candidate.Article.CanonicalUrl);
            var hashNew = seenHashes.Add(p.Candidate.Revision.ContentHash);
            if (urlNew && hashNew) unique.Add(p);
        }
        var duplicates = ranked.Count - unique.Count;

        var eligible = new List<ScoredCandidate>();
        foreach (var p in unique)
        {
            if (p.Score.Total < rules.ScoreFloor) { belowFloor++; continue; }
            if (!rules.IncludePreviouslySeen && p.Candidate.State?.LastDeliveredHash == p.Candidate.Revision.ContentHash) { delivered++; continue; }
            eligible.Add(p);
        }

        var topN = Math.Max(1, rules.TopN);
        var selections = rules.Mode == CurationMode.Stories
            ? SelectStories(eligible, topN)
            : eligible.Take(topN).Select((p, i) => new BriefingSelection(i + 1, p, [], null)).ToList();

        string? shortfall = null;
        if (selections.Count < topN)
        {
            var unit = rules.Mode == CurationMode.Stories ? "distinct stories" : "articles";
            shortfall = $"Requested {topN} {unit}; {selections.Count} qualified. " +
                        $"{considered} considered: {outside} outside the {rules.WindowHours}h window, {rejected} did not match rules, " +
                        $"{dismissed} dismissed, {duplicates} duplicates, {belowFloor} below score {rules.ScoreFloor}, {delivered} already delivered.";
        }

        return new CurationResult
        {
            Selections = selections, Matches = unique, Considered = considered, RejectedByRules = rejected,
            OutsideWindow = outside, Dismissed = dismissed, BelowFloor = belowFloor, AlreadyDelivered = delivered,
            DuplicatesRemoved = duplicates, Requested = topN, Shortfall = shortfall,
        };
    }

    /// <summary>
    /// Groups by strong identifiers only (currently CVE IDs). Similar titles alone never merge items.
    /// Items sharing any identifier join the same group (union-find); the highest-ranked item leads.
    /// </summary>
    private static List<BriefingSelection> SelectStories(List<ScoredCandidate> eligible, int topN)
    {
        var parent = Enumerable.Range(0, eligible.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var keys = new List<HashSet<string>>();
        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < eligible.Count; i++)
        {
            var c = eligible[i].Candidate;
            var ids = CveId().Matches(c.Article.Title + "\n" + c.Revision.Text).Select(m => m.Value.ToUpperInvariant()).ToHashSet();
            keys.Add(ids);
            foreach (var id in ids)
            {
                if (owner.TryGetValue(id, out var j)) parent[Find(i)] = Find(j);
                else owner[id] = i;
            }
        }

        // eligible is already in rank order, so the first member of each group is its lead.
        var groups = eligible.Select((p, i) => (p, i)).GroupBy(x => Find(x.i)).Select(g => g.ToList()).ToList();
        return groups.Take(topN).Select((g, rank) =>
        {
            var ids = g.SelectMany(x => keys[x.i]).Distinct().Order().ToList();
            return new BriefingSelection(rank + 1, g[0].p, g.Skip(1).Select(x => x.p).ToList(), ids.Count > 0 ? string.Join(",", ids) : null);
        }).ToList();
    }
}
