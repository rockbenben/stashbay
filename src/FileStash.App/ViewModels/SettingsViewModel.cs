using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Shell;

namespace FileStash.App.ViewModels;

/// <summary>设置窗口的 ViewModel。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICleanupService _cleanup;
    private readonly IBlobStore _blobStore;
    private readonly IOperationRepository _operations;
    private readonly IDialogService _dialogs;
    private readonly Func<AppSettings, bool> _applyShellIntegration;
    private readonly ILogonStartupService _startupService;

    /// <summary>打开这个窗口时的语言：用来判断用户有没有改它。</summary>
    private AppLanguage _languageBeforeEdit;

    public SettingsViewModel(
        ISettingsService settingsService,
        ICleanupService cleanup,
        IBlobStore blobStore,
        IOperationRepository operations,
        IDialogService dialogs,
        Func<AppSettings, bool>? applyShellIntegration = null,
        ILogonStartupService? startupService = null)
    {
        _settingsService = settingsService;
        _cleanup = cleanup;
        _blobStore = blobStore;
        _operations = operations;
        _dialogs = dialogs;
        _applyShellIntegration = applyShellIntegration ?? (settings =>
            Application.Current is not App || App.ApplyShellIntegration(settings));

        _startupService = startupService ?? new LogonStartupService(
            disabled: Application.Current is not App || App.ShellIntegrationDisabled);
        Settings = settingsService.Current.Clone();
        _languageBeforeEdit = Settings.Language;
    }

    /// <summary>
    /// 语言变了。界面文字是 x:Static，XAML 解析的时候就定死了 —— 得重启应用才生效。
    /// </summary>
    /// <remarks>
    /// 重启由 App 做（ViewModel 不碰进程），所以这里只把"该重启了"这件事说出来。
    /// </remarks>
    public event Action? LanguageChangeRequested;

    /// <summary>表单绑定的设置对象。</summary>
    public AppSettings Settings { get; private set; }

    public bool LastSaveSucceeded { get; private set; }

    private string? _startupDelayText;
    public string StartupDelayText
    {
        get => _startupDelayText ?? Settings.AdministratorStartupDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set { _startupDelayText = value; OnPropertyChanged(); }
    }

    /// <summary>排除规则：一行一个通配符。</summary>
    public string ExcludeGlobsText
    {
        get => string.Join(Environment.NewLine, Settings.ExcludeGlobs);
        set
        {
            Settings.ExcludeGlobs = value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string _storageText = string.Empty;

    [ObservableProperty]
    private string _exportText = string.Empty;

    [ObservableProperty]
    private string _historyText = string.Empty;

    public IReadOnlyList<EnumOption<ConflictPolicy>> Policies { get; } =
    [
        new(ConflictPolicy.Ask, Strings.ConflictAsk),
        new(ConflictPolicy.Overwrite, Strings.ActionOverwrite),
        new(ConflictPolicy.Skip, Strings.ActionSkip),
        new(ConflictPolicy.Rename, Strings.ActionRename),
    ];

    /// <summary>
    /// 语言选项。语言名本身用该语言自己的写法（简体中文 / English），
    /// 只有"跟随系统"这一条要翻译 —— 用户得先看懂这一条才知道别的选项是什么意思。
    /// </summary>
    public IReadOnlyList<EnumOption<AppLanguage>> Languages { get; } =
    [
        new(AppLanguage.System, Strings.LanguageSystem),
        new(AppLanguage.Chinese, Strings.LanguageChinese),
        new(AppLanguage.TraditionalChinese, Strings.LanguageTraditionalChinese),
        new(AppLanguage.English, Strings.LanguageEnglish),
        new(AppLanguage.Japanese, Strings.LanguageJapanese),
        new(AppLanguage.Korean, Strings.LanguageKorean),
        new(AppLanguage.Spanish, Strings.LanguageSpanish),
        new(AppLanguage.PortugueseBrazil, Strings.LanguagePortugueseBrazil),
        new(AppLanguage.French, Strings.LanguageFrench),
        new(AppLanguage.German, Strings.LanguageGerman),
        new(AppLanguage.Italian, Strings.LanguageItalian),
        new(AppLanguage.Russian, Strings.LanguageRussian),
        new(AppLanguage.Arabic, Strings.LanguageArabic),
        new(AppLanguage.Hindi, Strings.LanguageHindi),
        new(AppLanguage.Indonesian, Strings.LanguageIndonesian),
        new(AppLanguage.Thai, Strings.LanguageThai),
        new(AppLanguage.Turkish, Strings.LanguageTurkish),
        new(AppLanguage.Vietnamese, Strings.LanguageVietnamese),
        new(AppLanguage.Bengali, Strings.LanguageBengali),
    ];

    /// <summary>
    /// 外观选项。"跟随系统"单独一条键，不复用语言那条 ——
    /// 语言表里的"跟随系统"在德语写的是「Der Systemsprache folgen」（明说语言），
    /// 挂在主题上是句错话。
    /// </summary>
    public IReadOnlyList<EnumOption<ThemeChoice>> Themes { get; } =
    [
        new(ThemeChoice.System, Strings.ThemeSystem),
        new(ThemeChoice.Light, Strings.ThemeLight),
        new(ThemeChoice.Dark, Strings.ThemeDark),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairStartupCommand))]
    private bool _isStartupBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairStartupCommand))]
    private bool _startupUncertain;

    [ObservableProperty]
    private string _startupStatusText = Strings.StartupStatusUnknown;

    private bool CanSave() => !IsStartupBusy && !StartupUncertain;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveCoreAsync(repair: false);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task RepairStartupAsync() => SaveCoreAsync(repair: true);

    [RelayCommand]
    private async Task RefreshStartupAsync()
    {
        if (IsStartupBusy) return;
        // An invalid saved setting must not trap the editor in an unknown/retry-disabled state.
        bool valid = int.TryParse(StartupDelayText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int seconds) && (uint)seconds <= 300;
        var status = await _startupService.ReadAsync(valid ? seconds : 3);
        SetStartupStatus(status);
        StartupUncertain = _startupService.IsPending || status.State == LogonStartupState.Unknown;
    }

    private void SetStartupStatus(LogonStartupStatus status)
    {
        StartupStatusText = status.State switch
        {
            LogonStartupState.Disabled => Strings.StartupStatusDisabled,
            LogonStartupState.Plain => Strings.StartupStatusPlain,
            LogonStartupState.Elevated => Strings.StartupStatusElevated,
            LogonStartupState.PlainNeedsRepair or LogonStartupState.ElevatedNeedsRepair => Strings.StartupStatusRepair,
            LogonStartupState.Conflict => Strings.StartupStatusConflict,
            LogonStartupState.Suppressed => Strings.StartupStatusSuppressed,
            _ => Strings.StartupStatusUnknown,
        };
        if (status.Detail is not null) Serilog.Log.Information("Startup readback {State}: {Detail}", status.State, status.Detail);
    }

    private void RestoreStartupSelection(LogonStartupStatus status)
    {
        if (status.State is LogonStartupState.Unknown or LogonStartupState.Conflict or LogonStartupState.Suppressed) return;
        Settings = Settings.Clone();
        Settings.StartWithWindows = status.State != LogonStartupState.Disabled;
        Settings.StartWithWindowsAsAdministrator = status.State is LogonStartupState.Elevated or LogonStartupState.ElevatedNeedsRepair;
        OnPropertyChanged(nameof(Settings));
    }

    private async Task SaveCoreAsync(bool repair)
    {
        if (!CanSave()) return;
        LastSaveSucceeded = false;
        if (!int.TryParse(StartupDelayText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int seconds) || (uint)seconds > 300)
        {
            _dialogs.ShowWarning(Strings.SettingsTitle, Strings.StartupDelayRange);
            return;
        }
        Settings.AdministratorStartupDelaySeconds = seconds;
        IsStartupBusy = true;
        StartupStatusText = Strings.StartupStatusBusy;
        try
        {
            // Capture the request before awaiting UAC. The form is disabled while it is pending.
            var committed = Settings.Clone();
            ElevatedStartup.ValidateDelay(committed.AdministratorStartupDelaySeconds);
            var result = await _startupService.ChangeAsync(committed.StartWithWindows,
                committed.StartWithWindowsAsAdministrator, repair, committed.AdministratorStartupDelaySeconds);
            int readbackDelay = committed.AdministratorStartupDelaySeconds;
            if (result.Outcome == StartupOutcome.Cancelled &&
                (uint)_settingsService.Current.AdministratorStartupDelaySeconds <= 300)
                readbackDelay = _settingsService.Current.AdministratorStartupDelaySeconds;
            var status = await _startupService.ReadAsync(readbackDelay);
            SetStartupStatus(status);
            if (result.Outcome is not (StartupOutcome.Applied or StartupOutcome.Unchanged))
            {
                Serilog.Log.Warning("Startup change {Outcome}: {Detail}", result.Outcome, result.Detail);
                StartupUncertain = result.Outcome == StartupOutcome.Uncertain ||
                    _startupService.IsPending || status.State == LogonStartupState.Unknown;
                RestoreStartupSelection(status);
                if (result.Outcome == StartupOutcome.Cancelled)
                {
                    Settings.AdministratorStartupDelaySeconds = readbackDelay;
                    _startupDelayText = null;
                    OnPropertyChanged(nameof(StartupDelayText));
                    _dialogs.ShowToast(Strings.SettingsTitle, Strings.StartupCancelled);
                }
                else
                    _dialogs.ShowWarning(Strings.SettingsTitle, result.Outcome == StartupOutcome.Uncertain
                        ? Strings.StartupUncertainMessage : StartupFailureMessage(result.Failure));
                return;
            }
            var expected = !committed.StartWithWindows ? LogonStartupState.Disabled :
                committed.StartWithWindowsAsAdministrator ? LogonStartupState.Elevated : LogonStartupState.Plain;
            if ((status.State != expected && status.State != LogonStartupState.Suppressed) || _startupService.IsPending)
            {
                StartupUncertain = true;
                _dialogs.ShowWarning(Strings.SettingsTitle, Strings.StartupUncertainMessage);
                return;
            }
            var languageChanged = committed.Language != _languageBeforeEdit;
            var current = _settingsService.Current;
            committed.LastGroupId = current.LastGroupId;
            committed.WindowBounds = current.Clone().WindowBounds;
            committed.WorkbenchViewMode = current.WorkbenchViewMode;
            committed.CompactIconView = current.CompactIconView;
            committed.ShelfRetentionDays = current.ShelfRetentionDays; // Preserve the legacy JSON field for older builds.
            _settingsService.Save(committed);
            ThemeService.ApplyFromSettings(committed);
            if (Application.Current is App app) app.ReloadGlobalHotkey();
            if (!_applyShellIntegration(committed))
            {
                _dialogs.ShowWarning(Strings.SettingsTitle, Strings.ShellIntegrationFailedMessage);
                return;
            }
            _languageBeforeEdit = committed.Language;
            LastSaveSucceeded = true;
            if (languageChanged) LanguageChangeRequested?.Invoke();
            else _dialogs.ShowInfo(Strings.SettingsTitle, Strings.SettingsSavedMessage);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Saving settings/startup failed; read system state before retrying.");
            var status = await _startupService.ReadAsync();
            SetStartupStatus(status);
            StartupUncertain = _startupService.IsPending || status.State == LogonStartupState.Unknown;
            _dialogs.ShowWarning(Strings.SettingsTitle, Strings.Format(Strings.StartupFailedMessage, new AppPaths().LogDirectory));
        }
        finally { IsStartupBusy = false; }
    }

    private static string StartupFailureMessage(StartupFailure failure) => Strings.Format(failure switch
    {
        StartupFailure.AccountMismatch => Strings.StartupAccountMessage,
        StartupFailure.AccessDenied => Strings.StartupDeniedMessage,
        StartupFailure.OwnershipConflict => Strings.StartupConflictMessage,
        StartupFailure.RollbackIncomplete => Strings.StartupRollbackMessage,
        _ => Strings.StartupFailedMessage,
    }, new AppPaths().LogDirectory);

    /// <summary>取消未保存的修改，回滚内存设置。</summary>
    [RelayCommand]
    private void Cancel()
    {
        Settings = _settingsService.Current.Clone();
        _startupDelayText = null;
        OnPropertyChanged(nameof(StartupDelayText));
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ExcludeGlobsText));
    }

    /// <summary>刷新存储占用显示。</summary>
    [RelayCommand]
    public async Task RefreshStorageAsync()
    {
        var stats = await _cleanup.GetStorageStatsAsync(CancellationToken.None);

        StorageText = Strings.Format(Strings.StorageUsageLabel, FormatSize(stats.StashBytes));
        ExportText = Strings.Format(Strings.StorageExportLabel, FormatSize(stats.ExportBytes));

        var operations = await _operations.GetAllAsync(CancellationToken.None);
        HistoryText = Strings.Format(Strings.StorageHistoryLabel, operations.Count);
    }

    /// <summary>清理没人引用的 blob 与拖出临时文件（备份要留着，否则撤不回来）。</summary>
    [RelayCommand]
    private async Task CleanupUnreferencedAsync()
    {
        if (!_dialogs.Confirm(Strings.CleanupConfirmTitle, Strings.CleanupConfirmMessage))
        {
            return;
        }

        var deletedBlobs = await _cleanup.DeleteUnreferencedBlobsAsync(CancellationToken.None);
        var deletedExports = await _cleanup.DeleteExpiredExportsAsync(TimeSpan.Zero, CancellationToken.None);
        var deletedStale = await _cleanup.DeleteStaleTemporaryFilesAsync(TimeSpan.Zero, CancellationToken.None);

        await RefreshStorageAsync();

        var total = deletedBlobs + deletedExports + deletedStale;
        _dialogs.ShowInfo(Strings.CleanupConfirmTitle, Strings.Format(Strings.CleanupDoneMessage, total));
    }

    /// <summary>清空所有历史（备份也跟着没了，那些操作就撤不回来了）。</summary>
    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!_dialogs.Confirm(Strings.ClearHistoryConfirmTitle, Strings.ClearHistoryConfirmMessage))
        {
            return;
        }

        var operations = await _operations.GetAllAsync(CancellationToken.None);

        foreach (var operation in operations)
        {
            await _operations.DeleteAsync(operation.Id, CancellationToken.None);
        }

        await RefreshStorageAsync();

        _dialogs.ShowInfo(Strings.ClearHistoryConfirmTitle, Strings.Format(Strings.ClearHistoryDoneMessage, operations.Count));
    }

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>根目录预设的 ViewModel。</summary>
public sealed partial class RootPresetsViewModel : ObservableObject
{
    private readonly IRootPresetRepository _repository;
    private readonly IDialogService _dialogs;

    public RootPresetsViewModel(IRootPresetRepository repository, IDialogService dialogs)
    {
        _repository = repository;
        _dialogs = dialogs;
    }

    public ObservableCollection<RootPreset> Presets { get; } = [];

    [RelayCommand]
    public async Task LoadAsync()
    {
        var presets = await _repository.GetAllAsync(CancellationToken.None);

        Presets.Clear();

        foreach (var preset in presets)
        {
            Presets.Add(preset);
        }
    }

    /// <summary>新增一个预设（名字 + 路径）。</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        var name = _dialogs.PromptText(Strings.NewPresetTitle, string.Empty);
        var path = _dialogs.PickFolder();

        if (string.IsNullOrWhiteSpace(name) || path is null)
        {
            return;
        }

        await _repository.AddAsync(
            new RootPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Path = path,
                SortOrder = Presets.Count,
            },
            CancellationToken.None);

        await LoadAsync();
    }

    /// <summary>删除选中的预设。</summary>
    [RelayCommand]
    private async Task DeleteAsync(RootPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        await _repository.DeleteAsync(preset.Id, CancellationToken.None);
        await LoadAsync();
    }
}
