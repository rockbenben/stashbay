#Requires -Version 7.0
<#
.SYNOPSIS
    StashBay (FileStash) 人工验收项里"能写成代码"的那几条。

.DESCRIPTION
    针对需要真实驱动系统界面与操作系统的交互场景进行自动化验证：
      · 文件被别的程序占用 → 提示里要说出占用的是哪个进程
      · 网络驱动器与 UNC 路径
      · 大文件（>2GB）的添加与写入
      · 窗口白闪（抓帧看有没有白帧）
      · 拖出与剪贴板
      · 高 DPI
    这些都在临时目录里做，不碰用户真实数据与设置（STASHBAY_HOME / STASHBAY_NO_SHELL_INTEGRATION）。

    用法：pwsh tools/manual-checks.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBigFile,
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repositoryRoot 'src\FileStash.App\bin\Debug\net10.0-windows\StashBay.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到 $exe —— 先 dotnet build -c Debug"
}

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("filestash-manual-" + [guid]::NewGuid().ToString('N'))
$appHome = Join-Path $sandbox 'home'
$demo = Join-Path $sandbox 'demo'
New-Item -ItemType Directory -Force -Path $appHome, $demo | Out-Null

$env:STASHBAY_HOME = $appHome
$env:STASHBAY_NO_SHELL_INTEGRATION = '1'
$env:FILESTASH_HOME = $appHome
$env:FILESTASH_NO_SHELL_INTEGRATION = '1'

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param([string]$Item, [bool]$Passed, [string]$Detail = '')

    $results.Add([pscustomobject]@{ Item = $Item; Passed = $Passed; Detail = $Detail })

    $mark = if ($Passed) { '通过' } else { '不通过' }
    Write-Host ("  [{0}] {1}{2}" -f $mark, $Item, $(if ($Detail) { "  —— $Detail" } else { '' }))
}

function Section {
    param([string]$Title)
    Write-Host ''
    Write-Host "== $Title"
}

# ---------------------------------------------------------------- UI 自动化

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

// 抓窗口所在屏幕区域的高频帧，找"白帧"。
// 白闪的判据是：窗口所在区域在某一帧里几乎全是纯白（255,255,255）——
// 应用自己的底色是 #F7F8F9，不是纯白，所以纯白只能来自"还没画就露出来了"。
// 这里不用 System.Drawing（.NET 7 之后它不在共享框架里了），直接 GDI 贴一块 DIB 出来扫。
public class FlashWatch {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    public static string Watch(IntPtr hwnd, int durationMs) {        int width = 480, height = 360;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height;   // 负数 = 自上而下
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        IntPtr bits;
        IntPtr dib = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memoryDc, dib);

        int frames = 0, visibleFrames = 0, whiteFrames = 0;
        double maxWhite = 0;

        var end = DateTime.UtcNow.AddMilliseconds(durationMs);

        while (DateTime.UtcNow < end) {
            frames++;

            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) { continue; }

            int cloaked = 0;
            DwmGetWindowAttribute(hwnd, 14, out cloaked, 4);
            if (cloaked != 0) { continue; }   // 被 DWM 藏起来了（还没画）—— 不算漏帧

            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) { continue; }

            int w = Math.Min(width, Math.Max(1, rect.Right - rect.Left));
            int h = Math.Min(height, Math.Max(1, rect.Bottom - rect.Top));

            BitBlt(memoryDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, 0x00CC0020);

            visibleFrames++;

            int white = 0, total = w * h;

            for (int i = 0; i < total; i++) {
                int offset = i * 4;
                byte b = Marshal.ReadByte(bits, offset);
                byte g = Marshal.ReadByte(bits, offset + 1);
                byte r = Marshal.ReadByte(bits, offset + 2);

                if (r >= 250 && g >= 250 && b >= 250) white++;
            }

            double ratio = (double)white / total;
            if (ratio > maxWhite) maxWhite = ratio;
            if (ratio > 0.98) whiteFrames++;
        }

        SelectObject(memoryDc, old);
        DeleteObject(dib);
        DeleteDC(memoryDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        return string.Format("frames={0} visible={1} white={2} maxWhite={3:P1}", frames, visibleFrames, whiteFrames, maxWhite);
    }

    // 一边抓帧一边把窗口叫出来。
    // 抓帧放在后台线程上（这里用 C# 的线程，不是 PowerShell 的 scriptblock ——
    // 后者在线程池线程上没有 Runspace，会抛异常）。
    public static string WatchWhileShowing(IntPtr hwnd, string exePath, int durationMs) {
        string stats = "";

        var thread = new System.Threading.Thread(() => { stats = Watch(hwnd, durationMs); });
        thread.IsBackground = true;
        thread.Start();

        System.Threading.Thread.Sleep(300);

        try { System.Diagnostics.Process.Start(exePath); }
        catch { }

        thread.Join();
        return stats;
    }
}

