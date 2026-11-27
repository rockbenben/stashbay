using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.App.ViewModels;

namespace FileStash.App.Views;

/// <summary>悬浮窗：小的置顶拖放区，可拖入暂存，双击唤出主面板，按住可任意拖动位置。</summary>
public partial class FloatingWindow : Window
{
    private readonly MainViewModel _viewModel;
    private int _dragCounter;

    public FloatingWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;

        // 初始位置放到"自己将要出现的那块屏"的右下角。
        // 不能用 SystemParameters.WorkArea —— 那只有主屏的工作区，
        // 副屏上的悬浮球会被摆到主屏去（多屏下直接跑出视野）。
        // 构造函数里窗口还没有 hwnd，拿不到它属于哪块屏；SourceInitialized 之后才行，
        // 所以在那里重新定位一次。这里先给一个不会把窗口扔到副屏外的兜底值。
        SourceInitialized += (_, _) =>
        {
            var area = WindowEffects.GetWindowWorkArea(this);
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 24;
        };

        void OnItemsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshCount();

        Loaded += (_, _) =>
        {
            RefreshCount();
            _viewModel.Items.CollectionChanged += OnItemsChanged;
        };

        Unloaded += (_, _) =>
        {
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
        };
    }

    /// <summary>刷新上面的暂存项数量。</summary>
    public void RefreshCount()
    {
        var count = _viewModel.Items.Sum(item => 1 + item.Children.Count);
        CountText.Text = count.ToString();

        if (CountText.RenderTransform is ScaleTransform st)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 1.35, TimeSpan.FromMilliseconds(130))
            {
                AutoReverse = true
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private Point _downScreenPos;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _downScreenPos = PointToScreen(e.GetPosition(this));

        if (e.ClickCount == 2)
        {
            _viewModel.ShowCompactPanel();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // DragMove can throw if released early
            }
            finally
            {
                SnapToScreenEdge();
            }
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        var upScreenPos = PointToScreen(e.GetPosition(this));
        var delta = upScreenPos - _downScreenPos;

        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            _viewModel.ShowCompactPanel();
        }
    }

    /// <summary>靠近屏幕边缘时自动磁吸贴边停靠。</summary>
    /// <remarks>
    /// 量的是**窗口当前所在那块屏**的工作区（<see cref="WindowEffects.GetWindowWorkArea"/>），
    /// 不是主屏的 —— 用户把悬浮球拖到副屏再松手，拿主屏的工作区比会把右边界判成负数，
    /// 结果"磁吸"把它丢回主屏去。
    /// </remarks>
    private void SnapToScreenEdge()
    {
        var workArea = WindowEffects.GetWindowWorkArea(this);
        const double threshold = 32;

        if (Left - workArea.Left < threshold) Left = workArea.Left + 8;
        else if (workArea.Right - (Left + Width) < threshold) Left = workArea.Right - Width - 8;

        if (Top - workArea.Top < threshold) Top = workArea.Top + 8;
        else if (workArea.Bottom - (Top + Height) < threshold) Top = workArea.Bottom - Height - 8;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (DataPayloadHelper.IsSupported(e.Data))
        {
            _dragCounter++;
            Panel.Background = (Brush)FindResource("Brush.AccentSoft");
            Panel.BorderThickness = new Thickness(2.5);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
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

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragCounter--;
        if (_dragCounter <= 0)
        {
            _dragCounter = 0;
            Panel.Background = (Brush)FindResource("Brush.SurfaceElevated");
            Panel.BorderThickness = new Thickness(2);
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dragCounter = 0;
        Panel.Background = (Brush)FindResource("Brush.SurfaceElevated");
        Panel.BorderThickness = new Thickness(2);

        var paths = DataPayloadHelper.ExtractPayloadPaths(e.Data, _viewModel.ClipboardDirectory);
        if (paths.Count == 0)
        {
            return;
        }

        e.Handled = true;

        try
        {
            await _viewModel.AddPathsCommand.ExecuteAsync(paths);
        }
        catch (Exception ex)
        {
            App.ShowError(this, Strings.Format(Strings.AddToStashFailed, ex.Message));
        }

        RefreshCount();
    }
}
