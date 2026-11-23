using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using NewsDesk.Desktop.ViewModels;

namespace NewsDesk.Desktop.Views;

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public bool EditProject(ProjectEditorViewModel editor) =>
        new ProjectEditorWindow { DataContext = editor, Owner = Owner }.ShowDialog() == true;

    public void ShowExport(ExportViewModel export) =>
        new ExportWindow { DataContext = export, Owner = Owner }.ShowDialog();

    public string? SaveFile(string defaultName, string filter)
    {
        var dialog = new SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void Info(string message) =>
        MessageBox.Show(Owner!, message, "Personal News Desk", MessageBoxButton.OK, MessageBoxImage.Information);

    public string? Prompt(string title, string message, string initial)
    {
        var window = new PromptWindow(title, message, initial) { Owner = Owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    /// <summary>Opens only http(s) links, in the default browser. Never launches files or other schemes.</summary>
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
