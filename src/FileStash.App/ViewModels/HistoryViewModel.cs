using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;

namespace FileStash.App.ViewModels;

/// <summary>历史里的一条操作。</summary>
public sealed partial class OperationViewModel : ObservableObject
{
    public OperationViewModel(Operation operation, string groupName)
    {
        Operation = operation;
        GroupName = groupName;
    }

    public Operation Operation { get; }

    public string Id => Operation.Id;

    public string GroupName { get; }

    public string TimeText => Operation.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string TargetText => Operation.TargetDescription;

    public string StatusText => Operation.Status switch
    {
        OperationStatus.Succeeded => Strings.OpSucceeded,
        OperationStatus.PartiallyFailed => Strings.OpPartiallyFailed,
        OperationStatus.Failed => Strings.OpFailed,
        OperationStatus.Cancelled => Strings.OpCancelled,
        OperationStatus.Undone => Strings.OpUndone,
        _ => Strings.OpRunning,
    };

    public string SummaryText => Operation.Summary ?? string.Empty;

    public Brush? StatusForeground => Application.Current?.TryFindResource(Operation.Status switch
    {
        OperationStatus.Succeeded => "Brush.Success",
        OperationStatus.PartiallyFailed => "Brush.Warning",
        OperationStatus.Failed => "Brush.Danger",
        _ => "Brush.TextSecondary",
    }) as Brush;

    public Brush? StatusBackground => Application.Current?.TryFindResource(Operation.Status switch
    {
        OperationStatus.Succeeded => "Brush.SuccessSoft",
        OperationStatus.PartiallyFailed => "Brush.WarningSoft",
        OperationStatus.Failed => "Brush.DangerSoft",
        _ => "Brush.SurfaceSunken",
    }) as Brush;

    /// <summary>
    /// 能不能撤销。拖出那一条虽然状态是"成功"，但它没有写入条目 ——
    /// 对它点「撤销这次」必然什么都不发生，所以不给这个入口。
    /// </summary>
    public bool CanUndo => Operation.TargetMode != TargetMode.DragExport
        && Operation.Status is OperationStatus.Succeeded
            or OperationStatus.PartiallyFailed
            or OperationStatus.Cancelled;
}

/// <summary>历史里的一条条目。</summary>
public sealed class OperationEntryViewModel(OperationEntry entry)
{
    public long Id => entry.Id;
    public bool NeedsReview => entry.Pending || entry.UndoPending;
    public string TargetPath => entry.TargetPath;

    public string ActionText => entry.Pending ? Strings.OpRunning : entry.Action switch
    {
        EntryResult.Created => Strings.ResultCreated,
        EntryResult.Overwritten => Strings.ResultOverwritten,
        EntryResult.Skipped => Strings.ResultSkipped,
        EntryResult.Renamed => Strings.ResultRenamed,
        EntryResult.Failed => Strings.ResultFailed,
        EntryResult.DirectoryCreated => Strings.ResultDirectoryCreated,
        _ => entry.Action.ToString(),
    };

    public Brush? ActionForeground => Application.Current?.TryFindResource(entry.Pending
        ? "Brush.Warning"
        : entry.Action switch
        {
            EntryResult.Created or EntryResult.DirectoryCreated => "Brush.Success",
            EntryResult.Overwritten or EntryResult.Renamed => "Brush.Warning",
            EntryResult.Failed => "Brush.Danger",
            _ => "Brush.TextSecondary",
        }) as Brush;

    public Brush? ActionBackground => Application.Current?.TryFindResource(entry.Pending
        ? "Brush.WarningSoft"
        : entry.Action switch
        {
            EntryResult.Created or EntryResult.DirectoryCreated => "Brush.SuccessSoft",
            EntryResult.Overwritten or EntryResult.Renamed => "Brush.WarningSoft",
            EntryResult.Failed => "Brush.DangerSoft",
            _ => "Brush.SurfaceSunken",
        }) as Brush;

    public string Error => entry.Error ?? string.Empty;
}

