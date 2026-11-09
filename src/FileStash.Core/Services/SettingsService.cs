using System.Text.Json;
using System.Text.Json.Serialization;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>读写 settings.json，缺字段用默认值。</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _settingsPath;

    public SettingsService(AppPaths paths)
    {
        _settingsPath = paths.SettingsPath;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    /// <summary>首次运行时接纳安装器已创建的启动项，不覆盖用户保存过的偏好。</summary>
    public void ImportStartupPreference(bool enabled)
    {
        if (!enabled || File.Exists(_settingsPath))
        {
            return;
        }

        Current.StartWithWindows = true;
        Save(Current);
    }

    public void Load()
    {
        if (!File.Exists(_settingsPath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 文件坏了：留一份现场，回退默认值，别让整个应用起不来
            TryBackupCorruptFile();
            Current = new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Current = settings;

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 原子写：先写临时文件再改名，避免写一半掉电留下半个 JSON
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = _settingsPath + ".corrupt";
            File.Move(_settingsPath, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留档失败不影响启动
        }
    }
}
