using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.Core.Models;
using FileStash.Core.Utils;

namespace FileStash.App.ViewModels;

/// <summary>树里一行的公共部分。暂存项与它下面的条目共用同一套列。</summary>
public abstract partial class TreeRowViewModel : ObservableObject
{
    public abstract string DisplayName { get; }

    public abstract string RelativePath { get; }

    /// <summary>表格里显示的路径尾部（父目录已经在上一级显示过了）。</summary>
    public abstract string PathTail { get; }

    public virtual string SizeText => string.Empty;

    public virtual string FileCountText => string.Empty;

    public virtual string BranchText => string.Empty;

    public virtual string StatusText => string.Empty;

    public virtual string AddedText => string.Empty;

    /// <summary>状态列的前景色。</summary>
    public virtual Brush? StatusBrush => null;
}

/// <summary>一个暂存项。</summary>
public sealed partial class ItemViewModel : TreeRowViewModel
{
    public ItemViewModel(StashItem item)
    {
        Item = item;
    }

    public StashItem Item { get; }

    public string Id => Item.Id;

    public string SourcePath => Item.SourcePath;

    public bool IsFolder => Item.Kind == StashItemKind.Folder;

    /// <summary>系统真实文件/文件夹图标。</summary>
    public ImageSource? FileIcon => ShellIconService.GetIcon(SourcePath, IsFolder);

    public override string DisplayName => Item.DisplayName;

    public override string RelativePath => Item.RelativePath;

    /// <summary>
    /// 单文件项的子路径就是它自己的名字，再说一遍只是噪声（父子两行念同一个名字）。
    /// </summary>
    public override string PathTail =>
        string.Equals(Item.RelativePath, Item.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Item.RelativePath;

    /// <summary>
    /// 紧凑面板里一行的提示：那里只有 320 DIP，状态词放不下，只能靠 tooltip 交代。
    /// </summary>
    public string CompactToolTip => string.IsNullOrEmpty(StatusText)
        ? SourcePath
        : $"{StatusText}{Environment.NewLine}{SourcePath}";

    /// <summary>紧凑双行卡片的副标题：展示大小与原始所在目录。</summary>
    public string CompactSubtitle
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(SourcePath);
            return string.IsNullOrEmpty(dir) ? SizeText : $"{SizeText} · {dir}";
        }
    }

    public override string SizeText => FormatSize(Item.TotalSize);

    public override string FileCountText => Item.FileCount.ToString();

    public override string BranchText => Item.GitBranch ?? string.Empty;

    public override string AddedText => Item.AddedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>源状态：未计算时为空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactToolTip))]
    [NotifyPropertyChangedFor(nameof(IsSourceMissing))]
    private string _statusText = string.Empty;

    /// <summary>算出来的源状态本身（名字要据此变暗，不能只靠一颗点）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceMissing))]
    private ItemSourceState? _sourceState;

    public bool IsSourceMissing => SourceState == ItemSourceState.Missing;

    /// <summary>源状态对应的颜色。</summary>
    [ObservableProperty]
    private Brush? _statusBrush;

    /// <summary>文件夹默认展开。</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>展开后显示的条目。</summary>
    public ObservableCollection<EntryViewModel> Children { get; } = [];

    public static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>暂存项里的一个条目（文件或空目录）。</summary>
public sealed partial class EntryViewModel : TreeRowViewModel
{
    private readonly string _itemRelativePath;

    public EntryViewModel(StashEntry entry, string itemRelativePath)
    {
        Entry = entry;
        _itemRelativePath = itemRelativePath;
    }

    public StashEntry Entry { get; }

    public override string DisplayName =>
        Path.GetFileName(Entry.RelativePath) is { Length: > 0 } name ? name : Entry.RelativePath;

    public override string RelativePath => Entry.RelativePath;

    /// <summary>只显示父项之下的那一段（父目录上一级已经显示过了）。</summary>
    public override string PathTail
    {
        get
        {
            var tail = !string.IsNullOrEmpty(_itemRelativePath)
                && Entry.RelativePath.StartsWith(_itemRelativePath, StringComparison.OrdinalIgnoreCase)
                ? Entry.RelativePath[_itemRelativePath.Length..].TrimStart('\\')
                : Entry.RelativePath;

            // 与名字相同的那一段不再重复显示（"logo.png  logo.png" 那种）
            return string.Equals(tail, DisplayName, StringComparison.OrdinalIgnoreCase) ? string.Empty : tail;
        }
    }

    public override string SizeText => Entry.IsDirectory ? string.Empty : ItemViewModel.FormatSize(Entry.Size);

    /// <summary>系统真实文件/文件夹图标。</summary>
    public ImageSource? FileIcon => ShellIconService.GetIcon(Entry.RelativePath, Entry.IsDirectory);
}

/// <summary>一个暂存组。</summary>
public sealed partial class GroupViewModel : TreeRowViewModel
{
    public GroupViewModel(StashGroup group)
    {
        Group = group;
    }

    public StashGroup Group { get; }

    public string Id => Group.Id;

    public override string DisplayName => Group.Name;

    public override string RelativePath => string.Empty;

    public override string PathTail => string.Empty;

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private int _fileCount;

    /// <summary>选中态（抽屉标签被拉出来的那一个）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>收纳格上的一行摘要。</summary>
    public string SummaryText => ItemCount == 0
        ? Strings.TreeEmpty
        : FileCount == 0
            ? Strings.Format(Strings.TreeItemCount, ItemCount)
            : Strings.Format(Strings.TreeItemFileCount, ItemCount, FileCount);

    public override string FileCountText => ItemCount > 0 ? ItemCount.ToString() : string.Empty;
}

/// <summary>
/// "按来源"档里的目录节头行。
/// </summary>
/// <remarks>
/// 名字刻意避开 <see cref="GroupViewModel"/>：那个已经是"暂存箱"，两者不是一回事。
/// </remarks>
public sealed partial class SourceFolderRowViewModel : TreeRowViewModel
{
    public SourceFolderRowViewModel(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public ObservableCollection<ItemViewModel> Children { get; } = [];

    public override string DisplayName => Directory;

    public override string RelativePath => string.Empty;

    public override string PathTail => string.Empty;

    /// <summary>节头默认摊开：收着的分节等于看不见那一组文件。</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>TreeViewItem 样式双向绑定这个；缺了它节头点不出选中态。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>节头念"几个文件"，不念大小：大小是子项的事，节头重复一遍只是噪声。</summary>
    public override string FileCountText => Children.Count.ToString();
}
