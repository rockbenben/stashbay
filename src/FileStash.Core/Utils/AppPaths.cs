namespace FileStash.Core.Utils;

/// <summary>
/// 应用用到的全部路径。构造参数可注入，测试用临时目录即可，不必碰真实用户目录。
/// </summary>
public sealed class AppPaths
{
    /// <summary>
    /// 环境变量名：设了它就用它当根目录（数据与设置都放进去）。
    /// </summary>
    /// <remarks>
    /// 验收脚本与并行跑测时用这个把应用完全隔离到临时目录，不碰用户真实的数据。
    /// </remarks>
    public const string HomeVariable = "STASHBAY_HOME";

    /// <summary>旧版环境变量名，向后兼容。</summary>
    public const string LegacyHomeVariable = "FILESTASH_HOME";

    /// <summary>
    /// 环境变量名：设了它就**完全不动**资源管理器右键菜单 / "发送到" / 开机自启。
    /// </summary>
    /// <remarks>
    /// 给自动化与无头运行用。跟 <see cref="HomeVariable"/> 的区别：
    /// 那一个只隔离数据，这一个连注册表都不碰 —— 既不注册也不注销，
    /// 免得"关掉开关"变成一个静默改变用户既有设置的动作。
    /// </remarks>
    public const string NoShellIntegrationVariable = "STASHBAY_NO_SHELL_INTEGRATION";

    /// <summary>旧版外壳跳过环境变量名，向后兼容。</summary>
    public const string LegacyNoShellIntegrationVariable = "FILESTASH_NO_SHELL_INTEGRATION";

    public AppPaths(string? localAppData = null, string? roamingAppData = null)
    {
        // 环境变量**只在没有显式指定目录时**才生效 ——
        // 显式指定的优先（单元测试每个用例都有自己的临时目录，不能被环境变量一锅端）
        var home = Environment.GetEnvironmentVariable(HomeVariable);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable(LegacyHomeVariable);
        }

        if (!string.IsNullOrWhiteSpace(home) && localAppData is null && roamingAppData is null)
        {
            localAppData = home;
            roamingAppData = home;
        }

        var local = localAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = roamingAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // 优先使用新品牌目录 StashBay，若旧目录存在则平滑过渡
        var primaryLocal = Path.Combine(local, "StashBay");
        var legacyLocal = Path.Combine(local, "FileStash");
        RootDirectory = Directory.Exists(legacyLocal) && !Directory.Exists(primaryLocal)
            ? legacyLocal
            : primaryLocal;

        DatabasePath = Path.Combine(RootDirectory, "stashbay.db");
        if (!File.Exists(DatabasePath))
        {
            var legacyDb = Path.Combine(RootDirectory, "filestash.db");
            if (File.Exists(legacyDb))
            {
                DatabasePath = legacyDb;
            }
        }

        BlobDirectory = Path.Combine(RootDirectory, "blobs");
        BlobTempDirectory = Path.Combine(BlobDirectory, "tmp");
        ExportDirectory = Path.Combine(RootDirectory, "export");
        ClipboardDirectory = Path.Combine(RootDirectory, "clipboard");
        LogDirectory = Path.Combine(RootDirectory, "logs");

        var primarySettings = Path.Combine(roaming, "StashBay", "settings.json");
        var legacySettings = Path.Combine(roaming, "FileStash", "settings.json");
        SettingsPath = File.Exists(legacySettings) && !File.Exists(primarySettings)
            ? legacySettings
            : primarySettings;
    }

    /// <summary>%LOCALAPPDATA%\StashBay</summary>
    public string RootDirectory { get; }

    public string DatabasePath { get; }

    /// <summary>内容寻址存储，文件名 = SHA-256，前 2 位为子目录。</summary>
    public string BlobDirectory { get; }

    /// <summary>写 blob 时的临时目录（写完即移走）。</summary>
    public string BlobTempDirectory { get; }

    /// <summary>拖出时临时物化的文件。</summary>
    public string ExportDirectory { get; }

    /// <summary>剪贴板图片与文本临时落盘的目录。</summary>
    public string ClipboardDirectory { get; }

    public string LogDirectory { get; }

    /// <summary>%APPDATA%\StashBay\settings.json</summary>
    public string SettingsPath { get; }

    /// <summary>把需要写入的目录建出来。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(BlobDirectory);
        Directory.CreateDirectory(BlobTempDirectory);
        Directory.CreateDirectory(ExportDirectory);
        Directory.CreateDirectory(ClipboardDirectory);
        Directory.CreateDirectory(LogDirectory);

        var settingsDirectory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }
    }
}
