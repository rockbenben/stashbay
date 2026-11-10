using System.Collections.Specialized;
using FileStash.App;
using FileStash.App.Services;
using FileStash.App.ViewModels;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Native;
using FileStash.Infrastructure.Services;

namespace FileStash.Core.Tests;

/// <summary>
/// 主窗口 ViewModel：加入暂存之后，收纳格的选中态不能丢。
/// </summary>
/// <remarks>
/// 回归：重载分组列表时 Groups.Clear() 会让列表框的选中项变成 null，
/// 双向绑定随之把 SelectedGroup 也置成 null。后果不只是"界面上没选中"：
/// 紧接着的 LoadItemsAsync(SelectedGroup) 直接 NRE，
/// 抛错之后 SelectedGroup 一直是 null —— 于是"加入暂存"从第二次起就不再生效
/// （直接撞上"没有暂存组"的模态框），整个应用就此卡住。
/// </remarks>
public sealed class MainViewModelTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();
    private readonly RecordingDialogService _dialogs = new();

    private MainViewModel _viewModel = null!;

    public async ValueTask InitializeAsync()
    {
        var paths = _workspace.Paths;
        var factory = _workspace.ConnectionFactory;

        await new DatabaseMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken);

        var groups = new StashGroupRepository(factory);
        var items = new StashItemRepository(factory);
        var entries = new StashEntryRepository(factory);
        var operations = new OperationRepository(factory);
        var operationEntries = new OperationEntryRepository(factory);

        var blobStore = new BlobStore(paths, TimeSpan.Zero);
        var settings = new SettingsService(paths);
        settings.Load();

        var git = new GitService();

        _viewModel = new MainViewModel(
            new StashService(groups, items, entries, blobStore, git, settings),
            groups,
            items,
            entries,
            new ApplyPlanner(entries, git),
            new ApplyExecutor(blobStore, operations, operationEntries, new AtomicFileWriter(), new RestartManagerLockInspector(), settings),
            blobStore,
            settings,
            new DragExportService(entries, blobStore, paths),
            operations,
            operationEntries,
            _dialogs,
            paths);
    }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_ShouldSelectTheFirstGroup()
    {
        await _viewModel.InitializeAsync();

        Assert.NotNull(_viewModel.SelectedGroup);
        Assert.Single(_viewModel.Groups);
    }

    [Fact]
    public async Task AddPathsAsync_ShouldKeepGroupSelectedAndRefreshItems()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var first = _workspace.CreateFile("a.txt", "第一个");
        await _viewModel.AddPathsAsync([first]);

        // 加完之后选中态必须还在：掉了的话 LoadItemsAsync 拿到 null 会 NRE
        Assert.NotNull(_viewModel.SelectedGroup);
        Assert.Single(_viewModel.Items);

        var second = _workspace.CreateFile("b.txt", "第二个");
        await _viewModel.AddPathsAsync([second]);

        // 第二次加入必须照样生效 —— 之前这里会被"没有暂存组"的模态框挡住
        Assert.NotNull(_viewModel.SelectedGroup);
        Assert.Equal(2, _viewModel.Items.Count);
        Assert.Empty(_dialogs.Warnings);

        // 回执是 toast，不是模态框
        Assert.Equal(2, _dialogs.Toasts.Count);
        Assert.Contains("已加入 1 项", _dialogs.Toasts[0]);
    }

    /// <summary>
    /// 模拟 ListBox 的双向绑定：列表一清空，选中项就被回写成 null。
    /// </summary>
    /// <remarks>
    /// 这一步不是装饰，是这个回归的核心：真正把 SelectedGroup 置空的是 WPF 的绑定
    /// （ListBox.SelectedItem 绑到 SelectedGroup，ItemsSource 清空时它回写 null），
    /// 没有它，ViewModel 自己那份代码根本不会出错。
    /// 用 CollectionChanged 钩住 Clear 那一刻，把绑定的行为原样搬过来。
    /// </remarks>
    private static void SimulateListBoxSelectionBinding(MainViewModel viewModel)
    {
        viewModel.Groups.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                viewModel.SelectedGroup = null;
            }
        };
    }

    [Fact]
    public async Task AddPathsAsync_ShouldThrowWhenNoGroupIsSelected()
    {
        await _viewModel.InitializeAsync();

        _viewModel.SelectedGroup = null;

        var file = _workspace.CreateFile("c.txt", "第三个");
        await _viewModel.AddPathsAsync([file]);

        // 没有组就不能加，而且要说清楚为什么
        Assert.Single(_dialogs.Warnings);
        Assert.Empty(_viewModel.Items);
    }

    [Fact]
    public async Task RefreshCommand_ShouldReloadItemsAndKeepSelectedGroup()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var first = _workspace.CreateFile("first.txt", "第一条");
        await _viewModel.AddPathsAsync([first]);

        Assert.Single(_viewModel.Items);
        Assert.NotNull(_viewModel.SelectedGroup);

        await _viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Single(_viewModel.Items);
        Assert.NotNull(_viewModel.SelectedGroup);
    }

    [Fact]
    public async Task ClearAllBoxesCommand_ShouldRemoveAllItemsAndResetToDefaultGroup()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var first = _workspace.CreateFile("clear_a.txt", "内容A");
        var second = _workspace.CreateFile("clear_b.txt", "内容B");
        await _viewModel.AddPathsAsync([first, second]);

        Assert.Equal(2, _viewModel.Items.Count);

        _dialogs.ConfirmResult = true;
        await _viewModel.ClearAllBoxesCommand.ExecuteAsync(null);

        Assert.Empty(_viewModel.Items);
        Assert.Single(_viewModel.Groups);
        Assert.NotNull(_viewModel.SelectedGroup);
        Assert.NotEmpty(_dialogs.Infos);
        Assert.False(_viewModel.IsBusy);
        Assert.True(_viewModel.ClearAllBoxesCommand.CanExecute(null));
        Assert.Equal("内容A", await File.ReadAllTextAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal("内容B", await File.ReadAllTextAsync(second, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearAllBoxesCommand_WhenCancelled_ShouldKeepBoxesAndItems()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);
        await _viewModel.AddPathsAsync([_workspace.CreateFile("keep.txt", "keep")]);
        var groupId = _viewModel.SelectedGroup!.Id;
        _dialogs.ConfirmResult = false;

        await _viewModel.ClearAllBoxesCommand.ExecuteAsync(null);

        Assert.Single(_viewModel.Items);
        Assert.Equal(groupId, Assert.Single(_viewModel.Groups).Id);
        Assert.Empty(_dialogs.Infos);
        Assert.False(_viewModel.IsBusy);
    }

    [Fact]
    public async Task ClearAllBoxesCommand_WhileBusy_ShouldBeDisabled()
    {
        await _viewModel.InitializeAsync();
        _viewModel.IsBusy = true;
        Assert.False(_viewModel.ClearAllBoxesCommand.CanExecute(null));
        _viewModel.IsBusy = false;
        Assert.True(_viewModel.ClearAllBoxesCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearCurrentBoxCommand_ShouldRemoveItemsInActiveBox()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var file = _workspace.CreateFile("current_box.txt", "测试清空当前箱");
        await _viewModel.AddPathsAsync([file]);
        Assert.Single(_viewModel.Items);

        _dialogs.ConfirmResult = true;
        await _viewModel.ClearCurrentBoxCommand.ExecuteAsync(null);

        Assert.Empty(_viewModel.Items);
        Assert.NotEmpty(_dialogs.Infos);
    }

    [Fact]
    public async Task RemoveItemDirectCommand_ShouldRemoveSingleItemInstantly()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var f1 = _workspace.CreateFile("f1.txt", "1");
        var f2 = _workspace.CreateFile("f2.txt", "2");
        await _viewModel.AddPathsAsync([f1, f2]);
        Assert.Equal(2, _viewModel.Items.Count);

        var target = _viewModel.Items[0];
        await _viewModel.RemoveItemDirectCommand.ExecuteAsync(target);

        Assert.Single(_viewModel.Items);
        Assert.NotEqual(target.Id, _viewModel.Items[0].Id);
    }

    [Fact]
    public async Task ClearAllDirectCommand_ShouldRemoveAllItems()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var f1 = _workspace.CreateFile("clear1.txt", "1");
        var f2 = _workspace.CreateFile("clear2.txt", "2");
        await _viewModel.AddPathsAsync([f1, f2]);
        Assert.Equal(2, _viewModel.Items.Count);

        await _viewModel.ClearAllDirectCommand.ExecuteAsync(null);
        Assert.Empty(_viewModel.Items);
    }

    [Fact]
    public async Task AddItemsCommand_StashesMixedSelectionAndIgnoresCancellation()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);
        var file = _workspace.CreateFile("one.txt", "one");
        var folder = Path.GetDirectoryName(_workspace.CreateFile("folder/two.txt", "two"))!;
        _dialogs.StashPaths = [file, folder];
        await _viewModel.AddItemsCommand.ExecuteAsync(null);
        Assert.Contains(_viewModel.Items, item => !item.IsFolder && item.SourcePath == file);
        Assert.Contains(_viewModel.Items, item => item.IsFolder && item.SourcePath == folder);

        _dialogs.StashPaths = null;
        await _viewModel.AddItemsCommand.ExecuteAsync(null);
        Assert.Equal(2, _viewModel.Items.Count);
        Assert.Empty(_dialogs.Warnings);
    }

    [Fact]
    public async Task PasteCommand_WhenNoFilePathsPresent_ShouldLeaveStashEmpty()
    {
        await _viewModel.InitializeAsync();
        _viewModel.PasteCommand.Execute(null);
        Assert.Empty(_viewModel.Items);
    }

    [Fact]
    public async Task PasteCommand_WhenFilePathsPresent_ShouldAddFilesToStash()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var snippetPath = _workspace.CreateFile("Snippet_test.txt", "Hello World");
        _dialogs.ClipboardPaths = [snippetPath];

        _viewModel.PasteCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && _viewModel.Items.Count == 0)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Single(_viewModel.Items);
        Assert.Equal("Snippet_test.txt", _viewModel.Items[0].DisplayName);
    }

    // --------------------------------------------------------------- 工作台三档视图

    /// <summary>
    /// 树档与列表档都不该插进"来源目录"这种合成行 —— 分节只属于第三档。
    /// 插错档的后果不只是难看：右键菜单与拖放都按行类型分派，节头行会被当成暂存项。
    /// </summary>
    [Theory]
    [InlineData(WorkbenchViewMode.Tree)]
    [InlineData(WorkbenchViewMode.List)]
    [InlineData(WorkbenchViewMode.Icons)]
    public async Task WorkbenchRows_ShouldNotInsertFolderHeaders(WorkbenchViewMode mode)
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var a = _workspace.CreateFile("inbox/a.txt", "A");
        var c = _workspace.CreateFile("docs/c.txt", "C");
        await _viewModel.AddPathsAsync([a, c]);

        _viewModel.WorkbenchViewMode = mode;

        Assert.Equal(2, _viewModel.WorkbenchRows.Count);
        Assert.All(_viewModel.WorkbenchRows, row => Assert.IsType<ItemViewModel>(row));

        // 同一批对象，不是复制品：选中态与展开态要跟着走
        Assert.Same(_viewModel.Items[0], _viewModel.WorkbenchRows[0]);
    }

    [Fact]
    public async Task ReloadItems_ShouldPublishOneWorkbenchUpdateForTheWholeBatch()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);
        var paths = Enumerable.Range(0, 12)
            .Select(index => _workspace.CreateFile($"batch/{index}.txt", "content"))
            .ToArray();
        await _viewModel.AddPathsAsync(paths);
        _viewModel.WorkbenchViewMode = WorkbenchViewMode.BySourceFolder;

        var updates = 0;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WorkbenchRows)) updates++;
        };
        var reload = typeof(MainViewModel).GetMethod("LoadItemsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)reload.Invoke(_viewModel, [_viewModel.SelectedGroup])!;

        Assert.Equal(1, updates);
        Assert.Equal(12, _viewModel.Items.Count);
        Assert.Equal(12, Assert.IsType<SourceFolderRowViewModel>(Assert.Single(_viewModel.WorkbenchRows)).Children.Count);
    }

    /// <summary>按来源文件夹分档：同一目录进来的条目落进同一节，节序按首次出现，组内保持加入序。</summary>
    [Fact]
    public async Task WorkbenchRows_WhenBySourceFolder_ShouldGroupItemsBySourceDirectory()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var a = _workspace.CreateFile("inbox/a.txt", "A");
        var c = _workspace.CreateFile("docs/c.txt", "C");
        var b = _workspace.CreateFile("inbox/b.txt", "B");

        // 交错加入：a、c、b。分节按"第一次见到那个目录"排序，所以 inbox 在前
        await _viewModel.AddPathsAsync([a, c, b]);

        _viewModel.WorkbenchViewMode = WorkbenchViewMode.BySourceFolder;

        Assert.Equal(2, _viewModel.WorkbenchRows.Count);

        var inbox = Assert.IsType<SourceFolderRowViewModel>(_viewModel.WorkbenchRows[0]);
        Assert.Equal(Path.GetDirectoryName(a), inbox.Directory, ignoreCase: true);
        Assert.Equal(["a.txt", "b.txt"], inbox.Children.Select(item => item.DisplayName));

        var docs = Assert.IsType<SourceFolderRowViewModel>(_viewModel.WorkbenchRows[1]);
        Assert.Equal(Path.GetDirectoryName(c), docs.Directory, ignoreCase: true);
        Assert.Equal(["c.txt"], docs.Children.Select(item => item.DisplayName));
    }

    /// <summary>
    /// Windows 路径大小写不敏感：同一个目录被写成 `case` 和 `CASE` 时必须并成一节，
    /// 否则架子会给出"两个看起来一样的目录"，正是这档视图要解决的问题。
    /// </summary>
    [Fact]
    public async Task WorkbenchRows_WhenBySourceFolder_ShouldMergeDirectoriesDifferingOnlyInCase()
    {
        await _viewModel.InitializeAsync();
        SimulateListBoxSelectionBinding(_viewModel);

        var lower = _workspace.CreateFile("case/a.txt", "A");
        _workspace.CreateFile("case/b.txt", "B");

        // 同一个目录换一种拼法（文件系统不区分，落点还是那个目录）
        var upper = Path.Combine(_workspace.Root, "CASE", "b.txt");

        // 先证明夹具真的种下了两种拼法 —— 否则这条测试是空转
        Assert.NotEqual(
            Path.GetDirectoryName(lower),
            Path.GetDirectoryName(upper),
            StringComparer.Ordinal);
        Assert.True(File.Exists(upper));

        await _viewModel.AddPathsAsync([lower, upper]);
        Assert.Equal(2, _viewModel.Items.Count);

        _viewModel.WorkbenchViewMode = WorkbenchViewMode.BySourceFolder;

        var section = Assert.Single(_viewModel.WorkbenchRows);
        var group = Assert.IsType<SourceFolderRowViewModel>(section);
        Assert.Equal(2, group.Children.Count);
    }

    /// <summary>
    /// 默认必须是树：老 settings.json 里没有这个键，缺省值落到 0，
    /// 0 若不是树，升级后所有人的工作台会突然换一种看法。
    /// </summary>
    [Fact]
    public async Task WorkbenchViewMode_ShouldDefaultToTreeAndPersist()
    {
        await _viewModel.InitializeAsync();

        Assert.Equal(WorkbenchViewMode.Tree, _viewModel.WorkbenchViewMode);

        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        _viewModel.WorkbenchViewMode = WorkbenchViewMode.Icons;
        Assert.Contains(nameof(MainViewModel.WorkbenchViewMode), notified);
        Assert.Contains(nameof(MainViewModel.IsIconView), notified);
        Assert.True(_viewModel.IsIconView);

        var reloaded = new SettingsService(_workspace.Paths);
        reloaded.Load();
        Assert.Equal(WorkbenchViewMode.Icons, reloaded.Current.WorkbenchViewMode);
    }

    [Fact]
    public async Task CompactIconView_ShouldDefaultToGridAndPersistListChoice()
    {
        await _viewModel.InitializeAsync();
        Assert.True(_viewModel.CompactIconView);
        _viewModel.CompactIconView = false;
        var reloaded = new SettingsService(_workspace.Paths);
        reloaded.Load();
        Assert.False(reloaded.Current.CompactIconView);
    }

    /// <summary>
    /// 无头诊断设档位是为了拍各档的样子，不该把它留给用户当默认。
    /// </summary>
    /// <remarks>
    /// <c>App.InSmoke</c> 是进程级开关，这里只在测试线程上短暂翻转，
    /// 且同一条用例内还原；其余测试类都不读它，所以并行也撞不上。
    /// </remarks>
    [Fact]
    public async Task WorkbenchViewMode_WhenHeadless_ShouldNotPersist()
    {
        await _viewModel.InitializeAsync();

        var previous = FileStash.App.App.InSmoke;
        FileStash.App.App.InSmoke = true;

        try
        {
            _viewModel.WorkbenchViewMode = WorkbenchViewMode.BySourceFolder;
        }
        finally
        {
            FileStash.App.App.InSmoke = previous;
        }

        Assert.Equal(WorkbenchViewMode.BySourceFolder, _viewModel.WorkbenchViewMode);

        var reloaded = new SettingsService(_workspace.Paths);
        reloaded.Load();
        Assert.Equal(WorkbenchViewMode.Tree, reloaded.Current.WorkbenchViewMode);
    }

    [Fact]
    public void DialogService_WhenHeadless_ShouldCancelEveryModalPrompt()
    {
        var previous = FileStash.App.App.InSmoke;
        FileStash.App.App.InSmoke = true;
        try
        {
            // Run on an MTA thread: reaching WPF construction fails immediately,
            // so a missing guard cannot leave the test waiting for user input.
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var dialogs = new WpfDialogService();
                    Assert.Null(dialogs.PromptText("Test", "initial"));
                    Assert.Null(dialogs.PickSetRoot(@"C:\source\file.txt"));
                    Assert.Null(dialogs.PickGroup([("group", "Group")], "group"));
                    Assert.Null(dialogs.PickStashPaths());
                    Assert.Null(dialogs.PickFolder());
                    Assert.False(dialogs.Confirm("Test", "Confirm?"));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure);
        }
        finally
        {
            FileStash.App.App.InSmoke = previous;
        }
    }

}

/// <summary>只记不弹的对话框替身。</summary>
internal sealed class RecordingDialogService : IDialogService
{
    public List<string> Warnings { get; } = [];

    public List<string> Infos { get; } = [];

    public List<string> Toasts { get; } = [];

    public IReadOnlyList<string>? StashPaths { get; set; }
    public IReadOnlyList<string>? PickStashPaths() => StashPaths;

    public string? PickFolder() => null;

    public string? PromptText(string title, string initialValue) => null;

    public bool ConfirmResult { get; set; } = true;
    public bool Confirm(string title, string message) => ConfirmResult;

    public string? PickSetRoot(string sourcePath) => null;

    public string? PickGroup(IReadOnlyList<(string Id, string Name)> groups, string currentGroupId) => null;

    public IReadOnlyList<string>? ClipboardPaths { get; set; }
    public IReadOnlyList<string>? GetClipboardPaths() => ClipboardPaths;


    public void SetClipboardPaths(IReadOnlyList<string> paths)
    {
    }

    public void ShowInfo(string title, string message) => Infos.Add($"{title}：{message}");

    public void ShowWarning(string title, string message) => Warnings.Add($"{title}：{message}");

    public void ShowToast(string title, string summary) => Toasts.Add($"{title}：{summary}");
}
