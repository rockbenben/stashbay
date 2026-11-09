using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;

namespace FileStash.App.ViewModels;

/// <summary>git status 里的一个可选项。</summary>
public sealed partial class GitStatusItemViewModel : ObservableObject
{
    public GitStatusItemViewModel(GitStatusEntry entry)
    {
        Entry = entry;
    }

    public GitStatusEntry Entry { get; }

    public string Path => Entry.Path;

    public string StatusText => Entry.Describe();

    /// <summary>已删除的文件不能加入暂存（源路径已经不存在了）。</summary>
    public bool CanSelect => !Entry.IsDeleted;

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>「从 git status 选择改动文件加入暂存」的 ViewModel。</summary>
public sealed partial class GitStatusViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IStashService _stash;
    private readonly IDialogService _dialogs;

    public GitStatusViewModel(IGitService git, IStashService stash, IDialogService dialogs)
    {
        _git = git;
        _stash = stash;
        _dialogs = dialogs;
    }

    public ObservableCollection<GitStatusItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _repoRoot = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>载入仓库的改动列表。</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        Items.Clear();

        if (string.IsNullOrEmpty(RepoRoot))
        {
            return;
        }

        var status = await _git.GetStatusAsync(RepoRoot, CancellationToken.None);

        foreach (var entry in status)
        {
            Items.Add(new GitStatusItemViewModel(entry));
        }

        var deleted = Items.Count(item => item.Entry.IsDeleted);

        StatusText = deleted > 0
            ? Strings.Format(Strings.GitChangedSummaryWithDeleted, Items.Count, deleted)
            : Strings.Format(Strings.GitChangedSummary, Items.Count);
    }

    /// <summary>把选中的文件加入暂存。</summary>
    [RelayCommand]
    private async Task AddSelectedAsync()
    {
        if (string.IsNullOrEmpty(RepoRoot))
        {
            return;
        }

        var selected = Items
            .Where(item => item.IsSelected && item.CanSelect)
            .Select(item => item.Entry.Path)
            .ToList();

        if (selected.Count == 0)
        {
            _dialogs.ShowWarning(Strings.AddToStashTitle, Strings.GitNoSelection);
            return;
        }

        var paths = selected.Select(relative => Path.Combine(RepoRoot, relative)).ToList();

        var result = await _stash.AddAsync(
            CurrentGroupId,
            paths,
            null,
            CancellationToken.None);

        var deletedSkipped = Items.Count(item => item.Entry.IsDeleted);

        var message = Strings.Format(Strings.GitAddedMessage, result.Added.Count);

        if (deletedSkipped > 0)
        {
            message += "\n" + Strings.Format(Strings.GitDeletedSkipped, deletedSkipped);
        }

        _dialogs.ShowInfo(Strings.AddToStashTitle, message);
    }

    /// <summary>目标组：由调用方在打开对话框前设好。</summary>
    public string CurrentGroupId { get; set; } = string.Empty;
}
