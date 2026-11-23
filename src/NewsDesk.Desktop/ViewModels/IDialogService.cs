namespace NewsDesk.Desktop.ViewModels;

public interface IDialogService
{
    bool EditProject(ProjectEditorViewModel editor);
    void ShowExport(ExportViewModel export);
    string? SaveFile(string defaultName, string filter);
    bool Confirm(string message, string title);
    void Info(string message);
    /// <summary>Asks for a line of text. Returns null if cancelled.</summary>
    string? Prompt(string title, string message, string initial);
    void OpenUrl(string url);
}
