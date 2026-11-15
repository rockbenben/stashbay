#Requires -Version 7.0
<#
.SYNOPSIS
    出图脚本：造夹具工作区 → 播数据 → 渲染整张截图矩阵，并证明拍到的不是空架。

.DESCRIPTION
    --shots 只看当前重定向库里的东西，库里没数据它就安静地拍 200 张空窗口，
    而空窗口照样是"成功退出、文件齐全"—— 上一轮"目视比对暗浅两套"就是这么
    被判成已完成的。这个脚本把三步串起来，并在最后按哈希判定"有数据的架子和
    空架子必须是两张不同的图"，把那个坑变成会红的检查。

    数据与设置进临时沙箱（STASHBAY_HOME），注册表一个都不碰
    （STASHBAY_NO_SHELL_INTEGRATION）。

    用法：pwsh tools/shots.ps1
          pwsh tools/shots.ps1 -Out artifacts\shots-review -KeepData
          pwsh tools/shots.ps1 -CompactOnly  # 极简模式，深浅主题 × 最小/最大工作区
          pwsh tools/shots.ps1 -SettingsOnly -Language German # Every settings tab, including long labels
#>
[CmdletBinding()]
param(
    [string]$Out = '',
    [string]$Verify = '',
    [switch]$KeepData,
    [switch]$CompactOnly,
    [switch]$SettingsOnly,
    [switch]$WorkbenchOnly,
    [ValidateSet('System', 'Chinese', 'English', 'TraditionalChinese', 'Japanese', 'Korean',
        'Spanish', 'PortugueseBrazil', 'French', 'German', 'Italian', 'Russian', 'Arabic',
        'Hindi', 'Indonesian', 'Thai', 'Turkish', 'Vietnamese', 'Bengali')]
    [string]$Language = 'Chinese',
    [switch]$IncludeToasts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
if ([int]$SettingsOnly.IsPresent + [int]$CompactOnly.IsPresent + [int]$WorkbenchOnly.IsPresent -gt 1)
{
    throw 'Choose one focused screenshot mode: SettingsOnly, CompactOnly or WorkbenchOnly.'
}

# 子进程输出按本机代码页回来会变乱码，中文文件名（夹具里一大把）就对不上了
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\FileStash.App\FileStash.App.csproj'
$smokeReport = Join-Path ([System.IO.Path]::GetTempPath()) 'filestash-smoke.txt'

$sandboxRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('filestash-shots-' + [guid]::NewGuid().ToString('N'))
$workspace = Join-Path $sandboxRoot 'workspace'
$home1 = Join-Path $workspace '.stashbay-fixture'
if (-not $Out) { $Out = Join-Path $repositoryRoot 'artifacts\shots' }
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)

New-Item -ItemType Directory -Force -Path $home1 | Out-Null
$env:STASHBAY_HOME = $home1
$env:STASHBAY_NO_SHELL_INTEGRATION = '1'

function Stop-Step {
    param([string]$Message)

    Write-Host ''
    Write-Host "失败：$Message"
    exit 1
}

# ---------------------------------------------------------------- 夹具工作区

function New-FixtureFile {
    param([string]$Path, [int]$Size)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

    # 伪随机填充：全零会被某些列当成"空文件"，大小与内容都要像个真东西
    $buffer = New-Object byte[] $Size
    for ($i = 0; $i -lt $Size; $i++) { $buffer[$i] = [byte](0x21 + ($i % 0x5e)) }

    $head = [System.Text.Encoding]::UTF8.GetBytes('StashBay fixture')
    if ($Size -ge $head.Length) { [Array]::Copy($head, $buffer, $head.Length) }

    [System.IO.File]::WriteAllBytes($Path, $buffer)
}

function Test-ShotsNotEmpty {
    param([string]$Dir)

    # 有货的架子和空架子必须拍成两张不同的图。相同 = 数据没进到界面，
    # 整张矩阵就只是"窗口还活着"的证据，不是"改造后的观感成立"的证据。
    # 这一对不是设想出来的：2026-11-06 那批 208 张截图就是这么"全绿"过去的 ——
    # Compact 与 Compact.Empty 当时逐字节相同，进程退出码 0，没人发问。
    $pairs = @(
        @{ This = 'MainWindow.Compact.Dark@1032.png'; That = 'MainWindow.Compact.Empty.Dark@1032.png'; Why = '架子上有没有货' }
        @{ This = 'MainWindow.Compact.Light@1032.png'; That = 'MainWindow.Compact.Dark@1032.png'; Why = '深浅主题必须区分' }
    )

    foreach ($pair in $pairs)
    {
        $thisPath = Join-Path $Dir $pair.This
        $thatPath = Join-Path $Dir $pair.That

        foreach ($path in @($thisPath, $thatPath))
        {
            if (-not (Test-Path -LiteralPath $path)) { Stop-Step "缺图：$path" }
        }

        $thisHash = (Get-FileHash -LiteralPath $thisPath -Algorithm SHA256).Hash
        $thatHash = (Get-FileHash -LiteralPath $thatPath -Algorithm SHA256).Hash

        if ($thisHash -eq $thatHash)
        {
            Stop-Step "$($pair.This) 与 $($pair.That) 逐字节相同（$($pair.Why)）—— 数据没进到出图那一步"
        }

        Write-Host "  [通过] $($pair.This) ≠ $($pair.That)  —— $($pair.Why)"
    }
}

