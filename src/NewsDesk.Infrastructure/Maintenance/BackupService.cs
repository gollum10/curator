using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NewsDesk.Infrastructure.Storage;

namespace NewsDesk.Infrastructure.Maintenance;

public sealed record BackupManifest(int Format, string App, DateTimeOffset CreatedUtc, int SchemaVersion, int Projects, int Articles);

public sealed record BackupCheck(bool Ok, string Message, BackupManifest? Manifest);

/// <summary>
/// Backups are zip files holding a consistent database snapshot (VACUUM INTO, never a copy of the live file),
/// the settings file, and a manifest. No credentials are stored in the database or settings, so none are in backups.
/// </summary>
public static class BackupService
{
    public const string DatabaseEntry = "newsdesk.db";
    public const string SettingsEntry = "settings.json";
    public const string ManifestEntry = "manifest.json";
    private const int Format = 1;
    private const long MaxEntryBytes = 4L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static BackupManifest Create(Database db, string zipPath, string? settingsPath = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "newsdesk-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var snapshot = Path.Combine(work, DatabaseEntry);
            using (var c = db.Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "VACUUM INTO $path";
                cmd.Parameters.AddWithValue("$path", snapshot);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var manifest = ReadManifestFromDatabase(snapshot);
            var tmpZip = zipPath + ".partial";
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(snapshot, DatabaseEntry, CompressionLevel.Optimal);
                if (settingsPath is not null && File.Exists(settingsPath))
                    zip.CreateEntryFromFile(settingsPath, SettingsEntry, CompressionLevel.Optimal);
                using var w = new StreamWriter(zip.CreateEntry(ManifestEntry).Open());
                w.Write(JsonSerializer.Serialize(manifest, Json));
            }
            File.Move(tmpZip, zipPath, overwrite: true);
            return manifest;
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private static BackupManifest ReadManifestFromDatabase(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        int Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return new BackupManifest(Format, "PersonalNewsDesk", DateTimeOffset.UtcNow,
            Scalar("SELECT COALESCE(MAX(version), 0) FROM schema_version"), Scalar("SELECT COUNT(*) FROM projects"), Scalar("SELECT COUNT(*) FROM articles"));
    }

    /// <summary>Checks a backup without changing anything: expected entries only, readable manifest, intact database, supported schema.</summary>
    public static BackupCheck Inspect(string zipPath)
    {
        var work = Path.Combine(Path.GetTempPath(), "newsdesk-inspect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (ok, message, manifest, _) = Extract(zipPath, work);
            return new BackupCheck(ok, message, manifest);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private static (bool Ok, string Message, BackupManifest? Manifest, string? DbPath) Extract(string zipPath, string work)
    {
        Directory.CreateDirectory(work);
        BackupManifest? manifest = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                // Only the known flat names are accepted; anything else (paths, extra files) means this is not our backup.
                if (entry.FullName is not (DatabaseEntry or SettingsEntry or ManifestEntry))
                    return (false, $"Unexpected file in backup: {entry.FullName}", null, null);
                if (entry.Length > MaxEntryBytes)
                    return (false, "Backup entry is too large.", null, null);
                entry.ExtractToFile(Path.Combine(work, entry.FullName), overwrite: true);
            }
        }
        catch (InvalidDataException)
        {
            return (false, "This file is not a valid backup (not a zip archive).", null, null);
        }

        var manifestPath = Path.Combine(work, ManifestEntry);
        var dbPath = Path.Combine(work, DatabaseEntry);
        if (!File.Exists(manifestPath) || !File.Exists(dbPath))
            return (false, "This file is not a Personal News Desk backup (missing database or manifest).", null, null);
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), Json);
        }
        catch (JsonException)
        {
            return (false, "The backup manifest is unreadable.", null, null);
        }
        if (manifest is null || manifest.App != "PersonalNewsDesk" || manifest.Format != Format)
            return (false, "This file is not a Personal News Desk backup.", null, null);
        if (manifest.SchemaVersion > Database.SchemaVersion)
            return (false, "This backup was made by a newer version of the app. Update the app before restoring it.", manifest, null);

        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            var result = cmd.ExecuteScalar() as string;
            if (result != "ok") return (false, "The backup database failed its integrity check.", manifest, null);
            cmd.CommandText = "SELECT COUNT(*) FROM projects";
            cmd.ExecuteScalar();
        }
        catch (SqliteException ex)
        {
            return (false, "The backup database could not be read: " + ex.Message, manifest, null);
        }

        return (true, $"Backup from {manifest.CreatedUtc.ToLocalTime():g} with {manifest.Projects} project(s) and {manifest.Articles} stored article(s).", manifest, dbPath);
    }

    /// <summary>
    /// Replaces the database (and settings, if present in the backup) with the backup's contents. The caller must ensure no
    /// other code is using the database and should restart afterwards. A safety backup of the current data is written first.
    /// </summary>
    public static (bool Ok, string Message, string? SafetyBackup) Restore(string zipPath, Database liveDb, string? settingsPath, string safetyFolder)
    {
        var work = Path.Combine(Path.GetTempPath(), "newsdesk-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (ok, message, _, extractedDb) = Extract(zipPath, work);
            if (!ok || extractedDb is null) return (false, message, null);

            Directory.CreateDirectory(safetyFolder);
            var safety = Path.Combine(safetyFolder, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            Create(liveDb, safety, settingsPath);

            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(liveDb.Path + suffix)) File.Delete(liveDb.Path + suffix);
            File.Copy(extractedDb, liveDb.Path, overwrite: true);

            var extractedSettings = Path.Combine(work, SettingsEntry);
            if (settingsPath is not null && File.Exists(extractedSettings))
                File.Copy(extractedSettings, settingsPath, overwrite: true);

            // Bring an older backup's schema up to date now, so problems surface before the app restarts.
            _ = new Database(liveDb.Path);
            SqliteConnection.ClearAllPools();
            return (true, "Restored. " + message, safety);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>Writes a weekly automatic backup into <paramref name="folder"/> and keeps the newest <paramref name="keep"/>.</summary>
    public static string? AutoBackupIfDue(NewsDeskStore store, Database db, string folder, string? settingsPath, DateTimeOffset nowUtc, int keep = 4)
    {
        if (store.GetMetaTime("last_auto_backup_utc") is { } last && nowUtc - last < TimeSpan.FromDays(7)) return null;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"auto-{nowUtc.ToLocalTime():yyyyMMdd-HHmmss}.zip");
        Create(db, path, settingsPath);
        store.SetMeta("last_auto_backup_utc", nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        foreach (var old in new DirectoryInfo(folder).GetFiles("auto-*.zip").OrderByDescending(f => f.Name).Skip(keep))
        {
            try { old.Delete(); } catch (IOException) { }
        }
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
