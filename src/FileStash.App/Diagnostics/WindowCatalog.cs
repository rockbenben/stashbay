using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileStash.App.Resources;
using FileStash.App.ViewModels;
using FileStash.App.Views;
using FileStash.App.Views.Dialogs;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FileStash.App.Diagnostics;

/// <summary>
/// 需要被自查（构造 + 布局 + 截图）的窗口与状态清单。
/// </summary>
/// <remarks>
/// 新增窗口时在这里登记，否则它既不会被 smoke 覆盖，也不会出现在截图矩阵里 ——
/// 那一行就成了零像素。
///
/// 工厂必须每次给出**新实例**：窗口 Close 之后不能再 Show。
///
/// <see cref="Shot.Drive"/> 是"Show 之后还要把界面推到那个状态"的部分
/// （选中某箱、填目标、选一条历史）。没有它，条件性 UI 在截图里永远是空的 ——
/// 而这正是最容易看错的一种"没问题"。
/// </remarks>
internal static class WindowCatalog
{
    public sealed record Shot(
        string Name,
        Func<Task<Window>> Factory,
        Func<Window, IServiceProvider, Task>? Drive = null);

    public static IReadOnlyList<Shot> All(IServiceProvider services)
    {
        var viewModel = services.GetRequiredService<MainViewModel>();
        var fixture = Fixture.Load();

        // 默认箱按 id 认（名字跟着界面语言走，按名字选会在英文那一轮选到别的箱）
        var defaultBox = Fixture.DefaultBoxId ?? Strings.DefaultGroupName;

        var shots = new List<Shot>
        {
            // 主窗口不是两个窗口，是同一个窗口的几种真实状态：
            // 状态要在造窗口之前设好（ViewModel 是单例），选中项要等数据回来之后驱动。
            new("MainWindow.Compact", () => CreateMainWindow(services, viewModel, compact: true),
                (window, provider) => SelectGroup(window, provider, viewModel, defaultBox)),

            new("MainWindow.Compact.Narrow", () => CreateMainWindow(services, viewModel, compact: true),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);
                    window.Width = window.MinWidth;
                    await Pump(window);
                }),

