#Requires -Version 7.4
<#
.SYNOPSIS
    发布包结构校验与统一打包：本地与 CI 共用的打包验证防线。

.DESCRIPTION
    本脚本由 tools/release.ps1 与 GitHub Actions release workflow 共同调用：
      1. 验证各架构发布产物（PE 头与 Machine 架构校验：x64 为 0x8664，ARM64 为 0xaa64）
      2. 校验单文件发布清单（SQLite 必须嵌入 EXE）
      3. 可选 Authenticode 签名有效性验证（启用 -RequireSigning 时）
      4. 通过 tar 归档为 ZIP，并即时解包进行双向文件清单与内容 SHA-256 哈希比对
      5. 在独立隔离沙箱中解包运行 x64 版本的无头窗口冒烟（--smoke）
      6. 输出纯 LF 换行的 SHA256SUMS.txt 清单，并在 CI 模式下写入 GITHUB_OUTPUT

.PARAMETER AppName
    应用名称，如 StashBay。

.PARAMETER Tag
    发布版本标签（格式如 v1.0.0）。

.PARAMETER PublishRoot
    dotnet publish 产物所在的根目录（包含 publish-x64 等子目录）。

.PARAMETER Installer
    编译完成的 Inno Setup 安装包路径。

.PARAMETER OutputDirectory
    输出 ZIP 包与校验和清单的目标目录。

.PARAMETER RequiredFiles
    各发布 profile 额外必须存在的文件列表。

.PARAMETER SmokeEnvironment
    传递给冒烟进程的环境变量哈希表。

.PARAMETER SmokeMarker
    冒烟成功状态写入的标记文件名（默认 filestash-smoke.txt）。

.PARAMETER RequireSigning
    若指定，则所有可执行文件与安装包必须具有合法的 Authenticode 签名。

.PARAMETER SkipArm64
    跳过 ARM64 架构包的打包与检查。

.PARAMETER SkipSmoke
    跳过解包后的 x64 冒烟测试。

.EXAMPLE
    pwsh tools/package-release.ps1 -AppName StashBay -Tag v1.0.0 `
        -PublishRoot artifacts -Installer artifacts/StashBay-v1.0.0-setup.exe `
        -OutputDirectory artifacts
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$AppName,
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$Installer,
    [string]$OutputDirectory = '.',
    [string[]]$RequiredFiles = @(), # e.g. e_sqlite3.dll and resources/defaults.json
    [hashtable]$SmokeEnvironment = @{}, # app-specific data / shell-integration overrides
    [string]$SmokeMarker = 'filestash-smoke.txt',
    [switch]$RequireSigning,
    [switch]$SkipArm64,
    [switch]$SkipSmoke
)
$ErrorActionPreference = 'Stop'
$architectures = if ($SkipArm64) { @('x64') } else { @('x64', 'arm64') }
$PublishRoot = (Resolve-Path -LiteralPath $PublishRoot).Path
$Installer = (Resolve-Path -LiteralPath $Installer).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
$installerAsset = Join-Path $outputRoot "$AppName-$Tag-setup.exe"
$sameBytes = (Test-Path -LiteralPath $installerAsset -PathType Leaf) -and ((Get-FileHash -LiteralPath $Installer).Hash -eq (Get-FileHash -LiteralPath $installerAsset).Hash)
if (-not $sameBytes) { Copy-Item -LiteralPath $Installer -Destination $installerAsset -Force }
$Installer = $installerAsset
$assets = @($Installer)
$executables = @($Installer)

function Assert-PeArchitecture([string]$File, [int]$Expected) {
    $stream = [IO.File]::OpenRead($File)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE: $File" }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $File" }
        $machine = $reader.ReadUInt16()
        if ($machine -ne $Expected) { throw ("Wrong PE architecture: {0} (expected 0x{1:x4}, actual 0x{2:x4})" -f $File, $Expected, $machine) }
    } finally { $stream.Dispose() }
}

