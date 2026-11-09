#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$CurrentPackages,
    [Parameter(Mandatory)][string]$BaselineRepository,
    [Parameter(Mandatory)][string]$BaselinePackages,
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) ('stashbay-installer-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$outRoot = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $outRoot) { throw "Use a fresh validation directory: $outRoot" }
New-Item -ItemType Directory $outRoot | Out-Null
$testId = [guid]::NewGuid().ToString()
$testName = 'StashBay Validation ' + $testId

function Build-TestInstaller([string]$sourceRoot, [string]$packages, [string]$version) {
    $text = Get-Content -LiteralPath (Join-Path $sourceRoot 'tools/installer/FileStash.iss') -Raw
    # Isolate application identity and OS-facing paths. File installation and
    # uninstall rules stay exactly as declared by the corresponding source.
    $text = $text -replace '(?m)^AppId=.*$', ('AppId={{' + $testId + '}')
    $text = $text.Replace('#define AppName "StashBay"', ('#define AppName "' + $testName + '"'))
    $text = $text.Replace('..\..\artifacts', [IO.Path]::GetFullPath($packages))
    $text = $text.Replace('..\..\src\FileStash.App\Resources\app.ico', (Join-Path $sourceRoot 'src/FileStash.App/Resources/app.ico'))
    $text = $text.Replace('lang\ChineseSimplified.isl', (Join-Path $sourceRoot 'tools/installer/lang/ChineseSimplified.isl'))
    $text = $text.Replace('{userappdata}', (Join-Path $outRoot 'roaming'))
    $text = $text.Replace('{group}', (Join-Path $outRoot 'shortcuts'))
    $text = $text.Replace('{autodesktop}', (Join-Path $outRoot 'desktop'))
    $script = Join-Path $outRoot "$version.iss"
    $text | Set-Content -LiteralPath $script -Encoding utf8
    & $Compiler "/DAppVersion=$version" "/O$outRoot" $script | Set-Content -LiteralPath (Join-Path $outRoot "$version-compile.log")
    if ($LASTEXITCODE) { throw "Installer compilation failed: $version" }
    return Join-Path $outRoot "StashBay-$version-setup.exe"
}

function Run-TestProcess([string]$exe, [string[]]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.Environment['STASHBAY_HOME'] = Join-Path $outRoot 'data'
    $start.Environment['STASHBAY_NO_SHELL_INTEGRATION'] = '1'
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(90000)) { $process.Kill(); throw 'Installer validation timed out' }
    if ($process.ExitCode) { throw "Validation process failed: $($process.ExitCode)" }
    $process.Dispose()
}

$legacy = Build-TestInstaller ([IO.Path]::GetFullPath($BaselineRepository)) $BaselinePackages '1.0.0'
$current = Build-TestInstaller $repository $CurrentPackages '1.0.1'
$results = @()
foreach ($scenario in 'fresh', 'upgrade') {
    $install = Join-Path $outRoot $scenario
    $first = if ($scenario -eq 'upgrade') { $legacy } else { $current }
    $arguments = @('/CURRENTUSER', '/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=', "/DIR=$install")
    Run-TestProcess $first ($arguments + "/LOG=$outRoot/$scenario-first.log")
    $sentinels = @((Join-Path $install 'user-notes.txt'), (Join-Path "$install-needs-dotnet10" 'user-notes.txt'), (Join-Path $outRoot "$scenario-neighbor.txt"))
    foreach ($file in $sentinels) { [IO.File]::WriteAllText($file, 'User-owned content must survive') }
    if ($scenario -eq 'upgrade') { Run-TestProcess $current ($arguments + "/LOG=$outRoot/$scenario-update.log") }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'StashBay.exe')).ProductVersion
    if (-not $version.StartsWith('1.0.1')) { throw "Unexpected installed version: $version" }
    Run-TestProcess (Join-Path $install 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$outRoot/$scenario-uninstall.log")
    $preserved = @($sentinels | Where-Object { (Test-Path -LiteralPath $_) -and [IO.File]::ReadAllText($_) -eq 'User-owned content must survive' }).Count
    # Inno's final helper deletes its own temporary/uninstall files after the
    # parent process exits. Bound the wait rather than treating that as residue.
    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $remainingOwnedFiles = @(Get-ChildItem -LiteralPath $install,"$install-needs-dotnet10" -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notin $sentinels })
        if ($remainingOwnedFiles.Count) { Start-Sleep -Milliseconds 100 }
    } while ($remainingOwnedFiles.Count -and [DateTime]::UtcNow -lt $cleanupDeadline)
    $programRemoved = $remainingOwnedFiles.Count -eq 0
    $results += [pscustomobject]@{ scenario=$scenario; version=$version; preserved=$preserved; expected=$sentinels.Count; programRemoved=$programRemoved; remainingOwnedFiles=$remainingOwnedFiles.Count }
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outRoot 'results.json') -Encoding utf8
    if ($preserved -ne $sentinels.Count -or -not $programRemoved) { throw "Uninstall ownership failure: $scenario (preserved=$preserved)" }
    Write-Host "PASS $scenario installation/uninstallation"
}
Write-Host "Results: $outRoot"