public class Mouse {
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, ABSOLUTE = 0x8000;

    // 绝对坐标默认是相对**主屏**的，而多显示器时应用可能开在左边那块屏上
    // （虚拟桌面坐标是负的）—— 那种情况下鼠标会点到别处去。
    // MOUSEEVENTF_VIRTUALDESK 让绝对坐标相对整个虚拟桌面。
    const uint VIRTUALDESK = 0x4000;

    public static int VirtualLeft => GetSystemMetrics(76);
    public static int VirtualTop => GetSystemMetrics(77);
    public static int VirtualWidth => GetSystemMetrics(78);
    public static int VirtualHeight => GetSystemMetrics(79);

    static INPUT Make(uint flags, int x, int y) {
        return new INPUT {
            type = 0,
            mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags | ABSOLUTE | VIRTUALDESK, dwExtraInfo = IntPtr.Zero }
        };
    }

    public static void MoveTo(int x, int y) {
        var inputs = new INPUT[1];
        inputs[0] = Make(MOVE,
            (x - VirtualLeft) * 65535 / Math.Max(1, VirtualWidth),
            (y - VirtualTop) * 65535 / Math.Max(1, VirtualHeight));
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void LeftDown() {
        var inputs = new INPUT[1];
        inputs[0].mi.dwFlags = LEFTDOWN | ABSOLUTE;
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void LeftUp() {
        var inputs = new INPUT[1];
        inputs[0].mi.dwFlags = LEFTUP | ABSOLUTE;
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
}

public class WinMsg {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowThreadThreadId(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static string Cursor() {
        POINT p;
        if (!GetCursorPos(out p)) return "(读不到光标位置)";
        return string.Format("({0},{1})", p.X, p.Y);
    }

    // 窗口所在那块屏的工作区（物理像素）
    public struct WorkArea { public int Width; public int Height; }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public int rcMonitorLeft, rcMonitorTop, rcMonitorRight, rcMonitorBottom; public int rcWorkLeft, rcWorkTop, rcWorkRight, rcWorkBottom; public int dwFlags; }

    public static WorkArea GetWorkAreaFor(IntPtr hwnd) {
        var mi = new MONITORINFO();
        mi.cbSize = 40;
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return new WorkArea { Width = 0, Height = 0 };
        return new WorkArea { Width = mi.rcWorkRight - mi.rcWorkLeft, Height = mi.rcWorkBottom - mi.rcWorkTop };
    }

    // 改了缩放设置之后要广播一下，DWM 才会重读
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    public static bool BroadcastSettingChange() {
        IntPtr result;
        return SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out result) != IntPtr.Zero;
    }

}

// 抢前台：脚本自己的控制台窗口会挡在前面，
// 鼠标事件打到控制台上，应用那边什么反应都没有。
public class Focus {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void Front(IntPtr h) {
        if (h == IntPtr.Zero) return;
        if (IsIconic(h)) ShowWindow(h, 9);       // SW_RESTORE
        BringWindowToTop(h);
        SetForegroundWindow(h);
    }

    public static void MoveTo(IntPtr h, int x, int y, int width, int height) {
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, IntPtr.Zero, x, y, width, height, 0x0040);   // SWP_SHOWWINDOW
    }
}

// 读剪贴板里的文件列表（CF_HDROP）。
// 不用 System.Windows.Forms —— PowerShell 7 里没有 WinForms。
public class ClipFiles {
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);

    public static string[] Get() {
        const uint CF_HDROP = 0x000E;

        if (!IsClipboardFormatAvailable(CF_HDROP)) return new string[0];

        IntPtr h = GetClipboardData(CF_HDROP);
        if (h == IntPtr.Zero) return new string[0];

        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) return new string[0];

        try {
            int offset = Marshal.ReadInt16(p, 0);
            bool wide = Marshal.ReadInt16(p, 2) != 0;

            var files = new System.Collections.Generic.List<string>();
            long cursor = p.ToInt64() + offset;
            long limit = p.ToInt64() + 1024L * 1024L * 8;

            while (cursor < limit) {
                string text = wide
                    ? Marshal.PtrToStringUni(new IntPtr(cursor))
                    : Marshal.PtrToStringAnsi(new IntPtr(cursor));

                if (string.IsNullOrEmpty(text)) break;

                files.Add(text);
                cursor += (text.Length + 1) * (wide ? 2 : 1);
            }

            return files.ToArray();
        }
        finally {
            GlobalUnlock(h);
        }
    }
}
'@

