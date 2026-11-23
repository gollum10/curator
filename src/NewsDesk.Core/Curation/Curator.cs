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
    /// <summary>Connector of the source this came from ("feed", "cisa-kev", ...).</summary>
    public string SourceConnector { get; init; } = "feed";
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

public enum ExploitationLevel { None, Reported, Confirmed }

/// <summary>Exploitation evidence for a story group. Confirmed comes only from a structured source (CISA KEV).</summary>
public sealed record ExploitationEvidence(ExploitationLevel Level, string Description, int Boost)
{
    public static readonly ExploitationEvidence None = new(ExploitationLevel.None, "", 0);
}

public sealed record BriefingSelection(int Rank, ScoredCandidate Lead, IReadOnlyList<ScoredCandidate> Supporting, string? GroupKey,
    ExploitationEvidence? Evidence = null, int? BaseScore = null, string? NewInfo = null)
{
    /// <summary>Best member score plus any exploitation-evidence boost, capped at 100.</summary>
    public int GroupScore => Math.Min(100, (BaseScore ?? Lead.Score.Total) + (Evidence?.Boost ?? 0));

    public string Reason => (Evidence is { Level: not ExploitationLevel.None } e ? e.Description + " · " : "")
                            + Lead.Match.Describe() + $" · score {GroupScore}"
                            + (Evidence is { Boost: > 0 } b ? $" (incl. +{b.Boost} exploitation evidence)" : "")
                            + (Lead.DateUnknown ? " · publication date unknown" : "")
                            + (NewInfo is not null ? " · " + NewInfo : Lead.IsUpdate ? " · updated since last briefing" : "");
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

        var aboveFloor = new List<ScoredCandidate>();
        foreach (var p in unique)
        {
            if (p.Score.Total < rules.ScoreFloor) { belowFloor++; continue; }
            aboveFloor.Add(p);
        }
        bool AlreadyDelivered(ScoredCandidate p) =>
            !rules.IncludePreviouslySeen && p.Candidate.State?.LastDeliveredHash == p.Candidate.Revision.ContentHash;

        var topN = Math.Max(1, rules.TopN);
        List<BriefingSelection> selections;
        if (rules.Mode == CurationMode.Stories)
        {
            // Repeat suppression works per story: a story returns when it gains a new or changed member (e.g. CISA KEV
            // confirmation after earlier news), with the earlier coverage kept as context.
            selections = SelectStories(aboveFloor, topN, AlreadyDelivered, out delivered);
        }
        else
        {
            var eligible = aboveFloor.Where(p => !AlreadyDelivered(p)).ToList();
            delivered = aboveFloor.Count - eligible.Count;
            selections = eligible.Take(topN).Select((p, i) => new BriefingSelection(i + 1, p, [], null)).ToList();
        }

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

    public const string KevConnector = "cisa-kev";
    /// <summary>Articles listing more CVEs than this are digests (batch advisories, patch roundups) and never merge distinct CVEs.</summary>
    public const int DigestCveThreshold = 3;
    public const int ConfirmedBoost = 10;
    public const int ReportedBoost = 4;

    [GeneratedRegex(@"\b(exploited in the wild|actively exploited|active exploitation|under active attack|zero-day|0-day)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExploitClaim();

    /// <summary>
    /// Groups by strong identifiers only (currently CVE IDs); similar titles alone never merge items. Items sharing a CVE
    /// join one group. Digest articles (more than three CVEs) attach as supporting coverage to each matching group instead of
    /// merging them. A KEV record in a group is confirmed exploitation evidence; news claims alone are "reported".
    /// Groups rank by their best member score plus an evidence boost.
    /// </summary>
    private static List<BriefingSelection> SelectStories(List<ScoredCandidate> eligible, int topN, Func<ScoredCandidate, bool> alreadyDelivered, out int suppressed)
    {
        var n = eligible.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var keys = new List<HashSet<string>>(n);
        var isKev = new bool[n];
        var isDigest = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var c = eligible[i].Candidate;
            isKev[i] = c.SourceConnector == KevConnector;
            var ids = isKev[i] && c.Article.PublisherId is { } pid && pid.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pid.ToUpperInvariant() }
                : CveId().Matches(c.Article.Title + "\n" + c.Revision.Text).Select(m => m.Value.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            keys.Add(ids);
            isDigest[i] = !isKev[i] && ids.Count > DigestCveThreshold;
        }

        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            if (isDigest[i]) continue;
            foreach (var id in keys[i])
            {
                if (owner.TryGetValue(id, out var j)) parent[Find(i)] = Find(j);
                else owner[id] = i;
            }
        }

        var groups = Enumerable.Range(0, n).Where(i => !isDigest[i]).GroupBy(Find).ToDictionary(g => g.Key, g => g.ToList());
        var attached = groups.Keys.ToDictionary(k => k, _ => new List<int>());
        for (var i = 0; i < n; i++)
        {
            if (!isDigest[i]) continue;
            var targets = keys[i].Where(owner.ContainsKey).Select(id => Find(owner[id])).Distinct().ToList();
            if (targets.Count == 0) { groups[i] = [i]; attached[i] = []; }
            else foreach (var t in targets) attached[t].Add(i);
        }

        BriefingSelection? Build(List<int> members, List<int> digests)
        {
            // Members are the story itself; attached digests are context and never make an old story "new".
            var fresh = members.Where(i => !alreadyDelivered(eligible[i])).ToList();
            if (fresh.Count == 0) return null;

            // eligible is in rank order, so a lower index means a higher score. Prefer a readable report over a raw KEV record as lead.
            var lead = members.Where(i => !isKev[i]).DefaultIfEmpty(members.Min()).Min();
            var supporting = members.Concat(digests).Where(i => i != lead).Distinct().Order().Select(i => eligible[i]).ToList();
            var ids = members.SelectMany(i => keys[i]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            var best = members.Concat(digests).Max(i => eligible[i].Score.Total);

            ExploitationEvidence evidence;
            var kev = members.Where(i => isKev[i]).Select(i => eligible[i].Candidate).FirstOrDefault();
            if (kev is not null) evidence = KevEvidence(kev);
            else if (members.Any(i => ExploitClaim().IsMatch(eligible[i].Candidate.Article.Title + "\n" + eligible[i].Candidate.Revision.Text)))
                evidence = new ExploitationEvidence(ExploitationLevel.Reported, "Exploitation reported in coverage (not confirmed by CISA KEV)", ReportedBoost);
            else evidence = ExploitationEvidence.None;

            string? newInfo = null;
            if (alreadyDelivered(eligible[lead]))
                newInfo = fresh.All(i => isKev[i]) ? "new since last briefing: CISA KEV confirmation"
                    : fresh.Count == 1 ? "new since last briefing: 1 new or updated report"
                    : $"new since last briefing: {fresh.Count} new or updated reports";

            return new BriefingSelection(0, eligible[lead], supporting, ids.Count > 0 ? string.Join(",", ids) : null, evidence, best, newInfo);
        }

        var built = groups.Select(g => (Members: g.Value, Selection: Build(g.Value, attached[g.Key]))).ToList();
        suppressed = built.Where(b => b.Selection is null).Sum(b => b.Members.Count);
        return built.Select(b => b.Selection).OfType<BriefingSelection>()
            .OrderByDescending(s => s.GroupScore).ThenByDescending(s => s.Lead.RankDate).ThenBy(s => s.Lead.Candidate.Article.Id)
            .Take(topN).Select((s, i) => s with { Rank = i + 1 }).ToList();
    }

    private static ExploitationEvidence KevEvidence(Candidate kev)
    {
        string? added = null, due = null, ransomware = null;
        if (kev.Revision.StructuredJson is { } json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                string? Get(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                added = Get("dateAdded");
                due = Get("dueDate");
                ransomware = Get("knownRansomwareCampaignUse");
            }
            catch (System.Text.Json.JsonException) { }
        }
        var parts = new List<string>();
        if (added is not null) parts.Add($"added {added}");
        if (due is not null) parts.Add($"federal patch deadline {due}");
        if (string.Equals(ransomware, "Known", StringComparison.OrdinalIgnoreCase)) parts.Add("known ransomware use");
        var detail = parts.Count > 0 ? $" ({string.Join("; ", parts)})" : "";
        return new ExploitationEvidence(ExploitationLevel.Confirmed, $"Confirmed exploited — CISA KEV{detail}", ConfirmedBoost);
    }
}
