using FileStash.App.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;

namespace FileStash.App.Views;

/// <summary>写入预览对话框。</summary>
public partial class PreviewDialog : Window
{
    private readonly ApplyPlan _plan;
    private readonly IApplyExecutor _executor;
    private readonly IBlobStore _blobStore;
    private readonly ISettingsService _settings;

    /// <summary>正在跑的写入，用来中止。</summary>
    private CancellationTokenSource? _execution;

    public PreviewDialog(
        ApplyPlan plan,
        IApplyExecutor executor,
        IBlobStore blobStore,
        ISettingsService settings)
    {
        InitializeComponent();

        _plan = plan;
        _executor = executor;
        _blobStore = blobStore;
        _settings = settings;

        WindowEffects.FitToWorkArea(this);

        TargetText.Text = DescribeTarget(plan.Target);
        EntryGrid.ItemsSource = plan.Entries;
        UpdateSummary();

        if (plan.GitContext is { } git)
        {
            GitText.Text = git.Describe();
            GitText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>执行结果（供调用方显示提示）。</summary>
    public Operation? Result { get; private set; }

    private static string DescribeTarget(TargetSpec target)
    {
        var path = Core.Services.PathMapper.ExpandTargetPath(target.TargetPath);

        return target.Mode switch
        {
            TargetMode.OriginalLocation => Strings.PreviewTargetOriginal,
            TargetMode.AnotherRoot => Strings.Format(Strings.PreviewTargetAnotherRoot, path),
            TargetMode.SpecifiedPath => Strings.Format(Strings.PreviewTargetSpecifiedPath, path),
            TargetMode.DragExport => Strings.PreviewTargetDragExport,
            _ => target.Mode.ToString(),
        };
    }

    private void UpdateSummary()
    {
        var entries = _plan.Entries;

        SummaryText.Text = Strings.Format(
            Strings.PreviewSummary,
            entries.Count(entry => entry.State == PlannedState.New),
            entries.Count(entry => entry.State == PlannedState.Overwrite),
            entries.Count(entry => entry.State == PlannedState.Identical),
            entries.Count(entry => entry.State == PlannedState.TypeConflict),
            entries.Count(entry => entry.State == PlannedState.Invalid));
    }

    private async void OnExecute(object sender, RoutedEventArgs e)
    {
        ExecuteButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;

        // 几 GB 的文件要写好一会儿，得给用户一个中止的办法。
        // 写入期间"取消"变成"中止"：直接关窗的话写入还在后台跑着，
        // 用户以为停了其实没停 —— Esc 也要一并关掉，免得按 Esc 把窗关了而写入还在跑。
        _execution = new CancellationTokenSource();
        CancelButton.IsCancel = false;
        CancelButton.Content = Strings.AbortButton;

        try
        {
            var progress = new Progress<ProgressInfo>(info =>
                ProgressText.Text = $"{info.Done}/{info.Total}{Strings.ProgressSeparator}{info.CurrentPath}");

            Result = await _executor.ExecuteAsync(_plan, progress, _execution.Token);

            ProgressText.Text = Strings.Format(Strings.PreviewWritten, Result.Status);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = Strings.PreviewAborted;
            ExecuteButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ProgressText.Text = Strings.Format(Strings.PreviewFailed, ex.Message);
            ExecuteButton.IsEnabled = true;
        }
        finally
        {
            _execution?.Dispose();
            _execution = null;
            CancelButton.Content = Strings.CancelButton;
            CancelButton.IsCancel = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // 正在写 → 中止；没在写 → 关窗
        if (_execution is not null)
        {
            _execution.Cancel();
            ProgressText.Text = Strings.StatusAborting;
            return;
        }

        DialogResult = false;
    }

    /// <summary>
    /// 对比差异：把暂存版本物化成临时文件，用外部工具打开。
    /// </summary>
    private async void OnCompareDiff(object sender, RoutedEventArgs e)
    {
        if (EntryGrid.SelectedItem is not PlannedEntry entry || entry.Source.Sha256 is null)
        {
            return;
        }

        try
        {
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "filestash-diff");
            Directory.CreateDirectory(temporaryDirectory);

            var right = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.tmp");
            await _blobStore.CopyBlobToAsync(entry.Source.Sha256, right, CancellationToken.None);

            var template = _settings.Current.DiffToolCommand;
            var (fileName, arguments) = SplitCommand(template);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments
                    .Replace("{left}", entry.TargetPath)
                    .Replace("{right}", right),
                UseShellExecute = true,
            };

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(
                this,
                Strings.Format(Strings.CompareFailed, ex.Message),
                Strings.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>把 "code --diff \"{left}\" \"{right}\"" 拆成程序名与参数。</summary>
    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        var trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);

            if (end > 0)
            {
                return (trimmed[1..end], trimmed[(end + 1)..].Trim());
            }
        }

        var space = trimmed.IndexOf(' ');

        return space < 0
            ? (trimmed, string.Empty)
            : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }
}