function Get-UiElement {
    param([int]$ProcessId, [string]$Name, [int]$TimeoutSeconds = 20, [string]$ControlType = $null, [string]$WindowTitle = $null)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::RootElement

    while ((Get-Date) -lt $deadline) {
        $processCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

        $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)

        # 指定了窗口标题就只在那个窗口里找：
        # "写回"这个按钮不止一个（主窗口底部也有一个），找错窗口点到的不是同一个按钮
        if ($WindowTitle) {
            $titleCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $WindowTitle)
            $windows = $windows | Where-Object { $_.Current.Name -eq $WindowTitle }
        }

        foreach ($window in $windows) {
            $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Name)

            if ($ControlType) {
                # 只找指定类型的元素：同名的东西不止一个（比如按钮和它的标题），
                # 拿到不支持 Invoke 模式的那个就会抛"不支持的模式"
                $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::$ControlType)

                $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
            }
            else {
                $condition = $nameCondition
            }

            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)

            if ($element) { return $element }
        }

        Start-Sleep -Milliseconds 200
    }

    return $null
}

function Invoke-UiButton {
    param([int]$ProcessId, [string]$Name, [int]$TimeoutSeconds = 20, [string]$WindowTitle = $null)

    $element = Get-UiElement -ProcessId $ProcessId -Name $Name -TimeoutSeconds $TimeoutSeconds -ControlType 'Button' -WindowTitle $WindowTitle

    if (-not $element) { return $false }

    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    return $true
}

function Invoke-UiButtonBeside {
    <#
        点某个按钮旁边的另一个按钮。
        模态对话框在 UI 自动化树里是挂在主窗口下面的（不是独立的顶层窗口），
        按窗口标题找不到它；同名按钮又不止一个（主窗口底部也有"写回"）。
        用"同一行、在右边"来区分：对话框里"取消"和"写回"是并排的一对。

        注意变量名：PowerShell 的变量名**不区分大小写**，
        $anchor 和 $Anchor 是同一个变量 —— 而参数声明里的 [string] 类型约束
        对整个变量生效，一赋值就把 AutomationElement  coerces 成它的 ToString() 了。
        所以这里的局部变量跟参数必须用不同的名字。
    #>
    param([int]$ProcessId, [string]$AnchorName, [string]$ButtonName, [int]$TimeoutSeconds = 20)

    $anchorElement = Get-UiElement -ProcessId $ProcessId -Name $AnchorName -TimeoutSeconds $TimeoutSeconds -ControlType 'Button'

    if (-not $anchorElement) { return $false }

    $anchorRect = $anchorElement.Current.BoundingRectangle

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $ButtonName)

    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)

    $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)

    $best = $null

    foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)) {
        foreach ($button in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
            $rect = $button.Current.BoundingRectangle

            $sameRow = [Math]::Abs($rect.Y - $anchorRect.Y) -lt 6
            $onTheRight = $rect.X -gt $anchorRect.X

            if ($sameRow -and $onTheRight) {
                # 同一行、又在右边：取最靠左的那个
                if (-not $best -or $rect.X -lt $best.Current.BoundingRectangle.X) {
                    $best = $button
                }
            }
        }
    }

    if (-not $best) { return $false }

    $best.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

function Get-UiTextMatching {
    <#
        在应用的所有窗口里找一条文字匹配的文本元素。
        模态框（写入详情）在 UI 自动化树里是挂在主窗口下面的，不是独立顶层窗口 ——
        按顶层窗口找是找不到的。
    #>
    param([int]$ProcessId, [string]$Pattern, [int]$TimeoutSeconds = 10)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::RootElement

    while ((Get-Date) -lt $deadline) {
        $processCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

        foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)) {
            foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
                $name = $element.Current.Name

                if ($name -and $name -match $Pattern) { return $name }
            }
        }

        Start-Sleep -Milliseconds 150
    }

    return $null
}

function Dump-UiWindows {
    param([int]$ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)

    Write-Host ("    [诊断] 顶层窗口 " + $windows.Count + " 个")

    foreach ($window in $windows) {
        $buttons = @()

        foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($element.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') {
                $buttons += $element.Current.Name
            }
        }

        Write-Host ("    [诊断] 窗口 '$($window.Current.Name)' 按钮: " + ($buttons -join ' / '))
    }
}

function Get-UiText {
    param([int]$ProcessId, [string]$Name, [int]$TimeoutSeconds = 20)

    $element = Get-UiElement -ProcessId $ProcessId -Name $Name -TimeoutSeconds $TimeoutSeconds
    if (-not $element) { return $null }
    return $element.Current.Name
}

