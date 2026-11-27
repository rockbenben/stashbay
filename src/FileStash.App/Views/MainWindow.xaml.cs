using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FileStash.App.Controls;
using FileStash.App.Services;
using FileStash.App.Resources;
using FileStash.App.ViewModels;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;

namespace FileStash.App.Views;

public partial class MainWindow : Window
{
    /// <summary>完整工作台的尺寸（跟 XAML 里声明的一致）。</summary>
    private const double WorkbenchWidth = 1180;

    private const double WorkbenchHeight = 760;

    private const double WorkbenchMinWidth = 900;

    private const double WorkbenchMinHeight = 380;

    private const double CompactMinWidth = 220;
    private const double CompactMinHeight = 180;
    private const double CompactDefaultHeight = 220;

    private const double CompactMaxHeight = 480;

    private Point _dragStart;
    private ItemViewModel? _dragCandidate;

    /// <summary>展开之前工作台在哪儿，收起小面板时好还原。</summary>
    private Rect _workbenchBounds;
    private bool _workbenchBoundsSaved;
    private Size _compactSize = new(CompactMinWidth, CompactDefaultHeight);
    private Point _compactPosition;
    private bool _compactPositionSaved;
    private bool _workbenchIsMaximized;

    private readonly ShelfLifetimeService? _lifetime;
    private bool _initialized;

    public MainWindow(MainViewModel viewModel, ISettingsService settings, ShelfLifetimeService? lifetime = null)
    {
        InitializeComponent();

        // 按窗口所在那块屏的工作区封顶：1366x768@150% 只有 464 DIP 可用，
        // 固定高度超出去的话，底部的按钮会落到屏幕外
        WindowEffects.FitToWorkArea(this);

        ViewModel = viewModel;
        DataContext = viewModel;
        _settings = settings;
        _lifetime = lifetime;

        // 防白闪：窗口显示到第一帧之间有真实空档，那一帧是白的
        WindowEffects.EnableAntiFlash(this);

        // 默认就是小暂存区：多数时候用户只想"把这个拖走"
        ApplyMode(ViewModel.IsCompact);

        // 工作台恢复持久化的尺寸与位置；小面板使用独立的尺寸。
        if (!ViewModel.IsCompact)
        {
            RestoreWindowBounds();
        }

        SourceInitialized += (_, _) => WindowEffects.SetShelfChrome(this, ViewModel.IsCompact);
        StateChanged += OnWindowStateChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RestoreSucceeded += OnRestoreSucceeded;

        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += OnLocationChanged;
        IsVisibleChanged += async (_, _) => await UpdateLifetimeAsync();
        Closed += async (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.RestoreSucceeded -= OnRestoreSucceeded;
            await UpdateLifetimeAsync(closing: true);
        };
        Activated += async (_, _) =>
        {
            if (App.InSmoke || !_initialized || ViewModel.IsBusy) return;
            try { await ViewModel.RefreshAsync(); }
            catch (Exception ex) { App.ShowError(this, ex.Message); }
        };
    }

