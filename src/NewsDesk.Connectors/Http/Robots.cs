using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NewsDesk.Connectors.Http;

/// <summary>
/// robots.txt rules for one host (RFC 9309): the most specific group matching our product token applies, else "*";
/// the longest matching Allow/Disallow pattern wins (Allow on ties); "*" and "$" wildcards are supported.
/// </summary>
public sealed class RobotsPolicy
{
    public const string ProductToken = "PersonalNewsDesk";
    private readonly List<(bool Allow, string Pattern, Regex Regex)> _rules;

    private RobotsPolicy(List<(bool, string, Regex)> rules) => _rules = rules;

    public static readonly RobotsPolicy AllowAll = new([]);
    public static readonly RobotsPolicy DisallowAll = new([(false, "/", Compile("/"))]);

    public static RobotsPolicy Parse(string text, string productToken = ProductToken)
    {
        // Collect groups: consecutive user-agent lines followed by rules.
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        List<string>? agents = null;
        List<(bool, string)>? rules = null;
        var lastWasAgent = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent || agents is null)
                {
                    agents = [];
                    rules = [];
                    groups.Add((agents, rules));
                }
                agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if (key is "allow" or "disallow" && rules is not null)
            {
                lastWasAgent = false;
                if (value.Length == 0) continue; // empty Disallow means allow everything
                rules.Add((key == "allow", value));
            }
            else lastWasAgent = false;
        }

        var token = productToken.ToLowerInvariant();
        var specific = groups.Where(g => g.Agents.Any(a => a != "*" && token.StartsWith(a, StringComparison.Ordinal))).ToList();
        var chosen = specific.Count > 0 ? specific : groups.Where(g => g.Agents.Contains("*")).ToList();
        var merged = chosen.SelectMany(g => g.Rules).Select(r => (r.Item1, r.Item2, Compile(r.Item2))).ToList();
        return new RobotsPolicy(merged);
    }

    private static Regex Compile(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '*') sb.Append(".*");
            else if (ch == '$' && i == pattern.Length - 1) sb.Append('$');
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }

    public bool IsAllowed(Uri uri)
    {
        var path = uri.PathAndQuery;
        if (path == "/robots.txt") return true;
        (bool Allow, int Length)? best = null;
        foreach (var (allow, pattern, regex) in _rules)
        {
            if (!regex.IsMatch(path)) continue;
            var len = pattern.Length;
            if (best is null || len > best.Value.Length || (len == best.Value.Length && allow)) best = (allow, len);
        }
        return best?.Allow ?? true;
    }
}

/// <summary>Fetches and caches robots.txt per host for a day. Missing (4xx) means allow; unreachable (5xx, errors) means disallow.</summary>
public sealed class RobotsCache(IFetcher fetcher, TimeSpan? lifetime = null)
{
    private readonly ConcurrentDictionary<string, (RobotsPolicy Policy, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromHours(24);

    public async Task<RobotsPolicy> GetAsync(Uri uri, CancellationToken ct)
    {
        var key = uri.GetLeftPart(UriPartial.Authority);
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow) return hit.Policy;
        RobotsPolicy policy;
        try
        {
            var r = await fetcher.GetAsync(new Uri(new Uri(key), "/robots.txt"), null, null, ct);
            var code = (int)r.Status;
            policy = code is >= 200 and < 300 ? RobotsPolicy.Parse(Encoding.UTF8.GetString(r.Body))
                : code is >= 400 and < 500 ? RobotsPolicy.AllowAll
                : RobotsPolicy.DisallowAll;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            policy = RobotsPolicy.DisallowAll;
        }
        _cache[key] = (policy, DateTimeOffset.UtcNow + _lifetime);
        return policy;
    }

    public async Task<bool> IsAllowedAsync(Uri uri, CancellationToken ct) => (await GetAsync(uri, ct)).IsAllowed(uri);
}