/// <summary>历史窗口的 ViewModel。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IOperationRepository _operations;
    private readonly IOperationEntryRepository _entries;
    private readonly IStashGroupRepository _groups;
    private readonly IApplyExecutor _executor;
    private readonly IDialogService _dialogs;

    public HistoryViewModel(
        IOperationRepository operations,
        IOperationEntryRepository entries,
        IStashGroupRepository groups,
        IApplyExecutor executor,
        IDialogService dialogs)
    {
        _operations = operations;
        _entries = entries;
        _groups = groups;
        _executor = executor;
        _dialogs = dialogs;
    }

    public ObservableCollection<OperationViewModel> Operations { get; } = [];

    public ObservableCollection<OperationEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanUndoSelected))]
    private OperationViewModel? _selectedOperation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanKeepCurrent))]
    private OperationEntryViewModel? _selectedEntry;

    public bool CanKeepCurrent => SelectedEntry?.NeedsReview == true;
    public bool HasSelection => SelectedOperation is not null;

    /// <summary>
    /// 「撤销这次」按钮的可点性：以前只要有选中就可点，
    /// 而拖出那一条撤销不了 —— 点下去什么都不发生，也没有一句解释。
    /// </summary>
    public bool CanUndoSelected => SelectedOperation?.CanUndo == true;

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _executor.RecoverAsync(CancellationToken.None);
        var operations = await _operations.GetAllAsync(CancellationToken.None);
        var groups = await _groups.GetAllAsync(CancellationToken.None);
        var groupNames = groups.ToDictionary(group => group.Id, group => group.Name);

        Operations.Clear();

        foreach (var operation in operations)
        {
            var groupName = operation.GroupId is not null && groupNames.TryGetValue(operation.GroupId, out var name)
                ? name
                : string.Empty;

            Operations.Add(new OperationViewModel(operation, groupName));
        }

        if (SelectedOperation is not null)
        {
            SelectedOperation = Operations.FirstOrDefault(item => item.Id == SelectedOperation.Id);
        }
    }

    partial void OnSelectedOperationChanged(OperationViewModel? value)
    {
        _ = LoadEntriesAsync(value);
    }

    private async Task LoadEntriesAsync(OperationViewModel? operation)
    {
        SelectedEntry = null;
        Entries.Clear();

        if (operation is null)
        {
            return;
        }

        var entries = await _entries.GetByOperationAsync(operation.Id, CancellationToken.None);
        if (SelectedOperation?.Id != operation.Id) return;

        foreach (var entry in entries)
        {
            Entries.Add(new OperationEntryViewModel(entry));
        }
    }

    /// <summary>撤销选中的那次操作（有修改冲突时会提示）。</summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (SelectedOperation is null || !SelectedOperation.CanUndo)
        {
            return;
        }

        if (!_dialogs.Confirm(
                Strings.UndoConfirmTitle,
                Strings.Format(Strings.UndoConfirmMessage, SelectedOperation.TimeText) + "\n"
                + Strings.UndoConfirmNote))
        {
            return;
        }

        var result = await _executor.UndoAsync(SelectedOperation.Id, CancellationToken.None);

        var message = Strings.Format(Strings.UndoDoneMessage, result.Restored, result.Deleted);

        if (result.HasIssues)
        {
            message += Environment.NewLine + string.Join(Environment.NewLine, result.Messages.Take(10));
        }

        _dialogs.ShowInfo(Strings.UndoButton, message);

        await LoadAsync();
    }

    [RelayCommand]
    private async Task KeepCurrentAsync()
    {
        var operation = SelectedOperation;
        var entry = SelectedEntry;
        if (operation is null || entry?.NeedsReview != true) return;
        if (!_dialogs.Confirm(Strings.KeepCurrentButton,
                Strings.Format(Strings.KeepCurrentConfirm, entry.TargetPath))) return;
        await _executor.KeepCurrentAsync(operation.Id, entry.Id, CancellationToken.None);
        await LoadAsync();
        await LoadEntriesAsync(SelectedOperation);
    }

    /// <summary>打开目标文件夹。</summary>
    [RelayCommand]
    private void OpenTargetFolder()
    {
        if (SelectedOperation is null)
        {
            return;
        }

        var target = SelectedOperation.Operation.TargetDescription;

        if (string.IsNullOrEmpty(target) || !System.IO.Directory.Exists(target))
        {
            _dialogs.ShowWarning(Strings.OpenTargetFolderButton, Strings.TargetFolderMissing);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowWarning(Strings.OpenFailedTitle, ex.Message);
        }
    }
}
