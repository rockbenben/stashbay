using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace FileStash.App.Controls;

/// <summary>
/// 全局快捷键（RegisterHotKey）多动作管理器。
/// </summary>
/// <remarks>
/// **不要用 `WH_KEYBOARD_LL` 低级钩子**：钩子回调一旦拖过 `LowLevelHooksTimeout`
/// （Win10 1709 起系统上限 1000ms），系统会**静默摘掉这个钩子且不通知应用**——
/// 快捷键在整个会话里无声失效，而触发它的那几个按键本身也被吞掉。
/// `RegisterHotKey` 没有这个尾巴。
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int DefaultHotkeyId = 0xBEEF;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly HashSet<int> _registeredIds = new();
    private readonly Action? _defaultAction;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("StashBayHotkey")
        {
            HwndSourceHook = OnMessage,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
    }

    public HotkeyManager(Action onTriggered) : this()
    {
        _defaultAction = onTriggered;
    }

    /// <summary>注册单默认快捷键；成功返回 true（兼容旧签名）。</summary>
    public bool Register(string gesture)
    {
        if (_defaultAction is null)
        {
            throw new InvalidOperationException("Default action handler was not provided. Use Register(id, gesture, callback).");
        }

        return Register(DefaultHotkeyId, gesture, _defaultAction);
    }

    /// <summary>注册指定 ID 的快捷键；成功返回 true。</summary>
    public bool Register(int id, string gesture, Action onTriggered)
    {
        ArgumentNullException.ThrowIfNull(onTriggered);

        if (!TryParse(gesture, out var modifiers, out var virtualKey))
        {
            return false;
        }

        Unregister(id);

        // 尝试附带 MOD_NOREPEAT (0x4000)，防止长按按键时造成高频刷屏触发
        var registered = RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, virtualKey);
        if (!registered)
        {
            registered = RegisterHotKey(_source.Handle, id, modifiers, virtualKey);
        }

        if (registered)
        {
            _callbacks[id] = onTriggered;
            _registeredIds.Add(id);
            return true;
        }

        return false;
    }

    public void Unregister(int id)
    {
        if (_registeredIds.Remove(id))
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _callbacks.Remove(id);
    }

    public void Unregister() => Unregister(DefaultHotkeyId);

    public void UnregisterAll()
    {
        foreach (var id in _registeredIds.ToList())
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _registeredIds.Clear();
        _callbacks.Clear();
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var action))
            {
                handled = true;
                action();
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>解析 "Win+Alt+S" 这样的写法，支持日常习惯键名与德/法/西/俄等多语言本地化习惯。</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var trimmed = gesture.Trim();
        var parts = new List<string>();

        // 特殊处理末尾是 "+" 键的情况，如 "Ctrl+Alt++"
        if (trimmed.EndsWith("++", StringComparison.Ordinal) || trimmed.EndsWith("+ +", StringComparison.Ordinal))
        {
            var prefix = trimmed[..trimmed.LastIndexOf('+')].TrimEnd('+').Trim();
            parts.AddRange(prefix.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            parts.Add("+");
        }
        else
        {
            parts.AddRange(trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var part in parts)
        {
            if (TryParseModifier(part, out var mod))
            {
                modifiers |= mod;
            }
            else
            {
                if (virtualKey != 0)
                {
                    // 已有主键，出现多个主键视为无效手势
                    return false;
                }

                if (!TryParseKey(part, out _, out virtualKey))
                {
                    return false;
                }
            }
        }

        return virtualKey != 0 && modifiers != 0;
    }

    /// <summary>
    /// 解析修饰键（支持英文常规习惯与德/法/西等多语言习惯）。
    /// </summary>
    public static bool TryParseModifier(string part, out uint modifier)
    {
        modifier = 0;
        switch (part.Trim().ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
            case "CTL":
            case "STRG":       // 德语 Steuerung
                modifier = 0x0002;
                return true;

            case "ALT":
            case "OPTION":
                modifier = 0x0001;
                return true;

            case "SHIFT":
            case "MAJ":        // 法语 Majuscule
            case "MAYUS":      // 西班牙语 Mayúsculas
            case "MAYÚS":
            case "MAIUSC":     // 意大利语 Maiusc
            case "UMSCHALT":   // 德语 Umschalt
                modifier = 0x0004;
                return true;

            case "WIN":
            case "WINDOWS":
            case "SUPER":
            case "META":
            case "CMD":
            case "COMMAND":
                modifier = 0x0008;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 解析主按键名称（符合日常习惯与本地化常用键名）。
    /// </summary>
    public static bool TryParseKey(string part, out Key key, out uint virtualKey)
    {
        key = Key.None;
        virtualKey = 0;

        var token = part.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var upper = token.ToUpperInvariant();

        // 1. 主键盘单数字 '0' ~ '9'
        if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
        {
            key = Key.D0 + (token[0] - '0');
            virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
            return true;
        }

        // 2. 小键盘数字: "NUM0"~"NUM9", "NUMPAD0"~"NUMPAD9"
        if (upper.StartsWith("NUM", StringComparison.Ordinal))
        {
            var numPart = upper.Replace("NUMPAD", "", StringComparison.Ordinal).Replace("NUM", "", StringComparison.Ordinal).Trim();
            if (numPart.Length == 1 && numPart[0] >= '0' && numPart[0] <= '9')
            {
                key = Key.NumPad0 + (numPart[0] - '0');
                virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
                return true;
            }
        }

        // 3. 日常习惯键名与本地化多语言键名对照
        switch (upper)
        {
            // Enter
            case "ENTER":
            case "RETURN":
            case "EINGABE":
            case "ENTREE":
            case "ENTRÉE":
            case "INTRO":
            case "ВВОД":
                key = Key.Return;
                break;

            // Esc
            case "ESC":
            case "ESCAPE":
            case "ÉCHAP":
            case "ECHAP":
                key = Key.Escape;
                break;

            // Backspace
            case "BACKSPACE":
            case "BACK":
            case "RÜCKTASTE":
            case "RUCKTASTE":
            case "RETOUR":
            case "RETROCESO":
                key = Key.Back;
                break;

            // Delete
            case "DELETE":
            case "DEL":
            case "ENTF":
            case "ENTFERNEN":
            case "SUPPR":
            case "SUPR":
            case "УДАЛИТЬ":
                key = Key.Delete;
                break;

            // Insert
            case "INSERT":
            case "INS":
            case "EINFG":
            case "INSER":
                key = Key.Insert;
                break;

            // PageUp
            case "PAGEUP":
            case "PGUP":
            case "PRIOR":
            case "BILDAUF":
            case "BILD AUF":
            case "PAGE UP":
            case "RE PAG":
            case "REPAG":
                key = Key.Prior;
                break;

            // PageDown
            case "PAGEDOWN":
            case "PGDN":
            case "NEXT":
            case "BILDAB":
            case "BILD AB":
            case "PAGE DOWN":
            case "AV PAG":
            case "AVPAG":
                key = Key.Next;
                break;

            // Home / End
            case "HOME":
            case "POS1":
            case "DEBUT":
            case "DÉBUT":
            case "INICIO":
                key = Key.Home;
                break;
            case "END":
            case "ENDE":
            case "FIN":
                key = Key.End;
                break;

            // Space
            case "SPACE":
            case "SPACEBAR":
            case "LEERTASTE":
            case "ESPACE":
            case "ESPACIO":
            case "ПРОБЕЛ":
                key = Key.Space;
                break;

            // Tab
            case "TAB":
            case "TABULATOR":
            case "TABULATION":
                key = Key.Tab;
                break;

            // CapsLock
            case "CAPSLOCK":
            case "CAPS":
            case "CAPITAL":
            case "FESTSTELLTASTE":
                key = Key.Capital;
                break;

            // 方向键
            case "UP":
            case "ARROWUP":
                key = Key.Up;
                break;
            case "DOWN":
            case "ARROWDOWN":
                key = Key.Down;
                break;
            case "LEFT":
            case "ARROWLEFT":
                key = Key.Left;
                break;
            case "RIGHT":
            case "ARROWRIGHT":
                key = Key.Right;
                break;

            // 标点符号与 OEM 键
            case ",":
            case "COMMA":
            case "OEMCOMMA":
                key = Key.OemComma;
                break;
            case ".":
            case "PERIOD":
            case "DOT":
            case "OEMPERIOD":
                key = Key.OemPeriod;
                break;
            case "-":
            case "MINUS":
            case "OEMMINUS":
                key = Key.OemMinus;
                break;
            case "=":
            case "+":
            case "PLUS":
            case "EQUAL":
            case "EQUALS":
            case "OEMPLUS":
                key = Key.OemPlus;
                break;
            case "/":
            case "SLASH":
            case "QUESTION":
            case "OEMQUESTION":
                key = Key.OemQuestion;
                break;
            case ";":
            case "SEMICOLON":
            case "OEM1":
            case "OEMSEMICOLON":
                key = Key.Oem1;
                break;
            case "`":
            case "~":
            case "TILDE":
            case "BACKQUOTE":
            case "OEM3":
            case "OEMTILDE":
                key = Key.Oem3;
                break;
            case "[":
            case "OPENBRACKET":
            case "OEMOPENBRACKETS":
            case "OEM4":
                key = Key.OemOpenBrackets;
                break;
            case "]":
            case "CLOSEBRACKET":
            case "OEM6":
            case "OEMCLOSEBRACKETS":
                key = Key.Oem6;
                break;
            case "\\":
            case "BACKSLASH":
            case "OEM5":
            case "OEMPIPE":
                key = Key.Oem5;
                break;
            case "'":
            case "\"":
            case "QUOTE":
            case "QUOTES":
            case "OEM7":
            case "OEMQUOTES":
                key = Key.Oem7;
                break;
            case "*":
            case "MULTIPLY":
                key = Key.Multiply;
                break;

            // PrintScreen / ScrollLock / Pause
            case "PRINTSCREEN":
            case "PRTSC":
            case "SNAPSHOT":
            case "DRUCK":
            case "IMPR":
                key = Key.PrintScreen;
                break;
            case "SCROLLLOCK":
            case "SCROLL":
            case "ROLLEN":
                key = Key.Scroll;
                break;
            case "PAUSE":
            case "UNTBR":
                key = Key.Pause;
                break;

            default:
                // 4. 标准 Enum.TryParse 兼容（支持 A-Z, F1-F24 等）
                if (Enum.TryParse<Key>(token, ignoreCase: true, out var parsedKey) && Enum.IsDefined(parsedKey))
                {
                    key = parsedKey;
                    break;
                }

                return false;
        }

        virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
