using FileStash.App.Controls;
using System.Windows;
using FileStash.App.ViewModels;

namespace FileStash.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        // 按窗口所在那块屏的工作区封顶：1366x768@150% 只有 464 DIP 可用，
        // 固定高度超出去的话，底部的按钮会落到屏幕外
        WindowEffects.FitToWorkArea(this);

        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.RefreshStartupCommand.ExecuteAsync(null);
            await viewModel.RefreshStorageAsync();
        };
        Closing += (_, e) => { if (viewModel.IsStartupBusy) e.Cancel = true; };
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            if (!viewModel.SaveCommand.CanExecute(null)) return;
            await viewModel.SaveCommand.ExecuteAsync(null);
            if (!viewModel.LastSaveSucceeded) return;
        }

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            if (viewModel.IsStartupBusy) return;
            viewModel.CancelCommand.Execute(null);
        }

        Close();
    }

    private void OnClearHotkeyAddClick(object sender, RoutedEventArgs e) => HotkeyAddBox.Text = string.Empty;

    private void OnClearHotkeyWorkbenchClick(object sender, RoutedEventArgs e) => HotkeyWorkbenchBox.Text = string.Empty;

    private void OnClearHotkeyPasteClick(object sender, RoutedEventArgs e) => HotkeyPasteBox.Text = string.Empty;
}
