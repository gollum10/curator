using System.Windows.Controls;
using System.Windows.Input;

namespace NewsDesk.Desktop.Views;

public partial class ProjectView : UserControl
{
    public ProjectView()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(new FocusSearchCommand(this), Key.F, ModifierKeys.Control));
    }

    private sealed class FocusSearchCommand(ProjectView view) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (!view.SearchBox.IsVisible) return;
            view.SearchBox.Focus();
            view.SearchBox.SelectAll();
        }
    }
}
