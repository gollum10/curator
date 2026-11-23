using System.Windows;
using System.Windows.Threading;
using NewsDesk.Desktop.Services;
using NewsDesk.Desktop.ViewModels;
using NewsDesk.Desktop.Views;

namespace NewsDesk.Desktop;

public partial class App : Application
{
    private AppServices? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = new AppServices();

        if (e.Args.Contains("--run-due"))
        {
            // Headless mode used by the scheduled task: no window, one worker at a time.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await RunDueHeadlessAsync(_services);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ThemeManager.Apply(_services.Settings.DarkTheme);
        var window = new MainWindow { DataContext = new MainViewModel(_services, restart: Restart) };
        MainWindow = window;
        window.Show();
        _ = Task.Run(() => RunMaintenance(_services));
    }

    private static void RunMaintenance(AppServices services)
    {
        try
        {
            if (services.Engine.RunMaintenanceIfDue(services.BackupFolder, services.SettingsPath) is { } done)
                services.Log("maintenance: " + done);
        }
        catch (Exception ex)
        {
            services.Log($"maintenance failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Starts a fresh instance and exits this one (used after restoring a backup).</summary>
    private void Restart()
    {
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        Shutdown();
    }

    private static async Task RunDueHeadlessAsync(AppServices services)
    {
        using var mutex = new Mutex(false, AppServices.WorkerMutexName);
        if (!mutex.WaitOne(0)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
            var reports = await Task.Run(() => services.Engine.RunDueAsync(cts.Token));
            foreach (var r in reports)
                services.Log($"scheduled run {r.Run.Trigger}: {r.Run.Status} — {r.Run.Summary}");
            RunMaintenance(services);
        }
        catch (Exception ex)
        {
            services.Log($"scheduled run failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log($"UI error: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "Personal News Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
