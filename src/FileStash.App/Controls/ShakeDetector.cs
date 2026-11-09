using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FileStash.App.Controls;

/// <summary>
/// 拖拽手势检测器：通过光标晃动（左右或上下快速晃动）呼出小搁架。
/// </summary>
/// <remarks>
/// 保持极致轻量：回调内无堆分配、纯整数运算，耗时远远低于系统钩子超时上限。
/// 仅当启用该选项时挂钩，禁用或退出时立即注销。
/// </remarks>
public sealed class ShakeDetector : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const short VkLButton = 0x01;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public int ptX;
        public int ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _enabled;

    // 晃动状态追踪（支持 X 与 Y 双轴，阈值更宽容自然）
    private int _lastDirX;
    private int _lastDirY;
    private int _lastTurnX;
    private int _lastTurnY;
    private int _reversalsX;
    private int _reversalsY;
    private long _windowStartTime;
    private long _cooldownUntil;

    private const int MinAmplitude = 16;      // 单程最小折返位移（像素），16px 即可识别自然手抖/晃动
    private const int RequiredReversals = 3;  // 至少 3 次反向（左-右-左 或 上-下-上）
    private const int TimeWindowMs = 750;     // 判定时间窗口放宽至 750ms，更加从容
    private const int CooldownMs = 1200;      // 触发后冷却 1.2 秒

    public event Action? ShakeDetected;

    public ShakeDetector(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookCallback;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;

        if (_enabled)
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            _hookId = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(curModule?.ModuleName), 0);
        }
        else
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmMouseMove)
        {
            // 只有按住鼠标左键时才做晃动分析。
            if ((GetAsyncKeyState(VkLButton) & 0x8000) != 0)
            {
                var hookStruct = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                ProcessMove(hookStruct.ptX, hookStruct.ptY);
            }
            else
            {
                ResetTracking();
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ProcessMove(int x, int y)
    {
        var now = Environment.TickCount64;
        if (now < _cooldownUntil)
        {
            return;
        }

        // 只识别双轴晃动，不因靠近屏幕边缘触发，避免拖动滚动条时误唤出。
        CheckShake(x, y, now);
    }

    private void CheckShake(int x, int y, long now)
    {
        if (_windowStartTime == 0 || now - _windowStartTime > TimeWindowMs)
        {
            _windowStartTime = now;
            _lastTurnX = x;
            _lastTurnY = y;
            _lastDirX = 0;
            _lastDirY = 0;
            _reversalsX = 0;
            _reversalsY = 0;
            return;
        }

        var dx = x - _lastTurnX;
        var dy = y - _lastTurnY;

        // X 轴反向检测
        if (dx > MinAmplitude && _lastDirX <= 0)
        {
            _reversalsX++;
            _lastDirX = 1;
            _lastTurnX = x;
        }
        else if (dx < -MinAmplitude && _lastDirX >= 0)
        {
            _reversalsX++;
            _lastDirX = -1;
            _lastTurnX = x;
        }

        // Y 轴反向检测
        if (dy > MinAmplitude && _lastDirY <= 0)
        {
            _reversalsY++;
            _lastDirY = 1;
            _lastTurnY = y;
        }
        else if (dy < -MinAmplitude && _lastDirY >= 0)
        {
            _reversalsY++;
            _lastDirY = -1;
            _lastTurnY = y;
        }

        if (_reversalsX >= RequiredReversals || _reversalsY >= RequiredReversals)
        {
            _cooldownUntil = now + CooldownMs;
            ResetTracking();
            TriggerSummon();
        }
    }

    private void TriggerSummon()
    {
        _dispatcher.BeginInvoke(() =>
        {
            ShakeDetected?.Invoke();
        }, DispatcherPriority.Normal);
    }

    private void ResetTracking()
    {
        _lastDirX = 0;
        _lastDirY = 0;
        _reversalsX = 0;
        _reversalsY = 0;
        _lastTurnX = 0;
        _lastTurnY = 0;
        _windowStartTime = 0;
    }

    public void Dispose()
    {
        SetEnabled(false);
    }
}
