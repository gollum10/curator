using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewsDesk.Desktop.Services;
using NewsDesk.Infrastructure.Maintenance;

namespace NewsDesk.Desktop.ViewModels;

/// <summary>Storage usage, cleanup, and backup/restore.</summary>
public sealed partial class DataViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;
    private readonly Action _restart;

    public DataViewModel(AppServices services, IDialogService dialogs, Action restart)
    {
        _s = services;
        _dialogs = dialogs;
        _restart = restart;
        Refresh();
    }

    public string DataFolder => _s.DataDirectory;
    public string BackupFolder => _s.BackupFolder;

    [ObservableProperty] private string usage = "";
    [ObservableProperty] private string counts = "";
    [ObservableProperty] private string lastCleanup = "";
    [ObservableProperty] private string lastAutoBackup = "";
    [ObservableProperty] private string status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpCommand), nameof(RestoreCommand), nameof(CleanUpCommand))]
    private bool isBusy;

    private bool NotBusy() => !IsBusy;

    private void Refresh()
    {
        var st = _s.Store.GetStorageStats();
        Usage = $"Database size: {FormatBytes(st.DatabaseBytes)}";
        Counts = $"{st.Projects} project(s) · {st.Sources} source(s) · {st.Articles} stored article(s) · {st.KeptItems} kept · {st.Collections} collection(s) · {st.Notes} note(s)";
        LastCleanup = _s.Store.GetMetaTime("last_retention_utc") is { } c ? $"Last cleanup: {c.ToLocalTime():g}" : "Last cleanup: not yet";
        LastAutoBackup = _s.Store.GetMetaTime("last_auto_backup_utc") is { } b ? $"Last automatic backup: {b.ToLocalTime():g}" : "Last automatic backup: not yet";
    }

    private static string FormatBytes(long b) => b switch
    {
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task BackUpAsync()
    {
        var path = _dialogs.SaveFile($"PersonalNewsDesk-backup-{DateTime.Now:yyyy-MM-dd}.zip", "Backup (*.zip)|*.zip");
        if (path is null) return;
        IsBusy = true;
        Status = "Creating backup…";
        try
        {
            var manifest = await Task.Run(() => BackupService.Create(_s.Store.Database, path, _s.SettingsPath));
            Status = $"Backup saved: {path} ({manifest.Projects} project(s), {manifest.Articles} article(s)).";
            _s.Log("manual backup: " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = "Backup failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RestoreAsync()
    {
        var path = _dialogs.OpenFile("Backup (*.zip)|*.zip");
        if (path is null) return;
        IsBusy = true;
        Status = "Checking backup…";
        try
        {
            var check = await Task.Run(() => BackupService.Inspect(path));
            if (!check.Ok) { Status = check.Message; return; }
            if (!_dialogs.Confirm(
                    $"{check.Message}\n\nRestoring replaces all current projects, items, notes and collections with the backup's contents. " +
                    $"Your current data is saved to the backups folder first, so this can be undone.\n\nThe app will restart afterwards.",
                    "Restore backup"))
            {
                Status = "Restore cancelled.";
                return;
            }

            Status = "Restoring…";
            // A mutex must be released by the thread that acquired it, so acquire, restore and release on one thread.
            var result = await Task.Run<(bool Ok, string Message, string? Safety)>(() =>
            {
                using var mutex = new Mutex(false, AppServices.WorkerMutexName);
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return (false, "Background collection is busy. Try again in a minute.", null);
                try { return BackupService.Restore(path, _s.Store.Database, _s.SettingsPath, _s.BackupFolder); }
                finally { mutex.ReleaseMutex(); }
            });
            if (!result.Ok) { Status = result.Message; return; }
            _s.Log($"restored backup {path}; previous data saved to {result.Safety}");
            _dialogs.Info($"{result.Message}\n\nYour previous data was saved to:\n{result.Safety}\n\nThe app will now restart.");
            _restart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = "Restore failed: " + ex.Message + " Your current data was not changed unless a message above said otherwise.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CleanUpAsync()
    {
        IsBusy = true;
        try
        {
            var stats = await Task.Run(() => _s.Store.ApplyRetention(DateTimeOffset.UtcNow));
            Status = stats.Describe() + " Kept items, notes, tags and collections are never removed.";
            Refresh();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenDataFolder() => Process.Start("explorer.exe", $"\"{_s.DataDirectory}\"");

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(_s.BackupFolder);
        Process.Start("explorer.exe", $"\"{_s.BackupFolder}\"");
    }
}
