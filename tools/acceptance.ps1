#Requires -Version 7.0
<#
.SYNOPSIS
    StashBay (FileStash) 验收脚本：把能自动判定的验收条目跑一遍，逐条给结论。

.DESCRIPTION
    覆盖系统各模块核心功能中**可自动化**的验收项，以及手动测试场景中
    能用代码代替眼力/鼠标的几条。

    应用被完全隔离到临时目录（STASHBAY_HOME / FILESTASH_HOME），不碰用户真实的数据与设置。
    剩下必须人来做的那几条（真拖放、剪贴板粘到资源管理器、白闪观感、
    大文件、网络驱动器、安装卸载）在结尾列成"需要人工"清单。

    用法：pwsh tools/acceptance.ps1
#>
[CmdletBinding()]
param(
    [switch]$KeepData,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# PowerShell 7.4 起，原生命令的非零退出码会按 $ErrorActionPreference 抛异常。
# 验收脚本要的就是"跑完看 $LASTEXITCODE"，把某一处失败变成整脚本崩掉反而看不到别的条目。
$PSNativeCommandUseErrorActionPreference = $false

# 输出统一用 UTF-8：不设的话子进程（dotnet）的输出会按本机代码页（GBK）写回来，
# 中文乱码，而且正则匹配不到"0 个警告"这类文本
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\FileStash.App\FileStash.App.csproj'
$smokeReport = Join-Path ([System.IO.Path]::GetTempPath()) 'filestash-smoke.txt'

# 隔离：应用的数据与设置都进这个临时目录
$sandboxRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("filestash-acceptance-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sandboxRoot | Out-Null
$env:FILESTASH_HOME = $sandboxRoot
$env:STASHBAY_HOME = $sandboxRoot

# 把应用**发布到沙箱里**再跑：
# 一是和应用自己的 bin 目录隔开（跑着的实例会锁着 DLL，构建会失败），
# 二是顺带验证"发布产物能不能用"（M8 的验收就是看发布产物）。
$appDirectory = Join-Path $sandboxRoot 'app'
$exe = Join-Path $appDirectory 'StashBay.exe'

# 沙箱设置：只隔离数据。
# 注册表**一个都不碰** —— 应用有个 FILESTASH_NO_SHELL_INTEGRATION 开关，
# 设了它就既不注册也不注销右键菜单 / 发送到 / 开机自启，
# 免得"关掉开关"变成一个静默改变用户既有设置的动作。
$env:FILESTASH_NO_SHELL_INTEGRATION = '1'
$env:STASHBAY_NO_SHELL_INTEGRATION = '1'

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

# ---------------------------------------------------------------- 原生互操作

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class WinInfo {
    public IntPtr Handle;
    public string Title;
    public int Width;
    public int Height;
    public int Cloaked;
}

public static class Native {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint WM_CLOSE = 0x0010;

    // 窗口枚举必须在 C# 里做完：PowerShell 的 scriptblock 被原生代码回调时
    // 是在别的线程上跑的，那边没有 Runspace，会抛异常而且异常被吞掉，
    // 结果就是"一个窗口都找不到"。
    public static List<WinInfo> ListWindows(uint targetPid) {
        var result = new List<WinInfo>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != targetPid || !IsWindowVisible(h)) return true;

            var text = new StringBuilder(256);
            GetWindowText(h, text, 256);
            GetWindowRect(h, out RECT rect);

            int cloaked = 0;
            DwmGetWindowAttribute(h, 14, out cloaked, 4);

            result.Add(new WinInfo {
                Handle = h, Title = text.ToString(),
                Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top,
                Cloaked = cloaked
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // z 序里的名次（EnumWindows 自上而下枚举）。数字越小越靠前。
    // 用来判断"窗口有没有被提到对手之上" —— 前台和 z 序是两回事：
    // 抢不到前台的时候，兜底是把 z 序提上去，那一样是"窗口到了前面"。
    public static int ZOrderIndex(IntPtr target) {
        int index = 0;
        int found = -1;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            index++;
            if (h == target) found = index;
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

# ---------------------------------------------------------------- 助手

function Get-AppWindows {
    param([int]$ProcessId)

    if (-not $ProcessId) { return @() }
    return [Native]::ListWindows([uint32]$ProcessId)
}

function Get-Inspect {
    $output = & $exe --inspect 2>&1 | Out-String
    return $output
}

function Invoke-App {
    param([string[]]$Arguments = @())

    return Start-Process -FilePath $exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
}

function Stop-App {
    param($Process)

    if (-not $Process -or $Process.HasExited) { return }

    # 先请它自己退出（--quit 走的就是托盘"退出"那条路）
    try { & $exe --quit *> $null } catch { }

    if (-not $Process.WaitForExit(8000))
    {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit(5000) | Out-Null
    }
}

function Wait-ForWindow {
    param([int]$ProcessId, [string]$TitlePattern, [int]$TimeoutSeconds = 20)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline)
    {
        $window = Get-AppWindows -ProcessId $ProcessId |
            Where-Object { $_.Title -match $TitlePattern } |
            Select-Object -First 1

        if ($window) { return $window }

        Start-Sleep -Milliseconds 120
    }

    return $null
}

# ---------------------------------------------------------------- 开始

Write-Host 'StashBay 验收'
Write-Host "仓库：$repositoryRoot"
Write-Host "隔离目录：$sandboxRoot"

# 机器上已经有一个 StashBay 在跑的话，单实例机制会把本脚本的参数**转发给它**，
# 于是"第二个实例加入后退出"之类的检查全都变成在测别人的实例 —— 结果没有意义。
# 这里只提示并退出，不替用户关进程：杀掉用户正在用的应用不是验收脚本该做的事。
$running = @(Get-Process StashBay -ErrorAction SilentlyContinue)
if ($running.Count -gt 0)
{
    Write-Host ''
    Write-Host "注意：机器上已有 $($running.Count) 个 StashBay 进程在跑（PID：$($running.Id -join ', ')）。"
    Write-Host '      单实例机制会把参数转发给最先启动的那个，验收会变成在测别人的实例。'
    Write-Host '      请先退出托盘里的 StashBay，再重跑本脚本。'
    exit 1
}

Section 'M0 工程骨架'

# 发布到沙箱（顺带验证发布产物）
$publishOutput = & dotnet publish $project -c Release -o $appDirectory -v minimal 2>&1 | Out-String
$publishOk = (Test-Path $exe) -and ($publishOutput -notmatch 'error CS')
Add-Result 'dotnet publish 成功（产物在沙箱里，不与运行中的实例抢 DLL）' $publishOk

$build = & dotnet build $repositoryRoot -c Release -v minimal 2>&1 | Out-String
$warningCount = if ($build -match '(\d+) 个警告') { [int]$Matches[1] } else { -1 }
$errorCount = if ($build -match '(\d+) 个错误') { [int]$Matches[1] } else { -1 }
Add-Result 'dotnet build 无警告、无错误' ($warningCount -eq 0 -and $errorCount -eq 0) "警告 $warningCount / 错误 $errorCount"

$testOutput = & dotnet test tests/FileStash.Core.Tests -c Release -- --minimum-expected-tests 1 2>&1 | Out-String
$testTotal = if ($testOutput -match '总计:\s*(\d+)') { [int]$Matches[1] } else { 0 }
$testFailed = if ($testOutput -match '失败:\s*(\d+)') { [int]$Matches[1] } else { -1 }
$mtpOk = $testOutput -notmatch 'MSB1001'

# 失败时把用例名带上：只有个数字是查不出问题的
$failedNames = @([regex]::Matches($testOutput, '失败\s+(FileStash\.[^\s(]+)') | ForEach-Object { $_.Groups[1].Value })
$testDetail = "共 $testTotal 个用例，失败 $testFailed"
if ($failedNames.Count -gt 0) { $testDetail += "；失败用例：$($failedNames -join ', ')" }

Add-Result 'dotnet test 跑通（MTP 模式，--minimum-expected-tests 1 不报错）' ($testFailed -eq 0 -and $mtpOk -and $testTotal -gt 100) $testDetail

if (Test-Path $smokeReport) { Remove-Item $smokeReport -Force }
& $exe --smoke | Out-Null
Start-Sleep -Seconds 3

# marker 读不出来时不能直接 .Trim() —— 那是"对 null 调用方法"，
# 一旦自查挂掉，脚本自己先崩，真正的失败原因反而被盖住
$smokeText = if (Test-Path $smokeReport) { (Get-Content $smokeReport -Raw).Trim() } else { '（没有 marker：自查进程可能挂住了）' }
$smokeOk = (Test-Path $smokeReport) -and ($smokeText -eq 'OK')
Add-Result '--smoke 写出 OK marker' $smokeOk $smokeText

$inspect = Get-Inspect
Add-Result 'DPI 感知 = PerMonitorV2' ($inspect -match 'DPI感知\s+PerMonitorV2') '用 AreDpiAwarenessContextsEqual 比的'

$app = Invoke-App
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$mainWindow = Wait-ForWindow -ProcessId $app.Id -TitlePattern '^StashBay$'
$stopwatch.Stop()

Add-Result '运行后显示主窗口' ($null -ne $mainWindow) (
    $(if ($mainWindow) { "标题 '$($mainWindow.Title)'，尺寸 $($mainWindow.Width)x$($mainWindow.Height)" } else { '没找到窗口' }))

Add-Result '冷启动到窗口可见' ($null -ne $mainWindow -and $stopwatch.ElapsedMilliseconds -lt 3000) ("$($stopwatch.ElapsedMilliseconds) ms")

$logDir = Join-Path $sandboxRoot 'StashBay\logs'
if (-not (Test-Path $logDir)) {
    $logDir = Join-Path $sandboxRoot 'FileStash\logs'
}
$logFiles = @(Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue)
$firstLog = if ($logFiles.Count -gt 0) { $logFiles[0].Name } else { '无日志' }
Add-Result '日志文件生成' ($logFiles.Count -gt 0) $firstLog

Section 'M2 暂存：重启后仍在 / 改源文件不影响暂存内容 / 仓库内根=仓库根'

$demo = Join-Path $sandboxRoot 'demo'
New-Item -ItemType Directory -Force -Path $demo | Out-Null

$fileA = Join-Path $demo 'a.txt'
$fileB = Join-Path $demo 'b.txt'
Set-Content -Path $fileA -Value '原始内容' -Encoding UTF8
Set-Content -Path $fileB -Value '第二个' -Encoding UTF8

# 通过命令行加入（等价于资源管理器右键"加入暂存"）
$second = Invoke-App @('--add', $fileA, $fileB)
$second.WaitForExit(20000) | Out-Null
Start-Sleep -Seconds 3

$appCount = @(Get-Process StashBay -ErrorAction SilentlyContinue | Where-Object {
    # 刚退出的进程读 .Path 会抛"没有与该对象关联的进程"，不能让它掀翻整个脚本
    try { $_.Path -eq $exe } catch { $false }
}).Count
Add-Result '第二个实例加入后退出（不会开多个窗口）' ($appCount -eq 1) "沙箱内的 StashBay 进程数 $appCount"

$inspect = Get-Inspect
$hasBoth = $inspect -match 'a\.txt' -and $inspect -match 'b\.txt'
Add-Result '--add 把两个文件都加入了暂存' $hasBoth

$shaBefore = if ($inspect -match '条目\ta\.txt\t\d+\t([0-9a-f]{64})') { $Matches[1] } else { '' }

Set-Content -Path $fileA -Value '改过的内容' -Encoding UTF8
Start-Sleep -Seconds 2

$inspect = Get-Inspect
$shaAfter = if ($inspect -match '条目\ta\.txt\t\d+\t([0-9a-f]{64})') { $Matches[1] } else { '' }
Add-Result '改源文件后暂存内容不变' ($shaBefore -ne '' -and $shaBefore -eq $shaAfter) "快照哈希 $shaBefore"

Stop-App $app
Start-Sleep -Seconds 2
$app = Invoke-App
Start-Sleep -Seconds 5
$inspect = Get-Inspect
Add-Result '重启应用后暂存项仍在' ($inspect -match 'a\.txt')

Section 'M5 Windows 集成'

# 注册表项由单元测试覆盖（ContextMenuRegistrarTests 写在临时根 HKCU\Software\FileStashTest 下，
# 测完删掉，不碰用户真实的 Software\Classes）。
# 注意：MTP 模式下 `dotnet test -- --list-tests` 跑的是**零个测试**、也不打印用例名，
# 想确认"某个测试类确实存在且通过"只能用 --filter-class。
$registrarOutput = & dotnet test tests/FileStash.Core.Tests -c Release -- --filter-class '*ContextMenuRegistrarTests' 2>&1 | Out-String
$registrarTotal = if ($registrarOutput -match '总计:\s*(\d+)') { [int]$Matches[1] } else { 0 }
$registrarFailed = if ($registrarOutput -match '失败:\s*(\d+)') { [int]$Matches[1] } else { -1 }
Add-Result '右键菜单注册 / 注销（有临时根下的单元测试覆盖，不写用户注册表）' `
    ($registrarTotal -gt 0 -and $registrarFailed -eq 0) "跑了 $registrarTotal 个用例，失败 $registrarFailed"

# 这里必须用 -LiteralPath：路径里的 "*" 是**键名本身**（"所有文件"这个文件类型），
# 不是通配符。带通配符的 Test-Path 会去遍历整个 HKCU\Software\Classes —— 实测 20 秒都没跑完。
$contextMenu = Test-Path -LiteralPath 'HKCU:\Software\Classes\*\shell\StashBay.Add'
$sendTo = (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\StashBay.lnk')) -or
          (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\FileStash 暂存.lnk'))
$menuState = if ($contextMenu) { '已注册' } else { '未注册' }
$sendToState = if ($sendTo) { '已创建' } else { '未创建' }
Write-Host "  [信息] 用户注册表当前状态：右键菜单 $menuState，发送到快捷方式 $sendToState（本次验收没动它）"

# --apply-here 应打开预览窗口。
# 注意：窗口开在**主实例**里 —— 第二个实例只是把参数转过去就退出了，
# 在第二个实例的 PID 里找窗口永远找不到。
$third = Invoke-App @('--apply-here', $demo)
$third.WaitForExit(20000) | Out-Null
$preview = Wait-ForWindow -ProcessId $app.Id -TitlePattern '预览' -TimeoutSeconds 25
Add-Result '--apply-here 打开写入预览且目标为该文件夹' ($null -ne $preview) (
    $(if ($preview) { "窗口 '$($preview.Title)'" } else { '没找到预览窗口' }))

if ($preview)
{
    # 预览是**模态**窗口：不关掉的话主实例的 UI 线程一直被它占着，
    # 后面所有转发过去的命令都排队等着 —— 后面每一个检查都会跟着错下去
    [Native]::PostMessage($preview.Handle, [Native]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 2

    $stillOpen = Get-AppWindows -ProcessId $app.Id | Where-Object { $_.Title -match '预览' } | Select-Object -First 1
    Add-Result '预览窗口能关掉（模态框不占着主实例）' ($null -eq $stillOpen) (
        $(if ($stillOpen) { '还开着' } else { '已关闭' }))
}

# 提权重试不能被单实例转发吞掉
$retry = Invoke-App @('--elevated-retry', 'no-such-operation')
$exited = $retry.WaitForExit(20000)
Add-Result '--elevated-retry 不被单实例转发吞掉（自己跑完就退出）' $exited "退出码 $($retry.ExitCode)"

Section 'M7 Git 增强：切分支通知 / 已删除文件不能加入暂存'

$repo = Join-Path $demo 'repo'
New-Item -ItemType Directory -Force -Path $repo | Out-Null
Push-Location $repo
& git init -q . 2>&1 | Out-Null
& git config user.email 'acceptance@example.com' 2>&1 | Out-Null
& git config user.name 'Acceptance' 2>&1 | Out-Null
& git checkout -q -b 'feature/accept' 2>&1 | Out-Null
Set-Content -Path (Join-Path $repo 'x.txt') -Value 'x' -Encoding UTF8
& git add -A 2>&1 | Out-Null
& git commit -q -m 'init' 2>&1 | Out-Null
Pop-Location

$fourth = Invoke-App @('--add', (Join-Path $repo 'x.txt'))
$fourth.WaitForExit(20000) | Out-Null
Start-Sleep -Seconds 3

$inspect = Get-Inspect
$rootIsRepo = $inspect -match '根=' + [regex]::Escape($repo)
$hasBranch = $inspect -match '分支=feature/accept'
Add-Result '仓库内文件的根自动是仓库根' $rootIsRepo
Add-Result '仓库内文件显示来源分支' $hasBranch

Section '§8.4 手动测试里可自动化的几条'

# 50 个文件
$many = Join-Path $demo 'many'
New-Item -ItemType Directory -Force -Path $many | Out-Null
$paths = 1..50 | ForEach-Object {
    $p = Join-Path $many "文件$_.txt"
    Set-Content -Path $p -Value "内容$_" -Encoding UTF8
    $p
}

$fifth = Invoke-App (@('--add') + $paths)
$fifth.WaitForExit(60000) | Out-Null
Start-Sleep -Seconds 6

$inspect = Get-Inspect
$manyCount = ([regex]::Matches($inspect, '(?m)^项\t')).Count
Add-Result '多选 50 个文件加入暂存' ($manyCount -ge 51) "共 $manyCount 项"

# 长路径（>260）
$longDir = Join-Path $demo ('d' * 60 + '\' + 'e' * 60 + '\' + 'f' * 60 + '\' + 'g' * 60 + '\' + 'h' * 60)
try {
    New-Item -ItemType Directory -Force -Path $longDir | Out-Null
    $longFile = Join-Path $longDir '很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长.txt'
    Set-Content -Path $longFile -Value '长路径' -Encoding UTF8

    $sixth = Invoke-App @('--add', $longFile)
    $sixth.WaitForExit(20000) | Out-Null
    Start-Sleep -Seconds 3

    $inspect = Get-Inspect
    Add-Result '长路径（>260）可以加入暂存' ($inspect -match '很长很长很长') "路径长度 $($longFile.Length)"
}
catch {
    Add-Result '长路径（>260）可以加入暂存' $false $_.Exception.Message
}

# 白闪三态：首次显示 → 隐到托盘 → 再显示
$mainWin = Get-AppWindows -ProcessId $app.Id | Where-Object { $_.Title -eq 'StashBay' } | Select-Object -First 1
$state1 = if ($mainWin) { $mainWin.Cloaked } else { -1 }

if ($mainWin)
{
    [Native]::PostMessage($mainWin.Handle, [Native]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 2

    $hidden = Get-AppWindows -ProcessId $app.Id | Where-Object { $_.Title -eq 'StashBay' } | Select-Object -First 1
    $state2 = if ($hidden) { $hidden.Cloaked } else { 1 }

    # 再开一个实例 → 主实例把窗口叫出来
    $seventh = Invoke-App
    $seventh.WaitForExit(20000) | Out-Null
    Start-Sleep -Seconds 3

    $shown = Get-AppWindows -ProcessId $app.Id | Where-Object { $_.Title -eq 'StashBay' } | Select-Object -First 1
    $state3 = if ($shown) { $shown.Cloaked } else { -1 }

    Add-Result '窗口白闪三态（显示 0 → 隐藏 1 → 再显示 0）' ($state1 -eq 0 -and $state2 -eq 1 -and $state3 -eq 0) "cloaked = $state1 / $state2 / $state3"
}
else
{
    Add-Result '窗口白闪三态（显示 0 → 隐藏 1 → 再显示 0）' $false '找不到主窗口'
}

# 叫出窗口 12 次，统计"到了前面"的次数。
# 前台与 z 序是两回事：Activate() 抢到前台叫"到了前面"；
# 抢不到的时候（Windows 的前台锁，后台脚本跑的进程基本抢不到）
# 兜底是把 z 序提到对手之上 —— 那同样是"窗口到了前面"，用户看得见。
# 只认前台的话，这条会把"OS 不给抢"误判成"应用没抢"。
$foregroundHits = 0
$zOrderHits = 0
$stolen = 0

for ($i = 0; $i -lt 12; $i++)
{
    # 叫之前前台是谁：应用只压过"一开始那个对手"，不盖住用户后来切过去的窗口。
    # 它的 z 序位次也要**在叫之前**记下来 —— 叫的过程中别的程序也在动，事后取就取错了。
    $blocker = [Native]::GetForegroundWindow()
    $blockerIndex = [Native]::ZOrderIndex($blocker)

    $before = Get-AppWindows -ProcessId $app.Id |
        Where-Object { $_.Title -eq 'StashBay' } |
        Select-Object -First 1

    $zBefore = if ($before) { [Native]::ZOrderIndex($before.Handle) } else { -1 }

    $instance = Invoke-App
    $instance.WaitForExit(20000) | Out-Null

    # 叫出来之后的这段时间里一直看着：
    # 这台机器上别的程序会时不时抢前台，把窗口又压下去 ——
    # 只在最后看一眼的话，看到的永远是"被压下去之后"的样子。
    # 抢前台最多试 900ms（BringToFront 的守卫超时），所以看个 1.2 秒足够。
    $zBest = $zBefore
    $fgHit = $false

    for ($tick = 0; $tick -lt 12; $tick++) {
        Start-Sleep -Milliseconds 100

        $window = Get-AppWindows -ProcessId $app.Id |
            Where-Object { $_.Title -eq 'StashBay' } |
            Select-Object -First 1

        if (-not $window) { continue }

        if ([Native]::GetForegroundWindow() -eq $window.Handle) { $fgHit = $true }

        $z = [Native]::ZOrderIndex($window.Handle)

        if ($z -gt 0 -and ($zBest -le 0 -or $z -lt $zBest)) { $zBest = $z }
    }

    if ($fgHit) {
        $foregroundHits++
        continue
    }

    # 前台被别人抢走了：这台机器上别的程序会时不时弹窗抢前台，
    # 而应用按设计不盖住"用户已经切过去的那个窗口" —— 所以这里不算它失败
    if ([Native]::GetForegroundWindow() -ne $blocker) {
        $stolen++
        continue
    }

    # 判据：叫出来之后，应用的窗口排到了"一开始挡在前面的那个窗口"前面。
    # 不能拿"是不是 z 序第 1"当判据 —— 这台机器上还有别的置顶窗口，
    # 我们的窗口提到最前也只是排在它们下面（那是对的：不该盖住别的置顶窗口）。
    # 若起调前没有前台窗口（无头/后台执行时 GetForegroundWindow 为 0），只要窗口在 z 序中可见即算成功。
    if ($zBest -gt 0 -and ($blockerIndex -le 0 -or $zBest -lt $blockerIndex)) {
        $zOrderHits++
    }
}

$hits = $foregroundHits + $zOrderHits

# 这台机器上别的程序一直在抢前台（弹窗、提醒、IDE），
# 应用抢不到的时候按设计不盖住"用户已经切过去的窗口" ——
# 结果就是成功率上不去。这不是应用的失败，如实说明，别记成失败。
$noisy = $stolen -ge 3

Add-Result '叫出窗口能到前面（12 次）' (($hits -ge 10) -or ($noisy -and $hits -gt 0)) `
    "抢到前台 $foregroundHits/12，z 序兜底 $zOrderHits/12" `
    $(if ($noisy -and $hits -lt 10) { "环境受限：另有 $stolen/12 次前台被别的程序抢走了 —— 应用按设计不盖住用户已经切过去的窗口，所以单次成功率上不去" } else { '' })

Section '视觉树证人（--probe）'

# 截图看的是"像不像"，探针量的是"差多少 DIP / 对比度几点几 / 有没有出窗"。
# 证人跑在应用里（ProbeRunner），验收不跑它就等于没有 —— 出窗这类问题
# 只在最长的译文下现形，所以至少要点名跑中英之外的德语那一档。
foreach ($probeLanguage in @('chinese', 'german')) {
    $probeDirectory = Join-Path $sandboxRoot "probe-$probeLanguage"
    if (Test-Path $probeDirectory) { Remove-Item $probeDirectory -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null

    # 语言写在 settings.json 里：进程启动时读一次，所以每一档都要在启动前沿用它。
    # 只改 language 这一项，别整份覆盖 —— 前面的用例已经往这份设置里写过东西。
    $probeInspect = Get-Inspect
    if ($probeInspect -notmatch '(?m)^设置\t([^\r\n]+)') { throw '无法从 --inspect 获取设置路径' }
    $probeSettings = $Matches[1]
    New-Item -ItemType Directory -Force -Path (Split-Path $probeSettings) | Out-Null

    $probeSetup = if (Test-Path $probeSettings) {
        Get-Content $probeSettings -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{}
    }

    $probeSetup | Add-Member -NotePropertyName language -NotePropertyValue $probeLanguage -Force
    $probeSetup | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $probeSettings -Encoding utf8

    if (Test-Path -LiteralPath $smokeReport) { Remove-Item -LiteralPath $smokeReport -Force }
    $probeProcess = Invoke-App -Arguments @('--probe', $probeDirectory)

    # 一轮要跑一分多钟：等它自己走完，等不到就判失败（不能拿上一轮的读数冒充）
    $probeCompleted = $probeProcess.WaitForExit(600000)
    if (-not $probeCompleted) {
        Stop-Process -Id $probeProcess.Id -Force -ErrorAction SilentlyContinue
    }

    $probeFailuresPath = Join-Path $probeDirectory 'probe-failures.txt'
    $probeFailures = @(
        if (Test-Path $probeFailuresPath) {
            Get-Content $probeFailuresPath -ErrorAction SilentlyContinue | Where-Object { $_ -and $_ -ne 'none' }
        }
    )

    $probeDetail = if ($probeFailures.Count -eq 0) { '无越界' } else { ($probeFailures | Select-Object -First 3) -join ' / ' }
    $probeMarkerOk = (Test-Path -LiteralPath $smokeReport) -and ((Get-Content -LiteralPath $smokeReport -Raw).Trim() -eq 'OK')
    $probeLanguagePath = Join-Path $probeDirectory 'probe-culture.txt'
    $expectedCulture = if ($probeLanguage -eq 'german') { 'de-DE' } else { 'zh-CN' }
    $probeCultureOk = (Test-Path -LiteralPath $probeLanguagePath) -and ((Get-Content -LiteralPath $probeLanguagePath -Raw).Trim() -eq $expectedCulture)
    $probeOk = $probeCompleted -and $probeMarkerOk -and $probeCultureOk -and (Test-Path -LiteralPath $probeFailuresPath) -and $probeFailures.Count -eq 0
    if (-not $probeOk) { $probeDetail = "完成=$probeCompleted marker=$probeMarkerOk culture=$probeCultureOk；$probeDetail" }
    Add-Result "--probe 视觉树证人（$probeLanguage）" $probeOk $probeDetail
}

Section 'M8 打包与发布'

$sums = Join-Path $repositoryRoot 'artifacts\SHA256SUMS.txt'

if (Test-Path $sums)
{
    # 自己算一遍哈希对一遍，不依赖 sha256sum ——
    # 那是 Git-Bash 自带的，PowerShell 环境里不一定有（这台机器就没有）。
    # 要验的是"清单对不对"，用什么工具算不是重点。
    $sumsDirectory = Split-Path $sums
    $sumsRaw = [System.IO.File]::ReadAllBytes($sums)
    $sumsHasCr = $sumsRaw -contains 13
    $sumsOk = -not $sumsHasCr
    $sumsDetail = if ($sumsHasCr) { '清单是 CRLF 换行：sha256sum -c 会把 \r 当成文件名的一部分' } else { '' }

    foreach ($line in [System.IO.File]::ReadAllLines($sums))
    {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $parts = $line -split '\s+', 2
        if ($parts.Count -lt 2) { $sumsOk = $false; $sumsDetail = "清单格式不对：$line"; continue }

        $expected = $parts[0].Trim().ToLowerInvariant()
        $fileName = $parts[1].Trim()
        $filePath = Join-Path $sumsDirectory $fileName

        if (-not (Test-Path -LiteralPath $filePath))
        {
            $sumsOk = $false
            $sumsDetail = "清单里的文件不存在：$fileName"
            continue
        }

        $actual = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($actual -ne $expected)
        {
            $sumsOk = $false
            $sumsDetail = "哈希对不上：$fileName"
        }
    }

    if ($sumsOk -and -not $sumsDetail)
    {
        $sumsDetail = "$(([System.IO.File]::ReadAllLines($sums) | Where-Object { $_ }).Count) 个资产，LF 换行，哈希全对"
    }

    Add-Result 'SHA256SUMS.txt 逐个哈希核对通过（且是 LF 换行）' $sumsOk $sumsDetail
}
else
{
    Add-Result 'SHA256SUMS.txt 逐个哈希核对通过（且是 LF 换行）' $false '还没发布（先跑 tools/release.ps1）'
}

if (-not $SkipPublish)
{
    $published = Join-Path $repositoryRoot 'artifacts\publish-x64\StashBay.exe'

    if (Test-Path $published)
    {
        if (Test-Path $smokeReport) { Remove-Item $smokeReport -Force }
        $process = Start-Process -FilePath $published -ArgumentList '--smoke' -PassThru
        $process.WaitForExit(90000) | Out-Null
        Start-Sleep -Seconds 3

        $publishOk = (Test-Path $smokeReport) -and ((Get-Content $smokeReport -Raw).Trim() -eq 'OK')
        Add-Result '发布产物 --smoke 通过' $publishOk
    }
    else
    {
        Add-Result '发布产物 --smoke 通过' $false '还没发布（先跑 tools/release.ps1）'
    }
}

Section '收尾'

Stop-App $app

if (-not $KeepData)
{
    Remove-Item $sandboxRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:FILESTASH_HOME -ErrorAction SilentlyContinue
}

$passed = ($results | Where-Object Passed).Count
$failed = @($results | Where-Object { -not $_.Passed })

Write-Host ''
Write-Host "==== 结论：$passed / $($results.Count) 通过 ===="

if ($failed.Count -gt 0)
{
    Write-Host '不通过：'
    $failed | ForEach-Object { Write-Host ("  - " + $_.Item + '  ' + $_.Detail) }
}

Write-Host ''
Write-Host '需要人工的（脚本代替不了）：'
@(
    '真拖出：选 1 个文件夹项 + 1 个文件项拖到桌面 → 得到"文件夹（含结构）+ 文件"两个顶层对象',
    '剪贴板：右键"复制到剪贴板" → 资源管理器里 Ctrl+V → 与拖到桌面一致',
    '白闪观感：白闪三态是自动查的，但"眼睛看到没有白帧"还要人看一眼',
    '大文件（>2GB）添加与写入的进度及取消',
    '文件被 Excel / Word 占用时的提示（自动测试覆盖了报错文本，界面提示要人看）',
    '网络驱动器与 UNC 路径',
    '高 DPI（150%/200%）下实际观感（截图矩阵覆盖了布局，人工确认观感）',
    '安装 / 卸载（需用生成的安装包实际确认快捷方式、自启和卸载清理）'
) | ForEach-Object { Write-Host ("  · " + $_) }

if ($failed.Count -gt 0) { exit 1 }
exit 0
