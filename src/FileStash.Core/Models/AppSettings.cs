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

    public bool AutoGitAddAfterApply { get; set; }

    public bool WatchGitBranchSwitch { get; set; } = true;

    /// <summary>添加文件夹时排除，如 ["**/node_modules/**", "**/bin/**"]。</summary>
    public string[] ExcludeGlobs { get; set; } = [];

    /// <summary>仓库内文件夹仅暂存未被忽略的文件。</summary>
    public bool RespectGitIgnore { get; set; }

    public string DiffToolCommand { get; set; } = "code --diff \"{left}\" \"{right}\"";

    public string GlobalHotkeyAdd { get; set; } = "Win+Alt+S";

    /// <summary>呼出完整工作台的全局快捷键，默认 Win+Alt+W。</summary>
    public string GlobalHotkeyWorkbench { get; set; } = "Win+Alt+W";

    /// <summary>将当前剪贴板内容直接加入暂存的全局快捷键，默认 Win+Alt+V。</summary>
    public string GlobalHotkeyPaste { get; set; } = "Win+Alt+V";

    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

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
