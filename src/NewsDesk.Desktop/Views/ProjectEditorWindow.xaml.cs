using System.Windows;
using NewsDesk.Desktop.ViewModels;

namespace NewsDesk.Desktop.Views;

public partial class ProjectEditorWindow : Window
{
    public ProjectEditorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectEditorViewModel vm && vm.Save()) DialogResult = true;
    }
}