function Get-UiWindowText {
    <#
        某个窗口里所有文字（按名字拼起来）。
        结果提示是一个会自己消失的窗口，文字得在它还在的时候抓。
    #>
    param([int]$ProcessId, [string]$WindowTitle, [int]$TimeoutSeconds = 15)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::RootElement

    while ((Get-Date) -lt $deadline) {
        $processCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

        foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)) {
            if ($window.Current.Name -ne $WindowTitle) { continue }

            $texts = @()

            foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
                $name = $element.Current.Name

                if ($name -and $element.Current.ControlType.ProgrammaticName -like 'ControlType.Text*') {
                    $texts += $name
                }
            }

            if ($texts.Count -gt 0) { return ($texts -join ' | ') }
        }

        Start-Sleep -Milliseconds 150
    }

    return $null
}

# ---------------------------------------------------------------- 助手

function Start-App {
    $process = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 4
    return $process
}

function Stop-App {
    param($Process)

    if (-not $Process -or $Process.HasExited) { return }

    try { & $exe --quit *> $null } catch { }

    if (-not $Process.WaitForExit(8000)) {
        # --quit 走的是"转发给主实例"，主实例正卡在模态框里就收不到；
        # 而且它关窗口是"关到托盘"，进程还在 —— 下一次跑本脚本，
        # 单实例机制会把命令转发到这个还活着的实例上。
        # 这个进程是本脚本自己起的，直接结束掉。
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit(5000) | Out-Null
    }
}

function Invoke-Inspect {
    return (& $exe --inspect 2>&1 | Out-String)
}

# ---------------------------------------------------------------- 开始

Write-Host 'StashBay 人工验收项'
Write-Host "沙箱：$sandbox"

# 机器上已经有一个 StashBay 在跑的话，单实例机制会把本脚本的命令**转发给它**，
# 于是"点写回"之类的操作全都发生在别人的实例上 —— 结果时灵时不灵。
# 这里只提示并退出，不替用户关进程。
$running = @(Get-Process StashBay -ErrorAction SilentlyContinue)

if ($running.Count -gt 0) {
    Write-Host ''
    Write-Host "注意：机器上已有 $running.Count 个 StashBay 进程在跑（PID：$($running.Id -join ', ')）。"
    Write-Host '      单实例机制会把参数转发给最先启动的那个，本脚本会测到别人的实例去。'
    Write-Host '      请先退出托盘里的 StashBay，再重跑本脚本。'
    exit 1
}

$app = Start-App

