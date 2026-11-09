using System.Windows;
using System.Windows.Input;
using FileStash.App.Controls;

namespace FileStash.App.Views.Dialogs;

public partial class MessageDialog : Window
{
    public MessageDialog(string title, string message, bool confirmation)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        CancelAction.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        // 破坏性确认默认落在取消，防止弹出时的 Enter 意外确认。
        CancelAction.IsDefault = confirmation;
        AcceptAction.IsDefault = !confirmation;
        Loaded += (_, _) => (confirmation ? CancelAction : AcceptAction).Focus();
        WindowEffects.FitToWorkArea(this);
        WindowEffects.EnableAntiFlash(this);
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
