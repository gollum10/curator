using System.Windows;

namespace NewsDesk.Desktop.Views;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string message, string initial)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public string Value => Input.Text;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Input.Text)) { Input.Focus(); return; }
        DialogResult = true;
    }
}
