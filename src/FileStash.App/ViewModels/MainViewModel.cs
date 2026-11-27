using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.App.Views;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;

namespace FileStash.App.ViewModels;

/// <summary>主窗口的 ViewModel。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IStashService _stash;
    private readonly ShelfLifetimeService? _lifetime;
    private readonly IStashGroupRepository _groups;
    private readonly IStashItemRepository _items;
    private readonly IStashEntryRepository _entries;
    private readonly IApplyPlanner _planner;
    private readonly IApplyExecutor _executor;
    private readonly IBlobStore _blobStore;
    private readonly ISettingsService _settings;
    private readonly IDragExportService _dragExport;
    private readonly IOperationRepository _operations;
    private readonly IOperationEntryRepository _operationEntries;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly PathMapper _mapper = new();

    /// <summary>当前正在跑的长任务（加入暂存），用来中止。</summary>
    private CancellationTokenSource? _currentOperation;
    private readonly SemaphoreSlim _addLock = new(1, 1);

    public MainViewModel(
        IStashService stash,
        IStashGroupRepository groups,
        IStashItemRepository items,
        IStashEntryRepository entries,
        IApplyPlanner planner,
        IApplyExecutor executor,
        IBlobStore blobStore,
        ISettingsService settings,
        IDragExportService dragExport,
        IOperationRepository operations,
        IOperationEntryRepository operationEntries,
        IDialogService dialogs,
        AppPaths paths,
        ShelfLifetimeService? lifetime = null)
    {
        _stash = stash;
        _lifetime = lifetime;
        _groups = groups;
        _items = items;
        _entries = entries;
        _planner = planner;
        _executor = executor;
        _blobStore = blobStore;
        _settings = settings;
        _dragExport = dragExport;
        _operations = operations;
        _operationEntries = operationEntries;
        _dialogs = dialogs;
        _paths = paths;

        _workbenchViewMode = settings.Current.WorkbenchViewMode;
        _compactIconView = settings.Current.CompactIconView;

        // 条目在任何入口下都可能整批改写（加入 / 移出 / 清空 / 刷新），
        // 分节投影跟着集合自己走，不靠每个调用点记得重建
        Items.CollectionChanged += (_, _) =>
        {
            if (!_loadingItems)
            {
                RebuildWorkbenchRows();
            }
        };
        RebuildWorkbenchRows();
    }

    public ObservableCollection<GroupViewModel> Groups { get; } = [];

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    /// <summary>
    /// 工作台档位。切换即刻写回设置：这是工具栏上的偏好，不该等用户去点"保存"。
    /// </summary>
    public WorkbenchViewMode WorkbenchViewMode
    {
        get => _workbenchViewMode;
        set
        {
            if (_workbenchViewMode == value)
            {
                return;
            }

            SetProperty(ref _workbenchViewMode, value);
            OnPropertyChanged(nameof(IsIconView));

            // 无头诊断（--smoke / --shots / --probe）也会设档位来拍各档的样子：
            // 那不该变成用户重启后看到的默认档。和"无头不许弹模态框"同一条规矩。
            if (!App.InSmoke)
            {
                _settings.Current.WorkbenchViewMode = value;
                _settings.Save(_settings.Current);
            }

            RebuildWorkbenchRows();
        }
    }

    private WorkbenchViewMode _workbenchViewMode;

    public bool IsIconView => WorkbenchViewMode == WorkbenchViewMode.Icons;

    public bool CompactIconView
    {
        get => _compactIconView;
        set
        {
            if (!SetProperty(ref _compactIconView, value))
            {
                return;
            }

            if (!App.InSmoke)
            {
                _settings.Current.CompactIconView = value;
                _settings.Save(_settings.Current);
            }
        }
    }

    private bool _compactIconView;
    private bool _loadingItems;

    /// <summary>
    /// 树视图真正渲染的行：树与列表档就是 <see cref="Items"/> 本身，
    /// "按来源"档换成目录节头 + 节内条目。
    /// </summary>
    /// <remarks>
    /// 每次重建都复用同一批 <see cref="ItemViewModel"/> 实例，选中态与展开态才不会在刷新时掉。
    /// </remarks>
    public IReadOnlyList<TreeRowViewModel> WorkbenchRows => _workbenchRows;

    public IReadOnlyList<ItemViewModel> FilteredItems => _filteredItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    [NotifyPropertyChangedFor(nameof(HasSearchNoMatches))]
    private string _searchQuery = string.Empty;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool HasSearchNoMatches => HasSearchQuery && !HasNoItems && _filteredItems.Count == 0;

    partial void OnSearchQueryChanged(string value) => RebuildWorkbenchRows();

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    private List<TreeRowViewModel> _workbenchRows = [];
    private List<ItemViewModel> _filteredItems = [];

    private void RebuildWorkbenchRows()
    {
        var query = SearchQuery.Trim();
        _filteredItems = string.IsNullOrEmpty(query)
            ? [.. Items]
            : [.. Items.Where(item => MatchesSearch(item, query))];

        _workbenchRows = _workbenchViewMode == WorkbenchViewMode.BySourceFolder
            ? BuildSourceFolderRows(_filteredItems)
            : [.. _filteredItems.Cast<TreeRowViewModel>()];

        OnPropertyChanged(nameof(FilteredItems));
        OnPropertyChanged(nameof(HasSearchNoMatches));
        OnPropertyChanged(nameof(WorkbenchRows));
    }

    private static bool MatchesSearch(ItemViewModel item, string query) =>
        item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.SourcePath.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Children.Any(child =>
            child.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || child.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按来源目录分节：节序按"第一次见到那个目录"，节内保持加入序。
    /// 目录键走大小写不敏感比较 —— Windows 上同一个目录的两种拼法不能分成两节。
    /// </summary>
    private static List<TreeRowViewModel> BuildSourceFolderRows(IEnumerable<ItemViewModel> items)
    {
        var sections = new List<SourceFolderRowViewModel>();
        var byDirectory = new Dictionary<string, SourceFolderRowViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var directory = Path.GetDirectoryName(item.SourcePath) ?? string.Empty;
            var key = PathUtil.Normalize(directory);

            if (!byDirectory.TryGetValue(key, out var section))
            {
                // 节头念第一次出现的那种拼法，不做大小写归一显示
                section = new SourceFolderRowViewModel(directory);
                byDirectory[key] = section;
                sections.Add(section);
            }

            section.Children.Add(item);
        }

        return [.. sections.Cast<TreeRowViewModel>()];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    [NotifyPropertyChangedFor(nameof(CompactHeader))]
    [NotifyPropertyChangedFor(nameof(ShelfHeader))]
    private GroupViewModel? _selectedGroup;

    /// <summary>紧凑抬头念当前是哪个箱：只写"暂存区"时，多个箱看不出在看哪一个。</summary>
    public string CompactHeader => SelectedGroup?.DisplayName ?? Strings.CompactTitle;
    public string ShelfHeader => SelectedGroup?.Group.Kind == StashGroupKind.Default
        ? Strings.CompactTitle : CompactHeader;

    /// <summary>
    /// 紧凑模式：窗口缩成一个小小的"暂存区"，只管一件事 —— 把东西拖出去存到别处。
    /// 展开之后才是完整的工作台（各种规则转移：原位置 / 其他根目录 / 指定路径，结构与冲突策略）。
    /// </summary>
    /// <remarks>
    /// 默认是紧凑的：多数时候用户只想"把这个拖走"，不该先面对一屏规则。
    /// 需要改写回规则时再展开。
    /// </remarks>
    [ObservableProperty]
    private bool _isCompact = true;

    /// <summary>展开成完整工作台。</summary>
    [RelayCommand]
    private void Expand() => IsCompact = false;

    /// <summary>收回成小暂存区。</summary>
    [RelayCommand]
    private void Collapse() => IsCompact = true;

    /// <summary>切换紧凑货架与完整工作台。</summary>
    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    /// <summary>一键唤出：直接叫出小暂存区（托盘与全局快捷键走这条）。</summary>
    public void ShowCompactPanel()
    {
        IsCompact = true;
        var window = OwnWindow;

        if (window is not null)
        {
            if (_settings.Current.SummonAtCursor)
            {
                WindowEffects.PlaceNearCursor(window);
            }
            else
            {
                WindowEffects.PlaceAtBottomRight(window);
            }
        }

        ShowMainWindow();
    }



    /// <summary>打开完整工作台（托盘菜单走这条）。</summary>
    public void ShowWorkbench()
    {
        IsCompact = false;
        ShowMainWindow();
    }

    [ObservableProperty]
    private ItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _statusText = Strings.StatusReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    [NotifyCanExecuteChangedFor(nameof(ClearAllBoxesCommand))]
    private bool _isBusy;

    /// <summary>
    /// 进度。加入与写入都是批量的（几 GB 的文件要加好一会儿），屏上得有一个"跑到哪了"的读数 ——
    /// 以前 <see cref="StatusText"/> 被赋值十几处却没有任何一处绑定，
    /// 那些文案写在屏幕上不存在的地方，用户只看到多出一个「中止」。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private int _progressDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private int _progressTotal;

    /// <summary>0–1 的比例；总数未知时给 0（这时不画进度条，只有一行字）。</summary>
    public double ProgressValue => ProgressTotal <= 0 ? 0 : (double)ProgressDone / ProgressTotal;

    public bool HasProgress => IsBusy && ProgressTotal > 0;

    private void ReportProgress(ProgressInfo info)
    {
        ProgressDone = info.Done;
        ProgressTotal = info.Total;
    }

    private void ClearProgress()
    {
        ProgressDone = 0;
        ProgressTotal = 0;
    }

    // ---- 目标面板 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(HasTargetPath))]
    [NotifyPropertyChangedFor(nameof(DestinationPreview))]
    private TargetMode _targetMode = TargetMode.OriginalLocation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationPreview))]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationPreview))]
    private StructureMode _structure = StructureMode.FullRelative;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationPreview))]
    private int _stripLeadingSegments;

    [ObservableProperty]
    private bool _autoGitAdd;

    /// <summary>顶栏上的一句摘要。</summary>
    public string StatusSummary
    {
        get
        {
            var itemCount = Items.Count;
            var fileCount = Items.Sum(item => item.Children.Count);
            var size = Items.Sum(item => item.Item.TotalSize);

            return itemCount == 0
                ? Strings.StatusEmpty
                : Strings.Format(Strings.StatusSummary, itemCount, fileCount, ItemViewModel.FormatSize(size));
        }
    }

    public string ShelfSummary => Strings.Format(Strings.ShelfSummaryFormat, Items.Count,
        ItemViewModel.FormatSize(Items.Sum(item => item.Item.TotalSize)));

    public string DragAllBadgeText => Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>台面是不是空的（空台面要给个邀请，别留一片白）。</summary>
    public bool HasNoItems => Items.Count == 0;

    /// <summary>底部目标槽上"将写入"的落点。</summary>
    public string DestinationPreview
    {
        get => _destinationPreview;
        private set
        {
            if (string.Equals(_destinationPreview, value, StringComparison.Ordinal))
            {
                return;
            }

            _destinationPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDestination));
        }
    }

    /// <summary>算得出落点没有（没有内容时给个说明，别留一根空线）。</summary>
    public bool HasDestination => !string.IsNullOrEmpty(_destinationPreview);

    private string _destinationPreview = string.Empty;

    /// <summary>界面请求打开设置窗口（由 App 处理，ViewModel 不碰窗口）。</summary>
    public event Action? SettingsRequested;
    public event Action? HistoryRequested;
    public event Action? GitChangesRequested;
    public event Action? FloatingWindowToggleRequested;
    public event Action? RootPresetsRequested;
    public event Action? AboutRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void OpenHistory() => HistoryRequested?.Invoke();

    [RelayCommand]
    private void OpenGitChanges() => GitChangesRequested?.Invoke();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FloatingWindowActionText))]
    private bool _isFloatingWindowVisible;

    public string FloatingWindowActionText => IsFloatingWindowVisible
        ? Strings.TrayHideFloatingWindow
        : Strings.TrayFloatingWindow;

    [RelayCommand]
    private void ToggleFloatingWindow() => FloatingWindowToggleRequested?.Invoke();

    [RelayCommand]
    private void OpenRootPresets() => RootPresetsRequested?.Invoke();

    [RelayCommand]
    private void OpenAbout() => AboutRequested?.Invoke();

    public IReadOnlyList<EnumOption<TargetMode>> TargetModes { get; } =
    [
        new(TargetMode.OriginalLocation, Strings.TargetOriginal),
        new(TargetMode.AnotherRoot, Strings.TargetAnotherRoot),
        new(TargetMode.SpecifiedPath, Strings.TargetSpecifiedPath),
    ];

    public IReadOnlyList<EnumOption<StructureMode>> Structures { get; } =
    [
        new(StructureMode.FullRelative, Strings.StructureFullRelative),
        new(StructureMode.ItemOnly, Strings.StructureItemOnly),
        new(StructureMode.Flatten, Strings.StructureFlatten),
    ];

    public IReadOnlyList<EnumOption<ConflictPolicy>> Policies { get; } =
    [
        new(ConflictPolicy.Ask, Strings.ConflictAsk),
        new(ConflictPolicy.Overwrite, Strings.ActionOverwrite),
        new(ConflictPolicy.Skip, Strings.ActionSkip),
        new(ConflictPolicy.Rename, Strings.ActionRename),
    ];

    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>
    /// 台面上有没有东西。以前「写回」「预览」在任何时候都可点，
    /// 空箱点下去只在一行看不见的位置给一句反馈。
    /// </summary>
    public bool CanWrite => Items.Count > 0;

    private void RaiseWriteCommands()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>冲突策略。</summary>
    [ObservableProperty]
    private ConflictPolicy _conflictPolicy = ConflictPolicy.Ask;

    /// <summary>当前暂存项涉及哪些仓库（分支监听用）。</summary>
    public event Action<IReadOnlyList<string>>? RepositoriesChanged;

    /// <summary>原位置模式不需要填目标路径。</summary>
    public bool HasTargetPath => TargetMode != TargetMode.OriginalLocation;

    /// <summary>选一个目标目录。</summary>
    [RelayCommand]
    private void BrowseTargetPath()
    {
        var path = _dialogs.PickFolder();

        if (path is not null)
        {
            TargetPath = path;
        }
    }

    public string BusyText => IsBusy ? Strings.StatusBusy : string.Empty;

    /// <summary>切换组就换一份暂存项。</summary>
    partial void OnSelectedGroupChanged(GroupViewModel? value)
    {
        if (_suppressSelectionLoad) return;
        SyncGroupSelection();
        if (!string.IsNullOrEmpty(SearchQuery))
        {
            SearchQuery = string.Empty;
        }

        // 记住在哪个箱：下次开应用回到同一个界面
        _settings.Current.LastGroupId = value?.Id;

        if (value is null)
        {
            _itemLoadVersion++;
            CancelSourceStatus();
            Items.Clear();
            OnPropertyChanged(nameof(StatusSummary));
            OnPropertyChanged(nameof(DragAllBadgeText));
            OnPropertyChanged(nameof(ShelfSummary));
            OnPropertyChanged(nameof(HasNoItems));
            OnPropertyChanged(nameof(HasSearchNoMatches));
            UpdateDestinationPreview();
            return;
        }

        _ = LoadItemsAsync(value);
    }

    /// <summary>启动时加载组与暂存项。</summary>
    internal bool IsRebuildingGroups { get; private set; }
    private bool _suppressSelectionLoad;
    private int _groupLoadVersion;
    private int _itemLoadVersion;
    private int _sourceStatusVersion;
    private CancellationTokenSource? _sourceStatusCancellation;
    private Task? _initialization;
    public async Task InitializeAsync()
    {
        var initialization = _initialization ??= InitializeCoreAsync();
        try { await initialization; }
        catch
        {
            if (ReferenceEquals(_initialization, initialization)) _initialization = null;
            throw;
        }
    }

    private async Task InitializeCoreAsync()
    {
        var lastGroupId = _settings.Current.LastGroupId;
        await ShelfGroupMigration.AdoptLegacyAsync(_groups);
        await LoadGroupsAsync();

        if (!Groups.Any(group => group.Group.Kind == StashGroupKind.Default))
        {
            // 第一次运行：建一个默认组，否则界面是空的、"加入暂存"也没地方放
            await _stash.CreateGroupAsync(Strings.DefaultGroupName, StashGroupKind.Default);
            await LoadGroupsAsync();
        }

        // 上次在哪个箱就开哪个箱；那个箱被删了则回落到第一个
        SelectedGroup = Groups.FirstOrDefault(group => group.Id == lastGroupId)
            ?? Groups.FirstOrDefault(group => group.Group.Kind == StashGroupKind.Default) ?? Groups.FirstOrDefault();
        await LoadItemsAsync(SelectedGroup);
    }

    [RelayCommand]
    private async Task NewGroupAsync()
    {
        var name = _dialogs.PromptText(Strings.NewGroupTitle, string.Empty);

        if (name is null)
        {
            return;
        }

        var group = await _stash.CreateGroupAsync(name);
        await LoadGroupsAsync();

        SelectedGroup = Groups.FirstOrDefault(candidate => candidate.Id == group.Id);
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (SelectedGroup is null)
        {
            return;
        }

        var name = _dialogs.PromptText(Strings.RenameGroupTitle, SelectedGroup.DisplayName);

        if (name is null)
        {
            return;
        }

        await _stash.RenameGroupAsync(SelectedGroup.Id, name);
        await LoadGroupsAsync();

        SelectedGroup = Groups.FirstOrDefault(candidate => candidate.Id == SelectedGroup?.Id);
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is null)
        {
            return;
        }

        if (!_dialogs.Confirm(Strings.DeleteGroupTitle, Strings.Format(Strings.DeleteGroupConfirm, SelectedGroup.DisplayName)))
        {
            return;
        }

        await _stash.DeleteGroupAsync(SelectedGroup.Id);
        await LoadGroupsAsync();

        SelectedGroup = Groups.FirstOrDefault();
    }

    private bool CanClearAllBoxes() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClearAllBoxes))]
    private async Task ClearAllBoxesAsync()
    {
        var totalItems = Groups.Sum(group => group.ItemCount);

        if (totalItems > 0 || Groups.Count > 1)
        {
            if (!_dialogs.Confirm(Strings.ClearAllBoxesTitle, Strings.ClearAllBoxesConfirm))
            {
                return;
            }
        }

        IsBusy = true;
        StatusText = Strings.ClearAllBoxesTitle;
        try
        {
            // SQLite's async API can execute synchronously. Cascading deletes must
            // not block painting/input on the dispatcher, especially for large boxes.
            await Task.Run(async () =>
            {
                var allGroups = await _groups.GetAllAsync(CancellationToken.None);
                foreach (var group in allGroups)
                {
                    await _stash.DeleteGroupAsync(group.Id);
                }

                await _stash.CreateGroupAsync(Strings.DefaultGroupName, StashGroupKind.Default);
            });
            await LoadGroupsAsync();
            SelectedGroup = Groups.FirstOrDefault();
            _dialogs.ShowInfo(Strings.ClearAllBoxesTitle, Strings.ClearAllBoxesDone);
        }
        finally
        {
            IsBusy = false;
            StatusText = Strings.StatusReady;
        }
    }

    [RelayCommand]
    private async Task ClearCurrentBoxAsync()
    {
        if (SelectedGroup is null || Items.Count == 0)
        {
            return;
        }

        var clearedGroupName = SelectedGroup.DisplayName;
        if (!_dialogs.Confirm(Strings.ClearBoxButton, Strings.Format(Strings.ClearBoxConfirm, clearedGroupName)))
        {
            return;
        }

        var itemIds = Items.Select(item => item.Id).ToList();
        await _stash.RemoveItemsAsync(itemIds);
        await LoadGroupsAsync();

        if (SelectedGroup is not null)
        {
            await LoadItemsAsync(SelectedGroup);
        }

        _dialogs.ShowInfo(Strings.ClearBoxButton, Strings.Format(Strings.ClearBoxDone, clearedGroupName));
    }

    /// <summary>清空当前暂存区中的所有文件（不弹模态确认，直接生效）。</summary>
    [RelayCommand]
    public async Task ClearAllDirectAsync()
    {
        if (SelectedGroup is null || Items.Count == 0)
        {
            return;
        }

        var ids = Items.Select(i => i.Id).ToList();
        await _stash.RemoveItemsAsync(ids);

        if (SelectedGroup is not null)
        {
            await LoadGroupsAsync();
            await LoadItemsAsync(SelectedGroup);
        }
    }

    [RelayCommand]
    private async Task RemoveItemDirectAsync(ItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await _stash.RemoveItemsAsync([item.Id]);

        if (SelectedGroup is not null)
        {
            await LoadGroupsAsync();
            await LoadItemsAsync(SelectedGroup);
        }
    }

    public async Task RemoveExportedItemsAsync(IReadOnlyList<string> itemIds)
    {
        if (itemIds.Count == 0) return;
        await _stash.RemoveItemsAsync(itemIds.ToArray());
        await LoadGroupsAsync(loadSelection: false);
        await LoadItemsAsync(SelectedGroup);
    }

    [RelayCommand]
    private Task AddItemsAsync() => AddPathsAsync(_dialogs.PickStashPaths());

    /// <summary>拖入 / 粘贴进来的路径。</summary>
    [RelayCommand]
    public async Task AddPathsAsync(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (SelectedGroup is null)
        {
            _dialogs.ShowWarning(Strings.NoGroupTitle, Strings.NoGroupHint);
            return;
        }

        await AddPathsToGroupAsync(SelectedGroup.Id, paths);
    }

    public async Task AddPathsToGroupAsync(string groupId, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        await _addLock.WaitAsync();
        IsBusy = true;
        StatusText = Strings.Format(Strings.StatusAdding, 0, paths.Count);

        // 几 GB 的文件要加好一会儿，得给用户一个中止的办法
        _currentOperation = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ProgressInfo>(info =>
            {
                StatusText = Strings.Format(Strings.StatusAdding, info.Done, info.Total)
                    + Environment.NewLine
                    + info.CurrentPath;

                ReportProgress(info);
            });

            var result = await _stash.AddAsync(groupId, paths, progress, _currentOperation.Token);

            await LoadGroupsAsync();
            await LoadItemsAsync(SelectedGroup);

            ReportAddResult(result);
        }
        catch (OperationCanceledException)
        {
            await LoadGroupsAsync(loadSelection: false);
            await LoadItemsAsync(SelectedGroup);
            StatusText = Strings.StatusAddAborted;
        }
        finally
        {
            var cancelled = _currentOperation?.IsCancellationRequested == true;
            _currentOperation?.Dispose();
            _currentOperation = null;
            IsBusy = false;
            ClearProgress();
            StatusText = cancelled ? Strings.StatusAddAborted : Strings.StatusReady;
            _addLock.Release();
        }
    }

    /// <summary>中止当前正在跑的加入（几 GB 的文件要加好一会儿）。</summary>
    [RelayCommand]
    private void CancelCurrentOperation()
    {
        _currentOperation?.Cancel();
        StatusText = Strings.StatusAborting;
    }

    private void ReportAddResult(AddResult result)
    {
        var messages = new List<string>();

        if (result.Added.Count > 0)
        {
            messages.Add(Strings.Format(Strings.ToastAdded, result.Added.Count));
        }

        if (result.Duplicates.Count > 0)
        {
            messages.Add(Strings.Format(Strings.ToastDuplicates, result.Duplicates.Count));
        }

        if (result.Failed.Count > 0)
        {
            messages.Add(Strings.Format(Strings.ToastFailedPaths, result.Failed.Count));
        }

        if (result.Warnings.Count > 0)
        {
            messages.Add(Strings.Format(Strings.ToastWarnings, result.Warnings.Count));
        }

        // 回执用 toast，不用模态框：模态框会打断用户，还会占住主线程的模态循环
        // （后台会话里更是直接把进程吊住）
        if (messages.Count > 0)
        {
            _dialogs.ShowToast(Strings.AddToStashTitle, string.Join(Strings.ClauseSeparator, messages));
        }

        // 跳过的内容要看得见（哪个文件、为什么），这才是值得打断的事
        if (result.Warnings.Count > 0)
        {
            _dialogs.ShowWarning(Strings.SkippedTitle, string.Join(Environment.NewLine, result.Warnings.Take(20)));
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = Strings.StatusRefreshing;

        try
        {
            await LoadGroupsAsync();

            if (SelectedGroup is not null)
            {
                await LoadItemsAsync(SelectedGroup);
            }

            StatusText = Strings.StatusReady;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshSnapshotAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = Strings.StatusRefreshing;

        try
        {
            await _stash.RefreshSnapshotAsync([SelectedItem.Id], null, CancellationToken.None);

            if (SelectedGroup is not null)
            {
                await LoadItemsAsync(SelectedGroup);
            }

            StatusText = Strings.StatusSnapshotRefreshed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetRootAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var root = _dialogs.PickSetRoot(SelectedItem.SourcePath);

        if (root is null)
        {
            return;
        }

        try
        {
            await _stash.SetRootAsync([SelectedItem.Id], root);

            if (SelectedGroup is not null)
            {
                await LoadItemsAsync(SelectedGroup);
            }

            StatusText = Strings.StatusRootUpdated;
        }
        catch (ArgumentException ex)
        {
            _dialogs.ShowWarning(Strings.SetRootFailedTitle, ex.Message);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!_dialogs.Confirm(Strings.DeleteItemTitle, Strings.Format(Strings.DeleteItemConfirm, SelectedItem.DisplayName)))
        {
            return;
        }

        await _stash.RemoveItemsAsync([SelectedItem.Id]);

        if (SelectedGroup is not null)
        {
            await LoadGroupsAsync();
            await LoadItemsAsync(SelectedGroup);
        }
    }

    [RelayCommand]
    private async Task MoveToGroupAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var options = Groups
            .Select(group => (group.Id, group.DisplayName))
            .Where(pair => pair.Id != SelectedGroup?.Id)
            .ToList();

        if (options.Count == 0)
        {
            _dialogs.ShowWarning(Strings.MoveToGroupTitle, Strings.NoOtherGroupHint);
            return;
        }

        var target = _dialogs.PickGroup(options, SelectedGroup?.Id ?? string.Empty);

        if (target is null)
        {
            return;
        }

        await _stash.MoveItemsAsync([SelectedItem.Id], target);

        await LoadGroupsAsync();

        if (SelectedGroup is not null)
        {
            await LoadItemsAsync(SelectedGroup);
        }
    }

    [RelayCommand]
    private void OpenSourceLocation(ItemViewModel? target = null)
    {
        var item = target ?? SelectedItem;
        if (item is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.SourcePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowWarning(Strings.OpenSourceFailedTitle, ex.Message);
        }
    }

    [RelayCommand]
    private void CopySourcePath(ItemViewModel? target = null)
    {
        var item = target ?? SelectedItem;
        if (item is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.SourcePath);
            StatusText = Strings.StatusSourcePathCopied;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _dialogs.ShowWarning(Strings.CopyFailedTitle, Strings.ClipboardBusy);
        }
    }

    /// <summary>剪贴板与拖拽媒体文本暂存目录。</summary>
    public string ClipboardDirectory => _paths.ClipboardDirectory;

    [RelayCommand]
    private void Paste()
    {
        var paths = _dialogs.GetClipboardPaths();

        if (paths is not null && paths.Count > 0)
        {
            AddPathsCommand.Execute(paths);
            return;
        }


    }

    // ---- 拖出与剪贴板（M4） ----

    /// <summary>把选中的暂存项物化成真实文件（拖出前要先准备好，DoDragDrop 会阻塞 UI）。</summary>
    /// <summary>把当前暂存箱中的所有项全部物化出来供整体拖出。</summary>
    public async Task<IReadOnlyList<string>> MaterializeAllForDragAsync()
    {
        var allItems = Items.Select(i => i.Item).ToList();
        return await MaterializeForDragAsync(allItems);
    }

    public async Task<IReadOnlyList<string>> MaterializeForDragAsync(IReadOnlyList<StashItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        IsBusy = true;
        StatusText = Strings.StatusPreparingExport;

        try
        {
            return await _dragExport.MaterializeAsync(items, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>右键"复制"：物化后放进剪贴板，资源管理器里 Ctrl+V 就能粘贴。</summary>
    [RelayCommand]
    private async Task CopyToClipboardAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            var paths = await MaterializeForDragAsync([SelectedItem.Item]);

            if (paths.Count == 0)
            {
                return;
            }

            _dialogs.SetClipboardPaths(paths);
            StatusText = Strings.Format(Strings.StatusCopiedToClipboard, paths.Count);
        }
        catch (InvalidOperationException ex)
        {
            _dialogs.ShowWarning(Strings.CopyFailedTitle, ex.Message);
        }
    }

    /// <summary>
    /// 拖出完成后记一条历史（TargetMode=DragExport）。
    /// </summary>
    /// <remarks>
    /// 拖出没有"写到目标"这一步（是资源管理器那边接手的），所以这条历史无法撤销，仅作记录。
    /// </remarks>
    public async Task RecordDragExportAsync(IReadOnlyList<string> paths, DragDropEffects effect)
    {
        if (effect == DragDropEffects.None || paths.Count == 0) return;
        var operation = new Operation
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            TargetMode = TargetMode.DragExport,
            TargetDescription = Strings.Format(Strings.DragExportTarget, paths.Count),
            Status = OperationStatus.Succeeded,
        };

        await _operations.AddAsync(operation, CancellationToken.None);
    }

    // ---- 应用 ----

    /// <summary>应用到原位置（小搁架、托盘菜单、--apply-original 都走这里）。</summary>
    [RelayCommand]
    public async Task ApplyOriginalAsync(ItemViewModel? singleItem = null)
    {
        var consume = IsCompact;
        TargetMode = TargetMode.OriginalLocation;
        var targets = singleItem is not null
            ? [singleItem.Item]
            : (IReadOnlyList<StashItem>)Items.Select(i => i.Item).ToList();

        if (targets.Count == 0)
        {
            _dialogs.ShowInfo(Strings.NothingToWriteTitle, Strings.NothingToWriteHint);
            return;
        }

        var plan = await _planner.BuildPlanAsync(
            targets, BuildTargetSpec(), ConflictPolicy, CancellationToken.None,
            _settings.Current.SkipIdentical);

        var needsPreview = ConflictPolicy == ConflictPolicy.Ask
            || plan.Entries.Any(entry => entry.State == PlannedState.Overwrite);

        if (needsPreview)
        {
            var dialog = new PreviewDialog(plan, _executor, _blobStore, _settings);
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);

            if (owner is not null)
            {
                dialog.Owner = owner;
            }

            if (dialog.ShowDialog() == true && dialog.Result is not null)
            {
                await ShowResultAsync(dialog.Result, plan, targets, consume);
            }

            return;
        }

        IsBusy = true;

        try
        {
            var progress = new Progress<ProgressInfo>(info =>
            {
                StatusText = Strings.Format(Strings.StatusWriting, info.Done, info.Total);
                ReportProgress(info);
            });

            var operation = await _executor.ExecuteAsync(plan, progress, CancellationToken.None);
            await ShowResultAsync(operation, plan, targets, consume);
        }
        finally
        {
            IsBusy = false;
            ClearProgress();
        }
    }

    /// <summary>应用到指定目录（--apply-here）。</summary>
    public async Task ApplyHereAsync(string directory)
    {
        TargetMode = TargetMode.SpecifiedPath;
        TargetPath = directory;
        await PreviewAsync();
    }

    /// <summary>切到某个组（--group）。</summary>
    public async Task<string> SelectGroupAsync(string groupName)
    {
        await LoadGroupsAsync();

        var group = Groups.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayName, groupName, StringComparison.OrdinalIgnoreCase));

        if (group is null)
        {
            group = new GroupViewModel(await _stash.CreateGroupAsync(groupName));
            await LoadGroupsAsync();
        }

        SelectedGroup = Groups.FirstOrDefault(candidate => candidate.Id == group.Id);
        await LoadItemsAsync(SelectedGroup);
        return group.Id;
    }

    /// <summary>打开主窗口（托盘 / 快捷键用）。</summary>
    public void ShowMainWindow()
    {
        var window = OwnWindow;

        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        WindowEffects.BringToFront(window);
    }

    private Window? OwnWindow => Application.Current?.Windows.OfType<MainWindow>()
        .FirstOrDefault(window => ReferenceEquals(window.ViewModel, this));

    /// <summary>Creates a separately persisted transit shelf without a naming dialog.</summary>
    public async Task CreateShelfAsync()
    {
        await InitializeAsync();
        var name = $"{Strings.CompactTitle} · {DateTime.Now:MM-dd HH:mm:ss}";
        var group = _lifetime is null ? await _stash.CreateGroupAsync(name, StashGroupKind.Temporary)
            : await _lifetime.CreateTemporaryAsync(this, name);
        await LoadGroupsAsync();
        SelectedGroup = Groups.First(candidate => candidate.Id == group.Id);
        await LoadItemsAsync(SelectedGroup);
        IsCompact = true;
    }

    public event Action? NewShelfRequested;

    [RelayCommand]
    private void NewShelf() => NewShelfRequested?.Invoke();

    [RelayCommand]
    private async Task RestoreToFolderAsync()
    {
        var folder = _dialogs.PickFolder();
        if (folder is not null) await ApplyHereAsync(folder);
    }

    [RelayCommand]
    private async Task RestoreToRootAsync()
    {
        var folder = _dialogs.PickFolder();
        if (folder is null) return;
        TargetMode = TargetMode.AnotherRoot;
        TargetPath = folder;
        await PreviewAsync();
    }

    /// <summary>当前目标描述。</summary>
    public TargetSpec BuildTargetSpec() => new(
        TargetMode,
        TargetPath,
        Structure,
        StripLeadingSegments);

    /// <summary>要写入的暂存项：选中项优先，没选中就整组。</summary>
    private async Task<IReadOnlyList<StashItem>> ResolveTargetItemsAsync()
    {
        if (SelectedGroup is null)
        {
            return [];
        }

        return await _items.GetByGroupAsync(SelectedGroup.Id, CancellationToken.None);
    }

    /// <summary>打开预览对话框。</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task PreviewAsync()
    {
        var consume = IsCompact;
        var items = await ResolveTargetItemsAsync();

        if (items.Count == 0)
        {
            _dialogs.ShowInfo(Strings.NothingToWriteTitle, Strings.NothingToWriteHint);
            return;
        }

        var plan = await _planner.BuildPlanAsync(
            items, BuildTargetSpec(), ConflictPolicy, CancellationToken.None,
            _settings.Current.SkipIdentical);

        var dialog = new PreviewDialog(plan, _executor, _blobStore, _settings);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            await ShowResultAsync(dialog.Result, plan, items, consume);
        }
    }

    /// <summary>
    /// 应用：全是新增 / 相同、且冲突策略不是"询问"时直接执行，否则先看预览。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ApplyAsync()
    {
        var consume = IsCompact;
        var items = await ResolveTargetItemsAsync();

        if (items.Count == 0)
        {
            _dialogs.ShowInfo(Strings.NothingToWriteTitle, Strings.NothingToWriteHint);
            return;
        }

        var plan = await _planner.BuildPlanAsync(
            items, BuildTargetSpec(), ConflictPolicy, CancellationToken.None,
            _settings.Current.SkipIdentical);

        var needsPreview = ConflictPolicy == ConflictPolicy.Ask
            || plan.Entries.Any(entry => entry.State == PlannedState.Overwrite);

        if (needsPreview)
        {
            var dialog = new PreviewDialog(plan, _executor, _blobStore, _settings);
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);

            if (owner is not null)
            {
                dialog.Owner = owner;
            }

            if (dialog.ShowDialog() == true && dialog.Result is not null)
            {
                await ShowResultAsync(dialog.Result, plan, items, consume);
            }

            return;
        }

        IsBusy = true;

        try
        {
            var progress = new Progress<ProgressInfo>(info =>
            {
                StatusText = Strings.Format(Strings.StatusWriting, info.Done, info.Total);
                ReportProgress(info);
            });

            var operation = await _executor.ExecuteAsync(plan, progress, CancellationToken.None);
            await ShowResultAsync(operation, plan, items, consume);
        }
        finally
        {
            IsBusy = false;
            ClearProgress();
        }
    }

    /// <summary>撤销上一次写入。</summary>
    [RelayCommand]
    private async Task UndoLastAsync()
    {
        var operations = await _operations.GetAllAsync(CancellationToken.None);

        // 拖出那一条虽然状态是"成功"，但它没有写入条目，撤销它没有意义
        var last = operations.FirstOrDefault(operation =>
            operation.TargetMode != TargetMode.DragExport
            && operation.Status is OperationStatus.Succeeded or OperationStatus.PartiallyFailed or OperationStatus.Cancelled);

        if (last is null)
        {
            _dialogs.ShowInfo(Strings.NothingToUndoTitle, Strings.NothingToUndoMessage);
            return;
        }

        var result = await _executor.UndoAsync(last.Id, CancellationToken.None);

        var message = Strings.Format(Strings.UndoDoneMessage, result.Restored, result.Deleted);

        if (result.HasIssues)
        {
            message += Environment.NewLine + string.Join(Environment.NewLine, result.Messages.Take(10));
        }

        _dialogs.ShowInfo(Strings.UndoButton, message);
    }

    /// <summary>The current transit queue is drained; the owning compact window can dismiss.</summary>
    public event Action? RestoreSucceeded;

    private async Task ShowResultAsync(Operation operation, ApplyPlan plan,
        IReadOnlyList<StashItem> targets, bool consume)
    {
        var originalGroupId = targets.FirstOrDefault()?.GroupId;
        var selectedAtCompletion = SelectedGroup?.Id;
        var entries = await _operationEntries.GetByOperationAsync(operation.Id, CancellationToken.None);
        if (consume && operation.Status == OperationStatus.Succeeded)
            await ConsumeRestoredItemsAsync(plan, targets);

        if (!App.InSmoke && Application.Current?.Dispatcher.CheckAccess() == true)
        {
            var toast = new ResultToast(operation, entries);
            toast.UndoRequested += async () => await _executor.UndoAsync(operation.Id, CancellationToken.None);
            toast.Show();
        }
        if (consume && operation.Status == OperationStatus.Succeeded && originalGroupId is not null
            && selectedAtCompletion == originalGroupId
            && (await _items.GetByGroupAsync(originalGroupId, CancellationToken.None)).Count == 0)
            RestoreSucceeded?.Invoke();
    }

    private async Task ConsumeRestoredItemsAsync(ApplyPlan plan, IReadOnlyList<StashItem> targets)
    {
        // A skipped conflict is still pending work. Identical files are already delivered.
        var delivered = targets.Where(item =>
        {
            var planned = plan.Entries.Where(entry => entry.ItemId == item.Id).ToArray();
            return planned.Length > 0 && planned.All(entry =>
                entry.State is not (PlannedState.Invalid or PlannedState.TypeConflict) &&
                (entry.Action != PlannedAction.Skip || entry.State == PlannedState.Identical));
        }).ToDictionary(item => item.Id);
        if (delivered.Count == 0) return;

        await _addLock.WaitAsync();
        try
        {
            // Never consume a newer snapshot, an item moved to another shelf, or later additions.
            var current = await _items.GetByIdsAsync(delivered.Keys.ToArray(), CancellationToken.None);
            var ids = current.Where(item => delivered.TryGetValue(item.Id, out var captured)
                    && item.GroupId == captured.GroupId && item.SnapshotAt == captured.SnapshotAt)
                .Select(item => item.Id).ToArray();
            if (ids.Length == 0) return;
            await _stash.RemoveItemsAsync(ids);
            await LoadGroupsAsync(loadSelection: false);
            await LoadItemsAsync(SelectedGroup);
        }
        finally { _addLock.Release(); }
    }

    public async Task RefreshAfterGroupCleanupAsync()
    {
        var selectedId = SelectedGroup?.Id;
        await LoadGroupsAsync(loadSelection: false);
        if (SelectedGroup?.Id == selectedId && SelectedGroup is not null)
        {
            // Another window's cleanup only changes our sidebar. Preserve file selection,
            // expanded rows and an in-progress drag when this shelf's snapshots are unchanged.
            var current = await _items.GetByGroupAsync(selectedId!, CancellationToken.None);
            if (SelectedGroup?.Id != selectedId) return;
            var displayed = Items.ToDictionary(item => item.Id, item => item.Item.SnapshotAt);
            if (current.Count == displayed.Count && current.All(item =>
                displayed.TryGetValue(item.Id, out var snapshotAt) && snapshotAt == item.SnapshotAt)) return;
        }
        await LoadItemsAsync(SelectedGroup);
    }

    private async Task LoadGroupsAsync(bool loadSelection = true)
    {
        // 记住选中的那个：Groups.Clear() 会让列表框的选中项变成 null，
        // 双向绑定随之把 SelectedGroup 也置成 null ——
        // 不还原的话，之后按组取东西全炸（LoadItemsAsync 拿到 null 直接 NRE），
        // 而且"加入暂存"这类操作在第一次之后就再也不生效了。
        var version = ++_groupLoadVersion;
        var selectedAtStart = SelectedGroup;
        var selectedId = selectedAtStart?.Id;

        var groups = await _groups.GetAllAsync(CancellationToken.None);

        var loadedGroups = new List<GroupViewModel>();
        foreach (var group in groups)
        {
            var viewModel = new GroupViewModel(group);
            var items = await _items.GetByGroupAsync(group.Id, CancellationToken.None);
            viewModel.ItemCount = items.Count;
            viewModel.FileCount = items.Sum(item => item.FileCount);
            loadedGroups.Add(viewModel);
        }

        // Batch removal reloads items explicitly after restoring selection.
        // ListBox's null write-back must not trigger an intermediate reload.
        if (version != _groupLoadVersion) return;
        if (!ReferenceEquals(SelectedGroup, selectedAtStart)) selectedId = SelectedGroup?.Id;
        _suppressSelectionLoad = !loadSelection;
        IsRebuildingGroups = true;
        try
        {
            Groups.Clear();
            foreach (var group in loadedGroups) Groups.Add(group);
            SelectedGroup = Groups.FirstOrDefault(group => group.Id == selectedId) ?? Groups.FirstOrDefault(group => group.Group.Kind == StashGroupKind.Default) ?? Groups.FirstOrDefault();
        }
        finally { _suppressSelectionLoad = false; IsRebuildingGroups = false; }
        OnPropertyChanged(nameof(SelectedGroup));
        _settings.Current.LastGroupId = SelectedGroup?.Id;

        SyncGroupSelection();
    }

    /// <summary>同步收纳格的选中态（抽屉标签被拉出来的那一个）。</summary>
    private void SyncGroupSelection()
    {
        foreach (var group in Groups)
        {
            group.IsSelected = group.Id == SelectedGroup?.Id;
        }
    }

    private async Task LoadItemsAsync(GroupViewModel? group)
    {
        var version = ++_itemLoadVersion;
        CancelSourceStatus();
        // 没有组可看：把台面清空，别拿上一次的内容接着显示
        if (group is null)
        {
            Items.Clear();
            OnPropertyChanged(nameof(HasNoItems));
            RaiseWriteCommands();
            return;
        }

        try
        {
            var items = await _items.GetByGroupAsync(group.Id, CancellationToken.None);

            var loadedItems = new List<ItemViewModel>();
            foreach (var item in items)
            {
                var viewModel = new ItemViewModel(item);
                var entries = await _entries.GetByItemAsync(item.Id, CancellationToken.None);

                foreach (var entry in entries)
                {
                    viewModel.Children.Add(new EntryViewModel(entry, item.RelativePath));
                }

                loadedItems.Add(viewModel);
            }

            if (version != _itemLoadVersion || SelectedGroup?.Id != group.Id) return;
            _loadingItems = true;
            try
            {
                Items.Clear();
                foreach (var item in loadedItems)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _loadingItems = false;
                RebuildWorkbenchRows();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (version != _itemLoadVersion) return;
            StatusText = Strings.Format(Strings.StatusLoadFailed, ex.Message);
        }

        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(DragAllBadgeText));
        OnPropertyChanged(nameof(ShelfSummary));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(HasSearchNoMatches));
        RaiseWriteCommands();
        UpdateDestinationPreview();

        var repositories = Items
            .Select(item => item.Item.GitRepoRoot)
            .Where(root => !string.IsNullOrEmpty(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        RepositoriesChanged?.Invoke(repositories);

        _ = RefreshSourceStatusAsync(group);
    }

    /// <summary>
    /// 算出"会写到哪"，直接显示在目标槽上 —— 这一行不是装饰，它回答最关键的那个问题。
    /// </summary>
    private void UpdateDestinationPreview()
    {
        var first = Items.FirstOrDefault();
        var entry = first?.Children.FirstOrDefault();

        if (first is null || entry is null)
        {
            DestinationPreview = string.Empty;
            return;
        }

        try
        {
            var target = new TargetSpec(TargetMode, TargetPath, Structure, StripLeadingSegments);
            var context = new PathMapper.MappingContext { SingleFile = false };

            DestinationPreview = _mapper.Map(first.Item, entry.Entry, target, context);
        }
        catch (ArgumentException)
        {
            DestinationPreview = string.Empty;
        }

        OnPropertyChanged(nameof(WillWriteCaption));
    }

    /// <summary>台面里一共多少个文件（落点那一行要说清"这只是其中一个"）。</summary>
    public int EntryCount => Items.Sum(item => Math.Max(1, item.Children.Count));

    /// <summary>
    /// 落点预览的抬头。以前不管几个文件都写"将写入"，后面只跟一条路径 ——
    /// 26 个文件的箱看着像只写一个。
    /// </summary>
    public string WillWriteCaption => EntryCount > 1
        ? Strings.Format(Strings.WillWriteMany, EntryCount)
        : Strings.WillWrite;

    /// <summary>源状态是异步算的，别挡住界面。</summary>
    private void CancelSourceStatus()
    {
        _sourceStatusVersion++;
        _sourceStatusCancellation?.Cancel();
    }

    private async Task RefreshSourceStatusAsync(GroupViewModel group)
    {
        CancelSourceStatus();
        var version = _sourceStatusVersion;
        using var cancellation = new CancellationTokenSource();
        _sourceStatusCancellation = cancellation;
        try
        {
            var statuses = await _stash.CheckSourceStatusAsync(group.Id, cancellation.Token);
            if (version != _sourceStatusVersion || SelectedGroup?.Id != group.Id) return;

            foreach (var status in statuses)
            {
                var item = Items.FirstOrDefault(candidate => candidate.Id == status.ItemId);

                if (item is null)
                {
                    continue;
                }

                item.StatusText = status.State switch
                {
                    ItemSourceState.Unchanged => Strings.SourceUnchanged,
                    ItemSourceState.Modified => Strings.SourceModified,
                    ItemSourceState.Missing => Strings.SourceMissing,
                    _ => string.Empty,
                };

                item.StatusBrush = FindBrush(status.State);
                item.StatusBadgeBrush = FindBadgeBrush(status.State);
                item.SourceState = status.State;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (version == _sourceStatusVersion)
                StatusText = Strings.Format(Strings.StatusSourceStateFailed, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_sourceStatusCancellation, cancellation)) _sourceStatusCancellation = null;
        }
    }

    private static Brush? FindBrush(ItemSourceState state)
    {
        var key = state switch
        {
            ItemSourceState.Unchanged => "Brush.TextSecondary",
            ItemSourceState.Modified => "Brush.Warning",
            ItemSourceState.Missing => "Brush.Danger",
            _ => "Brush.TextSecondary",
        };

        return Application.Current?.TryFindResource(key) as Brush;
    }

    private static Brush? FindBadgeBrush(ItemSourceState state)
    {
        var key = state switch
        {
            ItemSourceState.Unchanged => "Brush.SurfaceSunken",
            ItemSourceState.Modified => "Brush.WarningSoft",
            ItemSourceState.Missing => "Brush.DangerSoft",
            _ => "Brush.SurfaceSunken",
        };

        return Application.Current?.TryFindResource(key) as Brush;
    }
}