            new("MainWindow.Compact.Minimum", () => CreateMainWindow(services, viewModel, compact: true),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    await Pump(window);
                }),

            new("MainWindow.Compact.DropReady", () => CreateMainWindow(services, viewModel, compact: true),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);
                    if (window.FindName("DropOverlay") is Border overlay)
                    {
                        overlay.Visibility = Visibility.Visible;
                        overlay.Opacity = 1;
                    }
                    await Pump(window);
                }),

            new("MainWindow.Compact.LegacyListPreference", () => CreateMainWindow(services, viewModel, compact: true),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);
                    viewModel.CompactIconView = false;
                    await Pump(window);
                }),

            new("MainWindow.Compact.Empty", () => CreateMainWindow(services, viewModel, compact: true),
                (window, provider) => SelectGroup(window, provider, viewModel, "空箱（还没放东西）")),

            new("MainWindow.Compact.RestoreActions", () => CreateMainWindow(services, viewModel, compact: true),
                (window, provider) => SelectGroup(window, provider, viewModel, defaultBox)),

            // 紧凑面板 + 长名：省略号到底生不生效，只有这一格量得出来
            new("MainWindow.Compact.LongName", () => CreateMainWindow(services, viewModel, compact: true),
                (window, provider) => SelectGroup(window, provider, viewModel, "2026 年第三季度营销物料汇总与设计稿备份（归档勿动）")),

            new("MainWindow.Expanded", () => CreateMainWindow(services, viewModel, compact: false),
                (window, provider) => SelectGroup(window, provider, viewModel, defaultBox)),

            new("MainWindow.Expanded.Empty", () => CreateMainWindow(services, viewModel, compact: false),
                (window, provider) => SelectGroup(window, provider, viewModel, "空箱（还没放东西）")),

            new("MainWindow.Expanded.Project", () => CreateMainWindow(services, viewModel, compact: false),
                (window, provider) => SelectGroup(window, provider, viewModel, "项目 A · 前端")),

            new("MainWindow.Expanded.LongName", () => CreateMainWindow(services, viewModel, compact: false),
                (window, provider) => SelectGroup(window, provider, viewModel, "2026 年第三季度营销物料汇总与设计稿备份（归档勿动）")),

            new(
                "MainWindow.Expanded.WriteTarget",
                () => CreateMainWindow(services, viewModel, compact: false),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);

                    viewModel.TargetMode = TargetMode.SpecifiedPath;
                    viewModel.TargetPath = fixture is null ? @"D:\目标目录" : Path.Combine(fixture, "out");
                    viewModel.Structure = StructureMode.ItemOnly;
                    viewModel.ConflictPolicy = ConflictPolicy.Rename;

                    await Pump(window);
                }),

            new(
                "MainWindow.Expanded.Busy",
                () => CreateMainWindow(services, viewModel, compact: false),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);

                    viewModel.IsBusy = true;
                    viewModel.StatusText = Strings.Format(Strings.StatusAdding, 7, 24);

                    await Pump(window);
                }),

            // 浮层：右键菜单与下拉。Popup 有自己的视觉根，窗口截图拍不到它们
            new(
                "MainWindow.Expanded.Menu",
                () => CreateMainWindow(services, viewModel, compact: false),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);

                    if (window.FindName("ItemTree") is TreeView { ContextMenu: { } } tree)
                    {
                        tree.ContextMenu.PlacementTarget = tree;
                        tree.ContextMenu.IsOpen = true;
                    }

                    await Pump(window);
                }),

            new(
                "MainWindow.Expanded.Dropdown",
                () => CreateMainWindow(services, viewModel, compact: false),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);

                    var combo = UiPump.Descendants<ComboBox>(window).FirstOrDefault();

                    if (combo is not null)
                    {
                        combo.IsDropDownOpen = true;
                    }

                    await Pump(window);
                }),

            // 档位挂在单例 VM 上，CreateMainWindow 每格先复位成树，这里再改成要拍的那一档
            new(
                "MainWindow.Expanded.ViewList",
                () => CreateMainWindow(services, viewModel, compact: false),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);

                    viewModel.WorkbenchViewMode = WorkbenchViewMode.List;

                    await Pump(window);
                }),

            new(
                "MainWindow.Expanded.ViewBySource",
                () => CreateMainWindow(services, viewModel, compact: false),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);

                    viewModel.WorkbenchViewMode = WorkbenchViewMode.BySourceFolder;

                    await Pump(window);
                }),

            new(
                "MainWindow.Expanded.ViewIcons",
                () => CreateMainWindow(services, viewModel, compact: false),
                async (window, provider) =>
                {
                    await SelectGroup(window, provider, viewModel, defaultBox);
                    viewModel.WorkbenchViewMode = WorkbenchViewMode.Icons;
                    await Pump(window);
                }),

            new("MessageDialog.Confirm", () => Task.FromResult<Window>(new MessageDialog(
                Strings.ClearAllBoxesTitle, Strings.ClearAllBoxesConfirm, confirmation: true))),
            new("MessageDialog.Info", () => Task.FromResult<Window>(new MessageDialog(
                Strings.ToastDetailsTitle, Strings.ToastNoFailures, confirmation: false))),

            new("PreviewDialog", () => CreatePreviewAsync(services, TargetMode.SpecifiedPath)),

            new("PreviewDialog.Original", () => CreatePreviewAsync(services, TargetMode.OriginalLocation)),

            new(
                "ResultToast.Succeeded",
                () => Task.FromResult<Window>(new ResultToast(
                    new Operation
                    {
                        Id = "toast",
                        StartedAt = DateTimeOffset.UtcNow,
                        TargetMode = TargetMode.OriginalLocation,
                        TargetDescription = Strings.TargetOriginal,
                        Status = OperationStatus.Succeeded,
                    },
                    [
                        new OperationEntry
                        {
                            TargetPath = @"C:\repo\src\a.ts",
                            Action = EntryResult.Created,
                            NewSha256 = new string('a', 64),
                        },
                    ]))),

            new(
                "ResultToast.Failed",
                () => CreateFailedToastAsync(services)),

            new(
                "ResultToast.Notice",
                () => Task.FromResult<Window>(new ResultToast(
                    Strings.AddToStashTitle,
                    Strings.Format(Strings.StatusClipboardFilled, 3)))),

            new(
                "ResultToast.Notice.HalfElapsed",
                () => Task.FromResult<Window>(new ResultToast(Strings.AddToStashTitle,
                    Strings.Format(Strings.StatusClipboardFilled, 3))),
                async (window, _) =>
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var countdown = (FileStash.App.Controls.ToastCountdown)typeof(ResultToast)
                        .GetField("_countdown", flags)!.GetValue(window)!;
                    countdown.Advance(TimeSpan.FromSeconds(3), paused: false);
                    typeof(ResultToast).GetMethod("UpdateCountdown", flags)!.Invoke(window, null);
                    await Pump(window);
                }),

            new(
                "HistoryWindow",
                () => Task.FromResult<Window>(
                    ActivatorUtilities.CreateInstance<HistoryWindow>(services)),
                async (window, provider) =>
                {
                    var history = (HistoryViewModel)window.DataContext;
                    var target = history.Operations.FirstOrDefault(
                        operation => operation.Operation.Status == OperationStatus.PartiallyFailed);

                    if (target is not null)
                    {
                        history.SelectedOperation = target;
                        await Pump(window);
                    }
                }),

            new("SettingsWindow", () => Task.FromResult<Window>(
                ActivatorUtilities.CreateInstance<SettingsWindow>(services))),

            new("SettingsWindow.Shelf", () => CreateSettingsPage(services, "ShelfSettingsTab")),
            new("SettingsWindow.Files", () => CreateSettingsPage(services, "FilesSettingsTab")),
            new("SettingsWindow.Windows", () => CreateSettingsPage(services, "WindowsSettingsTab")),
            new("SettingsWindow.Storage", () => CreateSettingsPage(services, "StorageSettingsTab")),
            new("SettingsWindow.Git", () => CreateSettingsPage(services, "FilesSettingsTab"),
                (window, _) =>
                {
                    ((FrameworkElement)window.FindName("GitSettingsSection")).BringIntoView();
                    return Pump(window);
                }),

            new("SettingsWindow.StartupElevated", () => CreateStartupSettings(services),
                (window, _) => SetStartupStatus(window, Strings.StartupStatusElevated)),
            new("SettingsWindow.StartupRepair", () => CreateStartupSettings(services),
                (window, _) => SetStartupStatus(window, Strings.StartupStatusRepair)),
            new("SettingsWindow.StartupUnknown", () => CreateStartupSettings(services),
                (window, _) => SetStartupStatus(window, Strings.StartupStatusUnknown, uncertain: true)),

            new("RootPresetsWindow", () => Task.FromResult<Window>(
                ActivatorUtilities.CreateInstance<RootPresetsWindow>(services))),

            new("GitStatusDialog", () => CreateGitStatusAsync(services, fixture)),

            new("FloatingWindow", () => Task.FromResult<Window>(
                ActivatorUtilities.CreateInstance<FloatingWindow>(services))),

            new("AboutWindow", () => Task.FromResult<Window>(
                ActivatorUtilities.CreateInstance<AboutWindow>(services))),

            new(
                "InputDialog",
                // 逐字照 MainViewModel.NewGroupAsync 的调用（title 与 initialValue 传的是同一个串），
                // 截图拍的就是那条路径上用户真正看到的框
                () => Task.FromResult<Window>(new InputDialog
                {
                    Title = Strings.NewGroupTitle,
                    InputText = Strings.NewGroupTitle,
                })),

            new("StashPickerDialog", () => Task.FromResult<Window>(new StashPickerDialog(
                fixture == null ? services.GetRequiredService<FileStash.Core.Utils.AppPaths>().RootDirectory : Path.Combine(fixture, "inbox")))),

            new("GroupPickerDialog", () => CreateGroupPickerAsync(services)),

            new("SetRootDialog", () => CreateSetRootAsync(services, fixture)),
        };

        return shots;
    }

    private static Task<Window> CreateSettingsPage(IServiceProvider services, string page)
    {
        var window = ActivatorUtilities.CreateInstance<SettingsWindow>(services);
        ((TabItem)window.FindName(page)).IsSelected = true;
        return Task.FromResult<Window>(window);
    }

    private static Task<Window> CreateStartupSettings(IServiceProvider services)
    {
        var viewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(services);
        viewModel.Settings.StartWithWindows = true;
        viewModel.Settings.StartWithWindowsAsAdministrator = true;
        var window = new SettingsWindow(viewModel);
        ((TabItem)window.FindName("WindowsSettingsTab")).IsSelected = true;
        return Task.FromResult<Window>(window);
    }

    private static Task SetStartupStatus(Window window, string text, bool uncertain = false)
    {
        var viewModel = (SettingsViewModel)window.DataContext;
        viewModel.StartupStatusText = text;
        viewModel.StartupUncertain = uncertain;
        return Task.CompletedTask;
    }

    private static async Task<Window> CreateMainWindow(
        IServiceProvider services,
        MainViewModel viewModel,
        bool compact)
    {
        viewModel.IsCompact = compact;

        // 写回槽的状态挂在单例 VM 上：不每格复位，后一格就带着前一格的选择拍出来
        // （实测：Busy 那一格顶着一整条"指定路径 + 仅暂存项自身 + 重命名"，
        //  看着像产品的默认值，其实是上一格留下的）
        viewModel.TargetMode = TargetMode.OriginalLocation;
        viewModel.TargetPath = string.Empty;
        viewModel.Structure = StructureMode.FullRelative;
        viewModel.ConflictPolicy = ConflictPolicy.Ask;
        viewModel.IsBusy = false;
        viewModel.WorkbenchViewMode = WorkbenchViewMode.Tree;
        viewModel.CompactIconView = true;

        // ActivatorUtilities 绕开 DI 里注册的单例，每次现造一个
        var window = ActivatorUtilities.CreateInstance<MainWindow>(services);
        await Pump(window);

        return window;
    }

    /// <summary>等窗口把 Loaded 里挂的异步查询跑完，再把选中项定下来。</summary>
    private static async Task SelectGroup(
        Window window,
        IServiceProvider services,
        MainViewModel viewModel,
        string nameOrId)
    {
        await Pump(window);

        var group = viewModel.Groups.FirstOrDefault(candidate =>
                candidate.Id == nameOrId || string.Equals(candidate.DisplayName, nameOrId, StringComparison.Ordinal))
            ?? viewModel.Groups.FirstOrDefault();

        viewModel.SelectedGroup = group;
        await Pump(window);
    }

    private static async Task<Window> CreatePreviewAsync(IServiceProvider services, TargetMode mode)
    {
        var planner = services.GetRequiredService<IApplyPlanner>();
        var items = services.GetRequiredService<IStashItemRepository>();
        var groups = services.GetRequiredService<IStashGroupRepository>();
        var settings = services.GetRequiredService<ISettingsService>();

        var group = (await groups.GetAllAsync(CancellationToken.None))
            .FirstOrDefault(candidate => IsDefaultBox(candidate))
            ?? (await groups.GetAllAsync(CancellationToken.None)).FirstOrDefault();

        var groupItems = group is null
            ? []
            : await items.GetByGroupAsync(group.Id, CancellationToken.None);

        var targetPath = mode == TargetMode.SpecifiedPath
            ? FixtureOut()
            : null;

        var plan = await planner.BuildPlanAsync(
            groupItems,
            new TargetSpec(mode, targetPath, StructureMode.FullRelative),
            settings.Current.DefaultConflictPolicy,
            CancellationToken.None);

        return new PreviewDialog(
            plan,
            services.GetRequiredService<IApplyExecutor>(),
            services.GetRequiredService<IBlobStore>(),
            settings);
    }

    /// <summary>默认箱：有 id 认 id，没有 id（没跑过 --seed）才退回按名字认。</summary>
    private static bool IsDefaultBox(StashGroup group) =>
        Fixture.DefaultBoxId is { } id
            ? group.Id == id
            : group.Name == Strings.DefaultGroupName;

    private static string FixtureOut()
    {
        var workspace = Fixture.Load();

        return workspace is null ? @"D:\目标目录" : Path.Combine(workspace, "out");
    }

    private static async Task<Window> CreateFailedToastAsync(IServiceProvider services)
    {
        var operations = services.GetRequiredService<IOperationRepository>();
        var operationEntries = services.GetRequiredService<IOperationEntryRepository>();

        var failed = (await operations.GetAllAsync(CancellationToken.None))
            .FirstOrDefault(operation => operation.Status == OperationStatus.PartiallyFailed);

        if (failed is null)
        {
            var dummy = new Operation
            {
                Id = "smoke-dummy-failed",
                Status = OperationStatus.PartiallyFailed,
                TargetMode = TargetMode.OriginalLocation,
                TargetDescription = @"D:\target",
                StartedAt = DateTimeOffset.Now,
            };
            return new ResultToast(dummy, [
                new OperationEntry
                {
                    Id = 1,
                    OperationId = dummy.Id,
                    Action = EntryResult.Failed,
                    TargetPath = @"D:\target\file.txt",
                    Error = "Locked",
                }
            ]);
        }

        return new ResultToast(failed, await operationEntries.GetByOperationAsync(failed.Id, CancellationToken.None));
    }

    private static async Task<Window> CreateGitStatusAsync(IServiceProvider services, string? fixture)
    {
        var statusViewModel = services.GetRequiredService<GitStatusViewModel>();
        var groups = services.GetRequiredService<IStashGroupRepository>();

        statusViewModel.RepoRoot = fixture is null ? string.Empty : Path.Combine(fixture, "repo");
        statusViewModel.CurrentGroupId = (await groups.GetAllAsync(CancellationToken.None))
            .FirstOrDefault(candidate => IsDefaultBox(candidate))?.Id ?? string.Empty;

        await statusViewModel.LoadCommand.ExecuteAsync(null);

        return ActivatorUtilities.CreateInstance<GitStatusDialog>(services);
    }

    private static async Task<Window> CreateGroupPickerAsync(IServiceProvider services)
    {
        var groups = services.GetRequiredService<IStashGroupRepository>();
        var all = await groups.GetAllAsync(CancellationToken.None);

        var dialog = new GroupPickerDialog();
        dialog.SetGroups(all, all.FirstOrDefault()?.Id ?? string.Empty);

        return dialog;
    }

    private static async Task<Window> CreateSetRootAsync(IServiceProvider services, string? fixture)
    {
        var dialog = new SetRootDialog();

        var source = fixture is null
            ? @"D:\Marketing\2026\Q3\设计物料\首页\按钮\确定.svg"
            : Path.Combine(fixture, "inbox", "素材", "图标", "01.png");

        dialog.SetSource(source);
        await Pump(dialog);

        return dialog;
    }

    /// <summary>泵一次调度器：Loaded 排队的收尾活与异步查询续体都要跑到闲。</summary>
    private static async Task Pump(Window window)
    {
        await UiPump.Settle(window);
    }
}
