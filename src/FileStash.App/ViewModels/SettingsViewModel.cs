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

namespace FileStash.App.ViewModels;

/// <summary>设置窗口的 ViewModel。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICleanupService _cleanup;
    private readonly IBlobStore _blobStore;
    private readonly IOperationRepository _operations;
    private readonly IDialogService _dialogs;

    /// <summary>打开这个窗口时的语言：用来判断用户有没有改它。</summary>
    private readonly AppLanguage _languageBeforeEdit;

    public SettingsViewModel(
        ISettingsService settingsService,
        ICleanupService cleanup,
        IBlobStore blobStore,
        IOperationRepository operations,
        IDialogService dialogs)
    {
        _settingsService = settingsService;
        _cleanup = cleanup;
        _blobStore = blobStore;
        _operations = operations;
        _dialogs = dialogs;

        Settings = settingsService.Current;
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

    [RelayCommand]
    private void Save()
    {
        var languageChanged = Settings.Language != _languageBeforeEdit;

        _settingsService.Save(Settings);

        // 配色是 DynamicResource，能当场翻过来（不像语言要重启）。
        // 不跟着变的：托盘图标本身（系统绘制）与已经算好的窗口尺寸。
        ThemeService.ApplyFromSettings(Settings);

        // 热重载全局快捷键（无需重启应用）
        if (Application.Current is App app)
        {
            app.ReloadGlobalHotkey();
        }

        if (languageChanged)
        {
            // 界面文字换不掉，只能重开进程。弹"已保存"没意义 —— 马上就重启了。
            LanguageChangeRequested?.Invoke();
            return;
        }

        _dialogs.ShowInfo(Strings.SettingsTitle, Strings.SettingsSavedMessage);
    }

    /// <summary>取消未保存的修改，回滚内存设置。</summary>
    [RelayCommand]
    private void Cancel()
    {
        _settingsService.Load();
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
