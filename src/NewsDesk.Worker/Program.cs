using System.Text;
using NewsDesk.Connectors.Feeds;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Model;
using NewsDesk.Core.Scheduling;
using NewsDesk.Exports;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Storage;

Console.OutputEncoding = Encoding.UTF8;
var cli = new Args(args);
if (cli.Command is null or "help" or "--help" or "-h") { PrintHelp(); return 0; }

var db = new Database(cli.Option("--db") ?? Database.DefaultPath());
var store = new NewsDeskStore(db);
using var http = new SafeHttp();
var robots = new RobotsCache(http);
var engine = new NewsDeskEngine(store, [new FeedCollector(http, robots: robots), new NewsDesk.Connectors.Kev.KevCollector(http),
    new NewsDesk.Connectors.Extraction.ExtractionCollector(http, robots)]);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var progress = new Progress<string>(m => Console.Error.WriteLine("  " + m));

try
{
    switch (cli.Command)
    {
        case "projects":
            foreach (var p in store.GetProjects())
                Console.WriteLine($"{p.Name,-28} {Describe(p.Schedule),-24} next: {p.NextDueUtc?.ToLocalTime().ToString("g") ?? "-",-18} sources: {store.GetProjectSources(p.Id).Count}");
            return 0;

        case "project-add":
        {
            var name = cli.Positional(0) ?? throw new UsageException("project-add <name>");
            var rules = new ProjectRules
            {
                IncludeTerms = cli.Options("--term"),
                ExcludeTerms = cli.Options("--exclude"),
                IncludeMode = cli.Option("--match") == "all" ? MatchMode.All : MatchMode.Any,
                WindowHours = int.Parse(cli.Option("--window") ?? "24"),
                TopN = int.Parse(cli.Option("--top") ?? "5"),
                Mode = cli.Flag("--stories") ? CurationMode.Stories : CurationMode.Articles,
                ScoreFloor = int.Parse(cli.Option("--floor") ?? "50"),
            };
            var schedule = ParseSchedule(cli.Option("--schedule") ?? "manual");
            var project = new Project { Name = name, Description = cli.Option("--description") ?? "", Schedule = schedule };
            project.NextDueUtc = ScheduleCalculator.NextDue(schedule, project.TimeZoneId, DateTimeOffset.UtcNow);
            store.CreateProject(project, rules, DateTimeOffset.UtcNow);
            Console.WriteLine($"Created project '{name}'. {Describe(schedule)}; top {rules.TopN} {rules.Mode.ToString().ToLower()} from the last {rules.WindowHours}h.");
            return 0;
        }

        case "source-add":
        {
            var project = RequireProject(cli.Positional(0));
            var url = cli.Positional(1) ?? throw new UsageException("source-add <project> <url>");
            var discovery = await new FeedDiscovery(http, robots).DiscoverAsync(url, cts.Token);
            Console.WriteLine(discovery.Message);
            foreach (var s in discovery.ExtractionSuggestions)
                Console.WriteLine($"  extract: {s.Prefix}  ({s.Count} links)  e.g. {string.Join(" | ", s.Samples)}");
            if (discovery.ExtractionSuggestions.Count > 0)
                Console.WriteLine($"Use: source-extract \"{project.Name}\" {discovery.ExtractionPage} <prefix>");
            if (discovery.Feeds.Count == 0) return 2;
            var pick = int.Parse(cli.Option("--pick") ?? "1");
            if (discovery.Feeds.Count > 1 && cli.Option("--pick") is null)
            {
                for (var i = 0; i < discovery.Feeds.Count; i++)
                    Console.WriteLine($"  {i + 1}. {discovery.Feeds[i].Title ?? "(untitled)"} — {discovery.Feeds[i].FeedUrl}");
                Console.WriteLine("Using feed 1. Pass --pick N to choose another.");
            }
            var feed = discovery.Feeds[Math.Clamp(pick, 1, discovery.Feeds.Count) - 1];
            var source = store.GetOrAddSource(feed.FeedUrl, feed.Title, feed.SiteUrl, feed.Connector);
            store.Subscribe(new ProjectSource(project.Id, source.Id, int.Parse(cli.Option("--priority") ?? "50"), cli.Flag("--primary")));
            Console.WriteLine($"Subscribed '{project.Name}' to {source.Title} ({source.FeedUrl}).");
            Console.WriteLine("Recent entries:");
            foreach (var item in feed.Preview.Take(5))
                Console.WriteLine($"  - {item.Title}  [{item.Published?.ToLocalTime().ToString("g") ?? "date unknown"}]");
            return 0;
        }

        case "source-extract":
        {
            var project = RequireProject(cli.Positional(0));
            var listUrl = cli.Positional(1) ?? throw new UsageException("source-extract <project> <list-page-url> <link-prefix> [--date-selector CSS]");
            var prefix = cli.Positional(2) ?? throw new UsageException("source-extract <project> <list-page-url> <link-prefix>");
            var template = new NewsDesk.Connectors.Extraction.ExtractionTemplate { ListUrl = listUrl, LinkPrefix = prefix, DateSelector = cli.Option("--date-selector") };
            var title = cli.Option("--title") ?? $"{new Uri(listUrl).Host} · {prefix}";
            var source = store.GetOrAddSource(NewsDesk.Connectors.Extraction.ExtractionCollector.SourceKey(template), title, listUrl,
                NewsDesk.Connectors.Extraction.ExtractionCollector.ConnectorName, NewsDesk.Connectors.Extraction.ExtractionCollector.SerializeTemplate(template));
            store.Subscribe(new ProjectSource(project.Id, source.Id, int.Parse(cli.Option("--priority") ?? "50"), cli.Flag("--primary")));
            Console.WriteLine($"Subscribed '{project.Name}' to {source.Title} (website extraction of {listUrl}, links under {prefix}).");
            return 0;
        }

        case "sources":
        {
            var list = cli.Positional(0) is { } pn
                ? store.GetProjectSources(RequireProject(pn).Id).Select(x => x.Source).ToList()
                : store.GetSources().ToList();
            foreach (var s in list)
            {
                Console.WriteLine($"{s.Title}\n    {s.FeedUrl}\n    health: {s.Health}; last success: {s.LastSuccessUtc?.ToLocalTime().ToString("g") ?? "never"}" +
                                  (s.LastError is null ? "" : $"; error: {s.LastError}") + (s.NextRetryUtc is { } nr ? $"; next retry {nr.ToLocalTime():g}" : ""));
            }
            return 0;
        }

        case "run":
        {
            var project = RequireProject(cli.Positional(0));
            var report = await engine.RefreshProjectAsync(project.Id, cts.Token, progress);
            PrintBriefing(project, report.Run);
            return report.Run.Status == RunStatus.Failed ? 1 : 0;
        }

        case "run-due":
        {
            // One worker at a time per user session.
            using var mutex = new Mutex(false, @"Local\PersonalNewsDesk.Worker");
            if (!mutex.WaitOne(0)) { Console.Error.WriteLine("Another worker is already running."); return 0; }
            try
            {
                var reports = await engine.RunDueAsync(cts.Token, progress);
                Console.WriteLine(reports.Count == 0 ? "No projects due." : $"{reports.Count} project run(s) completed.");
                var dataDir = Path.GetDirectoryName(Path.GetFullPath(db.Path))!;
                if (engine.RunMaintenanceIfDue(Path.Combine(dataDir, "backups"), Path.Combine(dataDir, "settings.json")) is { } done)
                    Console.WriteLine("Maintenance: " + done);
            }
            finally { mutex.ReleaseMutex(); }
            return 0;
        }

        case "briefing":
        {
            var project = RequireProject(cli.Positional(0));
            var run = store.GetLatestBriefingRun(project.Id);
            if (run is null) { Console.WriteLine("No briefing yet. Use: run <project>"); return 0; }
            PrintBriefing(project, run);
            return 0;
        }

        case "inbox":
        {
            var project = RequireProject(cli.Positional(0));
            var view = Enum.Parse<ItemView>(cli.Option("--view") ?? "Inbox", ignoreCase: true);
            foreach (var i in store.GetItems(project.Id, view, cli.Option("--search")))
                Console.WriteLine($"{(i.IsKept ? "★" : i.IsRead ? " " : "•")} {i.Title}\n    {i.SourceTitle} · {Exporters.DateLabel(i)} · {i.Url}");
            return 0;
        }

        case "export":
        {
            var project = RequireProject(cli.Positional(0));
            var format = (cli.Option("--format") ?? "md").ToLowerInvariant();
            var view = Enum.Parse<ItemView>(cli.Option("--view") ?? "Inbox", ignoreCase: true);
            var items = store.GetItems(project.Id, view);
            var options = new ExportOptions { ProjectName = project.Name, Title = $"{project.Name} — {view}", Scope = ContentScope.NotesAndExcerpt };
            if (format == "xlsx")
            {
                var file = cli.Option("--out") ?? throw new UsageException("--format xlsx requires --out FILE.xlsx");
                using (var stream = File.Create(file)) XlsxExporter.WriteItems(items, options, stream);
                Console.WriteLine($"Wrote {items.Count} items to {Path.GetFullPath(file)}");
                return 0;
            }
            var content = format switch
            {
                "csv" => Exporters.ToCsv(items, options),
                "md" or "markdown" => Exporters.ToMarkdown(items, options),
                "html" => Exporters.ToHtml(items, options),
                "txt" => Exporters.ToPlainText(items, options),
                _ => throw new UsageException("--format csv|md|html|txt"),
            };
            var output = cli.Option("--out");
            if (output is null) Console.Write(content);
            else
            {
                await File.WriteAllTextAsync(output, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == "csv"));
                Console.WriteLine($"Wrote {items.Count} items to {Path.GetFullPath(output)}");
            }
            return 0;
        }

        case "backup":
        {
            var output = cli.Positional(0) ?? throw new UsageException("backup <file.zip>");
            var dataDir = Path.GetDirectoryName(Path.GetFullPath(db.Path))!;
            var manifest = NewsDesk.Infrastructure.Maintenance.BackupService.Create(db, Path.GetFullPath(output), Path.Combine(dataDir, "settings.json"));
            Console.WriteLine($"Backup written to {Path.GetFullPath(output)} ({manifest.Projects} projects, {manifest.Articles} articles).");
            return 0;
        }

        case "cleanup":
            Console.WriteLine(store.ApplyRetention(DateTimeOffset.UtcNow).Describe());
            return 0;

        default:
            throw new UsageException($"Unknown command '{cli.Command}'.");
    }
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run with 'help' for usage.");
    return 64;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

