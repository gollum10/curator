using System.IO;
using System.Text.Json;
using NewsDesk.Connectors.Feeds;
using NewsDesk.Connectors.Http;
using NewsDesk.Infrastructure;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Desktop.Services;

/// <summary>Composition root shared by the UI and the headless scheduled run.</summary>
public sealed class AppServices : IDisposable
{
    /// <summary>Held by whoever is collecting or restoring, so the scheduled task and the app never collide.</summary>
    public const string WorkerMutexName = @"Local\PersonalNewsDesk.Worker";

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string BackupFolder => Path.Combine(DataDirectory, "backups");
    public NewsDeskStore Store { get; }
    public NewsDeskEngine Engine { get; }
    public FeedDiscovery Discovery { get; }
    public AppSettings Settings { get; }
    private readonly SafeHttp _http;

    public AppServices()
    {
        var dbPath = Database.DefaultPath();
        DataDirectory = Path.GetDirectoryName(dbPath)!;
        Store = new NewsDeskStore(new Database(dbPath));
        _http = new SafeHttp();
        Engine = new NewsDeskEngine(Store, [new FeedCollector(_http)]);
        Discovery = new FeedDiscovery(_http);
        Settings = AppSettings.Load(Path.Combine(DataDirectory, "settings.json"));
    }

    public void Log(string message)
    {
        try
        {
            var path = Path.Combine(DataDirectory, "activity.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class AppSettings
{
    public bool DarkTheme { get; set; }
    public string? LastProjectId { get; set; }

    private string _path = "";

    public static AppSettings Load(string path)
    {
        AppSettings s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { s = new(); }
        s._path = path;
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException) { }
    }
}