# 只复查一份已经存在的矩阵，不重新出图
if ($Verify)
{
    Write-Host "== 空架守卫（只查不出图）：$Verify"
    Test-ShotsNotEmpty $Verify
    exit 0
}

Write-Host 'StashBay 出图'
Write-Host "仓库：$repositoryRoot"
Write-Host "沙箱：$sandboxRoot"
Write-Host "输出：$Out"

Write-Host ''
Write-Host '== 夹具工作区'

# 这些名字与尺寸是 SeedRunner 写死的清单，改名等于换数据
New-FixtureFile -Path (Join-Path $workspace 'inbox\a.txt') -Size 200
New-FixtureFile -Path (Join-Path $workspace 'inbox\改过的.txt') -Size 100
New-FixtureFile -Path (Join-Path $workspace 'inbox\被删掉的.txt') -Size 100
New-FixtureFile -Path (Join-Path $workspace 'inbox\大文件.bin') -Size 300000
New-FixtureFile -Path (Join-Path $workspace 'inbox\素材\字幕.srt') -Size 100
New-FixtureFile -Path (Join-Path $workspace 'inbox\素材\纹理.png') -Size 100
New-FixtureFile -Path (Join-Path $workspace 'inbox\素材\音效.wav') -Size 100
New-FixtureFile -Path (Join-Path $workspace 'inbox\设计稿-首页-2026年第三季度-最终版-张三李四王五复核.psd') -Size 100
New-FixtureFile -Path (Join-Path $workspace 'inbox\季度报表 (修订).xlsx') -Size 100
New-Item -ItemType Directory -Force -Path (Join-Path $workspace 'inbox\空目录') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $workspace 'out') | Out-Null

New-FixtureFile -Path (Join-Path $workspace 'repo\src\components\VeryLongComponentNameThatIsSureToBeTruncatedInTheNameColumn.tsx') -Size 400
New-FixtureFile -Path (Join-Path $workspace 'repo\src\utils\path-mapper.ts') -Size 200
New-FixtureFile -Path (Join-Path $workspace 'repo\README.md') -Size 200

# 仓库那一列要显示"来源分支"，得是个真 git 仓库；没有 git 就只少那一列
if (Get-Command git -ErrorAction SilentlyContinue)
{
    Push-Location (Join-Path $workspace 'repo')
    try
    {
        & git init -q | Out-Null
        & git add -A | Out-Null
        & git -c user.email=fixture@localhost -c user.name=fixture commit -q -m 'fixture' | Out-Null
        & git checkout -q -b feature/stash-demo | Out-Null
    }
    finally
    {
        Pop-Location
    }
    Write-Host '  仓库夹具：git 已初始化，分支 feature/stash-demo'
}
else
{
    Write-Host '  仓库夹具：本机没有 git，"来源分支"那一列会是空的'
}

# ---------------------------------------------------------------- 构建

Write-Host ''
Write-Host '== 构建'

$buildOutput = & dotnet build $project --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Stop-Step "dotnet build 退出码 $LASTEXITCODE`n$buildOutput" }

$exe = Join-Path $repositoryRoot 'src\FileStash.App\bin\Debug\net10.0-windows\StashBay.exe'
if (-not (Test-Path -LiteralPath $exe)) { Stop-Step "构建完找不到 $exe" }

# ---------------------------------------------------------------- 播数据

Write-Host ''
Write-Host '== --seed'

if (Test-Path -LiteralPath $smokeReport) { Remove-Item -LiteralPath $smokeReport -Force }
Start-Process -FilePath $exe -ArgumentList @('--seed', $workspace) -Wait | Out-Null

if (-not (Test-Path -LiteralPath $smokeReport)) { Stop-Step '--seed 没写结果文件（进程没跑完？）' }

$seedReport = (Get-Content -LiteralPath $smokeReport -Raw).Trim()
if ($seedReport -notmatch '^OK groups=')
{
    # 夹具半途报废时库里有半份数据，截图会拍成"看着有货其实是残的"，必须停
    Stop-Step "--seed 没成功：$seedReport"
}
Write-Host "  $seedReport"

