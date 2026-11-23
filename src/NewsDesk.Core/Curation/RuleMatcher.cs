using System.Text.RegularExpressions;
using NewsDesk.Core.Model;

namespace NewsDesk.Core.Curation;

public sealed record TermHit(string Term, bool InTitle);

public sealed record MatchResult(bool Passed, IReadOnlyList<TermHit> Hits, string? RejectedBy, double Relevance)
{
    public string Describe() =>
        Hits.Count == 0 ? "All items from selected sources" :
        "Matched " + string.Join(", ", Hits.Select(h => $"\"{h.Term}\"" + (h.InTitle ? " (title)" : "")));
}

/// <summary>Applies required terms and exclusions. Matching is case-insensitive on word boundaries.</summary>
public sealed class RuleMatcher
{
    private readonly List<(string Label, Regex Pattern)> _includes;
    private readonly List<(string Label, Regex Pattern)> _excludes;
    private readonly MatchMode _mode;

    public RuleMatcher(ProjectRules rules)
    {
        _mode = rules.IncludeMode;
        _includes = rules.IncludeTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Compile).ToList();
        _excludes = rules.ExcludeTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Compile).ToList();
    }

    private static (string, Regex) Compile(string term)
    {
        var alternatives = term.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var body = string.Join("|", alternatives.Select(a =>
            Regex.Escape(a).Replace(@"\ ", @"\s+")));
        // Word-ish boundaries that also work for terms starting or ending in punctuation or digits.
        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{body})(?![\p{{L}}\p{{N}}])";
        return (alternatives.FirstOrDefault() ?? term, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
    }

    public MatchResult Match(string title, string text)
    {
        foreach (var (label, pattern) in _excludes)
            if ((pattern.Match(title) is { Success: true } m) || (m = pattern.Match(text)).Success)
                return new MatchResult(false, [], $"Excluded term \"{m.Value}\" ({label})", 0);

        if (_includes.Count == 0)
            return new MatchResult(true, [], null, 1.0);

        var hits = new List<TermHit>();
        var groupScores = new List<double>();
        foreach (var (_, pattern) in _includes)
        {
            // Report the words that actually matched, not the first alias of the group.
            if (pattern.Match(title) is { Success: true } t) { hits.Add(new(t.Value, true)); groupScores.Add(1.0); }
            else if (pattern.Match(text) is { Success: true } b) { hits.Add(new(b.Value, false)); groupScores.Add(0.6); }
            else groupScores.Add(0);
        }

        if (hits.Count == 0)
            return new MatchResult(false, [], "No include terms matched", 0);
        if (_mode == MatchMode.All && hits.Count < _includes.Count)
            return new MatchResult(false, hits, "Not all required terms matched", 0);

        var relevance = _mode == MatchMode.All
            ? groupScores.Average()
            : 0.7 * groupScores.Max() + 0.3 * groupScores.Average();
        return new MatchResult(true, hits, null, relevance);
    }
}
