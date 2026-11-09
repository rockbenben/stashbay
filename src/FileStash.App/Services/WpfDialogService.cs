using System.Windows;
using FileStash.App.Resources;
using FileStash.App.Views;
using FileStash.App.Views.Dialogs;
using FileStash.Core.Models;
using Microsoft.Win32;

namespace FileStash.App.Services;

/// <summary>WPF 版的界面交互实现。</summary>
public sealed class WpfDialogService : IDialogService
{
    private string? _lastStashDirectory;

    public IReadOnlyList<string>? PickStashPaths()
    {
        if (App.InSmoke) return null;

        var dialog = new StashPickerDialog(_lastStashDirectory) { Owner = GetOwner() };
        if (dialog.ShowDialog() != true) return null;

        _lastStashDirectory = dialog.CurrentDirectory;
        return dialog.SelectedPaths;
    }

    public string? PickFolder()
    {
        if (App.InSmoke) return null;

        var dialog = new OpenFolderDialog { Title = Strings.BrowseTargetTooltip };
        return dialog.ShowDialog(GetOwner()) == true ? dialog.FolderName : null;
    }

    public string? PromptText(string title, string initialValue)
    {
        var dialog = new InputDialog { Title = title, InputText = initialValue };
        var owner = GetOwner();

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }

    public bool Confirm(string title, string message)
    {
        // 自查模式不弹模态框：模态框有自己的消息循环，会把进程吊住
        // （既不写 marker 也不退出，CI 那边只看到一个超时的进程）
        if (App.InSmoke)
        {
            Serilog.Log.Information("自查模式下的确认请求（不弹模态框）：{Title} / {Message}", title, message);
            return false;
        }

        var owner = GetOwner();

        var result = owner is null
            ? ThemedMessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : ThemedMessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }

    public string? PickSetRoot(string sourcePath)
    {
        var dialog = new SetRootDialog();
        var owner = GetOwner();

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.SetSource(sourcePath);

        return dialog.ShowDialog() == true ? dialog.SelectedRoot : null;
    }

    public string? PickGroup(IReadOnlyList<(string Id, string Name)> groups, string currentGroupId)
    {
        var dialog = new GroupPickerDialog();
        var owner = GetOwner();

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        var models = groups
            .Select(group => new StashGroup { Id = group.Id, Name = group.Name })
            .ToList();

        dialog.SetGroups(models, currentGroupId);

        return dialog.ShowDialog() == true ? dialog.SelectedGroupId : null;
    }

    public IReadOnlyList<string>? GetClipboardPaths()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return null;
            }

            return Clipboard.GetFileDropList().Cast<string>().ToArray();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 剪贴板被别的进程锁着
            return null;
        }
    }

    public void SetClipboardPaths(IReadOnlyList<string> paths)
    {
        try
        {
            var collection = new System.Collections.Specialized.StringCollection();
            collection.AddRange([.. paths]);

            Clipboard.SetFileDropList(collection);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            throw new InvalidOperationException(Strings.ClipboardBusyLong);
        }
    }

    public void ShowInfo(string title, string message) => ShowToast(title, message);

    public void ShowWarning(string title, string message)
    {
        if (App.InSmoke)
        {
            Serilog.Log.Warning("自查模式下的警告（不弹模态框）：{Title} / {Message}", title, message);
            App.SmokeFailure ??= $"{title}：{message}";
            return;
        }

        var owner = GetOwner();

        if (owner is null)
        {
            ThemedMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            ThemedMessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>飘一个非模态提示。</summary>
    public void ShowToast(string title, string summary)
    {
        if (App.InSmoke)
        {
            Serilog.Log.Information("自查模式下的回执（不弹提示）：{Title} / {Summary}", title, summary);
            return;
        }

        // 自查模式下不弹窗：提示窗口在无头环境里既看不见也会挡住后面的检查
        var toast = new ResultToast(title, summary);
        toast.Show();
    }

    private static Window? GetOwner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
        ?? Application.Current?.MainWindow;
}
