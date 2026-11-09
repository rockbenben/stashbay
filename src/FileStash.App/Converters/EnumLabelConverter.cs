using System.Globalization;
using System.Windows.Data;
using FileStash.App.Resources;
using FileStash.Core.Models;

namespace FileStash.App.Converters;

/// <summary>枚举 → 中文显示名。</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PlannedState.New => Strings.StateNew,
        PlannedState.Overwrite => Strings.StateOverwrite,
        PlannedState.Identical => Strings.StateIdentical,
        PlannedState.TypeConflict => Strings.StateTypeConflict,
        PlannedState.Invalid => Strings.StateInvalid,

        PlannedAction.Create => Strings.ActionCreate,
        PlannedAction.Overwrite => Strings.ActionOverwrite,
        PlannedAction.Skip => Strings.ActionSkip,
        PlannedAction.Rename => Strings.ActionRename,

        OperationStatus.Running => Strings.OpRunning,
        OperationStatus.Succeeded => Strings.OpSucceeded,
        OperationStatus.PartiallyFailed => Strings.OpPartiallyFailed,
        OperationStatus.Failed => Strings.OpFailed,
        OperationStatus.Cancelled => Strings.OpCancelled,
        OperationStatus.Undone => Strings.OpUndone,

        EntryResult.Created => Strings.ResultCreated,
        EntryResult.Overwritten => Strings.ResultOverwritten,
        EntryResult.Skipped => Strings.ResultSkipped,
        EntryResult.Renamed => Strings.ResultRenamed,
        EntryResult.Failed => Strings.ResultFailed,
        EntryResult.DirectoryCreated => Strings.ResultDirectoryCreated,

        ItemSourceState.Unchanged => Strings.SourceUnchanged,
        ItemSourceState.Modified => Strings.SourceModified,
        ItemSourceState.Missing => Strings.SourceMissing,

        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
