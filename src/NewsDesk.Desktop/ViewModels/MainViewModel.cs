using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewsDesk.Core.Model;
using NewsDesk.Desktop.Services;

namespace NewsDesk.Desktop.ViewModels;

public sealed partial class ProjectNavItem(Guid id, string name, int unread, bool warning) : ObservableObject
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public int Unread { get; } = unread;
    public bool HasUnread => Unread > 0;
    public bool HasWarning { get; } = warning;
}

public sealed partial class ProjectTile(ProjectNavItem nav) : ObservableObject
{
    public ProjectNavItem Nav { get; } = nav;
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string NewItems { get; init; }
    public required string LastBriefing { get; init; }
    public required string NextRun { get; init; }
    public string? Warning { get; init; }
    public bool HasWarning => Warning is not null;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private bool _suppressNav;

    public MainViewModel(AppServices services, IDialogService? dialogs = null)
    {
        _s = services;
        Dialogs = dialogs ?? new Views.DialogService();
        backgroundEnabled = SafeIsBackgroundEnabled();
        ReloadProjects();
        if (Guid.TryParse(services.Settings.LastProjectId, out var last) && Projects.FirstOrDefault(p => p.Id == last) is { } nav)
            SelectedProject = nav;
        else
            ShowHome();
    }

    public IDialogService Dialogs { get; }
    public ObservableCollection<ProjectNavItem> Projects { get; } = [];
    public ObservableCollection<ProjectTile> Tiles { get; } = [];

    [ObservableProperty] private object? currentPage;
    [ObservableProperty] private ProjectNavItem? selectedProject;
    [ObservableProperty] private bool isHome;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool backgroundEnabled;
    [ObservableProperty] private bool hasProjects;

    public string ThemeLabel => _s.Settings.DarkTheme ? "☀ Light theme" : "☾ Dark theme";
    public string BackgroundLabel => BackgroundEnabled ? "Background collection: on" : "Background collection: off";

    partial void OnBackgroundEnabledChanged(bool value) => OnPropertyChanged(nameof(BackgroundLabel));

    partial void OnSelectedProjectChanged(ProjectNavItem? value)
    {
        if (_suppressNav || value is null) return;
        OpenProject(value.Id);
    }

    private void OpenProject(Guid id, DeskView view = DeskView.Briefing)
    {
        (CurrentPage as ProjectViewModel)?.Flush();
        IsHome = false;
        CurrentPage = new ProjectViewModel(_s, Dialogs, id, OnProjectChanged, view);
        _s.Settings.LastProjectId = id.ToString();
        _s.Settings.Save();
    }

    private void OnProjectChanged()
    {
        var selected = SelectedProject?.Id;
        ReloadProjects(selected);
    }

    public void ReloadProjects(Guid? select = null)
    {
        _suppressNav = true;
        try
        {
            Projects.Clear();
            Tiles.Clear();
            foreach (var p in _s.Store.GetProjects())
            {
                var stats = _s.Store.GetProjectStats(p.Id);
                var nav = new ProjectNavItem(p.Id, p.Name, stats.Unread, stats.SourceWarnings > 0);
                Projects.Add(nav);
                var run = _s.Store.GetLatestBriefingRun(p.Id);
                Tiles.Add(new ProjectTile(nav)
                {
                    Name = p.Name,
                    Description = p.Description.Length > 0 ? p.Description : $"{stats.Sources} source{(stats.Sources == 1 ? "" : "s")}",
                    NewItems = stats.Unread == 0 ? "No new items" : $"{stats.Unread} new item{(stats.Unread == 1 ? "" : "s")}",
                    LastBriefing = run is null ? "No briefing yet" : $"Last briefing {run.StartedUtc.ToLocalTime():ddd d MMM HH:mm} ({run.Status.ToString().ToLower()})",
                    NextRun = !p.Enabled ? "Paused" : p.Schedule.Kind == ScheduleKind.Manual ? "Manual refresh" : p.NextDueUtc is { } d ? $"Next run {d.ToLocalTime():ddd HH:mm}" : "Scheduled",
                    Warning = stats.Sources == 0 ? "No sources configured"
                        : stats.SourceWarnings > 0 ? $"{stats.SourceWarnings} source{(stats.SourceWarnings == 1 ? " needs" : "s need")} attention" : null,
                });
            }
            HasProjects = Projects.Count > 0;
            SelectedProject = select is null ? null : Projects.FirstOrDefault(p => p.Id == select);
        }
        finally { _suppressNav = false; }
    }

