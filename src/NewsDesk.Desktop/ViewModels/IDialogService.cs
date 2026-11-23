namespace NewsDesk.Desktop.ViewModels;

public interface IDialogService
{
    bool EditProject(ProjectEditorViewModel editor);
    void ShowExport(ExportViewModel export);
    string? SaveFile(string defaultName, string filter);
    bool Confirm(string message, string title);
    void Info(string message);
    void OpenUrl(string url);
}
