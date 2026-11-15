using System.IO;
using System.Windows;
using System.Windows.Controls;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.Core.Utils;

namespace FileStash.App.Views.Dialogs;

/// <summary>
/// 设置根目录：以面包屑方式列出源路径的每一级祖先，单选其一作为根，实时预览新的子路径。
/// </summary>
public partial class SetRootDialog : Window
{
    private sealed record AncestorOption(string Path, string Display, string Preview);

    public SetRootDialog()
    {
        InitializeComponent();

        // SizeToContent 的窗口必须按所在那块屏的工作区封顶，
        // 否则内容一多"确定/取消"整行会跑到屏幕外，需按工作区高度封顶 MaxHeight
        WindowEffects.FitToWorkArea(this);
    }

    public string? SelectedRoot { get; private set; }

    public void SetSource(string sourcePath)
    {
        SelectedRoot = null;
        ConfirmButton.IsEnabled = false;
        var options = new List<AncestorOption>();

        var directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sourcePath));

        var current = directory;

        while (!string.IsNullOrEmpty(current))
        {
            var relative = PathUtil.GetRelative(current, sourcePath);

            options.Add(new AncestorOption(
                current,
                current,
                string.IsNullOrEmpty(relative) ? Strings.EmptyRelativePath : relative));

            var parent = Path.GetDirectoryName(current);

            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        AncestorList.ItemsSource = options;
        AncestorList.SelectedIndex = 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AncestorList.SelectedItem is not AncestorOption option)
        {
            return;
        }

        SelectedRoot = option.Path;
        ConfirmButton.IsEnabled = true;
        PreviewText.Text = Strings.NewRelativePathLabel + option.Preview;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (SelectedRoot is null)
        {
            return;
        }

        DialogResult = true;
    }
}
