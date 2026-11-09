namespace FileStash.Core.Models;

/// <summary>
/// 用户在设置里选的那一档外观。
/// </summary>
/// <remarks>
/// 与 <c>AppTheme</c>（界面实际生效的两套配色）分开：这一层多一个"跟随系统"，
/// 它要在启动时查一次系统设置才知道落到哪一档。
/// 顺序不要动 —— 设置文件里存的是名字，改了顺序老用户的取值会读成别的档。
/// </remarks>
public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>
/// 把"用户选的那一档"落到"到底要不要深色"。纯函数：系统那一侧读到的值由调用方传进来，
/// 这样三种选择都能不碰注册表就验掉。
/// </summary>
public static class ThemeChoiceResolver
{
    public static bool WantsDark(ThemeChoice choice, bool systemIsLight) => choice switch
    {
        ThemeChoice.Dark => true,
        ThemeChoice.Light => false,

        // 跟随系统：系统说浅色就浅色，读不到时按浅色（见 SystemTheme）
        _ => !systemIsLight,
    };
}
