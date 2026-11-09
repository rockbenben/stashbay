using FileStash.App.ViewModels;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;

namespace FileStash.Core.Tests;

/// <summary>
/// 设置窗口：换语言要触发重启。
/// </summary>
/// <remarks>
/// 界面文字是 <c>x:Static</c>，XAML 解析的那一刻就定死了 ——
/// 运行中换不掉，只能重开进程。所以"保存设置"这件事在语言变了的时候
/// 不能只弹个"已保存"就完事：那个提示是在骗用户，
/// 而界面还是旧语言，下一次启动才换得过来。
/// </remarks>
public sealed class SettingsViewModelTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();
    private readonly RecordingDialogService _dialogs = new();
    private readonly SettingsService _settings;
    private readonly SettingsViewModel _viewModel;

    public async ValueTask InitializeAsync() =>
        await new DatabaseMigrator(_workspace.ConnectionFactory).MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }

    public SettingsViewModelTests()
    {
        var paths = _workspace.Paths;
        var factory = _workspace.ConnectionFactory;

        _settings = new SettingsService(paths);
        _settings.Load();

        var blobStore = new BlobStore(paths, TimeSpan.Zero);
        var operations = new OperationRepository(factory);

        _viewModel = new SettingsViewModel(
            _settings,
            new CleanupService(
                new StashEntryRepository(factory),
                new OperationEntryRepository(factory),
                operations,
                blobStore,
                paths),
            blobStore,
            operations,
            _dialogs);
    }

    [Fact]
    public void Save_语言没动_不重启()
    {
        var restarted = false;
        _viewModel.LanguageChangeRequested += () => restarted = true;

        _viewModel.SaveCommand.Execute(null);

        Assert.False(restarted);
        Assert.Single(_dialogs.Infos);
    }

    [Fact]
    public void Save_语言变了_必须重启()
    {
        var restarted = false;
        _viewModel.LanguageChangeRequested += () => restarted = true;

        _viewModel.Settings.Language = AppLanguage.English;
        _viewModel.SaveCommand.Execute(null);

        Assert.True(restarted);

        // 马上要重启了，弹"已保存"没有意义
        Assert.Empty(_dialogs.Infos);
    }

    [Fact]
    public void Save_语言必须落到设置文件里()
    {
        // 不落盘的话，重启后的新实例读到的还是旧语言 —— 界面看着换了，
        // 下次启动又变回去，比不换还让人困惑。
        _viewModel.Settings.Language = AppLanguage.English;
        _viewModel.SaveCommand.Execute(null);

        _settings.Load();

        Assert.Equal(AppLanguage.English, _settings.Current.Language);
    }

    [Fact]
    public async Task CleanupUnreferenced_ShouldDeleteExportsAndRefreshStats()
    {
        var exportSubdir = Path.Combine(_workspace.Paths.ExportDirectory, "export_test");
        Directory.CreateDirectory(exportSubdir);
        await File.WriteAllTextAsync(Path.Combine(exportSubdir, "data.bin"), "some dummy data", TestContext.Current.CancellationToken);

        await _viewModel.RefreshStorageAsync();
        Assert.DoesNotContain("0 B", _viewModel.ExportText);

        await _viewModel.CleanupUnreferencedCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(exportSubdir));
        Assert.Contains("0 B", _viewModel.ExportText);
    }

    [Fact]
    public void Cancel_ShouldDiscardUnsavedChanges()
    {
        var originalGitAdd = _viewModel.Settings.AutoGitAddAfterApply;
        _viewModel.Settings.AutoGitAddAfterApply = !originalGitAdd;

        _viewModel.CancelCommand.Execute(null);

        Assert.Equal(originalGitAdd, _settings.Current.AutoGitAddAfterApply);
    }
}
