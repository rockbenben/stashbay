using Microsoft.Win32;

namespace FileStash.Infrastructure.Native;

/// <summary>
/// 读系统的外观设置（浅色 / 深色）。
/// </summary>
/// <remarks>
/// Windows 把它放在 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>
/// 的 <c>AppsUseLightTheme</c>（DWORD，1 = 浅色）。这是**应用模式**的开关，
/// 与"系统模式"那个 <c>SystemUsesLightTheme</c> 不是一回事 —— 任务栏跟随后者，
/// 窗口标题栏与资源管理器跟随前者，我们是一扇普通窗口，跟前者。
///
/// 键不存在（老版本 Windows、或被组策略改掉）时按浅色算：
/// 猜错只是配色不合心意，报个错或不去读才是问题。
/// </remarks>
public static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string ValueName = "AppsUseLightTheme";

    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue(ValueName);

            return value is not int number || number != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 注册表读不到不是故障：没有权限或被策略锁住，按浅色走
            return true;
        }
    }
}
