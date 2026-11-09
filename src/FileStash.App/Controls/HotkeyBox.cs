using System.Windows.Controls;
using System.Windows.Input;

namespace FileStash.App.Controls;

/// <summary>
/// 交互式快捷键录制输入框。
/// 用户获得焦点后直接在键盘上按下组合键即可完成录制并规范化格式化（例如 "Win+Alt+S"）。
/// 按 Backspace 或 Delete 可清空；按 Esc 可取消录制。
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var hasModifier = win || ctrl || alt || shift;

        // 单独按下 Esc: 取消当前录制并失去焦点（带修饰键时允许录制为快捷键，如 Ctrl+Shift+Esc）
        if (key == Key.Escape && !hasModifier)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // 单独按下 Backspace 或 Delete: 清空快捷键（带修饰键时允许录制，如 Win+Alt+Delete）
        if (key is Key.Back or Key.Delete && !hasModifier)
        {
            Text = string.Empty;
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // 单独按下 Tab: 允许正常的焦点导航
        if (key == Key.Tab && !hasModifier)
        {
            return;
        }

        e.Handled = true;

        // 纯修饰键按下时暂不提交，等待最终主键
        if (IsModifierKey(key))
        {
            return;
        }

        // 至少包含一个修饰键才允许作为全局快捷键
        if (!hasModifier)
        {
            return;
        }

        var parts = new List<string>(4);
        if (win) parts.Add("Win");
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");

        parts.Add(FormatKey(key));

        Text = string.Join("+", parts);
        Keyboard.ClearFocus();
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System;

    /// <summary>
    /// 按日常习惯格式化主按键名称（Enter, Esc, Backspace, 0-9, PageUp, 符号键等）。
    /// </summary>
    public static string FormatKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num{key - Key.NumPad0}",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.Capital => "CapsLock",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Home => "Home",
        Key.End => "End",
        Key.PrintScreen => "PrintScreen",
        Key.Scroll => "ScrollLock",
        Key.Pause => "Pause",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.Oem1 => ";",
        Key.Oem3 => "`",
        Key.OemOpenBrackets => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Oem7 => "'",
        Key.Multiply => "*",
        Key.Add => "+",
        Key.Subtract => "-",
        Key.Divide => "/",
        Key.Decimal => ".",
        _ => key.ToString()
    };
}