foreach ($arch in $architectures) {
    foreach ($form in '', '-needs-dotnet10') {
        $directory = Join-Path $PublishRoot "publish-$arch$form"
        $exe = Join-Path $directory "$AppName.exe"
        # Every flavor embeds SQLite, including framework-dependent single-file bundles.
        $profileFiles = @("$AppName.exe") + $RequiredFiles
        foreach ($relative in $profileFiles) {
            $file = [IO.Path]::GetFullPath((Join-Path $directory $relative))
            if (-not $file.StartsWith([IO.Path]::GetFullPath($directory) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Required file outside profile: $relative" }
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing published file: $file" }
        }
        $expected = if ($arch -eq 'x64') { 0x8664 } else { 0xaa64 }
        Assert-PeArchitecture $exe $expected
        $publishedFiles = @(Get-ChildItem -LiteralPath $directory -Recurse -File -Force |
            Where-Object Extension -ne '.pdb' |
            ForEach-Object { [IO.Path]::GetRelativePath($directory, $_.FullName) })
        if (Compare-Object @($profileFiles | Sort-Object) @($publishedFiles | Sort-Object)) {
            throw "Single-file publish inventory differs: $directory ($($publishedFiles -join ', '))"
        }
        $executables += $exe
    }
}
# Check the selected applications and installer before packing or hashing.
$signatures = @($executables | Get-AuthenticodeSignature)
$signedCount = @($signatures | Where-Object Status -eq Valid).Count
if ($RequireSigning -and $signedCount -ne $executables.Count) { throw 'Configured signing did not produce valid signatures for every executable' }
Write-Host "Authenticode: $signedCount/$($executables.Count) executable signatures valid"

foreach ($arch in $architectures) {
    foreach ($form in '', '-needs-dotnet10') {
        $directory = Join-Path $PublishRoot "publish-$arch$form"
        $zip = Join-Path $outputRoot "$AppName-$Tag-win-$arch$form.zip"
        $sandbox = Join-Path ([IO.Path]::GetTempPath()) ("wpf-package-$([guid]::NewGuid())")
        $stage = Join-Path $sandbox 'stage'
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        try {
            Get-ChildItem -LiteralPath $directory -Force | Where-Object { $_.Extension -ne '.pdb' } |
                Copy-Item -Destination $stage -Recurse -Force
            tar -a -c -f $zip -C $stage .
            if ($LASTEXITCODE) { throw "Archive failed: $zip" }
            $unpacked = Join-Path $sandbox "verify-$arch$form"
            New-Item -ItemType Directory -Path $unpacked -Force | Out-Null
            tar -x -f $zip -C $unpacked
            if ($LASTEXITCODE) { throw "Unpack failed: $zip" }
            $expectedFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force |
                ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName) } | Sort-Object)
            $actualFiles = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File -Force |
                ForEach-Object { [IO.Path]::GetRelativePath($unpacked, $_.FullName) } | Sort-Object)
            if (Compare-Object $expectedFiles $actualFiles) { throw "Archive file inventory differs: $zip" }
            foreach ($relative in $expectedFiles) {
                if ((Get-FileHash -LiteralPath (Join-Path $stage $relative)).Hash -ne
                    (Get-FileHash -LiteralPath (Join-Path $unpacked $relative)).Hash) {
                    throw "Archive file content differs: $relative ($zip)"
                }
            }
            if ($arch -eq 'x64' -and -not $SkipSmoke) {
                $temp = Join-Path $sandbox 'temp'
                $data = Join-Path $sandbox 'data'
                New-Item -ItemType Directory -Path $temp,$data -Force | Out-Null
                # Process-local overrides prevent touching real settings or sharing smoke markers.
                $environment = @{} + $SmokeEnvironment
                $environment.TEMP = $temp; $environment.TMP = $temp
                $environment.APPDATA = $data; $environment.LOCALAPPDATA = $data
                $environment.STASHBAY_HOME = $data
                $environment.STASHBAY_NO_SHELL_INTEGRATION = '1'
                $environment.DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $sandbox 'bundle'
                $process = Start-Process -FilePath "$unpacked/$AppName.exe" -WorkingDirectory $unpacked -ArgumentList '--smoke' -Environment $environment -WindowStyle Hidden -PassThru
                if (-not $process.WaitForExit(90000)) { $process.Kill(); $process.WaitForExit(); throw "Packaged smoke timed out: $zip" }
                $marker = Join-Path $temp $SmokeMarker
                if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'OK' -or $process.ExitCode) { throw "Packaged smoke failed: $zip" }
                $sqlite = @(Get-ChildItem -LiteralPath $environment.DOTNET_BUNDLE_EXTRACT_BASE_DIR -Recurse -File -Filter e_sqlite3.dll)
                if ($sqlite.Count -ne 1) { throw "Expected one embedded SQLite extraction: $zip" }
                Assert-PeArchitecture $sqlite[0].FullName 0x8664
                Write-Host "Packaged smoke passed: $arch$form"
            }
            $assets += $zip
        } finally {
            $resolved = [IO.Path]::GetFullPath($sandbox)
            if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')) { throw 'Unexpected package cleanup directory' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
$checksums = Join-Path $outputRoot 'SHA256SUMS.txt'
# Explicit inventory excludes old packages, unrelated ZIPs and the checksum itself.
$lines = $assets | Get-FileHash -Algorithm SHA256 | ForEach-Object { "{0}  {1}" -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) }
[IO.File]::WriteAllText($checksums, ($lines -join "`n") + "`n")
if ($env:GITHUB_OUTPUT) {
    $files = (@($assets) + $checksums) | ForEach-Object { $_.Replace('\', '/') }
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, ((@('files<<END_ASSETS') + $files + 'END_ASSETS') -join "`n") + "`n")
    "signing=$signedCount/$($executables.Count) executable Authenticode signatures verified valid" >> $env:GITHUB_OUTPUT
}
Write-Host "Packaged $($assets.Count) assets. Checksums: $checksums"
