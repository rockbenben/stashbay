using FileStash.App.ViewModels;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Shell;

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
    private bool _shellIntegrationSucceeds = true;
    private readonly StartupStub _startup = new();

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
            _dialogs,
            _ => _shellIntegrationSucceeds,
            _startup);
    }



    [Fact]
    public async Task UnknownStartupStatusDoesNotBlockSavingOrdinaryPreferences()
    {
        _startup.State = LogonStartupState.Unknown;
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.True(_viewModel.SaveCommand.CanExecute(null));
        _viewModel.Settings.Language = AppLanguage.English;
        _viewModel.Settings.BackupBeforeOverwrite = false;

        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(_viewModel.LastSaveSucceeded);
        Assert.Equal(0, _startup.Calls);
        _settings.Load();
        Assert.Equal(AppLanguage.English, _settings.Current.Language);
        Assert.False(_settings.Current.BackupBeforeOverwrite);
    }

    [Theory]
    [InlineData(LogonStartupState.PlainNeedsRepair, true, false)]
    [InlineData(LogonStartupState.Elevated, true, true)]
    [InlineData(LogonStartupState.Disabled, false, false)]
    public async Task RefreshShowsActualStartupAndSavingOtherPreferencesPreservesTheSavedChoice(
        LogonStartupState actual, bool enabled, bool administrator)
    {
        _settings.Save(new AppSettings
        {
            StartWithWindows = !enabled,
            StartWithWindowsAsAdministrator = !enabled,
            AdministratorStartupDelaySeconds = 17,
            BackupBeforeOverwrite = true,
        });
        _viewModel.CancelCommand.Execute(null);
        _startup.State = actual;
        _viewModel.Settings.BackupBeforeOverwrite = false;
        var notifications = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.Equal(enabled, _viewModel.StartWithWindows);
        Assert.Equal(administrator, _viewModel.StartWithWindowsAsAdministrator);
        Assert.DoesNotContain(nameof(SettingsViewModel.Settings), notifications);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(_viewModel.LastSaveSucceeded);
        Assert.Equal(0, _startup.Calls);
        _settings.Load();
        Assert.False(_settings.Current.BackupBeforeOverwrite);
        Assert.Equal(!enabled, _settings.Current.StartWithWindows);
        Assert.Equal(!enabled, _settings.Current.StartWithWindowsAsAdministrator);
        Assert.Equal(17, _settings.Current.AdministratorStartupDelaySeconds);
    }

    [Theory]
    [InlineData(LogonStartupState.PlainNeedsRepair)]
    [InlineData(LogonStartupState.Elevated)]
    public async Task RefreshKeepsAnExplicitStartupEditAndSaveAppliesIt(LogonStartupState actual)
    {
        _startup.State = actual;
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.True(_viewModel.Settings.StartWithWindows);
        _viewModel.Settings.StartWithWindows = false;

        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.False(_viewModel.Settings.StartWithWindows);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(_viewModel.LastSaveSucceeded);
        Assert.Equal(1, _startup.Calls);
        Assert.Equal(LogonStartupState.Disabled, _startup.State);
        Assert.False(_settings.Current.StartWithWindows);
        Assert.False(_settings.Current.StartWithWindowsAsAdministrator);
    }

    [Fact]
    public async Task UnknownReadbackAfterStartupEditDoesNotPersistOrEnableReplay()
    {
        _startup.State = LogonStartupState.Unknown;
        _startup.Outcome = StartupOutcome.Failed;
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        _viewModel.Settings.StartWithWindows = true;

        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, _startup.Calls);
        Assert.False(_viewModel.LastSaveSucceeded);
        Assert.False(_settings.Current.StartWithWindows);
        Assert.False(_viewModel.SaveCommand.CanExecute(null));
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.False(_viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task PendingStartupHelperStillBlocksSaving()
    {
        _startup.IsPending = true;
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.False(_viewModel.SaveCommand.CanExecute(null));
        Assert.False(_viewModel.RepairStartupCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedStartupEditCanBeSavedAfterRefreshConfirmsUnregisteredState()
    {
        _startup.State = LogonStartupState.Unknown;
        _startup.Outcome = StartupOutcome.Failed;
        _viewModel.Settings.StartWithWindows = true;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(_viewModel.SaveCommand.CanExecute(null));

        _startup.State = LogonStartupState.Disabled;
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.True(_viewModel.SaveCommand.CanExecute(null));
        Assert.True(_viewModel.RepairStartupCommand.CanExecute(null));

        _startup.Outcome = StartupOutcome.Applied;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(_viewModel.LastSaveSucceeded);
        _settings.Load();
        Assert.True(_settings.Current.StartWithWindows);
        Assert.Equal(2, _startup.Calls);
    }

    [Fact]
    public async Task CancelledDelayChangeRestoresSavedValueAndReadsBackAgainstIt()
    {
        _settings.Save(new AppSettings { StartWithWindows = true, StartWithWindowsAsAdministrator = true });
        _viewModel.Settings.StartWithWindows = true;
        _viewModel.Settings.StartWithWindowsAsAdministrator = true;
        _startup.State = LogonStartupState.Elevated;
        _startup.Outcome = StartupOutcome.Cancelled;
        _viewModel.StartupDelayText = "17";
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(3, _startup.LastReadDelay);
        Assert.Equal("3", _viewModel.StartupDelayText);
        Assert.Equal(3, _settings.Current.AdministratorStartupDelaySeconds);
        Assert.False(_viewModel.LastSaveSucceeded);
    }

    [Fact]
    public async Task InvalidSavedDelayCanBeCorrectedWithoutAnUnknownStateDeadlock()
    {
        _viewModel.Settings.AdministratorStartupDelaySeconds = 1000;
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.Equal(3, _startup.LastReadDelay);
        Assert.True(_viewModel.SaveCommand.CanExecute(null));
        _viewModel.StartupDelayText = "17";
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.Equal(17, _startup.LastReadDelay);
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(_viewModel.LastSaveSucceeded);
        Assert.Equal(17, _settings.Current.AdministratorStartupDelaySeconds);
    }

    [Fact]
    public async Task DelayIsValidatedBeforeStartupChangesAndPersistedAfterSuccess()
    {
        foreach (string invalid in new[] { "-1", "301", "1.5", "", "abc" })
        {
            _viewModel.StartupDelayText = invalid;
            await _viewModel.SaveCommand.ExecuteAsync(null);
            Assert.Equal(0, _startup.Calls);
            Assert.False(_viewModel.LastSaveSucceeded);
            Assert.Equal(3, _settings.Current.AdministratorStartupDelaySeconds);
        }
        _viewModel.Settings.StartWithWindows = true;
        _viewModel.Settings.StartWithWindowsAsAdministrator = true;
        _viewModel.StartupDelayText = "17";
        await _viewModel.RepairStartupCommand.ExecuteAsync(null);
        _settings.Load();
        Assert.Equal(17, _startup.LastDelay);
        Assert.Equal(17, _settings.Current.AdministratorStartupDelaySeconds);
        _viewModel.StartupDelayText = "30";
        _viewModel.CancelCommand.Execute(null);
        Assert.Equal("17", _viewModel.StartupDelayText);
    }

    [Fact]
    public async Task AdministratorChoicePersistsOnlyAfterConfirmedRegistration()
    {
        _viewModel.Settings.StartWithWindows = true;
        _viewModel.Settings.StartWithWindowsAsAdministrator = true;
        _startup.Outcome = StartupOutcome.Cancelled;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(_settings.Current.StartWithWindowsAsAdministrator);
        Assert.False(_viewModel.LastSaveSucceeded);
        Assert.False(_viewModel.Settings.StartWithWindows);
        Assert.Empty(_dialogs.Warnings);

        _viewModel.Settings.StartWithWindows = true;
        _viewModel.Settings.StartWithWindowsAsAdministrator = true;
        _startup.Outcome = StartupOutcome.Applied;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        _settings.Load();
        Assert.True(_settings.Current.StartWithWindowsAsAdministrator);
        Assert.True(_viewModel.LastSaveSucceeded);
    }

    [Fact]
    public async Task FailedRemovalKeepsAdministratorChoiceAndDoesNotReportSuccess()
    {
        _settings.Save(new AppSettings { StartWithWindows = true, StartWithWindowsAsAdministrator = true });
        _viewModel.CancelCommand.Execute(null);
        _startup.State = LogonStartupState.Elevated;
        _startup.Outcome = StartupOutcome.Failed;
        _viewModel.Settings.StartWithWindows = false;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(_settings.Current.StartWithWindowsAsAdministrator);
        Assert.True(_viewModel.Settings.StartWithWindows);
        Assert.True(_viewModel.Settings.StartWithWindowsAsAdministrator);
        Assert.Single(_dialogs.Warnings);
        Assert.Empty(_dialogs.Infos);
        Assert.False(_viewModel.LastSaveSucceeded);
    }

    [Fact]
    public async Task UncertainResultBlocksReplayUntilExplicitSuccessfulRefresh()
    {
        _startup.Outcome = StartupOutcome.Uncertain;
        _startup.IsPending = true;
        _viewModel.Settings.StartWithWindows = true;
        _viewModel.Settings.StartWithWindowsAsAdministrator = true;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.False(_viewModel.SaveCommand.CanExecute(null));
        Assert.False(_viewModel.RepairStartupCommand.CanExecute(null));
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, _startup.Calls);
        Assert.False(_settings.Current.StartWithWindowsAsAdministrator);
        _startup.IsPending = false;
        _startup.State = LogonStartupState.Elevated;
        await _viewModel.RefreshStartupCommand.ExecuteAsync(null);
        Assert.True(_viewModel.SaveCommand.CanExecute(null));
        _startup.Outcome = StartupOutcome.Unchanged;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(_settings.Current.StartWithWindowsAsAdministrator);
    }

    [Fact]
    public async Task RepairUsesTheSelectedPrivilegeAndPreservesConcurrentWindowState()
    {
        _viewModel.Settings.StartWithWindows = true;
        _viewModel.Settings.StartWithWindowsAsAdministrator = true;
        _settings.Current.LastGroupId = "changed-during-edit";
        await _viewModel.RepairStartupCommand.ExecuteAsync(null);
        Assert.True(_startup.LastRepair);
        Assert.Equal(LogonStartupState.Elevated, _startup.State);
        Assert.Equal("changed-during-edit", _settings.Current.LastGroupId);
    }

    [Fact]
    public async Task FailedStartupSyncWarnsAndAllowsRetryBeforeLanguageRestart()
    {
        _shellIntegrationSucceeds = false;
        var restarts = 0;
        _viewModel.LanguageChangeRequested += () => restarts++;
        _viewModel.Settings.Language = AppLanguage.English;
        _viewModel.Settings.StartWithWindows = true;

        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Warnings);
        Assert.False(_viewModel.LastSaveSucceeded);
        Assert.Empty(_dialogs.Infos);
        Assert.Equal(0, restarts);
        Assert.True(_settings.Current.StartWithWindows);
        _shellIntegrationSucceeds = true;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, restarts);
        Assert.True(_viewModel.LastSaveSucceeded);
    }

    [Fact]
    public void SettingsCopyMustDetachArraysAndWindowBounds()
    {
        var original = new AppSettings
        {
            ExcludeGlobs = ["original"],
            WindowBounds = new WindowBounds { X = 10 }
        };
        var copy = original.Clone();
        copy.ExcludeGlobs[0] = "edited";
        copy.WindowBounds!.X = 20;
        Assert.Equal("original", original.ExcludeGlobs[0]);
        Assert.Equal(10, original.WindowBounds.X);
    }

    [Fact]
    public void EditingMustLeaveLiveSettingsAndNestedValuesUnchanged()
    {
        _viewModel.Settings.BackupBeforeOverwrite = false;
        _viewModel.Settings.ExcludeGlobs = ["changed"];
        Assert.True(_settings.Current.BackupBeforeOverwrite);
        Assert.Empty(_settings.Current.ExcludeGlobs);
    }

    [Fact]
    public void CancelMustPreserveConcurrentRuntimeChanges()
    {
        _viewModel.Settings.BackupBeforeOverwrite = false;
        _settings.Current.LastGroupId = "other-group";
        _viewModel.CancelCommand.Execute(null);
        Assert.True(_settings.Current.BackupBeforeOverwrite);
        Assert.Equal("other-group", _settings.Current.LastGroupId);
    }

    [Fact]
    public async Task SaveMustPreserveRuntimeStateAndDetachCommittedSettings()
    {
        _settings.Current.LastGroupId = "other-group";
        _settings.Current.CompactIconView = false;
        _viewModel.Settings.BackupBeforeOverwrite = false;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal("other-group", _settings.Current.LastGroupId);
        Assert.False(_settings.Current.CompactIconView);
        Assert.False(_settings.Current.BackupBeforeOverwrite);
        _viewModel.Settings.BackupBeforeOverwrite = true;
        Assert.False(_settings.Current.BackupBeforeOverwrite);
    }

    [Fact]
    public async Task LanguageSaveMustRequestRestartOnce()
    {
        var count = 0;
        _viewModel.LanguageChangeRequested += () => count++;
        _viewModel.Settings.Language = AppLanguage.English;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Save_语言没动_不重启()
    {
        var restarted = false;
        _viewModel.LanguageChangeRequested += () => restarted = true;

        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(restarted);
        Assert.Single(_dialogs.Infos);
    }

    [Fact]
    public async Task Save_语言变了_必须重启()
    {
        var restarted = false;
        _viewModel.LanguageChangeRequested += () => restarted = true;

        _viewModel.Settings.Language = AppLanguage.English;
        await _viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(restarted);

        // 马上要重启了，弹"已保存"没有意义
        Assert.Empty(_dialogs.Infos);
    }

    [Fact]
    public async Task Save_语言必须落到设置文件里()
    {
        // 不落盘的话，重启后的新实例读到的还是旧语言 —— 界面看着换了，
        // 下次启动又变回去，比不换还让人困惑。
        _viewModel.Settings.Language = AppLanguage.English;
        await _viewModel.SaveCommand.ExecuteAsync(null);

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

    private sealed class StartupStub : ILogonStartupService
    {
        public StartupOutcome Outcome = StartupOutcome.Applied;
        public LogonStartupState State = LogonStartupState.Disabled;
        public bool IsPending { get; set; }
        public bool LastRepair;
        public int Calls;
        public int LastDelay;
        public int? LastReadDelay;
        public Task<LogonStartupStatus> ReadAsync(int? delaySeconds = null)
        {
            LastReadDelay = delaySeconds;
            return Task.FromResult(new LogonStartupStatus(IsPending ? LogonStartupState.Unknown : State));
        }
        public Task<StartupResult> ChangeAsync(bool enabled, bool elevated, bool repair = false, int delaySeconds = 3)
        {
            Calls++;
            LastDelay = delaySeconds;
            LastRepair = repair;
            if (Outcome is StartupOutcome.Applied or StartupOutcome.Unchanged)
                State = !enabled ? LogonStartupState.Disabled : elevated ? LogonStartupState.Elevated : LogonStartupState.Plain;
            return Task.FromResult(new StartupResult(Outcome));
        }
    }
}