Project RequireProject(string? name) =>
    (name is null ? null : store.FindProject(name)) ?? throw new UsageException($"Project '{name}' not found.");

void PrintBriefing(Project project, RunRecord run)
{
    Console.WriteLine();
    Console.WriteLine($"=== {project.Name} — briefing {run.StartedUtc.ToLocalTime():g} ({run.Status}) ===");
    var rules = store.GetRules(run.RuleVersionId);
    Console.WriteLine($"Window: last {rules.WindowHours}h · top {rules.TopN} {rules.Mode.ToString().ToLower()} · AI: not used");
    foreach (var row in store.GetBriefing(project.Id, run.Id))
    {
        Console.WriteLine();
        Console.WriteLine($"{row.Rank}. {row.Lead.Title}");
        Console.WriteLine($"   {row.Lead.SourceTitle} · {Exporters.DateLabel(row.Lead)} · {Exporters.CoverageLabel(row.Lead.CaptureLevel)}");
        if (row.Lead.Excerpt.Length > 0) Console.WriteLine($"   {row.Lead.Excerpt}");
        Console.WriteLine($"   Why: {row.Reason}");
        Console.WriteLine($"   {row.Lead.Url}");
        if (row.GroupKey is not null) Console.WriteLine($"   Identifiers: {row.GroupKey}");
        foreach (var s in row.Supporting) Console.WriteLine($"     + {s.Title} ({s.SourceTitle})");
    }
    Console.WriteLine();
    Console.WriteLine(run.Summary);
    var problems = run.SourceOutcomes.Where(o => o.Outcome is not ("Success" or "NotModified" or "Skipped (fresh)")).ToList();
    if (problems.Count > 0)
    {
        Console.WriteLine("Unavailable sources:");
        foreach (var o in problems)
            Console.WriteLine($"  - {o.SourceTitle}: {o.Outcome}{(o.Error is null ? "" : $" ({o.Error})")}; data as of {o.LastSuccessUtc?.ToLocalTime().ToString("g") ?? "never"}");
    }
    foreach (var o in run.SourceOutcomes.Where(o => o.Warnings.Count > 0))
        foreach (var w in o.Warnings) Console.WriteLine($"  ! {o.SourceTitle}: {w}");
}