# Use the seeded sandbox settings, never the user's real language preference.
$fixtureSettings = Join-Path $home1 'StashBay\settings.json'
$fixturePreferences = if (Test-Path -LiteralPath $fixtureSettings)
{
    Get-Content -LiteralPath $fixtureSettings -Raw | ConvertFrom-Json -AsHashtable
}
else { @{} }
$fixturePreferences['language'] = $Language
$fixturePreferences | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $fixtureSettings -Encoding utf8

# ---------------------------------------------------------------- 出图

Write-Host ''
Write-Host '== --shots'

# 重用输出目录，只覆盖本轮生成的同名图片，不递归删除调用者提供的目录。
New-Item -ItemType Directory -Force -Path $Out | Out-Null

if (Test-Path -LiteralPath $smokeReport) { Remove-Item -LiteralPath $smokeReport -Force }
$shotArguments = @('--shots', ('"' + $Out + '"'))
if ($CompactOnly) { $shotArguments += '--shots-compact-only' }
if ($SettingsOnly) { $shotArguments += '--shots-settings-only' }
if ($WorkbenchOnly) { $shotArguments += '--shots-workbench-only' }
if ($IncludeToasts) { $shotArguments += '--shots-include-toasts' }
Start-Process -FilePath $exe -ArgumentList $shotArguments -WindowStyle Hidden -Wait | Out-Null

# --shots 自己会数失败并写回同一个结果文件（"FAIL shots=N ..."），但退出码不可信；
# 不读它的话，解析期炸掉的变体只会表现为"少了几张图"，脚本照样报完成。
if (-not (Test-Path -LiteralPath $smokeReport)) { Stop-Step '--shots 没写结果文件（进程没跑完？）' }

$shotsReport = (Get-Content -LiteralPath $smokeReport -Raw).Trim()
if ($shotsReport -notmatch '^OK')
{
    Stop-Step "--shots 自报了问题：$($shotsReport -replace "`r?`n", ' | ')"
}

$shots = @(Get-ChildItem -LiteralPath $Out -Filter '*.png' -File)
if ($shots.Count -eq 0) { Stop-Step "--shots 没产出任何 PNG（$Out）" }

# ---------------------------------------------------------------- 空架守卫

Write-Host ''
Write-Host '== 空架守卫'

if ($SettingsOnly)
{
    # Every tab must actually be selected: otherwise all screenshots can show Appearance.
    $settingsPages = @('SettingsWindow', 'SettingsWindow.Shelf', 'SettingsWindow.Files',
        'SettingsWindow.Windows', 'SettingsWindow.Storage', 'SettingsWindow.Git',
        'SettingsWindow.StartupElevated', 'SettingsWindow.StartupRepair', 'SettingsWindow.StartupUnknown')
    foreach ($theme in @('Light', 'Dark'))
    {
        foreach ($height in @(464, 1032))
        {
            $pageHashes = foreach ($page in $settingsPages)
            {
                $imagePath = Join-Path $Out "$page.$theme@$height.png"
                if (-not (Test-Path -LiteralPath $imagePath)) { Stop-Step "Missing settings screenshot: $imagePath" }
                (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
            }
            if (@($pageHashes | Select-Object -Unique).Count -ne $settingsPages.Count)
            {
                Stop-Step "Settings pages or startup states rendered identically: $theme@$height"
            }
        }
    }
    Write-Host '  [通过] All settings pages and startup states rendered distinctly in both themes and sizes.'
}
elseif ($WorkbenchOnly)
{
    foreach ($theme in @('Light', 'Dark'))
    {
        foreach ($height in @(464, 1032))
        {
            $populated = Join-Path $Out "MainWindow.Expanded.$theme@$height.png"
            $empty = Join-Path $Out "MainWindow.Expanded.Empty.$theme@$height.png"
            if ((Get-FileHash -LiteralPath $populated).Hash -eq (Get-FileHash -LiteralPath $empty).Hash)
            {
                Stop-Step "Workbench fixture did not render distinct populated and empty states: $theme@$height"
            }
        }
    }
    Write-Host '  [通过] Workbench populated and empty states rendered distinctly in both themes and sizes.'
}
else
{
    Test-ShotsNotEmpty $Out
}

Write-Host ''
Write-Host "==== 完成：$($shots.Count) 张 PNG 在 $Out ===="
Write-Host '下一步是人工目视：暗、浅两套都要过一眼（脚本证得了"有数据"，证不了"好看"）。'

if (-not $KeepData)
{
    Remove-Item -LiteralPath $sandboxRoot -Recurse -Force -ErrorAction SilentlyContinue
}
else
{
    Write-Host "沙箱保留在：$sandboxRoot"
}

exit 0
