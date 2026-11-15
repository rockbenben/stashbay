#Requires -Version 7.4
<#
.SYNOPSIS
    发布 StashBay：四份 pubxml + Inno Setup 安装包 + 校验和清单。

.DESCRIPTION
    产出到 artifacts\：
      publish-x64\                    自包含单文件（下载即用）
      publish-x64-needs-dotnet10\     框架依赖单文件（体积小）
      publish-arm64\                  自包含单文件（ARM）
      publish-arm64-needs-dotnet10\   框架依赖单文件（ARM）
      StashBay-v<版本>-setup.exe       Inno Setup 安装包
      StashBay-v<版本>-<平台>.zip    打包好的资产
      SHA256SUMS.txt                  校验和清单

    SHA256SUMS.txt **必须是 LF 换行**：CRLF 会让 sha256sum -c 把 \r 当成文件名的一部分，
    逐行报 "No such file or directory"。

    打包与校验调用 tools/package-release.ps1，在隔离沙箱中执行双向解包核对与 --smoke 冒烟。
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
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

if (-not $Version) {
    $Version = (& dotnet msbuild $project -target:VersionFromGitTag -getProperty:Version -nologo | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "无法确定发布版本：$Version"
    }
}

$iscc = Find-InnoCompiler
if (-not $iscc) { throw '需要 Inno Setup 6。安装编译器，或设 ISCC_PATH 指向 ISCC.exe。' }

Write-Host '==> 还原与构建'
Invoke-Checked 'dotnet' @('build', $project, '-c', 'Release', '-v', 'minimal', "-p:Version=$Version") '构建'

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
        "-p:Version=$Version",
        # 这里必须加括号：数组字面量里 'a' + $b 会被拆成三个元素
        # （'-p:PublishProfile='、'+'、'win-x64'），拼出来就成了
        # "dotnet publish <项目> ... + win-x64"，MSBuild 报 MSB1008「只能指定一个项目」
        ('-p:PublishProfile=' + $profile.Name),
        '-o', (Join-Path $artifacts $profile.Directory)
    ) "发布 $($profile.Name)"

    $published = Join-Path $artifacts $profile.Directory

    # 引用工程（Core / Infrastructure）的 pdb 还是会生成，发布产物里不需要
    Get-ChildItem $published -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

}

Write-Host '==> 编译安装包'

if ($iscc) {
    $issFile = Join-Path $repositoryRoot 'tools\installer\FileStash.iss'

    # 版本号是预处理器宏：/DAppVersion=...
    # /F 显式传入带 'v' 前缀的文件名，与 ZIP 资产命名规则保持统一
    & $iscc "/DAppVersion=$version" "/FStashBay-v$version-setup" $issFile
    if ($LASTEXITCODE -ne 0) {
        throw "编译安装包失败（退出码 $LASTEXITCODE）"
    }
}
$installer = Join-Path $artifacts "StashBay-v$version-setup.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw '安装包未生成' }

# 调用通用打包与验证逻辑（与 CI 共用）：PE 架构、外置 SQLite、ZIP 双向比对、沙箱隔离冒烟与 LF 校验和
& (Join-Path $PSScriptRoot 'package-release.ps1') -AppName StashBay -Tag "v$Version" `
    -PublishRoot $artifacts -Installer $installer -OutputDirectory $artifacts `
    -SkipArm64:$SkipArm64 -SkipSmoke:$SkipSmoke

Write-Host ''
Write-Host '==> 产出'
Get-ChildItem $artifacts | ForEach-Object {
    # 目录没有 Length 属性，StrictMode 下直接读会抛"在此对象上找不到属性"
    $size = if ($_.PSIsContainer) { '<目录>' } else { '{0,10:N0} bytes' -f $_.Length }
    '    {0,-46} {1}' -f $_.Name, $size
}

Write-Host ''
Write-Host "完成：$artifacts"