static Schedule ParseSchedule(string spec)
{
    // manual | interval:60 | daily@07:00 | weekly:Mon,Wed,Fri@07:30
    if (spec == "manual") return new Schedule { Kind = ScheduleKind.Manual };
    if (spec.StartsWith("interval:")) return new Schedule { Kind = ScheduleKind.Interval, IntervalMinutes = Math.Max(Schedule.MinimumIntervalMinutes, int.Parse(spec[9..])) };
    var at = spec.Split('@');
    var time = at.Length > 1 ? TimeOnly.Parse(at[1]) : new TimeOnly(7, 0);
    if (at[0] == "daily") return new Schedule { Kind = ScheduleKind.Daily, TimeOfDay = time };
    if (at[0].StartsWith("weekly:"))
    {
        var days = at[0][7..].Split(',').Select(d => Enum.GetValues<DayOfWeek>().First(x => x.ToString().StartsWith(d.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        return new Schedule { Kind = ScheduleKind.Weekly, TimeOfDay = time, Days = days };
    }
    throw new UsageException("--schedule manual | interval:MIN | daily@HH:MM | weekly:Mon,Tue@HH:MM");
}

static string Describe(Schedule s) => s.Kind switch
{
    ScheduleKind.Manual => "manual",
    ScheduleKind.Interval => $"every {s.IntervalMinutes} min",
    ScheduleKind.Daily => $"daily at {s.TimeOfDay:HH\\:mm}",
    _ => $"{string.Join(",", s.Days.Select(d => d.ToString()[..3]))} at {s.TimeOfDay:HH\\:mm}",
};

static void PrintHelp() => Console.WriteLine("""
    Personal News Desk worker

      projects                                   List projects
      project-add <name> [--term "A | alias"]... [--exclude X]... [--match any|all]
                  [--window HOURS] [--top N] [--stories] [--floor 50]
                  [--schedule manual|interval:MIN|daily@HH:MM|weekly:Mon,Fri@HH:MM]
      source-add <project> <url> [--pick N] [--priority 0-100] [--primary]
      source-extract <project> <list-page-url> <link-prefix> [--date-selector CSS] [--title T] [--priority N] [--primary]
      sources [project]                          Source health
      run <project>                              Refresh now and show the briefing
      run-due                                    Run scheduled projects that are due (for Task Scheduler)
      briefing <project>                         Show the latest briefing
      inbox <project> [--view inbox|allmatches|kept|dismissed] [--search TEXT]
      export <project> [--format md|csv|xlsx|html|txt] [--view ...] [--out FILE]
      backup <file.zip>                          Consistent backup of the database and settings
      cleanup                                    Apply retention now (kept items, notes, collections are never removed)

      --db PATH   Use a specific database (default: %LOCALAPPDATA%\PersonalNewsDesk\newsdesk.db)
    """);

sealed class UsageException(string message) : Exception(message);

sealed class Args
{
    private readonly List<string> _positional = [];
    private readonly List<(string Key, string? Value)> _options = [];
    private static readonly HashSet<string> Flags = ["--stories", "--primary"];

    public Args(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                if (Flags.Contains(args[i]) || i + 1 >= args.Length) _options.Add((args[i], null));
                else _options.Add((args[i], args[++i]));
            }
            else _positional.Add(args[i]);
        }
    }

    public string? Command => _positional.FirstOrDefault();
    public string? Positional(int index) => index + 1 < _positional.Count ? _positional[index + 1] : null;
    public string? Option(string key) => _options.LastOrDefault(o => o.Key == key).Value;
    public IReadOnlyList<string> Options(string key) => _options.Where(o => o.Key == key && o.Value is not null).Select(o => o.Value!).ToList();
    public bool Flag(string key) => _options.Any(o => o.Key == key);
}
