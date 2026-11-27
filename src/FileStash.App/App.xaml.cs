using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using FileStash.App.Controls;
using FileStash.App.Diagnostics;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.App.Startup;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using FileStash.App.ViewModels;
using FileStash.App.Views;
using FileStash.Core.Abstractions;
using FileStash.Core.Localization;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Ipc;
using FileStash.Infrastructure.Shell;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Native;
using FileStash.Infrastructure.Services;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace FileStash.App;

/// <summary>
/// 应用入口。启动顺序是有讲究的，别随手调换：
/// 路径/设置/语言（构造函数里）→ 日志 → 异常处理 → 宿主/DI → 开发期开关 → 单实例 → 管道服务 → 命令行 → 托盘 → 主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>smoke / shots 模式下置位：异常处理器改成"记 marker 并退出"，不再弹模态框。</summary>
    public static bool InSmoke { get; set; }

    /// <summary>
    /// 自查模式下遇到的第一个错误。非空即判失败 —— 自查不能"出了错还报 OK"。
    /// </summary>
    public static string? SmokeFailure { get; set; }

    /// <summary>正在退出：主窗口这时不该再"关闭到托盘"。</summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>smoke 结果文件，CI 判成败以它为准（GUI 进程的 ExitCode 有读空坑）。</summary>
    public static string SmokeReportPath { get; } = Path.Combine(Path.GetTempPath(), "filestash-smoke.txt");

    private IHost? _host;
    private TaskbarIcon? _tray;
    private SingleInstanceCoordinator? _instance;
    private HotkeyManager? _hotkey;
    private ShakeDetector? _shakeDetector;
    private GitBranchWatcher? _branchWatcher;
    private MainWindow? _mainWindow;
    private FloatingWindow? _floatingWindow;

    /// <summary>正在重启（换语言）：这时别响应别的实例发来的指令。</summary>
    private bool _restarting;

    private readonly AppPaths _paths;
    private readonly SettingsService _settings;

    // --add 的防抖聚合：资源管理器经典菜单多选时会启动多个进程
    private readonly List<(string GroupId, string[] Paths)> _pendingAddPaths = [];
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private bool _flushingAdds;
    private readonly DispatcherTimer _addAggregationTimer;
    private DispatcherTimer? _periodicCleanupTimer;

    public App()
    {
        // 路径 + 设置 + 语言都在这里做，比 OnStartup 更早，也比 OnStartup 更稳。
        //
        // 为什么不能放在 OnStartup 里：OnStartup 是 async void，
        // 在里面改区域性会被 async 状态机的作用域吃掉 —— 方法一返回，
        // 区域性就退回系统语言了。表现是"界面静默变回中文"，
        // 而日志里一个异常都没有。构造函数不是 async 的，
        // 改动落在进程的根上下文上，谁也带不走。
        var startup = CommandLine.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        if (startup.RetryHome is not null)
        {
            if (!Path.IsPathFullyQualified(startup.RetryHome) || !Directory.Exists(startup.RetryHome))
                throw new ArgumentException(nameof(startup.RetryHome));
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, startup.RetryHome);
        }
        _paths = new AppPaths();
        _paths.EnsureDirectories();

        _settings = new SettingsService(_paths);
        _settings.Load();

        // 语言必须在建第一个窗口之前定下来：界面文字走的是 x:Static，
        // XAML 解析的那一刻就把值取死了，之后再换 CurrentUICulture 已经太晚。
        LanguageService.Apply(AppLanguageResolver.Resolve(_settings.Current.Language));

        _addAggregationTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };

        _addAggregationTimer.Tick += async (_, _) =>
        {
            _addAggregationTimer.Stop();
            await FlushPendingAddPathsAsync();
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 1) 日志：所有输出进 %LOCALAPPDATA%\StashBay\logs
        //    日志器只建一份，宿主与全局异常处理器共用同一个实例
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(_paths.LogDirectory, "stashbay-.log"),
                rollingInterval: RollingInterval.Day,
                shared: true)
            .CreateLogger();

        Log.Information(
            "StashBay 启动：程序集 {Assembly}，版本 {Version}，命令行 {Args}",
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Name,
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            string.Join(' ', e.Args));

        Log.Information(
            "界面语言：{Language}，区域性 {Culture}",
            _settings.Current.Language,
            CultureInfo.CurrentUICulture.Name);

        // 2) 异常处理要在任何用户代码之前挂上
        HookExceptionHandlers();

        // InitializeComponent has now loaded Light.xaml. Apply the saved theme after it,
        // before creating any windows, or the later Light dictionary overrides Dark.
        ThemeService.ApplyFromSettings(_settings.Current);

        // 3) 宿主 + DI
        _host = BuildHost(_paths, _settings);
        _host.Services.GetRequiredService<IStashService>().GroupsChanged += OnShelfGroupsChanged;

        // 4) 开发期自查开关：必须放在单实例互斥体检查之前。
        //    托盘里正在用的实例照常工作，检查进程自己开自己关。
        var shotsIndex = Array.FindIndex(e.Args, a =>
            a.StartsWith("--shots", StringComparison.OrdinalIgnoreCase));

        if (shotsIndex >= 0)
        {
            ScheduleDevRunAsync(() => ShotsRunner.RunAsync(e.Args, shotsIndex, _host.Services));
            return;
        }

        var probeIndex = Array.FindIndex(e.Args, a =>
            a.StartsWith("--probe", StringComparison.OrdinalIgnoreCase));

        if (probeIndex >= 0)
        {
            ScheduleDevRunAsync(() => ProbeRunner.RunAsync(e.Args, probeIndex, _host.Services));
            return;
        }

        var seedIndex = Array.FindIndex(e.Args, a =>
            a.StartsWith("--seed", StringComparison.OrdinalIgnoreCase));

        if (seedIndex >= 0)
        {
            // 取景夹具的播数据步骤：--shots / --probe 都靠它，没有它们拍的是空库
            var workspace = e.Args.Length > seedIndex + 1
                ? e.Args[seedIndex + 1]
                : Path.Combine(Path.GetTempPath(), "filestash-fixture");

            ScheduleDevRunAsync(() => SeedRunner.RunAsync(_host.Services, workspace));
            return;
        }

        if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            ScheduleDevRunAsync(() => SmokeRunner.RunAsync(_host.Services));
            return;
        }

        if (e.Args.Contains("--inspect", StringComparer.OrdinalIgnoreCase))
        {
            ScheduleDevRunAsync(() => InspectRunner.Run(_host.Services));
            return;
        }

        var command = CommandLine.Parse(e.Args);
        if (command.UnregisterShell)
        {
            var result = await new LogonStartupService(disabled: ShellIntegrationDisabled).UninstallInstalledAsync();
            if (result.Outcome is not (StartupOutcome.Applied or StartupOutcome.Unchanged))
            {
                Log.Error("Uninstall startup cleanup {Outcome}: {Detail}", result.Outcome, result.Detail);
                Shutdown(result.Outcome == StartupOutcome.Cancelled ? 1223 : 4);
                return;
            }
            Shutdown(UnregisterShellIntegration() ? 0 : 4);
            return;
        }
        if (command.ElevatedLogon)
        {
            if (!IsProcessAdministrator())
            {
                Log.Error("Administrator logon launch did not receive an elevated token.");
                Shutdown(5);
                return;
            }
            // Explicitly replace a resident normal instance instead of forwarding an elevated
            // logon launch to it. No process is killed; take over only after graceful shutdown.
            using var messenger = new SingleInstanceCoordinator(SingleInstanceCoordinator.DefaultPipeName,
                TimeSpan.Zero, skipInstanceCheck: true);
            await messenger.SendToPrimaryAsync(["--quit"], attempts: 1);
        }


        // 5) 单实例
        //    等待时长分档：双击走 250ms（老实例活得好好的才是常态，等满了必然走满）；
        //    "以管理员身份重开自己""切语言后重开自己"这类接管路径给 3s。
        var takeover = (command.Show || command.ElevatedLogon) ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(250);

        var singleInstanceWatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _instance = new SingleInstanceCoordinator(
                SingleInstanceCoordinator.DefaultPipeName,
                takeover,
                skipInstanceCheck: command.ElevatedRetryOperationId is not null,
                wakePrimary: !command.ElevatedLogon);
        }
        catch (Exception ex)
        {
            // 单实例检查失败不该让应用起不来：起不来就是"整个工具没了"，
            // 比多开一个实例严重得多。退化成"不做单实例检查"，照样能跑。
            // 注意 OnStartup 里抛的异常接不到 Dispatcher 的异常处理器，
            // 会直接变成进程崩溃 —— 所以这里必须自己兜住。
            Log.Error(ex, "单实例检查失败，本次不做单实例检查继续启动");
            if (command.ElevatedLogon || _settings.Current.StartWithWindowsAsAdministrator)
            {
                Shutdown(4);
                return;
            }
            _instance = null;
        }

        // 构造耗时要记下来：它里面有一段 Mutex.WaitOne（等老实例放手），
        // 那是**阻塞 UI 线程**的。哪天又出现"转发慢得要死"，
        // 先看这一行 —— 它慢就说明是老实例没放手，不是管道慢。
        Log.Information(
            "单实例协调器就绪：IsPrimary={IsPrimary}，耗时 {Elapsed} ms",
            _instance?.IsPrimary,
            singleInstanceWatch.ElapsedMilliseconds);

        if (_instance is { IsPrimary: false })
        {
            // 第二个实例：把参数转给主实例（协调器在构造时已经发过一次），
            // 这里再补发一次带完整参数的消息。
            //
            // 转发次数要分档：常态（双击 exe）老实例活得好好的，
            // 多试几次免得命令丢掉；接管路径（--show）转发失败还有"自己接管"
            // 这条退路，不值得为一次注定失败的转发等满 8 轮。
            var forwarded = !command.ElevatedLogon &&
                await _instance.SendToPrimaryAsync(e.Args, command.Show ? 2 : 8);

            if (forwarded)
            {
                Log.Information("已有实例在运行，转交参数后退出");
                Shutdown();
                return;
            }

            // 转发失败 = 老实例没接住 = 它多半已经死了，或者正在死
            //（切语言重启就是这条：老实例正在退出，新实例没抢到互斥体）。
            // 这时候退出去 = 机器上一个实例都不剩。
            // 改成接着等互斥体去接管：老实例一放手就接上。
            Log.Warning("转发参数失败，老实例可能已经退出；继续等待接管");

            // 互斥体的所有权按线程算，ReleaseMutex 只能由持有者线程调用 ——
            // 协调器是在 UI 线程上建的，Dispose 也在 UI 线程上。
            // 所以等待必须回到 UI 线程去做，不能在 await 续体的线程上做。
            var takenOver = await Dispatcher.InvokeAsync(
                () => _instance.TryTakeover(TimeSpan.FromSeconds(15)));

            if (!takenOver)
            {
                Log.Error("既转发不了也接管不成，本次退出");
                Shutdown();
                return;
            }

            Log.Information("老实例已退出，本次接管为主实例");

            // 接着往下走：管道服务、命令行、托盘、主窗口 —— 下面那些活现在归我们做。
        }

        // 提权重试：这个进程已经是提权状态了，直接把失败的条目重写一遍就退出
        if (command.ElevatedRetryOperationId is not null)
        {
            _ = Dispatcher.BeginInvoke(async () =>
            {
                await RunElevatedRetryAsync(command.ElevatedRetryOperationId);
                Shutdown();
            }, DispatcherPriority.ApplicationIdle);
            return;
        }

        // Collect orphaned empty temporary shelves before commands restore a selection.
        await DeleteEmptyShelvesAsync();

        // 6) 管道服务：接收其它实例转发过来的参数
        if (_instance is not null)
        {
            _ = Task.Run(async () => await _instance.RunServerAsync(HandleRemoteCommandAsync, CancellationToken.None));
        }

        // 7) 处理自己的命令行
        _ = Dispatcher.InvokeAsync(() => HandleCommandAsync(command));

        // 8) 托盘图标
        _tray = TrayIconService.Create();
        BuildTrayMenu();

        // 9) 主窗口：先预热再显示。
        //    冷窗口首次显示 141ms、热窗口 15ms，差近十倍——那一下要现场把整棵可视树建出来。
        //    预热不 Show、不抢焦点、不进任务栏，所以必须在 Show 之前做。
        _mainWindow = _host.Services.GetRequiredService<MainWindow>();
        WindowWarmUp.Warm(_mainWindow);

        // 界面上的辅助窗口由 App 打开（ViewModel 不碰窗口）
        WireViewModelEvents(_host.Services.GetRequiredService<MainViewModel>());

        // --show 是接管路径（重启自己）用的：它要的是"窗口出来"，所以压过"启动时最小化"。
        if (!(command.Minimized || ((command.LogonStartup || command.ElevatedLogon) && _settings.Current.MinimizeToTray)) || command.Show)
        {
            _mainWindow.Show();
        }

        // 10) 全局快捷键
        RegisterGlobalHotkey();
        StartShakeDetector(_settings.Current);

        // 11) 分支切换监听
        StartBranchWatcher();

        // 12) 数据库迁移 + 启动清理：放后台，启动路径上不做重活。
        //     失败必须上报 —— 只兜不报的话，用户看到的是"什么都没发生也没有任何提示"。
        await Dispatcher.InvokeAsync(
            async () =>
            {
                try
                {
                    var migrator = _host.Services.GetRequiredService<DatabaseMigrator>();
                    var version = await migrator.EnsureReadyAsync(CancellationToken.None);
                    Log.Information("数据库迁移完成，schema 版本 {Version}", version);

                    await _host.Services.GetRequiredService<IApplyExecutor>().RecoverAsync(CancellationToken.None);
                    var settings = _host.Services.GetRequiredService<ISettingsService>();

                    // 设置在启动路径上已经读过了，这里不要再读一遍 ——
                    // 重读会把 Current 换成新对象，正在开着的窗口还攥着旧的那份。
                    if (!ShellIntegrationDisabled)
                    {
                        var startupStatus = await _host.Services.GetRequiredService<ILogonStartupService>().ReadAsync();
                        _settings.ImportStartupPreference(startupStatus.State is LogonStartupState.Plain or
                            LogonStartupState.PlainNeedsRepair or LogonStartupState.Elevated or LogonStartupState.ElevatedNeedsRepair,
                            startupStatus.State is LogonStartupState.Elevated or LogonStartupState.ElevatedNeedsRepair);
                    }
                    ApplyShellIntegration(settings.Current);
                    await ReconcileStartupAsync(settings.Current);

                    var cleanup = _host.Services.GetRequiredService<ICleanupService>();

                    var exports = await cleanup.DeleteExpiredExportsAsync(CleanupService.ExportRetention, CancellationToken.None);
                    var temporary = await cleanup.DeleteStaleTemporaryFilesAsync(TimeSpan.FromHours(1), CancellationToken.None);
                    var history = await cleanup.DeleteExpiredHistoryAsync(settings.Current.HistoryRetentionDays, CancellationToken.None);

                    Log.Information(
                        "启动清理：过期导出目录 {Exports}，临时文件 {Temporary}，过期历史 {History}",
                        exports,
                        temporary,
                        history);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "启动清理失败");
                }
            },
            DispatcherPriority.ApplicationIdle);

        // 13) 定期后台清理：每 30 分钟轮询清一次过期的拖出目录与临时 blob
        _periodicCleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _periodicCleanupTimer.Tick += async (_, _) =>
        {
            try
            {
                await DeleteEmptyShelvesAsync();
                var cleanup = _host?.Services.GetService<ICleanupService>();
                if (cleanup is not null)
                {
                    await Task.Run(async () =>
                    {
                        await cleanup.DeleteExpiredExportsAsync(CleanupService.ExportRetention, CancellationToken.None);
                        await cleanup.DeleteStaleTemporaryFilesAsync(TimeSpan.FromHours(1), CancellationToken.None);
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "后台定期清理异常");
            }
        };
        _periodicCleanupTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        Log.Information("StashBay 退出，退出码 {ExitCode}", e.ApplicationExitCode);
        _periodicCleanupTimer?.Stop();
        // Startup and periodic cleanup own cache expiry. Blocking here on a storage
        // lease can deadlock a writer whose continuation needs this UI thread.
        _hotkey?.Dispose();
        _shakeDetector?.Dispose();
        _branchWatcher?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    internal static bool ShellIntegrationDisabled => InSmoke ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.NoShellIntegrationVariable)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.LegacyNoShellIntegrationVariable));

    private static bool IsProcessAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private async Task ReconcileStartupAsync(Core.Models.AppSettings settings)
    {
        var service = _host!.Services.GetRequiredService<ILogonStartupService>();
        var status = await service.ReadAsync();
        if (status.State == LogonStartupState.Suppressed) return;
        var expected = !settings.StartWithWindows ? LogonStartupState.Disabled :
            settings.StartWithWindowsAsAdministrator ? LogonStartupState.Elevated : LogonStartupState.Plain;
        if (status.State == expected) return;
        // Startup only reads state. Registration/repair/removal is an explicit settings action,
        // so late startup housekeeping cannot undo a concurrent settings save.
        Log.Warning("Logon startup requires attention: {State} {Detail}", status.State, status.Detail);
        _host.Services.GetRequiredService<IDialogService>().ShowToast(Strings.SettingsTitle, Strings.StartupAttention);
    }

    /// <summary>按设置同步资源管理器右键菜单、"发送到"、开机自启。</summary>
    internal static bool ApplyShellIntegration(Core.Models.AppSettings settings)
    {
        // 自动化 / 无头运行：注册表**一个都不碰** ——
        // 连"关掉开关"都不做，免得变成一个静默改变用户既有设置的动作
        if (ShellIntegrationDisabled)
        {
            Log.Information("已按环境变量跳过资源管理器集成");
            return true;
        }

        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        try
        {
            var contextMenu = new ContextMenuRegistrar(executablePath);
            var sendTo = new SendToRegistrar(executablePath);

            if (settings.ContextMenuEnabled)
            {
                contextMenu.Register();
                sendTo.Register();
            }
            else
            {
                contextMenu.Unregister();
                sendTo.Unregister();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "同步资源管理器集成失败");
            return false;
        }
    }

    /// <summary>卸载时用：把注册表里的痕迹清干净。</summary>
    private static bool UnregisterShellIntegration()
    {
        if (ShellIntegrationDisabled)
        {
            return true;
        }

        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        try
        {
            new ContextMenuRegistrar(executablePath).Unregister();
            new SendToRegistrar(executablePath).Unregister();

            Log.Information("已注销资源管理器集成");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "注销资源管理器集成失败");
            return false;
        }
    }

    /// <summary>监听暂存项涉及的仓库，切分支时弹托盘通知。</summary>
    private void StartBranchWatcher()
    {
        if (_host is null)
        {
            return;
        }

        var settings = _host.Services.GetRequiredService<ISettingsService>();

        if (!settings.Current.WatchGitBranchSwitch)
        {
            return;
        }

        var git = _host.Services.GetRequiredService<IGitService>();

        if (!git.IsAvailable)
        {
            return;
        }

        _branchWatcher = new GitBranchWatcher(git);
        _branchWatcher.BranchChanged += (_, args) =>
            Dispatcher.InvokeAsync(() => OnBranchChanged(args));

        // 暂存项涉及的仓库都要盯着
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        viewModel.RepositoriesChanged += repos =>
        {
            foreach (var repo in repos)
            {
                _ = _branchWatcher.WatchAsync(repo, CancellationToken.None);
            }
        };
    }

    private async void OnBranchChanged(BranchChangedEventArgs args)
    {
        try
        {
            var viewModel = _host?.Services.GetRequiredService<MainViewModel>();

            if (viewModel is null)
            {
                return;
            }

            var count = viewModel.Items.Count(item =>
                item.Item.GitRepoRoot is not null &&
                string.Equals(item.Item.GitRepoRoot, args.RepoRoot, StringComparison.OrdinalIgnoreCase));

            if (count == 0)
            {
                return;
            }

            var message = Strings.Format(
                Strings.BranchSwitchedMessage,
                Path.GetFileName(args.RepoRoot),
                args.NewBranch,
                count);

            Log.Information(message);

            if (_tray is null)
            {
                return;
            }

            _tray.TrayBalloonTipClicked -= OnBalloonClick;
            _tray.TrayBalloonTipClicked += OnBalloonClick;

            ShowBranchBalloon(message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理分支切换通知失败");
        }
    }

    /// <summary>
    /// 弹一个托盘气泡。
    /// </summary>
    /// <remarks>
    /// H.NotifyIcon 2.x 没有经典的 ShowBalloon 了，只有自定义气泡（CustomBalloon 是个 Popup）。
    /// </remarks>
    private void ShowBranchBalloon(string message)
    {
        var content = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush?)FindResource("Brush.Surface"),
            BorderBrush = (System.Windows.Media.Brush?)FindResource("Brush.BorderStrong"),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(12),
            Padding = new System.Windows.Thickness(12),
            MaxWidth = 320,
            Child = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush?)FindResource("Brush.TextPrimary"),
            },
        };

        _tray!.ShowCustomBalloon(
            content,
            System.Windows.Controls.Primitives.PopupAnimation.Slide,
            8000);
    }

    private static object? FindResource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key);

    private async void OnBalloonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var viewModel = _host?.Services.GetRequiredService<MainViewModel>();

            if (viewModel is not null)
            {
                viewModel.ShowMainWindow();
                await viewModel.ApplyOriginalAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "托盘通知点击处理失败");
        }
    }

    // ---- M5：命令行、托盘菜单、快捷键 ----

    /// <summary>提权重试：把那次操作里失败的条目重写一遍。</summary>
    private async Task RunElevatedRetryAsync(string operationId)
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Log.Information("提权重试 TokenIsElevated={Elevated}", elevated);
            if (!elevated || InSmoke) return;
            var executor = _host!.Services.GetRequiredService<IApplyExecutor>();
            Log.Information("提权重试正在读取确认范围");
            var selection = await executor.PrepareRetryAsync(operationId, CancellationToken.None, inspectTargets: false);
            Log.Information("提权重试确认范围 {Count} 项", selection.Count);
            if (selection.Count == 0) return;
            var details = string.Join(Environment.NewLine + Environment.NewLine, selection.Select(entry =>
                $"{entry.TargetPath}{Environment.NewLine}" +
                $"{(entry.Action == Core.Models.PlannedAction.Overwrite ? Strings.ActionOverwrite : entry.Action == Core.Models.PlannedAction.Rename ? Strings.ActionRename : Strings.ActionCreate)}" +
                $"{Environment.NewLine}SHA-256 {entry.Sha256}"));
            if (ThemedMessageBox.Show(Strings.ElevatedRetryConfirm + Environment.NewLine + details,
                    Strings.ToastRetryElevated, MessageBoxButton.YesNo, MessageBoxImage.Question, showInTaskbar: true) != MessageBoxResult.Yes)
            {
                Log.Information("提权重试已取消");
                return;
            }
            var count = await executor.RetryApprovedAsync(operationId, selection, CancellationToken.None);

            Log.Information("提权重试完成：{Count} 个条目", count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提权重试失败");
        }
    }

    /// <summary>处理命令行（自己的或别的实例转发过来的）。</summary>
    private async Task HandleCommandAsync(CommandLine command)
    {
        if (_host is null)
        {
            return;
        }

        var viewModel = _host.Services.GetRequiredService<MainViewModel>();

        await _commandLock.WaitAsync();
        try
        {
            await viewModel.InitializeAsync();
            var groupId = viewModel.SelectedGroup?.Id;
            if (command.GroupName is not null)
            {
                groupId = await viewModel.SelectGroupAsync(command.GroupName);
            }

            if (command.AddPaths.Count > 0)
            {
                QueueAddPaths(command.AddPaths, groupId);
            }

            if (command.Quit)
            {
                // 让正在运行的那个实例退出（托盘"退出"的脚本化版本）
                ShutdownApplication();
                return;
            }

            if (command.ApplyHerePath is not null)
            {
                await viewModel.ApplyHereAsync(command.ApplyHerePath);
            }
            else if (command.ApplyOriginal)
            {
                await viewModel.ApplyOriginalAsync();
            }
            else if (!(command.Minimized || ((command.LogonStartup || command.ElevatedLogon) && _settings.Current.MinimizeToTray)) || command.Show)
            {
                // 没有具体动作的转发（又双击了一次 exe）→ 把窗口叫出来
                viewModel.ShowMainWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理命令行失败");
        }
        finally { _commandLock.Release(); }
    }

    /// <summary>别的实例通过管道转发过来的参数。</summary>
    private Task HandleRemoteCommandAsync(string[] args) =>
        Dispatcher.InvokeAsync(() =>
        {
            // 正在重启（换语言）：别响应别的实例发来的指令，老老实实退出。
            // 不然新实例一启动就被老实例"叫醒"，老实例把主窗口拉起来接着又关掉。
            if (_restarting)
            {
                return;
            }

            // 不 await：转发过来的指令是"加到暂存箱"这种后台活，
            // 等它做完会把管道服务的读循环堵住。
            _ = HandleCommandAsync(CommandLine.Parse(args));
        }).Task;

    /// <summary>
    /// 退出应用：先停止管道服务，再退出。
    /// </summary>
    /// <remarks>
    /// 停止管道服务 = 不再对别的实例回确认。
    /// 确认的含义是"这条命令我收下了"，可我们马上就要没了 ——
    /// 收到确认的实例会以为有人执行了、自己退出；
    /// 结果是老实例退了、新实例没接上，机器上一个都不剩。
    ///
    /// 停止要早于真正退出：窗口关闭、数据库清理都可能磨蹭，
    /// 那段时间里我们早就不该再接别人的指令了。
    /// </remarks>
    private async void ShutdownApplication()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        _instance?.StopServer();
        foreach (var window in Windows.OfType<MainWindow>().ToArray())
            await window.UpdateLifetimeAsync(closing: true);
        Shutdown();
    }

    /// <summary>
    /// 换语言了：界面文字是 x:Static，XAML 解析的时候就定死了 —— 运行中换不掉，只能重开进程。
    /// </summary>
    /// <remarks>
    /// 新实例带 <c>--show</c>：那条路径的互斥体等待是 3 秒（见上面的 takeover 分档），
    /// 正好留给当前实例退出、释放互斥体。
    /// </remarks>
    private void OnLanguageChangeRequested()
    {
        _restarting = true;

        // 先拉新实例，再退自己：新实例要等当前实例释放互斥体才能接管，
        // 顺序反了就变成"老实例退了、新实例没起来，机器上一个都没有"。
        if (!AppRestart.Relaunch("--show"))
        {
            Log.Error("语言切换失败：拉起新进程失败，设置里的语言要等下次手动启动才生效");
            _host?.Services.GetRequiredService<IDialogService>().ShowInfo(
                Strings.SettingsTitle,
                Strings.LanguageRestartFailed);
        }

        ShutdownApplication();
    }

    /// <summary>
    /// --add 的防抖聚合。
    /// </summary>
    /// <remarks>
    /// 资源管理器经典菜单多选时会启动多个进程，一个一个加会刷屏；
    /// 400ms 静默期后一次性加完。
    /// </remarks>
    private void QueueAddPaths(IEnumerable<string> paths, string? groupId = null)
    {
        groupId ??= _host?.Services.GetRequiredService<MainViewModel>().SelectedGroup?.Id;
        if (groupId is null) return;
        _pendingAddPaths.Add((groupId, paths.ToArray()));

        _addAggregationTimer.Stop();
        _addAggregationTimer.Start();
    }

    private async Task FlushPendingAddPathsAsync()
    {
        if (_flushingAdds || _pendingAddPaths.Count == 0 || _host is null) return;
        _flushingAdds = true;
        var requests = _pendingAddPaths.ToArray();
        _pendingAddPaths.Clear();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        try
        {
            foreach (var group in requests.GroupBy(request => request.GroupId))
            {
                try
                {
                    await viewModel.AddPathsToGroupAsync(group.Key,
                        group.SelectMany(request => request.Paths).ToArray());
                }
                catch (Exception ex) { Log.Error(ex, "加入暂存失败"); }
            }
        }
        finally
        {
            _flushingAdds = false;
            if (_pendingAddPaths.Count > 0) _addAggregationTimer.Start();
        }
    }

    /// <summary>托盘右键菜单。</summary>
    private void BuildTrayMenu()
    {
        if (_tray is null || _host is null)
        {
            return;
        }

        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        var menu = new System.Windows.Controls.ContextMenu { DataContext = viewModel };

        var newShelfItem = new MenuItem { Header = Strings.TrayNewShelf };
        newShelfItem.Click += async (_, _) => await OpenNewShelfAsync();
        menu.Items.Add(newShelfItem);

        // 一键唤出：小暂存区。多数时候只想"把这个拖走"
        var showItem = new MenuItem { Header = Strings.TrayShowCompact };
        showItem.Click += (_, _) => viewModel.ShowCompactPanel();
        menu.Items.Add(showItem);

        // 完整工作台：各种规则转移
        var workbenchItem = new MenuItem { Header = Strings.TrayOpenWorkbench };
        workbenchItem.Click += (_, _) => viewModel.ShowWorkbench();
        menu.Items.Add(workbenchItem);

        var floatingItem = new MenuItem { Command = viewModel.ToggleFloatingWindowCommand };
        floatingItem.SetBinding(HeaderedItemsControl.HeaderProperty,
            new Binding(nameof(MainViewModel.FloatingWindowActionText)) { Source = viewModel });
        menu.Items.Add(floatingItem);

        var applyItem = new MenuItem { Header = Strings.TrayApplyOriginal, Command = new RelayCommand(async () => await viewModel.ApplyOriginalAsync()) };
        menu.Items.Add(applyItem);

        var undoItem = new MenuItem { Header = Strings.TrayUndoLast, Command = new RelayCommand(async () => await viewModel.UndoLastCommand.ExecuteAsync(null)) };
        menu.Items.Add(undoItem);

        var clearAllItem = new MenuItem { Header = Strings.TrayClearAll, Command = viewModel.ClearAllBoxesCommand };
        menu.Items.Add(clearAllItem);

        menu.Items.Add(new Separator());

        var statusItem = new MenuItem { Header = Strings.TrayGitChanges };
        statusItem.Click += (_, _) => OpenGitStatusDialog();
        menu.Items.Add(statusItem);

        var historyItem = new MenuItem { Header = Strings.TrayHistory };
        historyItem.Click += (_, _) => OpenWindow<HistoryWindow>();
        menu.Items.Add(historyItem);

        var presetsItem = new MenuItem { Header = Strings.TrayRootPresets };
        presetsItem.Click += (_, _) => OpenWindow<RootPresetsWindow>();
        menu.Items.Add(presetsItem);

        var settingsItem = new MenuItem { Header = Strings.TraySettings };
        settingsItem.Click += (_, _) => OpenWindow<SettingsWindow>();
        menu.Items.Add(settingsItem);

        var aboutItem = new MenuItem { Header = Strings.TrayAbout };
        aboutItem.Click += (_, _) => OpenWindow<AboutWindow>();
        menu.Items.Add(aboutItem);

        var exitItem = new MenuItem { Header = Strings.TrayExit };
        exitItem.Click += (_, _) => ShutdownApplication();
        menu.Items.Add(exitItem);

        _tray.ContextMenu = menu;
        _tray.TrayMouseDoubleClick += async (_, _) => await OpenNewShelfAsync();
    }

    private async Task OpenNewShelfAsync()
    {
        if (_host is null || IsShuttingDown) return;
        try
        {
            var vm = ActivatorUtilities.CreateInstance<MainViewModel>(_host.Services);
            await vm.CreateShelfAsync();
            WireViewModelEvents(vm);
            var window = new MainWindow(vm, _settings,
                _host.Services.GetRequiredService<ShelfLifetimeService>());
            window.Show();
            WindowEffects.PlaceNearCursor(window);
            WindowEffects.BringToFront(window);
        }
        catch (Exception ex) { ShowError(_mainWindow, ex.Message); }
    }

    private void WireViewModelEvents(MainViewModel vm)
    {
        vm.SettingsRequested += () => OpenWindow<SettingsWindow>();
        vm.NewShelfRequested += async () => await OpenNewShelfAsync();
        vm.HistoryRequested += () => OpenWindow<HistoryWindow>();
        vm.GitChangesRequested += () => OpenGitStatusDialog();
        vm.IsFloatingWindowVisible = _floatingWindow?.IsVisible == true;
        vm.FloatingWindowToggleRequested += ToggleFloatingWindow;
        vm.RootPresetsRequested += () => OpenWindow<RootPresetsWindow>();
        vm.AboutRequested += () => OpenWindow<AboutWindow>();
    }

    private bool _shelfGroupsRefreshing;
    private bool _shelfGroupsRefreshRequested;
    private void OnShelfGroupsChanged()
    {
        if (InSmoke || IsShuttingDown) return;
        Dispatcher.BeginInvoke(async () =>
        {
            _shelfGroupsRefreshRequested = true;
            if (_shelfGroupsRefreshing) return;
            _shelfGroupsRefreshing = true;
            try
            {
                do
                {
                    _shelfGroupsRefreshRequested = false;
                    foreach (var vm in Windows.OfType<MainWindow>().Select(window => window.ViewModel).Distinct().ToArray())
                        await vm.RefreshAfterGroupCleanupAsync();
                } while (_shelfGroupsRefreshRequested && !IsShuttingDown);
            }
            catch (Exception error) { Log.Error(error, "Refreshing shelves after empty-group cleanup failed"); }
            finally { _shelfGroupsRefreshing = false; }
        });
    }

    private async Task DeleteEmptyShelvesAsync()
    {
        if (_host is null) return;
        await ShelfGroupMigration.AdoptLegacyAsync(_host.Services.GetRequiredService<IStashGroupRepository>());
        var count = await _host.Services.GetRequiredService<ShelfLifetimeService>().DeleteEmptyAsync();
        if (count > 0)
        {
            await _host.Services.GetRequiredService<ICleanupService>()
                .DeleteUnreferencedBlobsAsync(CancellationToken.None);
            Log.Information("Removed {Count} empty temporary shelves", count);
        }
    }

    /// <summary>从 git 改动里挑文件加入暂存。</summary>
    private void OpenGitStatusDialog()
    {
        if (_host is null)
        {
            return;
        }

        var viewModel = _host.Services.GetRequiredService<MainViewModel>();

        // 仓库取当前组里第一个属于仓库的暂存项
        var repoRoot = viewModel.Items
            .Select(item => item.Item.GitRepoRoot)
            .FirstOrDefault(root => !string.IsNullOrEmpty(root));

        if (repoRoot is null)
        {
            _host.Services.GetRequiredService<IDialogService>().ShowInfo(Strings.AppName, Strings.NoGitItems);
            return;
        }

        var statusViewModel = _host.Services.GetRequiredService<GitStatusViewModel>();
        statusViewModel.RepoRoot = repoRoot;
        statusViewModel.CurrentGroupId = viewModel.SelectedGroup?.Id ?? string.Empty;

        var dialog = new GitStatusDialog(statusViewModel);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.Show();
    }

    private void ToggleFloatingWindow()
    {
        if (_host is null || IsShuttingDown)
        {
            return;
        }

        if (_floatingWindow?.IsVisible == true)
        {
            _floatingWindow.Hide();
            return;
        }

        if (_floatingWindow is null)
        {
            var window = _host.Services.GetRequiredService<FloatingWindow>();
            var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            void UpdateVisibility(bool visible)
            {
                mainViewModel.IsFloatingWindowVisible = visible;
                foreach (var shelf in Windows.OfType<MainWindow>())
                    shelf.ViewModel.IsFloatingWindowVisible = visible;
            }

            window.IsVisibleChanged += (_, _) => UpdateVisibility(window.IsVisible);
            window.Closed += (_, _) =>
            {
                _floatingWindow = null;
                UpdateVisibility(false);
            };
            _floatingWindow = window;
        }

        _floatingWindow.Show();
    }

    /// <summary>开一个工具窗口（非模态，重复打开时激活已有的那个）。</summary>
    private void OpenWindow<TWindow>() where TWindow : Window
    {
        if (_host is null)
        {
            return;
        }

        var existing = Application.Current.Windows.OfType<TWindow>().FirstOrDefault();

        if (existing is not null)
        {
            if (!existing.IsVisible)
            {
                existing.Show();
            }

            WindowEffects.BringToFront(existing);
            return;
        }

        var window = _host.Services.GetRequiredService<TWindow>();
        if (window.DataContext is SettingsViewModel settingsViewModel)
        {
            settingsViewModel.LanguageChangeRequested += OnLanguageChangeRequested;
            window.Closed += (_, _) => settingsViewModel.LanguageChangeRequested -= OnLanguageChangeRequested;
        }
        window.Show();
    }

    /// <summary>
    /// 晃动检测器：快速左右晃鼠标，就地唤出搁架并加入资源管理器选中的文件。
    /// </summary>
    /// <remarks>
    /// 它和 <see cref="RegisterGlobalHotkey"/> 走同一条 <c>HandleSummonTrigger</c>，
    /// 两条路径都只读取资源管理器文件选中项。
    /// </remarks>
    private void StartShakeDetector(Core.Models.AppSettings settings)
    {
        try
        {
            _shakeDetector = new ShakeDetector(Dispatcher);
            _shakeDetector.ShakeDetected += () =>
            {
                try
                {
                    HandleSummonTrigger();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "晃动呼出暂存箱失败");
                }
            };
            _shakeDetector.SetEnabled(settings.EnableShakeToSummon);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化晃动检测器失败");
        }
    }

    private const int HotkeyAddId = 0xBEEF;
    private const int HotkeyWorkbenchId = 0xBEF0;
    private const int HotkeyPasteId = 0xBEF1;

    /// <summary>全局快捷键：前台是资源管理器就把它选中的文件加入暂存，否则只叫出主窗口。</summary>
    private void RegisterGlobalHotkey()
    {
        ReloadGlobalHotkey(showToastOnFailure: true);
    }

    /// <summary>
    /// 注册或热重载全局快捷键集合（呼出加入、工作台、剪贴板入箱）。
    /// 支持清空快捷键（安全禁用）；若按键被系统或第三方占用导致注册失败，弹出非模态 Toast 提示。
    /// </summary>
    public bool ReloadGlobalHotkey(bool showToastOnFailure = true)
    {
        if (_host is null)
        {
            return false;
        }

        try
        {
            _hotkey?.Dispose();
            _hotkey = new HotkeyManager();

            var settings = _host.Services.GetRequiredService<ISettingsService>();
            var failedGestures = new List<string>();

            // 1. 呼出小搁架 / 加入资源管理器选中项
            var gestureAdd = settings.Current.GlobalHotkeyAdd?.Trim();
            if (!string.IsNullOrWhiteSpace(gestureAdd))
            {
                if (!_hotkey.Register(HotkeyAddId, gestureAdd, OnGlobalHotkeyTriggered))
                {
                    Log.Warning("注册加入暂存快捷键失败：{Gesture}", gestureAdd);
                    failedGestures.Add(gestureAdd);
                }
                else
                {
                    Log.Information("成功注册加入暂存快捷键：{Gesture}", gestureAdd);
                }
            }

            // 2. 呼出完整工作台
            var gestureWorkbench = settings.Current.GlobalHotkeyWorkbench?.Trim();
            if (!string.IsNullOrWhiteSpace(gestureWorkbench))
            {
                if (!_hotkey.Register(HotkeyWorkbenchId, gestureWorkbench, OnGlobalHotkeyWorkbenchTriggered))
                {
                    Log.Warning("注册工作台快捷键失败：{Gesture}", gestureWorkbench);
                    failedGestures.Add(gestureWorkbench);
                }
                else
                {
                    Log.Information("成功注册工作台快捷键：{Gesture}", gestureWorkbench);
                }
            }

            // 3. 剪贴板快速存入暂存
            var gesturePaste = settings.Current.GlobalHotkeyPaste?.Trim();
            if (!string.IsNullOrWhiteSpace(gesturePaste))
            {
                if (!_hotkey.Register(HotkeyPasteId, gesturePaste, OnGlobalHotkeyPasteTriggered))
                {
                    Log.Warning("注册剪贴板入箱快捷键失败：{Gesture}", gesturePaste);
                    failedGestures.Add(gesturePaste);
                }
                else
                {
                    Log.Information("成功注册剪贴板入箱快捷键：{Gesture}", gesturePaste);
                }
            }

            if (failedGestures.Count > 0)
            {
                if (showToastOnFailure)
                {
                    var dialogs = _host.Services.GetService<IDialogService>();
                    dialogs?.ShowToast(
                        Strings.HotkeyRegisterFailedTitle,
                        Strings.Format(Strings.HotkeyRegisterFailedMessage, string.Join(", ", failedGestures)));
                }

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "注册全局快捷键异常");
            return false;
        }
    }

    private void OnGlobalHotkeyTriggered()
    {
        try
        {
            HandleSummonTrigger();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "全局快捷键唤出失败");
            _host?.Services.GetRequiredService<MainViewModel>().ShowCompactPanel();
        }
    }

    /// <summary>
    /// 处理晃动或全局快捷键唤出：
    /// 1. 若前台是资源管理器且选中了文件，将文件直接入箱；
    /// 2. 若无选中文件，正常就地展开小搁架。
    /// </summary>
    private void HandleSummonTrigger()
    {
        if (_host is null) return;
        var vm = _host.Services.GetService<MainViewModel>();
        if (vm is null) return;

        // 1. 若前台已经是 StashBay 自身，直接叫出/切换小面板，不抓取自身
        if (SelectionGrabber.IsForegroundCurrentProcess())
        {
            vm.ShowCompactPanel();
            return;
        }

        // 2. 若前台是资源管理器且选中了文件，将选中文件加入暂存
        try
        {
            var selectedFiles = ExplorerSelection.GetSelectedPaths();
            if (selectedFiles.Count > 0)
            {
                QueueAddPaths(selectedFiles);
                vm.ShowCompactPanel();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取资源管理器选中项失败");
        }

        // 未选中文件时只唤出搁架。
        vm.ShowCompactPanel();
    }

    private void OnGlobalHotkeyWorkbenchTriggered()
    {
        try
        {
            _host?.Services.GetRequiredService<MainViewModel>().ShowWorkbench();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "快捷键打开工作台失败");
        }
    }

    private void OnGlobalHotkeyPasteTriggered()
    {
        try
        {
            var vm = _host?.Services.GetRequiredService<MainViewModel>();
            if (vm is null) return;
            vm.ShowCompactPanel();
            vm.PasteCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "快捷键粘贴剪贴板入箱失败");
        }
    }

    private static IHost BuildHost(AppPaths paths, ISettingsService settings)
    {
        var builder = Host.CreateApplicationBuilder();

        // 存储层（M1）
        builder.Services.AddSingleton(paths);

        // 设置在启动路径上已经读过一遍（语言要知道它），这里注册的是同一个实例 ——
        // 注册两个实例的话，窗口拿到的 Current 会是另一份没读过的默认值。
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<IBlobStore, BlobStore>();
        builder.Services.AddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<DatabaseMigrator>();

        // 仓储拿到的连接必须先等迁移跑完（SchemaReadyConnectionFactory），
        // 否则窗口一 Loaded 就查表，表还不存在 —— 干净环境里第一次跑必炸
        builder.Services.AddSingleton<ISqliteConnectionFactory, SchemaReadyConnectionFactory>();
        builder.Services.AddSingleton<IStashGroupRepository, StashGroupRepository>();
        builder.Services.AddSingleton<IStashItemRepository, StashItemRepository>();
        builder.Services.AddSingleton<IStashEntryRepository, StashEntryRepository>();
        builder.Services.AddSingleton<IRootPresetRepository, RootPresetRepository>();
        builder.Services.AddSingleton<IOperationRepository, OperationRepository>();
        builder.Services.AddSingleton<IOperationEntryRepository, OperationEntryRepository>();

        // 暂存层（M2）
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddSingleton<IStashService, StashService>();

        // 应用引擎（M3）
        builder.Services.AddSingleton<IAtomicFileWriter, AtomicFileWriter>();
        builder.Services.AddSingleton<IFileLockInspector, RestartManagerLockInspector>();
        builder.Services.AddSingleton<IApplyPlanner, ApplyPlanner>();
        builder.Services.AddSingleton<IApplyExecutor, ApplyExecutor>();

        // 拖出与清理（M4）
        builder.Services.AddSingleton<IDragExportService, DragExportService>();
        builder.Services.AddSingleton<ICleanupService, CleanupService>();
        builder.Services.AddSingleton<ShelfLifetimeService>();

        // 界面（M2）
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        // 工具窗口（M6）
        builder.Services.AddTransient<HistoryViewModel>();
        builder.Services.AddSingleton<ILogonStartupService>(_ => new LogonStartupService(disabled: ShellIntegrationDisabled,
            delay: () => settings.Current.AdministratorStartupDelaySeconds));
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<RootPresetsViewModel>();
        builder.Services.AddTransient<GitStatusViewModel>();
        builder.Services.AddTransient<HistoryWindow>();
        builder.Services.AddTransient<SettingsWindow>();
        builder.Services.AddTransient<RootPresetsWindow>();
        builder.Services.AddTransient<GitStatusDialog>();
        builder.Services.AddTransient<FloatingWindow>();
        builder.Services.AddTransient<AboutWindow>();

        // 宿主日志接到已经建好的 Serilog 实例上（同一个日志器，不另配一遍）
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);

        return builder.Build();
    }

    /// <summary>
    /// 把自查回调推到调度器上跑，允许回调自己 await。
    /// </summary>
    /// <remarks>
    /// 不要在 OnStartup 里当场跑：当场跑时窗口 hwnd=0、尺寸全零。推迟的代价是零，就一律推迟。
    ///
    /// 也别用 <c>GetAwaiter().GetResult()</c> 在调度器线程上等异步 —— 那是在等一个
    /// 需要调度器线程才能继续的续体，等于自己等自己。
    /// </remarks>
    private static void ScheduleDevRunAsync(Func<Task<int>> run)
    {
        Application.Current.Dispatcher.BeginInvoke(
            async () =>
            {
                // 应用已经开始关闭时不要再跑：那时候 provider 已经释放，
                // 跑下去只会抛一个"对象已释放"的二次错误，把真正的错因盖住
                if (Application.Current.Dispatcher.HasShutdownStarted)
                {
                    return;
                }

                Application.Current.Shutdown(await run());
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void HookExceptionHandlers()
    {
        // UI 线程：唯一能救的一条，设 Handled=true 之后应用能继续跑
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 其他线程：进来时进程已经在终止，只能把堆栈写进日志
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "AppDomain 未处理异常，进程即将终止");
            }
            else
            {
                Log.Fatal("AppDomain 未处理异常（非 Exception）：{ExceptionObject}", args.ExceptionObject);
            }
        };

        // 没有 await 的 Task：默认不杀进程，异常要等 GC 回收任务时才浮出来 —— 只能写日志
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察到的任务异常");
            args.SetObserved();
        };
    }

    /// <summary>
    /// 模态错误框。
    /// </summary>
    /// <remarks>
    /// 自查模式下**绝不弹**：模态框有自己的消息循环，会把进程永远吊在那里 ——
    /// 既不写 marker 也不退出，表现就是"自检挂住了"。
    /// 那种情况下改成把错误记下来，让自查以失败收场。
    /// </remarks>
    public static void ShowError(Window? owner, string message)
    {
        if (InSmoke)
        {
            Log.Error("自查模式下的错误（不弹模态框）：{Message}", message);
            SmokeFailure ??= message;
            return;
        }

        ThemedMessageBox.Show(
            owner,
            message,
            Strings.UnhandledErrorTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常");

        if (InSmoke)
        {
            // 自检模式下绝不能弹模态框：那会让 CI 的 smoke 进程永远等着。
            SmokeFailure ??= e.Exception.ToString();
            File.WriteAllText(SmokeReportPath, e.Exception.ToString());
            e.Handled = true;
            Shutdown(1);
            return;
        }

        ThemedMessageBox.Show(
            e.Exception.Message,
            Strings.UnhandledErrorTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
