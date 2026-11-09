using FileStash.App.Controls;
using System.Windows;
using FileStash.App.ViewModels;

namespace FileStash.App.Views;

public partial class GitStatusDialog : Window
{
    public GitStatusDialog(GitStatusViewModel viewModel)
    {
        InitializeComponent();

        // 按窗口所在那块屏的工作区封顶：1366x768@150% 只有 464 DIP 可用，
        // 固定高度超出去的话，底部的按钮会落到屏幕外
        WindowEffects.FitToWorkArea(this);

        DataContext = viewModel;
        RepoText.Text = viewModel.RepoRoot;

        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
