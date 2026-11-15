#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('full','undo','first-write','quick')][string]$Mode='full',
    [string]$Out=(Join-Path ([IO.Path]::GetTempPath()) ('stashbay-performance-'+[guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference='Stop'
$Mode=$Mode.ToLowerInvariant()
$repository=Split-Path -Parent $PSScriptRoot
$outRoot=[IO.Path]::GetFullPath($Out)
if(Test-Path -LiteralPath $outRoot) { throw "Use a fresh output directory: $outRoot" }
New-Item -ItemType Directory -Path $outRoot | Out-Null
$source=Join-Path $outRoot 'source'
New-Item -ItemType Directory -Path $source | Out-Null
$baseline=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance/baseline-manifest.json') -Raw | ConvertFrom-Json
foreach($entry in $baseline.files.PSObject.Properties) {
    if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot ("performance/"+$entry.Name))).Hash -ne $entry.Value) {
        throw "Frozen baseline checksum changed: $($entry.Name)"
    }
}
$inventory=@()
$roots=@('src/FileStash.Core','src/FileStash.Infrastructure','tools/performance')
foreach($relativeRoot in $roots) {
    $original=Join-Path $repository $relativeRoot
    foreach($file in Get-ChildItem -LiteralPath $original -Recurse -File -Force) {
        $relative=[IO.Path]::GetRelativePath($repository,$file.FullName)
        if($relative -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        $destination=Join-Path $source $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        $hash=(Get-FileHash -LiteralPath $file.FullName).Hash
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        if((Get-FileHash -LiteralPath $destination).Hash -ne $hash -or
           (Get-FileHash -LiteralPath $file.FullName).Hash -ne $hash) { throw "Source changed while copying: $relative" }
        $inventory += [pscustomobject]@{path=$relative;sha256=$hash}
    }
}
$rootFiles=@(Get-ChildItem -LiteralPath $repository -File | Where-Object { $_.Name -like 'Directory.*.props' -or $_.Name -like 'Directory.*.targets' -or $_.Name -in 'global.json','NuGet.Config','nuget.config' })
foreach($file in $rootFiles) {
    $destination=Join-Path $source $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    $hash=(Get-FileHash -LiteralPath $file.FullName).Hash
    if((Get-FileHash -LiteralPath $destination).Hash -ne $hash) { throw 'Root build file changed' }
    $inventory += [pscustomobject]@{path=$file.Name;sha256=$hash}
}
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outRoot 'source-manifest.json')
$env:STASHBAY_HOME=Join-Path $outRoot 'home'
$env:STASHBAY_NO_SHELL_INTEGRATION='1'
$metadata=[ordered]@{mode=$Mode;status='running';startedUtc=[DateTime]::UtcNow.ToString('o');
    sdk=(& dotnet --version | Out-String).Trim();os=[Runtime.InteropServices.RuntimeInformation]::OSDescription;
    architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString();
    outputDirectory=$outRoot;memoryBytes=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;
    volume=@(Get-Volume -FilePath $outRoot -ErrorAction SilentlyContinue | Select-Object DriveLetter,FileSystem,SizeRemaining);
    cpu=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfLogicalProcessors);
    cache='No forced OS cache clearing; same warm preparation; process I/O includes cache';
    sourceManifest='source-manifest.json';worker='source/tools/performance/Program.cs'}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outRoot 'run.json')
try {
    $project=Join-Path $source 'tools/performance/Performance.csproj'
    & dotnet build $project -c Release -o (Join-Path $outRoot 'bin') --nologo *> (Join-Path $outRoot 'build.log')
    if($LASTEXITCODE) { throw 'Performance worker build failed; see build.log' }
    & (Join-Path $outRoot 'bin/Performance.exe') $outRoot $Mode | Tee-Object -FilePath (Join-Path $outRoot 'run.log')
    if($LASTEXITCODE) { throw "Performance run failed ($LASTEXITCODE); inspect retained fixture" }
    $metadata.status='passed'
} catch {
    $metadata.status='failed'
    throw
} finally {
    $metadata.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outRoot 'run.json')
}
Write-Host "Results: $outRoot"
