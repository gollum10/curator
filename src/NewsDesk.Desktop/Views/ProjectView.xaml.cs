using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NewsDesk.Desktop.ViewModels;

namespace NewsDesk.Desktop.Views;

public partial class ProjectView : UserControl
{
    public ProjectView()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(new DelegateCommand(FocusSearch), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new DelegateCommand(HighlightSelection), Key.M, ModifierKeys.Control));
    }

    private void FocusSearch()
    {
        if (!SearchBox.IsVisible) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void HighlightSelection()
    {
        if (DataContext is ProjectViewModel vm && ReaderBox.IsVisible) vm.AddHighlight(ReaderBox.GetSelectionRange());
    }

    private void OnHighlightClick(object sender, RoutedEventArgs e) => HighlightSelection();

    private sealed class DelegateCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