    [RelayCommand]
    private void ShowHome()
    {
        (CurrentPage as ProjectViewModel)?.Flush();
        ReloadProjects();
        IsHome = true;
        CurrentPage = this;
        _s.Settings.LastProjectId = null;
        _s.Settings.Save();
    }

    [RelayCommand]
    private void OpenTile(ProjectTile? tile)
    {
        if (tile is null) return;
        ReloadProjects(tile.Nav.Id);
        OpenProject(tile.Nav.Id);
    }

    [RelayCommand]
    private void NewProject()
    {
        var editor = ProjectEditorViewModel.ForNew(_s);
        if (!Dialogs.EditProject(editor) || editor.SavedProjectId is not { } id) return;
        ReloadProjects(id);
        OpenProject(id, DeskView.Sources);
        Status = "Project created. Add a website or feed to start collecting.";
    }

    [RelayCommand]
    private async Task RefreshDueAsync()
    {
        IsBusy = true;
        Status = "Checking projects that are due…";
        try
        {
            var progress = new Progress<string>(m => Status = m);
            var reports = await Task.Run(() => _s.Engine.RunDueAsync(CancellationToken.None, progress));
            Status = reports.Count == 0 ? "No scheduled projects are due." : $"Ran {reports.Count} due project{(reports.Count == 1 ? "" : "s")}.";
        }
        finally
        {
            IsBusy = false;
            if (CurrentPage is ProjectViewModel pvm) { pvm.Load(); ReloadProjects(pvm.Project.Id); }
            else ReloadProjects();
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        IsBusy = true;
        try
        {
            foreach (var p in _s.Store.GetProjects().Where(p => p.Enabled))
            {
                Status = $"Refreshing {p.Name}…";
                await Task.Run(() => _s.Engine.RefreshProjectAsync(p.Id, CancellationToken.None));
            }
            Status = "All projects refreshed.";
        }
        finally
        {
            IsBusy = false;
            ReloadProjects();
        }
    }

    [RelayCommand]
    private void DeleteProject()
    {
        if (CurrentPage is not ProjectViewModel pvm) return;
        if (!Dialogs.Confirm($"Delete the project “{pvm.Project.Name}”, including its briefings, notes and kept items? Exported files are not affected. This cannot be undone.",
                "Delete project")) return;
        _s.Store.DeleteProject(pvm.Project.Id);
        ShowHome();
        Status = $"Deleted {pvm.Project.Name}.";
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        _s.Settings.DarkTheme = !_s.Settings.DarkTheme;
        _s.Settings.Save();
        ThemeManager.Apply(_s.Settings.DarkTheme);
        OnPropertyChanged(nameof(ThemeLabel));
    }

    [RelayCommand]
    private void ToggleBackground()
    {
        var (ok, message) = BackgroundEnabled ? BackgroundCollection.Disable() : BackgroundCollection.Enable();
        BackgroundEnabled = SafeIsBackgroundEnabled();
        Status = message;
        if (!ok) Dialogs.Info(message);
    }

    [RelayCommand]
    private void OpenDataFolder() => System.Diagnostics.Process.Start("explorer.exe", $"\"{_s.DataDirectory}\"");

    private static bool SafeIsBackgroundEnabled()
    {
        try { return BackgroundCollection.IsEnabled(); }
        catch (Exception) { return false; }
    }
}
