#Requires -Version 7.4
<#
Checks both published x64 forms without UAC or OS startup writes. In particular,
the failing seed command must propagate WPF Shutdown(1) through the real entry point.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishRoot)
$ErrorActionPreference = 'Stop'
$publishDirectory = (Resolve-Path -LiteralPath $PublishRoot).Path
$sandboxRoot = Join-Path ([IO.Path]::GetTempPath()) ('stashbay-startup-entry-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandboxRoot | Out-Null

function Invoke-IsolatedApp([string]$Executable, [string]$Sandbox, [string[]]$Arguments, [string]$Guard = 'STASHBAY_NO_SHELL_INTEGRATION') {
    New-Item -ItemType Directory -Path $Sandbox -Force | Out-Null
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.Environment['STASHBAY_HOME'] = Join-Path $Sandbox 'home'
    $info.Environment['TEMP'] = $Sandbox
    $info.Environment['TMP'] = $Sandbox
    $info.Environment.Remove('STASHBAY_NO_SHELL_INTEGRATION') | Out-Null
    $info.Environment.Remove('FILESTASH_NO_SHELL_INTEGRATION') | Out-Null
    $info.Environment[$Guard] = '1'
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        while (-not $process.WaitForExit(1000)) {
            if ([DateTime]::UtcNow -gt $deadline) { $process.Kill(); throw 'Entry-point check timed out' }
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}

foreach ($form in 'x64', 'x64-needs-dotnet10') {
    $exe = Join-Path $publishDirectory "publish-$form/StashBay.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "Missing executable: $exe" }
    $smoke = Join-Path $sandboxRoot "$form-smoke"
    $code = Invoke-IsolatedApp $exe $smoke @('--smoke')
    $marker = Join-Path $smoke 'filestash-smoke.txt'
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'OK') {
        throw "Published smoke failed: $form"
    }
    Write-Output "PASS $form published smoke"

    foreach ($guard in 'STASHBAY_NO_SHELL_INTEGRATION', 'FILESTASH_NO_SHELL_INTEGRATION') {
        $helper = Join-Path $sandboxRoot "$form-$guard"
        $code = Invoke-IsolatedApp $exe $helper @('--startup-task', 'repair', 'S-1-5-21-1-2-3-1001') $guard
        if ($code -ne 4 -or (Test-Path -LiteralPath (Join-Path $helper 'home'))) {
            throw "Helper did not stop before WPF/settings initialization: $form / $guard"
        }
    }
    Write-Output "PASS $form current and legacy no-shell helper guards"

    $failure = Join-Path $sandboxRoot "$form-failure"
    $missingWorkspace = Join-Path $failure 'workspace-does-not-exist'
    $code = Invoke-IsolatedApp $exe $failure @('--seed', $missingWorkspace)
    $failureMarker = Join-Path $failure 'filestash-smoke.txt'
    if ($code -ne 1 -or -not (Test-Path -LiteralPath $failureMarker) -or (Get-Content -LiteralPath $failureMarker -Raw).Trim() -eq 'OK') {
        throw "WPF failure exit code was lost: $form / $code"
    }
    Write-Output "PASS $form WPF failure exit code = 1"
}
Write-Output "Entry-point checks passed. Isolated logs: $sandboxRoot"
