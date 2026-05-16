using System.Windows;

namespace VideoFixPro;

public partial class PromptWindow : Window
{
    public string ResponseText => InputBox.Text;

    public PromptWindow(string title, string message, string initialValue = "")
    {
        InitializeComponent();
        PromptTitle.Text = title;
        PromptMessage.Text = message;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public static bool TryShow(Window owner, string title, string message, string initialValue, out string value)
    {
        var dlg = new PromptWindow(title, message, initialValue)
        {
            Owner = owner
        };
        var ok = dlg.ShowDialog() == true;
        value = ok ? dlg.ResponseText : initialValue;
        return ok;
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {

    }
}
