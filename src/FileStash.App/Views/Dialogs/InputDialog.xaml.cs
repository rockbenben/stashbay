using System.Windows;

namespace FileStash.App.Views.Dialogs;

/// <summary>单行文本输入对话框（重命名等）。</summary>
public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => InputBox.Focus();
    }

    public string InputText
    {
        get => InputBox.Text;
        set => InputBox.Text = value;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            return;
        }

        DialogResult = true;
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (InputBox.Text.Length > 0 && InputBox.Text.Trim().Length == 0)
        {
            InputBox.Text = string.Empty;
        }
    }
}
