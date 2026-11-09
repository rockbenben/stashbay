using System.Text.Json;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;

namespace FileStash.Core.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filestash-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root, _root);
        _paths.EnsureDirectories();
        _service = new SettingsService(_paths);
    }

    public void Dispose()
    {
        TempDirectory.DeleteRecursively(_root);
    }

    [Fact]
    public void Load_WhenFileMissing_ShouldReturnDefaults()
    {
        _service.Load();

        Assert.Equal(ConflictPolicy.Ask, _service.Current.DefaultConflictPolicy);
        Assert.True(_service.Current.SkipIdentical);
        Assert.True(_service.Current.BackupBeforeOverwrite);
        Assert.True(_service.Current.RemoveAfterDragExport);
        Assert.True(_service.Current.AutoDismissWhenEmpty);
        Assert.True(_service.Current.SummonAtCursor);
        Assert.True(_service.Current.EnableShakeToSummon);
        Assert.Equal(30, _service.Current.HistoryRetentionDays);
        Assert.Equal("Win+Alt+S", _service.Current.GlobalHotkeyAdd);
        Assert.Equal("Win+Alt+W", _service.Current.GlobalHotkeyWorkbench);
        Assert.Equal("Win+Alt+V", _service.Current.GlobalHotkeyPaste);

        // 老用户的 settings.json 里没有主题这一项，缺省必须是浅色：
        // 系统恰好是深色时，一开机界面被换掉是惊吓不是便利。
        Assert.Equal(ThemeChoice.Light, _service.Current.Theme);
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTrip()
    {
        var settings = new AppSettings
        {
            DefaultConflictPolicy = ConflictPolicy.Overwrite,
            SkipIdentical = false,
            RemoveAfterDragExport = false,
            AutoDismissWhenEmpty = false,
            SummonAtCursor = false,
            EnableShakeToSummon = false,
            HistoryRetentionDays = 7,
            ExcludeGlobs = ["**/node_modules/**"],
            LastGroupId = "group-1",
            WindowBounds = new WindowBounds { X = 10, Y = 20, Width = 1024, Height = 768 },
        };

        _service.Save(settings);
        _service.Load();

        Assert.Equal(ConflictPolicy.Overwrite, _service.Current.DefaultConflictPolicy);
        Assert.False(_service.Current.RemoveAfterDragExport);
        Assert.False(_service.Current.AutoDismissWhenEmpty);
        Assert.False(_service.Current.SummonAtCursor);
        Assert.False(_service.Current.EnableShakeToSummon);
        Assert.False(_service.Current.SkipIdentical);
        Assert.Equal(7, _service.Current.HistoryRetentionDays);
        Assert.Equal(["**/node_modules/**"], _service.Current.ExcludeGlobs);
        Assert.Equal("group-1", _service.Current.LastGroupId);
        Assert.Equal(1024, _service.Current.WindowBounds!.Width);
    }

    [Fact]
    public void Save_ShouldWriteCamelCaseJson()
    {
        _service.Save(new AppSettings());

        var json = File.ReadAllText(_paths.SettingsPath);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("defaultConflictPolicy", out _));
        Assert.True(document.RootElement.TryGetProperty("historyRetentionDays", out _));
    }

    [Fact]
    public void Load_WhenFileCorrupted_ShouldFallBackToDefaultsAndKeepTheBadFile()
    {
        var path = _paths.SettingsPath;
        File.WriteAllText(path, "{ this is not json ");

        _service.Load();

        Assert.Equal(30, _service.Current.HistoryRetentionDays);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Load_WhenFieldsMissing_ShouldUseDefaults()
    {
        var path = _paths.SettingsPath;
        File.WriteAllText(path, """{ "historyRetentionDays": 3 }""");

        _service.Load();

        Assert.Equal(3, _service.Current.HistoryRetentionDays);
        Assert.True(_service.Current.SkipIdentical);
        Assert.Equal(ConflictPolicy.Ask, _service.Current.DefaultConflictPolicy);
    }

    [Fact]
    public void Load_ShouldReadTheLanguageFromJson()
    {
        // 设置文件里写的是语言名（小驼峰），不是枚举序号。
        // 读不出来的话界面会静默退回中文 —— 日志里一个字都看不出来。
        var path = _paths.SettingsPath;
        File.WriteAllText(path, """{ "language": "english" }""");

        _service.Load();

        Assert.Equal(AppLanguage.English, _service.Current.Language);
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTripTheLanguage()
    {
        _service.Save(new AppSettings { Language = AppLanguage.English });
        _service.Load();

        Assert.Equal(AppLanguage.English, _service.Current.Language);
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTripTheTheme()
    {
        _service.Save(new AppSettings { Theme = ThemeChoice.Dark });
        _service.Load();

        Assert.Equal(ThemeChoice.Dark, _service.Current.Theme);
    }

    /// <summary>
    /// 只有"跟随系统"那一档由系统决定；钉死浅/深的两档不许被系统覆盖。
    /// </summary>
    [Theory]
    [InlineData(ThemeChoice.Light, true, false)]
    [InlineData(ThemeChoice.Light, false, false)]
    [InlineData(ThemeChoice.Dark, true, true)]
    [InlineData(ThemeChoice.Dark, false, true)]
    [InlineData(ThemeChoice.System, true, false)]
    [InlineData(ThemeChoice.System, false, true)]
    public void ThemeChoice_ShouldResolveToWhatTheUserMeant(
        ThemeChoice choice, bool systemIsLight, bool wantsDark)
    {
        Assert.Equal(wantsDark, ThemeChoiceResolver.WantsDark(choice, systemIsLight));
    }
}