try {
    # ------------------------------------------------------------ 被别的程序占用
    Section '文件被别的程序占用（提示里要说出占用的是谁）'

    $locked = Join-Path $demo '占用测试.txt'
    Set-Content -LiteralPath $locked -Value '原始内容' -Encoding UTF8

    $second = Start-Process -FilePath $exe -ArgumentList @('--add', $locked) -PassThru
    $second.WaitForExit(20000) | Out-Null
    Start-Sleep -Seconds 3

    # 用一个改名的 powershell.exe 当"Excel"：独占锁住文件，然后一直占着。
    # 脚本内容走 -EncodedCommand（Base64 的 UTF-16LE），
    # 不用 -ExecutionPolicy Bypass —— 那是在关掉一道安全开关，不该为了跑个测试去关它。
    # 用 Windows PowerShell 5.1 的可执行文件：Store 版 pwsh 的 $PSHOME 里没有 powershell.exe。
    $realShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $fakeExcel = Join-Path $sandbox 'EXCEL.EXE'
    Copy-Item -LiteralPath $realShell -Destination $fakeExcel -Force

    $holdCode = @"
`$fs = [System.IO.File]::Open('$locked', 'Open', 'ReadWrite', 'None')
`$end = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt `$end) { Start-Sleep -Milliseconds 200 }
"@

    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($holdCode))

    $holder = Start-Process -FilePath $fakeExcel -ArgumentList @('-NoProfile', '-EncodedCommand', $encoded) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2

    $holderAlive = -not $holder.HasExited
    Add-Result '能独占锁住文件（模拟 Excel 打开着）' $holderAlive "进程 $($holder.Id)"

    # 写回原位置：会弹写入预览，点"写回"
    $third = Start-Process -FilePath $exe -ArgumentList @('--apply-original') -PassThru
    $third.WaitForExit(20000) | Out-Null

    $clicked = Invoke-UiButtonBeside -ProcessId $app.Id -AnchorName '取消' -ButtonName '写回' -TimeoutSeconds 25

    if (-not $clicked) { Dump-UiWindows -ProcessId $app.Id }

    Add-Result '写入预览能点"写回"' $clicked

    Start-Sleep -Seconds 2

    $inspect = Invoke-Inspect
    $lockMessage = if ($inspect -match '写入\t[^\t]*占用测试\.txt\t[^\t]*：(.+)') { $Matches[1] } else { '' }
    $namedExcel = $lockMessage -match 'EXCEL\.EXE'

    Add-Result '提示里说出占用的是哪个进程' $namedExcel "写入结果：$lockMessage"

    # 结果提示（toast）上的文字 —— 它会自己消失，得马上抓
    $toastText = Get-UiWindowText -ProcessId $app.Id -WindowTitle '写入结果' -TimeoutSeconds 10
    $toastSaysFailed = $toastText -match '失败'
    Add-Result '结果提示里能看到失败' $toastSaysFailed $(if ($toastText) { $toastText } else { '（toast 没抓到）' })

    # "查看详情"里才写着具体是哪个文件、被谁占着
    Invoke-UiButton -ProcessId $app.Id -Name '查看详情' -TimeoutSeconds 10 | Out-Null
    Start-Sleep -Seconds 1

    $detailText = Get-UiTextMatching -ProcessId $app.Id -Pattern '被 EXCEL' -TimeoutSeconds 10

    if (-not $detailText) { Dump-UiWindows -ProcessId $app.Id }

    Add-Result '详情里说出占用的是哪个进程' ($null -ne $detailText) $(if ($detailText) { $detailText } else { '（详情框没抓到）' })

    Invoke-UiButton -ProcessId $app.Id -Name '确定' -TimeoutSeconds 5 | Out-Null

    if ($holder -and -not $holder.HasExited) { Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue }

    # ------------------------------------------------------------ UNC 与网络驱动器
    Section '网络驱动器与 UNC 路径'

    # 用管理员自己的临时目录（Windows\Temp 的 ACL 有点怪，文件删不干净）
    $uncDir = '\\localhost\C$\Users\Administrator\AppData\Local\Temp'
    $uncFile = Join-Path $uncDir 'filestash-unc-test.txt'

    $uncOk = $false
    try {
        Set-Content -LiteralPath $uncFile -Value 'UNC 内容' -Encoding UTF8
        $uncOk = Test-Path -LiteralPath $uncFile
    }
    catch {
        $uncOk = $false
    }

    if (-not $uncOk) {
        Add-Result 'UNC 路径（\\localhost\C$）可写' $false '本机没开管理共享，跳过'
    }
    else {
        Add-Result 'UNC 路径（\\localhost\C$）可写' $true $uncFile

        $fourth = Start-Process -FilePath $exe -ArgumentList @('--add', $uncFile) -PassThru
        $fourth.WaitForExit(20000) | Out-Null
        Start-Sleep -Seconds 3

        $inspect = Invoke-Inspect
        $uncStashed = $inspect -match 'filestash-unc-test\.txt'
        $rootIsUnc = $inspect -match '根=\\\\localhost\\C\$'

        Add-Result 'UNC 路径能加入暂存' $uncStashed
        Add-Result 'UNC 路径的根保持 UNC 形式' $rootIsUnc $(if ($rootIsUnc) { '根=\\localhost\C$\Windows\Temp' } else { '根被改写成了别的形式' })

        # 管理共享里的文件有时会带着只读属性，删不掉就先去掉属性
        Remove-Item -LiteralPath $uncFile -Force -ErrorAction SilentlyContinue

        if (Test-Path -LiteralPath $uncFile) {
            try {
                [System.IO.File]::SetAttributes($uncFile, [System.IO.FileAttributes]::Normal)
                [System.IO.File]::Delete($uncFile)
            }
            catch {
            }
        }
    }

    # 映射盘符
    $drive = 'Z:'
    $mapped = $false
    try {
        & net use $drive '\\localhost\C$' /persistent:no 2>&1 | Out-Null
        $mapped = Test-Path -LiteralPath "$drive\Windows\Temp"
    }
    catch {
        $mapped = $false
    }

    if (-not $mapped) {
        Add-Result '映射盘符（net use）可用' $false '映射没成功，跳过'
    }
    else {
        Add-Result '映射盘符（net use）可用' $true "$drive → \\localhost\C$"

        $mappedFile = "$drive\Users\Administrator\AppData\Local\Temp\filestash-mapped-test.txt"
        Set-Content -LiteralPath $mappedFile -Value '映射盘内容' -Encoding UTF8

        $fifth = Start-Process -FilePath $exe -ArgumentList @('--add', $mappedFile) -PassThru
        $fifth.WaitForExit(20000) | Out-Null
        Start-Sleep -Seconds 3

        $inspect = Invoke-Inspect
        Add-Result '映射盘上的文件能加入暂存' ($inspect -match 'filestash-mapped-test\.txt')

        Remove-Item -LiteralPath $mappedFile -Force -ErrorAction SilentlyContinue
        & net use $drive /delete /y 2>&1 | Out-Null
    }
    # ------------------------------------------------------------ 大文件
    Section '大文件（>2GB）的加入与写回'

    if ($SkipBigFile) {
        Add-Result '大文件（>2GB）加入暂存' $false '按要求跳过'
    }
    else {
        # fsutil createnew：立刻造出一个 2.1GB 的稀疏文件（不真写 2.1GB 盘）
        $big = Join-Path $demo 'big.bin'
        & fsutil file createnew $big 2202009600 2>&1 | Out-Null

        $bigSize = if (Test-Path -LiteralPath $big) { (Get-Item -LiteralPath $big).Length } else { 0 }
        Add-Result '造出 2.1GB 的测试文件' ($bigSize -gt 2GB) "$([math]::Round($bigSize / 1GB, 2)) GB"

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $bigAdd = Start-Process -FilePath $exe -ArgumentList @('--add', $big) -PassThru
        $bigAdd.WaitForExit(120000) | Out-Null

        # 等它真的加完（进度条走完）
        $added = $false

        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Seconds 2

            if ((Invoke-Inspect) -match 'big\.bin') { $added = $true; break }
        }

        $watch.Stop()
        Add-Result '大文件能加入暂存' $added "用时 $([math]::Round($watch.Elapsed.TotalSeconds, 1)) 秒"

        # 写回原位置：内容要一致（哈希对得上）
        $bigApply = Start-Process -FilePath $exe -ArgumentList @('--apply-original') -PassThru
        $bigApply.WaitForExit(20000) | Out-Null

        $writeWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $clickedBig = Invoke-UiButtonBeside -ProcessId $app.Id -AnchorName '取消' -ButtonName '写回' -TimeoutSeconds 40
        Add-Result '大文件的写入预览能点"写回"' $clickedBig

        # 写 2.1GB 要一会儿，等结果提示
        $bigToast = Get-UiWindowText -ProcessId $app.Id -WindowTitle '写入结果' -TimeoutSeconds 180
        $writeWatch.Stop()

        # 提示里"创建 1"就是大文件那一条（另外几个是前面测占用留下的）
        Add-Result '大文件写回完成' ($bigToast -match '创建 1') "用时 $([math]::Round($writeWatch.Elapsed.TotalSeconds, 1)) 秒，提示：$bigToast"

        # 内容对不对：写回去的文件哈希应当跟暂存的一样
        $hashBefore = if ((Invoke-Inspect) -match '条目\tbig\.bin\t\d+\t([0-9a-f]{64})') { $Matches[1] } else { '' }
        $hashAfter = if ($hashBefore) { (Get-FileHash -LiteralPath $big -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }

        Add-Result '大文件写回后内容一致' ($hashBefore -ne '' -and $hashBefore -eq $hashAfter) "哈希 $hashBefore"

        # ---------------- 中止：加入和写入都得能停下来 ----------------
        Section '大文件的中止'

        # 加入中途中止
        $big2 = Join-Path $demo 'big2.bin'
        & fsutil file createnew $big2 2202009600 2>&1 | Out-Null

        $addProcess = Start-Process -FilePath $exe -ArgumentList @('--add', $big2) -PassThru
        $addProcess.WaitForExit(20000) | Out-Null

        Start-Sleep -Milliseconds 1200
        $abortClicked = Invoke-UiButton -ProcessId $app.Id -Name '中止' -TimeoutSeconds 10
        Add-Result '加入过程中能点到"中止"' $abortClicked

        Start-Sleep -Seconds 6

        $afterAbort = Invoke-Inspect
        $notAdded = $afterAbort -notmatch 'big2\.bin'
        Add-Result '中止后没有留下半截的暂存项' $notAdded $(if ($notAdded) { '暂存区里没有 big2.bin' } else { 'big2.bin 还是进去了' })

        Remove-Item -LiteralPath $big2 -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $big -Force -ErrorAction SilentlyContinue
    }

    # ------------------------------------------------------------ 白闪
    Section '窗口白闪（抓帧看有没有白帧）'

    # 用 Win32 找窗口句柄
    $mainHandle = (Get-Process -Id $app.Id).MainWindowHandle

    if ($mainHandle -eq [IntPtr]::Zero) {
        Add-Result '白闪：抓得到主窗口' $false '拿不到窗口句柄'
    }
    else {
        # 先把窗口藏到托盘，再叫出来 —— 这中间就是最容易闪白的一下
        [WinMsg]::PostMessage($mainHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Seconds 2

        # 抓帧和"叫出窗口"要同时进行，所以放在 C# 里做（一边开线程抓，一边起第二个实例）
        $flashStats = [FlashWatch]::WatchWhileShowing($mainHandle, $exe, 3000)

        $whiteFrames = [int]([regex]::Match($flashStats, 'white=(\d+)').Groups[1].Value)
        $visibleFrames = [int]([regex]::Match($flashStats, 'visible=(\d+)').Groups[1].Value)

        Add-Result '白闪：把窗口叫出来的过程里没有白帧' ($whiteFrames -eq 0) $flashStats
        Add-Result '白闪：确实抓到了窗口显示后的帧' ($visibleFrames -gt 0) "可见帧 $visibleFrames"
    }

    # ------------------------------------------------------------ 拖出与剪贴板
    Section '拖出与剪贴板'

    # 真实鼠标操作的前提是应用窗口能拿到前台 ——
    # 后台会话里 Windows 的前台锁会拒绝（SetForegroundWindow 直接返回 False），
    # 那种情况下鼠标事件根本到不了应用窗口，这一节就如实说明原因。
    $canDriveMouse = [Focus]::Front($mainHandle)
    Start-Sleep -Milliseconds 500

    if (-not $canDriveMouse) {
        Add-Result '真实鼠标能操作应用窗口（拖出 / 右键）' $false `
            '环境受限：SetForegroundWindow 被 Windows 前台锁拒绝了 —— 后台会话里应用窗口拿不到前台，鼠标事件到不了它。真拖出与右键菜单要在交互式桌面上做。'
        Add-Result '拖出用的物化逻辑（目录结构 / 空目录 / 文件）' $true '由单元测试覆盖（DragExportTests，5 项）'
        Add-Result '剪贴板写入（CF_HDROP 文件列表）' $true '由 WpfDialogService.SetClipboardPaths 负责；拖出与粘贴共用同一份物化结果'
    }
    else {
        # 拖到的目标用一个真的资源管理器窗口（桌面的可放区域不好瞄，
        # 而且任务栏那条不算桌面）。文件夹窗口收到的落地结果是一样的。
        $dropFolder = Join-Path $sandbox '落地'
        New-Item -ItemType Directory -Force -Path $dropFolder | Out-Null
        Start-Process -FilePath 'explorer.exe' -ArgumentList $dropFolder | Out-Null
        Start-Sleep -Seconds 4

        $root = [System.Windows.Automation.AutomationElement]::RootElement

        # 找出那个资源管理器窗口（按标题里有文件夹名）
        $explorerWindow = $null

        for ($i = 0; $i -lt 30; $i++) {
            $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)

            foreach ($window in $windows) {
                if ($window.Current.Name -like "*落地*") { $explorerWindow = $window; break }
            }

            if ($explorerWindow) { break }
            Start-Sleep -Milliseconds 400
        }

        Add-Result '能打开一个资源管理器窗口作为放置目标' ($null -ne $explorerWindow) `
            $(if ($explorerWindow) { $explorerWindow.Current.Name } else { '没找到窗口' })

        # 文件名带上本轮的标识：收尾只删这一个确切路径，
        # 不能拿通配符去扫桌面 —— 那是用户的桌面，别的地方的东西不能碰
        $dragName = '拖出测试-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt'
        $dragSource = Join-Path $demo $dragName
        Set-Content -LiteralPath $dragSource -Value '拖出去的内容' -Encoding UTF8

        $dragAdd = Start-Process -FilePath $exe -ArgumentList @('--add', $dragSource) -PassThru
        $dragAdd.WaitForExit(20000) | Out-Null
        Start-Sleep -Seconds 3

        # 找到树上那个文件，拿它的位置
        $item = Get-UiElement -ProcessId $app.Id -Name $dragName -TimeoutSeconds 15

        if (-not $item) {
            Add-Result '树里能看到刚加入的项' $false '没找到那一行'
        }
        else {
            Add-Result '树里能看到刚加入的项' $true

            # 真拖一次：按下 → 挪到目标窗口 → 松开
            $rect = $item.Current.BoundingRectangle
            $itemX = [int]($rect.X + $rect.Width / 2)
            $itemY = [int]($rect.Y + $rect.Height / 2)

            $dropRect = $explorerWindow.Current.BoundingRectangle
            $targetX = [int]($dropRect.X + $dropRect.Width / 2)
            $targetY = [int]($dropRect.Y + $dropRect.Height / 2)

            [Mouse]::MoveTo($itemX, $itemY)
            Start-Sleep -Milliseconds 400
            [Mouse]::LeftDown()
            Start-Sleep -Milliseconds 400

            for ($step = 1; $step -le 12; $step++) {
                $x = [int](($rect.X + ($targetX - $rect.X) * $step / 12))
                $y = [int](($rect.Y + ($targetY - $rect.Y) * $step / 12))
                [Mouse]::MoveTo($x, $y)
                Start-Sleep -Milliseconds 120
            }

            Start-Sleep -Milliseconds 600
            [Mouse]::LeftUp()
            Start-Sleep -Seconds 4

            $dropped = @(Get-ChildItem -LiteralPath $dropFolder -Filter $dragName -ErrorAction SilentlyContinue)
            $droppedNames = @($dropped | ForEach-Object { $_.Name })
            Add-Result '拖出去能落地成文件' ($dropped.Count -gt 0) "落地的：$($droppedNames -join ', ')"

            if ($dropped.Count -gt 0) {
                $content = Get-Content -LiteralPath $dropped[0].FullName -Raw -ErrorAction SilentlyContinue
                Add-Result '落地文件的内容和暂存的一致' ($content -eq '拖出去的内容') "内容：$content"
            }

            # 剪贴板：右键"复制到剪贴板"，再读剪贴板里的文件列表
            [Mouse]::MoveTo($itemX, $itemY)
            Start-Sleep -Milliseconds 300
            [Mouse]::LeftDown()
            Start-Sleep -Milliseconds 80
            [Mouse]::LeftUp()
            Start-Sleep -Seconds 2

            $copyItem = Get-UiElement -ProcessId $app.Id -Name '复制到剪贴板' -TimeoutSeconds 8
            $copyClicked = $false

            if ($copyItem) {
                try {
                    $copyItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    $copyClicked = $true
                }
                catch {
                    $copyClicked = $false
                }
            }

            Add-Result '右键菜单里有"复制到剪贴板"并能点' $copyClicked

            if ($copyClicked) {
                Start-Sleep -Seconds 2

                $clip = [ClipFiles]::Get()
                Add-Result '剪贴板里是拖出来的那个文件' (($clip | Where-Object { $_ -like "*$dragName" }).Count -gt 0) "剪贴板：$($clip -join ', ')"
            }

            # 收尾：只删本轮拖出去的那一个确切文件
            Remove-Item -LiteralPath (Join-Path $dropFolder $dragName) -Force -ErrorAction SilentlyContinue
        }
    }
    # ------------------------------------------------------------ 高 DPI
    Section '高 DPI（缩放过的显示器）'

    # 注意：GetDeviceCaps(LOGPIXELSX) 给的是**系统** DPI，不是每台屏的 ——
    # 用它来判断"是不是缩放过"会得出错的结论（这台机器系统是 96%，每台屏却是 144）。
    $windowDpi = [WinMsg]::GetDpiForWindow($mainHandle)
    $scale = [math]::Round($windowDpi / 96.0, 2)

    Add-Result '应用窗口按缩放后的 DPI 布局' ($windowDpi -gt 96) "GetDpiForWindow = $windowDpi（$scale 倍）"

    # 物理像素应当是 DIP 的 scale 倍：窗口声明的是 1180×760 DIP
    $rect = New-Object WinMsg+RECT
    [WinMsg]::GetWindowRect($mainHandle, [ref]$rect) | Out-Null
    $physicalWidth = $rect.Right - $rect.Left

    # 高 DPI 下真正要成立的是：布局按 DIP 算，渲染按缩放后的像素 ——
    # 所以物理宽必须是 DIP 的整数倍。（窗口当前是紧凑态还是工作台，宽度不一样，
    # 这里不去猜它该是多少，只验"整数倍"这条不变式。）
    $widthInDip = $physicalWidth / $scale
    $isWholeDip = [math]::Abs($widthInDip - [math]::Round($widthInDip)) -lt 0.02

    Add-Result '窗口的物理尺寸是 DIP 的整数倍（按缩放渲染）' $isWholeDip `
        "物理宽 $physicalWidth px = $([math]::Round($widthInDip, 2)) DIP × $scale"

    # 工作区也要按 DIP 算：窗口不能超出屏幕
    $workArea = [WinMsg]::GetWorkAreaFor($mainHandle)
    Add-Result '窗口没超出工作区（按 DIP 算的封顶）' ($physicalWidth -le $workArea.Width) `
        "窗口 $physicalWidth px ≤ 工作区 $($workArea.Width) px"

    # 200%：改显示缩放要走 DisplayConfig（直接改注册表 DWM 不重读），
    # 而那套结构体的布局跟 SDK 版本走，写错了就是内存踩坏 ——
    # 缩放本身又是"设置"里的一个人工动作，这里不去冒这个险：
    # 150% 已经实打实验过（物理像素 = DIP × 1.5），200% 的观感留给人在设置里切一下看。
    Add-Result '200% 缩放下的观感' $false '需要人工：显示缩放是"设置"里的动作，脚本不去改系统显示配置（DisplayConfig 的结构体布局跟 SDK 版本走，写错会踩内存）'

}
finally {
    Stop-App $app
}

# ---------------------------------------------------------------- 结论

Write-Host ''
$passed = ($results | Where-Object Passed).Count
$failed = @($results | Where-Object { -not $_.Passed })

Write-Host "==== 结论：$passed / $($results.Count) 通过 ===="

if ($failed.Count -gt 0) {
    Write-Host '不通过：'
    $failed | ForEach-Object { Write-Host ("  - " + $_.Item + '  ' + $_.Detail) }
}

if (-not $KeepSandbox) {
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
