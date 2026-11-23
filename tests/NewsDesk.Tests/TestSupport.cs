using NewsDesk.Core.Collection;
using NewsDesk.Core.Model;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Collector that returns scripted results per feed URL and counts calls.</summary>
public sealed class FakeCollector : ISourceCollector
{
    public Dictionary<string, Func<CollectResult>> Responses { get; } = new();
    public Dictionary<string, int> Calls { get; } = new();

    public bool CanHandle(Source source) => true;

    public Task<CollectResult> CollectAsync(Source source, CancellationToken cancellationToken)
    {
        Calls[source.FeedUrl] = Calls.GetValueOrDefault(source.FeedUrl) + 1;
        return Task.FromResult(Responses.TryGetValue(source.FeedUrl, out var f)
            ? f()
            : new CollectResult { Outcome = CollectOutcome.Failed, Error = "no script" });
    }

    public static CollectResult Feed(params RawItem[] items) => new() { Outcome = CollectOutcome.Success, Items = items };
}

public sealed class TempDb : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "newsdesk-tests", Guid.NewGuid().ToString("N"));
    public Database Database { get; }
    public NewsDeskStore Store { get; }

    public TempDb()
    {
        Database = new Database(Path.Combine(_dir, "test.db"));
        Store = new NewsDeskStore(Database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public static class Items
{
    public static RawItem Raw(string id, string title, string body, DateTimeOffset? published, string? link = null) => new()
    {
        PublisherId = id,
        Title = title,
        Link = link ?? $"https://example.com/{id}",
        SummaryHtml = body,
        Published = published,
    };
}
