#Requires -Version 5.1
<#
.SYNOPSIS
    发布 StashBay：四份 pubxml + 校验和清单。

.DESCRIPTION
    产出到 artifacts\：
      publish-x64\                    自包含单文件（下载即用）
      publish-x64-needs-dotnet10\     框架依赖单文件（体积小）
      publish-arm64\                  自包含单文件（ARM）
      publish-arm64-needs-dotnet10\   框架依赖单文件（ARM）
      StashBay-<版本>-<平台>.zip     打包好的资产
      SHA256SUMS.txt                  校验和清单

    SHA256SUMS.txt **必须是 LF 换行**：CRLF 会让 sha256sum -c 把 \r 当成文件名的一部分，
    逐行报 "No such file or directory"。
#>
[CmdletBinding()]
param(
    [switch]$SkipArm64,
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\FileStash.App\FileStash.App.csproj'
$artifacts = Join-Path $repositoryRoot 'artifacts'

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments, [string]$What)

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$What 失败（退出码 $LASTEXITCODE）"
    }
}

# 找 Inno Setup 的编译器 ISCC.exe。
# 不写死路径：Inno Setup 装在哪都行，机器上装了但注册表指向别处也不少见。
function Find-InnoCompiler {
    $candidates = @()

    if ($env:ISCC_PATH) { $candidates += $env:ISCC_PATH }

    $candidates += @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'D:\Backup\Libraries\Documents\GitHub\Projects\365\037-scene-anchor\.deps\inno\InnoSetup6\ISCC.exe'
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    # 注册表里登记的安装位置（装在别处时的唯一可靠来源）
    $uninstallKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    foreach ($key in $uninstallKeys) {
        $entry = Get-ItemProperty $key -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'Inno Setup*' } |
            Select-Object -First 1

        if ($entry -and $entry.PSObject.Properties['InstallLocation'] -and $entry.InstallLocation) {
            $candidate = Join-Path $entry.InstallLocation 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }

    return $null
}

Write-Host '==> 还原与构建'
Invoke-Checked 'dotnet' @('build', $project, '-c', 'Release', '-v', 'minimal') '构建'

$profiles = @(
    @{ Name = 'win-x64';                  Directory = 'publish-x64';                  Archive = 'win-x64' },
    @{ Name = 'win-x64-needs-dotnet10';   Directory = 'publish-x64-needs-dotnet10';   Archive = 'win-x64-needs-dotnet10' }
)

if (-not $SkipArm64) {
    $profiles += @(
        @{ Name = 'win-arm64';                Directory = 'publish-arm64';                Archive = 'win-arm64' },
        @{ Name = 'win-arm64-needs-dotnet10'; Directory = 'publish-arm64-needs-dotnet10'; Archive = 'win-arm64-needs-dotnet10' }
    )
}

$version = (& dotnet msbuild $project -getProperty:Version -nologo | Select-Object -Last 1).Trim()
Write-Host "==> 版本 $version"

