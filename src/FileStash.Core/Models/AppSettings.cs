namespace FileStash.Core.Models;

/// <summary>用户设置，对应 %APPDATA%\StashBay\settings.json（旧版兼容 %APPDATA%\FileStash\settings.json）。</summary>
public sealed class AppSettings
{
    public ConflictPolicy DefaultConflictPolicy { get; set; } = ConflictPolicy.Ask;

    /// <summary>内容相同的文件不写盘（保持目标 mtime 不变）。</summary>
    public bool SkipIdentical { get; set; } = true;

    /// <summary>覆盖前先备份到 blob（关掉之后覆盖就不可撤销）。</summary>
    public bool BackupBeforeOverwrite { get; set; } = true;

    /// <summary>历史与备份保留天数，0 = 永久。</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>旧设置文件兼容字段；空临时箱现已立即清理，有内容的箱不再按天数过期。</summary>
    public int ShelfRetentionDays { get; set; } = 7;

    /// <summary>写回成功后自动将修改文件加入 Git 暂存区（git add）。</summary>
    public bool AutoGitAddAfterApply { get; set; }

    /// <summary>监控 Git 分支切换并在分支改变时弹出通知提醒写回。</summary>
    public bool WatchGitBranchSwitch { get; set; } = true;

    /// <summary>添加文件夹时排除，如 ["**/node_modules/**", "**/bin/**"]。</summary>
    public string[] ExcludeGlobs { get; set; } = [];

    /// <summary>仓库内文件夹仅暂存未被 .gitignore 忽略的文件。</summary>
    public bool RespectGitIgnore { get; set; }

    /// <summary>外部差异对比工具命令模板，如 code --diff "{left}" "{right}"。</summary>
    public string DiffToolCommand { get; set; } = "code --diff \"{left}\" \"{right}\"";

    /// <summary>快速入箱 / 唤出搁架的全局快捷键，默认 Win+Alt+S。</summary>
    public string GlobalHotkeyAdd { get; set; } = "Win+Alt+S";

    /// <summary>呼出完整工作台的全局快捷键，默认 Win+Alt+W。</summary>
    public string GlobalHotkeyWorkbench { get; set; } = "Win+Alt+W";

    /// <summary>将当前剪贴板内容直接加入暂存的全局快捷键，默认 Win+Alt+V。</summary>
    public string GlobalHotkeyPaste { get; set; } = "Win+Alt+V";

    /// <summary>用户登录时自动启动 StashBay。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>可选管理员登录任务；默认普通权限，开启自启后才生效。</summary>
    public bool StartWithWindowsAsAdministrator { get; set; }

    /// <summary>管理员登录任务的延迟秒数，0–300，默认 3 秒。</summary>
    public int AdministratorStartupDelaySeconds { get; set; } = 3;

    /// <summary>启动时最小化到系统托盘。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>关闭窗口时最小化到托盘而不是直接退出应用。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>启用 Windows 资源管理器右键上下文菜单集成。</summary>
    public bool ContextMenuEnabled { get; set; } = true;

    /// <summary>
    /// 拖出后从暂存中自动移除（参考 DropPoint 行为），默认 true。
    /// 关掉后拖出仍保留在暂存箱中。
    /// </summary>
    public bool RemoveAfterDragExport { get; set; } = true;

    /// <summary>
    /// 当暂存箱为空（全部拖出或清空）时，自动隐藏紧凑悬浮窗口。
    /// </summary>
    public bool AutoDismissWhenEmpty { get; set; } = true;

    /// <summary>
    /// 快捷键唤醒时，在当前鼠标光标附近弹出窗口。
    /// </summary>
    public bool SummonAtCursor { get; set; } = true;

    /// <summary>
    /// 拖拽文件时晃动光标快速唤出暂存箱。
    /// </summary>
    public bool EnableShakeToSummon { get; set; } = true;

    /// <summary>工作台的条目视图档位（树 / 列表 / 按来源目录 / 图标）。</summary>
    public WorkbenchViewMode WorkbenchViewMode { get; set; } = WorkbenchViewMode.Tree;

    /// <summary>紧凑模式下是否采用大图标视图（默认 true，关掉后为紧凑列表）。</summary>
    public bool CompactIconView { get; set; } = true;

    /// <summary>上次选中的组。</summary>
    public string? LastGroupId { get; set; }

    /// <summary>
    /// 界面语言。
    /// </summary>
    /// <remarks>
    /// 默认简体中文，不是"跟随系统"：这个工具一直是中文界面的，
    /// 老用户的 settings.json 里根本没有这一项，缺省值就是老行为 ——
    /// 在英文系统上突然变英文界面，对只认中文界面的用户是惊吓而不是便利。
    /// 想跟着系统走的用户自己在设置里选。
    /// </remarks>
    public AppLanguage Language { get; set; } = AppLanguage.Chinese;

    /// <summary>
    /// 外观。默认浅色，不是"跟随系统"：和语言同一个理由 ——
    /// 老用户的 settings.json 里没有这一项，缺省值就该是老行为；
    /// 系统恰好是深色时，一开机界面被换掉是惊吓不是便利。
    /// 想跟着系统走的用户自己在设置里选"跟随系统"。
    /// </summary>
    public ThemeChoice Theme { get; set; } = ThemeChoice.Light;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ExcludeGlobs = ExcludeGlobs.ToArray();
        if (WindowBounds is { } bounds)
            copy.WindowBounds = new WindowBounds
            {
                X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
                IsMaximized = bounds.IsMaximized
            };
        return copy;
    }

    public WindowBounds? WindowBounds { get; set; }
}

/// <summary>主窗口位置与大小。</summary>
public sealed class WindowBounds
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool IsMaximized { get; set; }
}
