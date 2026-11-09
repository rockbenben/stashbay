using System.Windows;
using FileStash.App.Views.Dialogs;

namespace FileStash.App.Services;

internal static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image, bool showInTaskbar = false) =>
        Show(null, message, title, buttons, image, showInTaskbar);

    public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image, bool showInTaskbar = false)
    {
        if (App.InSmoke)
        {
            Serilog.Log.Information("自查模式下的消息：{Title} / {Message}", title, message);
            if (image is MessageBoxImage.Error or MessageBoxImage.Warning) App.SmokeFailure ??= message;
            return buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK;
        }

        var dialog = new MessageDialog(title, message, buttons == MessageBoxButton.YesNo)
        {
            ShowInTaskbar = showInTaskbar,
        };
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var accepted = dialog.ShowDialog() == true;
        return buttons == MessageBoxButton.YesNo
            ? (accepted ? MessageBoxResult.Yes : MessageBoxResult.No)
            : MessageBoxResult.OK;
    }
}
