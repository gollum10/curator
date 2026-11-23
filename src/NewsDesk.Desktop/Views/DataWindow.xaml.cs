using System.Windows;

namespace NewsDesk.Desktop.Views;

public partial class DataWindow : Window
{
    public DataWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
