using System.ComponentModel;
using System.Windows;
using NewsDesk.Desktop.ViewModels;

namespace NewsDesk.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Save any note being edited. Closing the window does not affect the background scheduled task.
        ((DataContext as MainViewModel)?.CurrentPage as ProjectViewModel)?.Flush();
        base.OnClosing(e);
    }
}
