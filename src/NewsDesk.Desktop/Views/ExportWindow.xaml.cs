using System.Windows;

namespace NewsDesk.Desktop.Views;

public partial class ExportWindow : Window
{
    public ExportWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
