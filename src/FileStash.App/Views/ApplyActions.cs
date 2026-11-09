using FileStash.App.Resources;
using FileStash.Core.Models;
using FileStash.App.ViewModels;

namespace FileStash.App.Views;

/// <summary>预览对话框里"动作"下拉框的选项（静态，供 x:Static 引用）。</summary>
public static class ApplyActions
{
    public static readonly IReadOnlyList<EnumOption<PlannedAction>> Items =
    [
        new(PlannedAction.Overwrite, Strings.ActionOverwrite),
        new(PlannedAction.Skip, Strings.ActionSkip),
        new(PlannedAction.Rename, Strings.ActionRename),
    ];
}
