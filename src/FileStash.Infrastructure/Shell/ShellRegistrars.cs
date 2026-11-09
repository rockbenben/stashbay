using System.Runtime.InteropServices;
using FileStash.Core.Resources;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;

namespace FileStash.Infrastructure.Shell;

/// <summary>
/// 资源管理器右键菜单（经典菜单，写 HKCU，不需要管理员）。
/// </summary>
/// <remarks>
/// Windows 11 下这些菜单位于"显示更多选项"里；一级菜单（IExplorerCommand + 稀疏包）是 V3 的事。
/// </remarks>
public sealed class ContextMenuRegistrar
{
    private readonly string _classesRoot;
    private readonly string _executablePath;

    public ContextMenuRegistrar(string executablePath, string? classesRoot = null)
    {
        _executablePath = executablePath;
        _classesRoot = classesRoot ?? @"Software\Classes";
    }

    /// <summary>写入菜单项。</summary>
    public void Register()
    {
        DeleteLegacyKeys();

        WriteItem(
            $@"{_classesRoot}\*\shell\StashBay.Add",
            Strings.ShellMenuAddToStash,
            $"\"{_executablePath}\" --add \"%1\"",
            _executablePath);

        WriteItem(
            $@"{_classesRoot}\Directory\shell\StashBay.Add",
            Strings.ShellMenuAddToStash,
            $"\"{_executablePath}\" --add \"%1\"",
            _executablePath);

        WriteItem(
            $@"{_classesRoot}\Directory\shell\StashBay.ApplyHere",
            Strings.ShellMenuApplyToFolder,
            $"\"{_executablePath}\" --apply-here \"%1\"",
            _executablePath);

        WriteItem(
            $@"{_classesRoot}\Directory\Background\shell\StashBay.ApplyHere",
            Strings.ShellMenuApplyHere,
            $"\"{_executablePath}\" --apply-here \"%V\"",
            _executablePath);
    }

    /// <summary>删除菜单项（设置里关掉开关时调用）。</summary>
    public void Unregister()
    {
        DeleteLegacyKeys();

        DeleteKey($@"{_classesRoot}\*\shell\StashBay.Add");
        DeleteKey($@"{_classesRoot}\Directory\shell\StashBay.Add");
        DeleteKey($@"{_classesRoot}\Directory\shell\StashBay.ApplyHere");
        DeleteKey($@"{_classesRoot}\Directory\Background\shell\StashBay.ApplyHere");
    }

    private void DeleteLegacyKeys()
    {
        DeleteKey($@"{_classesRoot}\*\shell\FileStash.Add");
        DeleteKey($@"{_classesRoot}\Directory\shell\FileStash.Add");
        DeleteKey($@"{_classesRoot}\Directory\shell\FileStash.ApplyHere");
        DeleteKey($@"{_classesRoot}\Directory\Background\shell\FileStash.ApplyHere");
    }

    private static void WriteItem(string keyPath, string label, string command, string iconPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);

        if (key is null)
        {
            return;
        }

        key.SetValue(null, label);

        // 图标要用**注册的那个 exe**，不是当前进程 —— 单文件发布下 Environment.ProcessPath
        // 恰好也是它，但两者不是一回事
        key.SetValue("Icon", $"{iconPath},0");
        key.SetValue("MultiSelectModel", "Player");

        using var commandKey = key.CreateSubKey("command");

        if (commandKey is not null)
        {
            commandKey.SetValue(null, command);
        }
    }

    private static void DeleteKey(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }
}

/// <summary>开机自启（HKCU\...\Run，不需要管理员）。</summary>
public sealed class StartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StashBay";
    private const string LegacyValueName = "FileStash";

    /// <summary>
    /// 写启动项。是否带 <c>--minimized</c> 由调用方按设置决定 ——
    /// 写死带上，"启动时最小化"那个勾就只是个摆设。
    /// </summary>
    public void Enable(bool minimized)
    {
        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executablePath))
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);

        if (key is not null)
        {
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

            // 单文件发布下 Assembly.Location 是空字符串，取程序集路径会落到盘符根目录
            var arguments = minimized ? " --minimized" : string.Empty;
            key.SetValue(ValueName, $"\"{executablePath}\"{arguments}");
        }
    }

    /// <summary>删启动项。</summary>
    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

            if (key is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);

                return key?.GetValue(ValueName) is not null || key?.GetValue(LegacyValueName) is not null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }
}

/// <summary>"发送到"快捷方式（一次性传入所有选中的路径）。</summary>
public sealed class SendToRegistrar
{
    private readonly string _executablePath;

    public SendToRegistrar(string executablePath)
    {
        _executablePath = executablePath;
    }

    public string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.SendTo),
        Strings.ShellShortcutName);

    /// <summary>创建快捷方式。</summary>
    public void Register()
    {
        RemoveLegacyShortcut();

        var directory = Path.GetDirectoryName(ShortcutPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            // 用 WScript.Shell 后期绑定，省掉一长串 IShellLinkW 的 vtable 声明
            var shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
            {
                return;
            }

            dynamic? shell = Activator.CreateInstance(shellType);

            if (shell is null)
            {
                return;
            }

            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = _executablePath;
            shortcut.Arguments = "--add";
            shortcut.WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? string.Empty;
            shortcut.Description = Strings.ShellShortcutDescription;
            shortcut.save();
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>删掉旧名字的快捷方式。</summary>
    /// <remarks>
    /// 快捷方式的**文件名不能跟着语言变**：变一次就在「发送到」里多留一个条目，
    /// 还得记得什么时候删它。所以名字固定成语言无关的，只有说明文字跟着语言走。
    /// </remarks>
    private void RemoveLegacyShortcut()
    {
        var sendToDir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        var legacies = new[]
        {
            Path.Combine(sendToDir, Strings.ShellLegacyShortcutName),
            Path.Combine(sendToDir, "FileStash.lnk"),
        };

        foreach (var legacy in legacies)
        {
            try
            {
                if (File.Exists(legacy))
                {
                    File.Delete(legacy);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 删不掉就留着：多一个条目不影响使用
            }
        }
    }

    /// <summary>删除快捷方式。</summary>
    public void Unregister()
    {
        RemoveLegacyShortcut();

        try
        {
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public bool IsRegistered => File.Exists(ShortcutPath);
}