if (Test-Path $artifacts) {
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifacts).Path
    if ($resolvedArtifacts -ne (Join-Path $repositoryRoot 'artifacts')) {
        throw "发布目录不在预期位置：$resolvedArtifacts"
    }
    # 只清理发布脚本拥有的产物，保留截图和探针报告。
    foreach ($profile in $profiles) {
        $publishDirectory = Join-Path $resolvedArtifacts $profile.Directory
        if (Test-Path -LiteralPath $publishDirectory) {
            $resolvedPublish = (Resolve-Path -LiteralPath $publishDirectory).Path
            if (-not $resolvedPublish.StartsWith($resolvedArtifacts + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "发布目录不在 artifacts 内：$resolvedPublish"
            }
            Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
        }
    }
    Get-ChildItem -LiteralPath $resolvedArtifacts -File |
        Where-Object { $_.Name -like 'StashBay-*.zip' -or $_.Name -like 'StashBay-*-setup.exe' -or $_.Name -eq 'SHA256SUMS.txt' } |
        Remove-Item -Force
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

foreach ($profile in $profiles) {
    Write-Host "==> 发布 $($profile.Name)"

    Invoke-Checked 'dotnet' @(
        'publish', $project,
        '-c', 'Release',
        # 这里必须加括号：数组字面量里 'a' + $b 会被拆成三个元素
        # （'-p:PublishProfile='、'+'、'win-x64'），拼出来就成了
        # "dotnet publish <项目> ... + win-x64"，MSBuild 报 MSB1008「只能指定一个项目」
        ('-p:PublishProfile=' + $profile.Name),
        '-o', (Join-Path $artifacts $profile.Directory)
    ) "发布 $($profile.Name)"

    $published = Join-Path $artifacts $profile.Directory

    # 引用工程（Core / Infrastructure）的 pdb 还是会生成，发布产物里不需要
    Get-ChildItem $published -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

    if (-not $SkipSmoke -and $profile.Archive -like 'win-x64*') {
        # WPF 的 XAML 是懒加载的，坏掉的 XAML 不开窗口就不报错 —— 发布产物必须再冒烟一次
        Write-Host '==> 对发布产物跑 --smoke'
        $exe = Join-Path $published 'StashBay.exe'
        $report = Join-Path ([System.IO.Path]::GetTempPath()) 'filestash-smoke.txt'

        if (Test-Path $report) {
            Remove-Item $report -Force
        }

        $smokeHome = Join-Path ([System.IO.Path]::GetTempPath()) ('stashbay-release-' + [guid]::NewGuid().ToString('N'))
        $savedHome = $env:STASHBAY_HOME
        $savedShell = $env:STASHBAY_NO_SHELL_INTEGRATION
        try {
            $env:STASHBAY_HOME = $smokeHome
            $env:STASHBAY_NO_SHELL_INTEGRATION = '1'
            $process = Start-Process -FilePath $exe -ArgumentList '--smoke' -PassThru -WindowStyle Hidden
            if (-not $process.WaitForExit(90000)) {
                $process.Kill()
                $process.WaitForExit()
                throw "发布产物的 smoke 超时：$($profile.Name)"
            }
        } finally {
            $env:STASHBAY_HOME = $savedHome
            $env:STASHBAY_NO_SHELL_INTEGRATION = $savedShell
            if (Test-Path -LiteralPath $smokeHome) {
                Remove-Item -LiteralPath $smokeHome -Recurse -Force
            }
        }

        if (-not (Test-Path $report) -or (Get-Content $report -Raw) -ne 'OK') {
            $detail = if (Test-Path $report) { Get-Content $report -Raw } else { '(没有生成报告)' }
            throw "发布产物的 smoke 没通过：$detail"
        }

        Write-Host '    smoke 通过'
    }

    $archive = Join-Path $artifacts ("StashBay-{0}-{1}.zip" -f $version, $profile.Archive)
    Compress-Archive -Path (Join-Path $published '*') -DestinationPath $archive -Force
}

Write-Host '==> 编译安装包'
$iscc = Find-InnoCompiler

if ($iscc) {
    $issFile = Join-Path $repositoryRoot 'tools\installer\FileStash.iss'

    # 版本号是预处理器宏：/DAppVersion=...
    & $iscc "/DAppVersion=$version" $issFile
    if ($LASTEXITCODE -ne 0) {
        throw "编译安装包失败（退出码 $LASTEXITCODE）"
    }
} else {
    Write-Host '    跳过：找不到 Inno Setup（ISCC.exe）。'
    Write-Host '    装一个：winget install JRSoftware.InnoSetup'
    Write-Host '    或者设 ISCC_PATH 指向 ISCC.exe。'
}

Write-Host '==> 生成校验和清单（LF 换行）'
$sums = Join-Path $artifacts 'SHA256SUMS.txt'
$lines = foreach ($file in Get-ChildItem $artifacts -File | Where-Object { $_.Name -like '*.zip' -or $_.Name -like '*-setup.exe' } | Sort-Object Name) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    # 用 "a + "`n" 明确写 LF：Set-Content 默认在 Windows 上写 CRLF，
    # 而 CRLF 会让 sha256sum -c 逐行报 "No such file or directory"
    $hash + '  ' + $file.Name + "`n"
}

[System.IO.File]::WriteAllText($sums, ($lines -join ''), [System.Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host '==> 产出'
Get-ChildItem $artifacts | ForEach-Object {
    # 目录没有 Length 属性，StrictMode 下直接读会抛"在此对象上找不到属性"
    $size = if ($_.PSIsContainer) { '<目录>' } else { '{0,10:N0} bytes' -f $_.Length }
    '    {0,-46} {1}' -f $_.Name, $size
}

Write-Host ''
Write-Host "完成：$artifacts"