    public async Task UpdateLifetimeAsync(bool closing = false)
    {
        if (App.InSmoke || _lifetime is null) return;
        try
        {
            await _lifetime.SetWindowGroupAsync(this,
                !closing && (IsVisible || ViewModel.IsBusy) ? ViewModel.SelectedGroup?.Id : null);
            if (closing || (!IsVisible && !ViewModel.IsBusy))
                await _lifetime.SetWindowGroupAsync(ViewModel, null);
            else await _lifetime.CompleteCreationAsync(ViewModel, this);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Unable to persist shelf lifetime"); }
    }

    private void OnCompactDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null && source != sender)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        // Resizing under a held second click must not turn the first click's
        // file candidate into a drag when the pointer moves relative to the window.
        _dragCandidate = null;
        _isDraggingAllCandidate = false;
        ViewModel.IsCompact = false;
        e.Handled = true;
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded || WindowState == WindowState.Minimized)
        {
            return;
        }

        if (ViewModel.IsCompact)
        {
            _compactPosition = new Point(Left, Top);
            _compactPositionSaved = true;
        }
        else if (WindowState == WindowState.Normal)
        {
            _workbenchBounds = new Rect(Left, Top, Width, Height);
            _workbenchBoundsSaved = true;
        }
    }

    public MainViewModel ViewModel { get; }

    private readonly ISettingsService _settings;

    private void OnEmptyShelfClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.AddItemsCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.AddItemsCommand.Execute(null);
        }
    }

    /// <summary>紧凑 ↔ 完整：换的是窗口的尺寸和位置，内容各走各的。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy)
            || (e.PropertyName == nameof(MainViewModel.SelectedGroup) && !ViewModel.IsRebuildingGroups))
            _ = UpdateLifetimeAsync();
        if (e.PropertyName == nameof(MainViewModel.DragAllBadgeText))
            PulseStatusBadge();
        if (e.PropertyName == nameof(MainViewModel.IsCompact))
        {
            // 只记录用户手动调整的尺寸，收回工作台时恢复；内容和视图切换不改大小。
            if (!ViewModel.IsCompact && WindowState == WindowState.Normal)
            {
                _compactSize = new Size(Width, Height);
            }

            ApplyMode(ViewModel.IsCompact);
        }
    }

    private void ApplyMode(bool compact)
    {
        SetResourceReference(BackgroundProperty, compact ? "Brush.ShelfSurface" : "Brush.Surface");
        WindowEffects.SetShelfChrome(this, compact);
        if (compact)
        {
            WorkbenchRoot.Margin = new Thickness(0);
            if (WindowState == WindowState.Normal)
            {
                _workbenchBounds = new Rect(Left, Top, Width, Height);
                _workbenchBoundsSaved = true;
            }
            else if (WindowState == WindowState.Maximized)
            {
                _workbenchIsMaximized = true;
                WindowState = WindowState.Normal;
            }

            var workArea = WindowEffects.GetWindowWorkArea(this);

            Topmost = true;

            MinWidth = CompactMinWidth;
            MinHeight = CompactMinHeight;
            MaxWidth = 440;
            MaxHeight = CompactMaxHeight;

            Width = _compactSize.Width;
            Height = _compactSize.Height;

            // 1. 如果启用了光标就近呼出（晃动或快捷键），优先光标旁就近呼出
            if (_settings.Current.SummonAtCursor)
            {
                WindowEffects.PlaceNearCursor(this);
            }
            // 2. 如果用户之前在当前显示器手动摆放过小面板，恢复用户记忆的位置
            else if (_compactPositionSaved && workArea.Contains(_compactPosition))
            {
                Left = _compactPosition.X;
                Top = _compactPosition.Y;
            }
            // 3. 否则紧贴当前显示器工作区右下角停靠
            else
            {
                Left = workArea.Right - Width - 16;
                Top = workArea.Bottom - Height - 16;
            }

            WindowEffects.ClampToWorkArea(this);
            return;
        }

        Topmost = false;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        MinWidth = WorkbenchMinWidth;
        MinHeight = WorkbenchMinHeight;

        var currentWorkArea = WindowEffects.GetWindowWorkArea(this);

        if (_workbenchBoundsSaved)
        {
            Width = Math.Max(_workbenchBounds.Width, WorkbenchMinWidth);
            Height = Math.Max(_workbenchBounds.Height, WorkbenchMinHeight);

            // 检查保存的工作台位置是否在当前屏幕附近，避免跨屏瞬移
            if (currentWorkArea.IntersectsWith(_workbenchBounds))
            {
                Left = _workbenchBounds.X;
                Top = _workbenchBounds.Y;
            }
            else
            {
                // 在小面板所在的当前活动显示器居中展开
                Left = currentWorkArea.Left + (currentWorkArea.Width - Width) / 2;
                Top = currentWorkArea.Top + (currentWorkArea.Height - Height) / 2;
            }
        }
        else
        {
            Width = WorkbenchWidth;
            Height = WorkbenchHeight;
            Left = currentWorkArea.Left + (currentWorkArea.Width - Width) / 2;
            Top = currentWorkArea.Top + (currentWorkArea.Height - Height) / 2;
        }

        WindowEffects.ClampToWorkArea(this);

        if (_workbenchIsMaximized)
        {
            _workbenchIsMaximized = false;
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// 恢复上次的位置与尺寸。显示器换了（拔掉副屏、投影）时旧坐标会落到够不着的地方，
    /// 所以只在"标题栏还回得来"的前提下采用。
    /// </summary>
    private void RestoreWindowBounds()
    {
        var saved = _settings.Current.WindowBounds;

        if (saved is null)
        {
            return;
        }

        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(saved.X, saved.Y,
            Math.Max(saved.Width, MinWidth), Math.Max(saved.Height, MinHeight));
        var visible = Rect.Intersect(rect, screen);

        // 至少留一条能抓住的边：宽够拖、高够点标题栏
        if (visible.IsEmpty || visible.Width < 80 || visible.Height < 32)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;

        if (saved.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>记住位置。最小化时 Left/Top 是 -32000，那种数不能存。</summary>
    private void SaveWindowBounds()
    {
        if (ViewModel.IsCompact || WindowState == WindowState.Minimized)
        {
            return;
        }

        var normal = RestoreBounds;

        _settings.Current.WindowBounds = new WindowBounds
        {
            X = normal.X,
            Y = normal.Y,
            Width = normal.Width,
            Height = normal.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        };
        _settings.Save(_settings.Current);
    }

    /// <summary>关闭到托盘：设置打开时不真的退出，只把窗口藏起来。</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!App.InSmoke)
        {
            SaveWindowBounds();
        }

        if (App.IsShuttingDown)
        {
            return;
        }

        // 自查模式要让窗口真的关掉：藏起来的话窗口还留在 Application.Windows 里，
        // 下一个窗口的 Show() 会变成"已经有一个同类型窗口"的怪状态
        if (App.InSmoke)
        {
            return;
        }

        if ((ViewModel.IsCompact || _settings.Current.CloseToTray) &&
            (ReferenceEquals(this, Application.Current.MainWindow) || ViewModel.IsBusy))
        {
            e.Cancel = true;
            Hide();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!e.Handled && e.Key == Key.Escape)
        {
            if (ViewModel.IsCompact)
            {
                Close();
            }
            else
            {
                // 完整工作台下按 Esc：直接缩回紧凑小面板
                ViewModel.CollapseCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync();
            _initialized = true;
            await UpdateLifetimeAsync();
        }
        catch (Exception ex)
        {
            App.ShowError(this, Strings.Format(Strings.LoadStashFailed, ex.Message));
        }
    }

    private void OnItemTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // TreeView.SelectedItem 是只读属性，不能直接双向绑定，用事件回填
        ViewModel.SelectedItem = e.NewValue as ItemViewModel;
    }

    private void OnItemTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DataPayloadHelper.IsSupported(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    /// <summary>记住按下位置：超过系统的拖动阈值才真的拖。</summary>
    private void OnItemTreePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = ItemUnder(ItemTree, e.OriginalSource as DependencyObject);
    }

    /// <summary>小暂存区里按下：记住从哪一行开始的。</summary>
    private void OnCompactPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = ItemUnder((ItemsControl)sender, e.OriginalSource as DependencyObject);
    }

    /// <summary>
    /// 找出鼠标底下那一行。
    /// </summary>
    /// <remarks>
    /// 不能直接用 SelectedItem：按下的时候选中项还没换过来，
    /// 第一次点某一行会拖走**上一行**。
    /// </remarks>
    private static ItemViewModel? ItemUnder(ItemsControl list, DependencyObject? source)
    {
        if (source is not null)
        {
            // TreeView.ContainerFromElement 只返回它直接拥有的顶层节点。
            // 从命中元素向上找，才能取到来源目录下面的暂存项。
            DependencyObject? container = source;
            while (container is not null && container != list && container is not TreeViewItem && container is not ListBoxItem)
            {
                container = container is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(container)
                    : LogicalTreeHelper.GetParent(container);
            }

            // 条目行也属于它上层的暂存项。
            while (container is TreeViewItem { DataContext: EntryViewModel })
            {
                container = ItemsControl.ItemsControlFromItemContainer(container);
            }

            if (container is FrameworkElement { DataContext: ItemViewModel item })
            {
                return item;
            }

            // 落在别的行类型上（"按来源"档的目录节头）：不能退回上一次选中的那一项，
            // 否则从节头起拖走的是"跟这行无关的某个文件"
            if (container is FrameworkElement { DataContext: not null })
            {
                return null;
            }
        }

        return list switch
        {
            ListBox box => box.SelectedItem as ItemViewModel,
            TreeView tree => tree.SelectedItem as ItemViewModel,
            _ => null,
        };
    }

    /// <summary>小暂存区里拖动：拖出去就是保存。</summary>
    private void OnCompactPreviewMouseMove(object sender, MouseEventArgs e)
    {
        StartDragIfMoved(e, (DependencyObject)sender);
    }

    /// <summary>
    /// 拖出（S4）：把暂存项物化成真实文件，再交给系统拖动。
    /// </summary>
    /// <remarks>
    /// 物化必须在 DoDragDrop **之前**做完 —— DoDragDrop 是模态循环，会阻塞 UI 线程，
    /// 期间没法做异步的拷贝工作。
    /// </remarks>
    private async void OnItemTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        await StartDragIfMovedAsync(e, ItemTree);
    }

    private void StartDragIfMoved(MouseEventArgs e, DependencyObject source)
    {
        _ = StartDragIfMovedAsync(e, source);
    }

    private async Task StartDragIfMovedAsync(MouseEventArgs e, DependencyObject source)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null)
        {
            return;
        }

        var delta = e.GetPosition(null) - _dragStart;

        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _dragCandidate;
        _dragCandidate = null;

        var container = ItemContainerUnder(source, item);
        if (container is not null)
        {
            container.Opacity = 0.45;
        }

        try
        {
            var paths = await ViewModel.MaterializeForDragAsync([item.Item]);

            if (paths.Count == 0)
            {
                if (container is not null) container.Opacity = 1.0;
                return;
            }

            var data = DataPayloadHelper.CreateFileDropData(paths);

            var result = RunExportDrag(source, data);

            // 拖出没有"写到目标"这一步（资源管理器接手的），但要记一条历史
            await ViewModel.RecordDragExportAsync(paths, result);

            // 拖动结果判定：不为 None 说明已成功放置到目标文件夹/窗口
            if (result != DragDropEffects.None)
            {
                // 如果开启了"拖出后自动从暂存移除"（默认开启，参考 DropPoint 行为）
                if (_settings.Current.RemoveAfterDragExport)
                {
                    if (container is not null)
                    {
                        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.45, 0, TimeSpan.FromMilliseconds(120));
                        fade.Completed += async (_, _) =>
                        {
                            container.Opacity = 1.0;
                            await ViewModel.RemoveItemDirectCommand.ExecuteAsync(item);
                            CheckAutoDismiss();
                        };
                        container.BeginAnimation(UIElement.OpacityProperty, fade);
                    }
                    else
                    {
                        await ViewModel.RemoveItemDirectCommand.ExecuteAsync(item);
                        CheckAutoDismiss();
                    }
                }
                else if (container is not null)
                {
                    var restore = new System.Windows.Media.Animation.DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(150));
                    container.BeginAnimation(UIElement.OpacityProperty, restore);
                }
            }
            else if (container is not null)
            {
                var restore = new System.Windows.Media.Animation.DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(150));
                container.BeginAnimation(UIElement.OpacityProperty, restore);
            }
        }
        catch (Exception ex)
        {
            if (container is not null) container.Opacity = 1.0;
            App.ShowError(this, Strings.Format(Strings.DragOutFailed, ex.Message));
        }
        finally { HideDropOverlay(); }
    }

    private async void OnItemTreeDrop(object sender, DragEventArgs e)
    {
        var paths = DataPayloadHelper.ExtractPayloadPaths(e.Data, ViewModel.ClipboardDirectory);
        if (paths.Count == 0)
        {
            return;
        }

        e.Handled = true;

        try
        {
            await ViewModel.AddPathsCommand.ExecuteAsync(paths);
        }
        catch (Exception ex)
        {
            App.ShowError(this, Strings.Format(Strings.AddToStashFailed, ex.Message));
        }
    }

    private int _windowDragCounter;
    private bool _isDraggingOut;
    private bool _preparingDragAll;
    private bool _dropOverlayActive;

    private DragDropEffects RunExportDrag(DependencyObject source, IDataObject data)
    {
        _isDraggingOut = true;
        ShelfExportSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, TimeSpan.FromMilliseconds(120)));
        try { return DragDrop.DoDragDrop(source, data, DragDropEffects.Copy); }
        finally
        {
            _isDraggingOut = false;
            _windowDragCounter = 0;
            ShelfExportSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        }
    }

    private void OnShelfStackHover(object sender, MouseEventArgs e)
    {
        if (ShelfStack.RenderTransform is TransformGroup transforms && transforms.Children[1] is TranslateTransform offset)
        {
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
                ShelfExportSurface.IsMouseOver ? -5 : 0, TimeSpan.FromMilliseconds(CanAnimate ? 180 : 0))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void OnShelfExportKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !ViewModel.IsBusy)
        {
            ViewModel.IsCompact = false;
            e.Handled = true;
        }
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (_isDraggingOut) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (DataPayloadHelper.IsSupported(e.Data))
        {
            _windowDragCounter++;
            e.Effects = DragDropEffects.Copy;
            ShowDropOverlay();
            e.Handled = true;
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (_isDraggingOut) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (DataPayloadHelper.IsSupported(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _windowDragCounter--;
        if (_windowDragCounter <= 0)
        {
            _windowDragCounter = 0;
            HideDropOverlay();
        }
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        _windowDragCounter = 0;
        HideDropOverlay();
        if (_isDraggingOut) { e.Handled = true; return; }

        var paths = DataPayloadHelper.ExtractPayloadPaths(e.Data, ViewModel.ClipboardDirectory);
        if (paths.Count == 0)
        {
            return;
        }

        e.Handled = true;

        try
        {
            await ViewModel.AddPathsCommand.ExecuteAsync(paths);
        }
        catch (Exception ex)
        {
            App.ShowError(this, Strings.Format(Strings.AddToStashFailed, ex.Message));
        }
    }

    private static bool CanAnimate => !App.InSmoke && SystemParameters.ClientAreaAnimation;

    private void ShowDropOverlay()
    {
        if (_dropOverlayActive) return;
        _dropOverlayActive = true;
        DropOverlay.Visibility = Visibility.Visible;
        DropOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(CanAnimate ? DropOverlay.Opacity : 1, 1, TimeSpan.FromMilliseconds(160)));
        if (CanAnimate && DropOverlay.RenderTransform is ScaleTransform scale)
        {
            var settle = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        }
    }

    private void HideDropOverlay()
    {
        _dropOverlayActive = false;
        if (DropOverlay.Visibility != Visibility.Visible) return;
        var anim = new DoubleAnimation(DropOverlay.Opacity, 0, TimeSpan.FromMilliseconds(CanAnimate ? 140 : 0));
        anim.Completed += (_, _) =>
        {
            if (!_dropOverlayActive) DropOverlay.Visibility = Visibility.Collapsed;
        };
        DropOverlay.BeginAnimation(OpacityProperty, anim);
    }


    private static UIElement? ItemContainerUnder(DependencyObject source, ItemViewModel item)
    {
        if (source is ItemsControl list)
        {
            return list.ItemContainerGenerator.ContainerFromItem(item) as UIElement;
        }

        return null;
    }

    private void PulseStatusBadge()
    {
        if (!CanAnimate || !ViewModel.IsCompact || ViewModel.HasNoItems) return;
        if (ShelfStack.RenderTransform is TransformGroup transforms && transforms.Children[0] is ScaleTransform scale)
        {
            var settle = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        }
    }


    private Point _dragAllStart;
    private bool _isDraggingAllCandidate;

    private void OnDragAllPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.Items.Count == 0 || ViewModel.IsBusy || _isDraggingOut || _preparingDragAll)
        {
            return;
        }

        _dragAllStart = e.GetPosition(null);
        _isDraggingAllCandidate = true;
    }

    private void OnDragAllPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_isDraggingAllCandidate)
        {
            _isDraggingAllCandidate = false;
            return;
        }

        var delta = e.GetPosition(null) - _dragAllStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isDraggingAllCandidate = false;
        _ = StartDragAllAsync(sender as DependencyObject ?? this);
    }

    /// <summary>一次性把暂存箱中的所有文件物化并拖出到外部目标（DropPoint 风格批量中转）。</summary>
    private async Task StartDragAllAsync(DependencyObject source)
    {
        if (ViewModel.Items.Count == 0 || ViewModel.IsBusy || _isDraggingOut || _preparingDragAll)
        {
            return;
        }

        _preparingDragAll = true;
        try
        {
            var exportedItems = ViewModel.Items.ToList();
            var paths = await ViewModel.MaterializeForDragAsync(exportedItems.Select(item => item.Item).ToList());
            if (paths.Count == 0)
            {
                return;
            }

            var data = DataPayloadHelper.CreateFileDropData(paths);

            var result = RunExportDrag(source, data);
            await ViewModel.RecordDragExportAsync(paths, result);

            if (result != DragDropEffects.None)
            {
                if (_settings.Current.RemoveAfterDragExport)
                {
                    await ViewModel.RemoveExportedItemsAsync(exportedItems.Select(item => item.Id).ToArray());
                    CheckAutoDismiss();
                }
            }
        }
        catch (Exception ex)
        {
            App.ShowError(this, Strings.Format(Strings.DragOutFailed, ex.Message));
        }
        finally { _preparingDragAll = false; HideDropOverlay(); }
    }

    private void OnCompactShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        CompactShell.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
    }

    private void OnRestoreSucceeded() => DismissCompactShelf();

    /// <summary>Only empty-shelf dismissal depends on the retention preference.</summary>
    private void CheckAutoDismiss()
    {
        if (_settings.Current.AutoDismissWhenEmpty && ViewModel.Items.Count == 0)
            DismissCompactShelf();
    }

    private void DismissCompactShelf()
    {
        if (!ViewModel.IsCompact || !IsVisible) return;
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(CanAnimate ? 160 : 0));
        anim.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!ViewModel.IsCompact) return;
            if (ReferenceEquals(this, Application.Current.MainWindow)) Hide();
            else Close();
        };
        BeginAnimation(OpacityProperty, anim);
    }


    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maximized = !ViewModel.IsCompact && WindowState == WindowState.Maximized;
        WorkbenchRoot.Margin = maximized ? new Thickness(7) : new Thickness(0);
        if (MaximizeGlyph is not null)
        {
            MaximizeGlyph.Data = Geometry.Parse(maximized
                ? "M2 0H10V8H8V2H2ZM0 2H8V10H0Z"
                : "M0 0H10V10H0Z");
        }
        if (MaximizeButton is not null)
        {
            var tip = maximized ? Strings.WindowRestore : Strings.WindowMaximize;
            MaximizeButton.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, tip);
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnToggleTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (sender is Button btn)
        {
            btn.Opacity = Topmost ? 1.0 : 0.45;
        }
    }
}
