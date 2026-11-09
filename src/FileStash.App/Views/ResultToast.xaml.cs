using System.Windows;
using System.Windows.Threading;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.Core.Models;

namespace FileStash.App.Views;

/// <summary>结果的非模态提示。</summary>
/// <remarks>
/// 两种用法：
/// ① 写入结果（构造函数带 Operation + 条目）—— 有详情、可撤销、可提权重试；
/// ② 普通提示（标题 + 一句话）—— 加入暂存之类的回执。
///
/// 为什么不用 MessageBox：模态框会把用户的输入打断，还会占住主线程的模态循环
/// （后台会话里更是直接把进程吊住）。回执这类信息应该"飘一下就走"。
/// </remarks>
public partial class ResultToast : Window
{
    private readonly Operation? _operation;
    private readonly IReadOnlyList<OperationEntry>? _entries;

    public ResultToast(Operation operation, IReadOnlyList<OperationEntry> entries)
    {
        InitializeComponent();

        // 内容多起来会顶到屏幕外（撤销 / 详情 / 重试 / 关闭 四颗键不能被挤掉）
        WindowEffects.FitToWorkArea(this);

        _operation = operation;
        _entries = entries;

        TitleText.Text = operation.Status switch
        {
            OperationStatus.Succeeded => Strings.ToastSucceeded,
            OperationStatus.PartiallyFailed => Strings.ToastPartiallyFailed,
            OperationStatus.Cancelled => Strings.ToastCancelled,
            _ => Strings.ToastTitle,
        };

        SummaryText.Text = Describe(operation, entries);

        var failed = entries.Count(entry => entry.Action == EntryResult.Failed);
        DetailsButton.Visibility = failed > 0 || entries.Any(entry => entry.Error is not null)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 有失败条目时提供"以管理员身份重试"
        RetryButton.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 失败时主键是"重试"而不是"撤销"：撤销不解决任何失败，
        // 而它此前一直是唯一的强调色按钮，用户的视线被领到不该点的那一颗上。
        // 重试那颗的强调样式写在 XAML 里（它只在失败时出现），这里只管把撤销降级。
        if (failed > 0)
        {
            UndoButton.ClearValue(StyleProperty);
        }

        // 需要人决定的回执不自己消失；纯告知的照旧 10 秒
        StartLife(failed > 0 ? 0 : 10);
    }

    /// <summary>普通提示：一句标题 + 一行摘要，没有可撤销的东西。</summary>
    public ResultToast(string title, string summary)
    {
        InitializeComponent();
        WindowEffects.FitToWorkArea(this);

        Title = title;
        TitleText.Text = title;
        TitleText.Margin = new Thickness(0, 0, 28, 0);
        SummaryText.Text = summary;
        MinHeight = 0;
        ActionPanel.Visibility = Visibility.Collapsed;
        NoticeCloseButton.Visibility = Visibility.Visible;

        DetailsButton.Visibility = Visibility.Collapsed;
        UndoButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;

        StartLife(6);
    }

    /// <summary>
    /// 放到右下角，过一会儿自己消失。<paramref name="seconds"/> 给 0 表示不自隐。
    /// </summary>
    private void StartLife(int seconds)
    {
        PlaceAtBottomRight();

        // 高度由内容定（SizeToContent），所以尺寸一变就要重新贴边
        SizeChanged += (_, _) => PlaceAtBottomRight();

        if (seconds <= 0)
        {
            return;
        }

        // 自查模式（probe / shots / smoke）要把这块板逐屏量和逐屏拍：
        // 自隐的定时器会在量到一半时把窗口收走 —— 德语那一轮实测量到 0 个可见元素，
        // 被"没量到东西"的自证判成不通过。产品行为不变，只是不自隐。
        if (App.InSmoke)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };

        timer.Start();
    }

    /// <summary>撤销请求（由调用方处理）。</summary>
    public event Action? UndoRequested;

    private static string Describe(Operation operation, IReadOnlyList<OperationEntry> entries)
    {
        var created = entries.Count(entry => entry.Action == EntryResult.Created);
        var overwritten = entries.Count(entry => entry.Action is EntryResult.Overwritten or EntryResult.Renamed);
        var skipped = entries.Count(entry => entry.Action == EntryResult.Skipped);
        var failed = entries.Count(entry => entry.Action == EntryResult.Failed);

        var parts = new List<string>();

        if (created > 0)
        {
            parts.Add(Strings.Format(Strings.ToastCreated, created));
        }

        if (overwritten > 0)
        {
            parts.Add(Strings.Format(Strings.ToastOverwritten, overwritten));
        }

        if (skipped > 0)
        {
            parts.Add(Strings.Format(Strings.ToastSkipped, skipped));
        }

        if (failed > 0)
        {
            parts.Add(Strings.Format(Strings.ToastFailed, failed));
        }

        var counts = parts.Count == 0
            ? Strings.ToastNothingWritten
            : string.Join(Strings.ItemSeparator, parts) + Strings.SentenceEnd;

        // 说清写到哪：回执常常是好几条操作里最新的那一条，只报数量认不出是哪次
        return string.IsNullOrEmpty(operation.TargetDescription)
            ? counts
            : counts + Environment.NewLine + Strings.Format(Strings.ToastTargetLine, operation.TargetDescription);
    }

    private void PlaceAtBottomRight()
    {
        // 按"窗口自己那块屏"的工作区贴右下角，不是主屏的 ——
        // 主窗口在副屏时，用主屏的 WorkArea 会把 Toast 贴到主屏去（用户看不见它）。
        var workArea = WindowEffects.GetWindowWorkArea(this);

        // 高度改成 SizeToContent 之后，Width/Height 这两个属性在量出来之前是 NaN，
        // 必须用 Actual*，否则框会被贴到屏幕外
        var width = double.IsNaN(ActualWidth) || ActualWidth <= 0 ? Width : ActualWidth;
        var height = double.IsNaN(ActualHeight) || ActualHeight <= 0 ? 132 : ActualHeight;

        Left = workArea.Right - width - 16;
        Top = workArea.Bottom - height - 16;
    }

    private void OnDetails(object sender, RoutedEventArgs e)
    {
        var details = (_entries ?? [])
            .Where(entry => entry.Error is not null || entry.Action == EntryResult.Failed)
            .Select(entry => Strings.Format(
                Strings.ToastDetailLine,
                entry.TargetPath,
                entry.Error ?? Strings.ResultFailed))
            .ToList();

        ThemedMessageBox.Show(
            details.Count == 0 ? Strings.ToastNoFailures : string.Join(Environment.NewLine, details),
            Strings.ToastDetailsTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        UndoRequested?.Invoke();
        Close();
    }

    /// <summary>以管理员身份重试失败的条目（拉起一个提权进程）。</summary>
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_operation is null || !ElevatedRetryLauncher.Launch(_operation.Id))
        {
            new WpfDialogService().ShowInfo(Strings.AppName, Strings.ToastElevatedFailed);
        }

        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
