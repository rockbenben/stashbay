#Requires -Version 7.0
<#
.SYNOPSIS
    StashBay 崩溃恢复与写入安全验收脚本：验证事务断点、恢复与一致性对齐。

.DESCRIPTION
    针对写入（Apply）与撤销（Undo）过程中的异常中断与进程崩溃执行自动化验证：
      · 阶段断点模拟（Stage Checkpoints）：新建写入前、覆盖前、撤销删除前、撤销还原前
      · 崩溃后重启时的自动恢复（RecoverAsync）与事务日志对齐
      · 幂等恢复：多次 RecoverAsync 不破坏文件与数据库一致性
      · 支持可选的提权写入重试验证（-ElevatedRetry）
    所有验证均在独立的临时沙箱中运行，不触碰真实数据与注册表。

    用法示例：
      pwsh tools/recovery-checks.ps1
      pwsh tools/recovery-checks.ps1 -ElevatedRetry
#>
[CmdletBinding()]
param([switch]$ElevatedRetry, [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) ('stashbay-recovery-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$outRoot = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $outRoot) { throw "Use a fresh output directory: $outRoot" }
New-Item -ItemType Directory -Path $outRoot | Out-Null
$project = Join-Path $outRoot 'RecoveryWorker.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$repository\src\FileStash.Infrastructure\FileStash.Infrastructure.csproj" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8
@'
using System.Text.Json;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Native;
using FileStash.Infrastructure.Services;

var root = Path.GetFullPath(args[1]);
var validationRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!;
if (!PathUtil.IsUnder(validationRoot, root) || validationRoot == root)
    throw new ArgumentException("Fixture must be inside the worker validation directory");
var paths = new AppPaths(root, root);
paths.EnsureDirectories();
var factory = new SqliteConnectionFactory(paths);
await new DatabaseMigrator(factory).MigrateAsync(default);
var groups = new StashGroupRepository(factory);
var items = new StashItemRepository(factory);
var entries = new StashEntryRepository(factory);
var operations = new OperationRepository(factory);
var operationEntries = new OperationEntryRepository(factory);
var blobs = new BlobStore(paths);
var settings = new SettingsService(paths);
settings.Load();
var git = new GitService();
var source = Path.Combine(root, "source", "sample.txt");
var target = Path.Combine(root, "target", "sample.txt");
var checkpoint = args.Length > 2 ? args[2] : "";
var armed = false;
void StopAt(string stage)
{
    if (!armed || stage != checkpoint) return;
    File.WriteAllText(Path.Combine(root, "checkpoint.txt"), stage);
    Thread.Sleep(Timeout.Infinite);
}
var writer = new CheckpointWriter(StopAt);
var journal = new CheckpointJournal(operationEntries, StopAt);
var executor = new ApplyExecutor(blobs, operations, journal, writer, new RestartManagerLockInspector(), settings);
if (args[0] == "worker" || args[0] == "fixture")
{
    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
    File.WriteAllText(source, "snapshot");
    var overwrite = args[3] == "overwrite";
    if (overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "original");
    }
    var stash = new StashService(groups, items, entries, blobs, git, settings);
    var group = await stash.CreateGroupAsync("fixture");
    var added = await stash.AddAsync(group.Id, [source], null, default);
    var plan = await new ApplyPlanner(entries, git).BuildPlanAsync(added.Added,
        new TargetSpec(TargetMode.SpecifiedPath, Path.GetDirectoryName(target), StructureMode.Flatten), ConflictPolicy.Overwrite, default);
    if (args[0] == "fixture") { using var locked = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None); var failed = await executor.ExecuteAsync(plan, null, default); Console.WriteLine(JsonSerializer.Serialize(new { failed.Id, target })); return; }
    armed = !checkpoint.StartsWith("Undo");
    var operation = await executor.ExecuteAsync(plan, null, default);
    if (checkpoint.StartsWith("Undo"))
    {
        armed = true;
        await executor.UndoAsync(operation.Id, default);
    }
    throw new Exception("Checkpoint was not reached: " + checkpoint);
}
if (args[0] == "verify")
{
    var before = await executor.RecoverAsync(default);
    await executor.RecoverAsync(default);
    var operation = (await operations.GetAllAsync(default)).First();
    var rows = await operationEntries.GetByOperationAsync(operation.Id, default);
    if (rows.Any(row => row.Pending || row.UndoPending)) throw new Exception("Unresolved journal after recovery");
    var recovered = File.Exists(target) ? File.ReadAllText(target) : null;
    await executor.UndoAsync(operation.Id, default);
    await executor.UndoAsync(operation.Id, default);
    var final = File.Exists(target) ? File.ReadAllText(target) : null;
    var expected = args[3] == "overwrite" ? "original" : null;
    if (final != expected) throw new Exception($"Restoration mismatch: {final} vs {expected}");
    Console.WriteLine(JsonSerializer.Serialize(new { checkpoint, reconciled = before, recovered, final, passed = true }));
}

if (args[0] == "elevated-check")
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var elevated = new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    if (!elevated) throw new Exception("Administrator token required");
    var operation = (await operations.GetAllAsync(default)).First();
    var selection = await executor.PrepareRetryAsync(operation.Id, default, inspectTargets: false);
    if (selection.Count != 1 || selection[0].TargetPath != target
        || selection[0].Sha256 != HashUtil.ComputeSha256(System.Text.Encoding.UTF8.GetBytes("snapshot")))
        throw new Exception("Unexpected fixture authorization");
    var count = await executor.RetryApprovedAsync(operation.Id, selection, default);
    if (count != 1 || File.ReadAllText(target) != "snapshot") throw new Exception("Elevated retry failed");
    await executor.UndoAsync(operation.Id, default);
    if (File.ReadAllText(target) != "original") throw new Exception("Elevated undo failed");
    File.WriteAllText(Path.Combine(root, "elevated-result.json"),
        JsonSerializer.Serialize(new { elevated, approved = count, restored = true, operation.Id }));
}
if (args[0] == "protect-fixture")
{
    var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
    var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
    var folderAcl = new System.Security.AccessControl.DirectorySecurity();
    folderAcl.SetAccessRuleProtection(true, false);
    folderAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(user,
        System.Security.AccessControl.FileSystemRights.ReadAndExecute,
        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
    folderAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(admins,
        System.Security.AccessControl.FileSystemRights.FullControl,
        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
    new DirectoryInfo(Path.GetDirectoryName(target)!).SetAccessControl(folderAcl);
    var fileAcl = new System.Security.AccessControl.FileSecurity();
    fileAcl.SetAccessRuleProtection(true, false);
    fileAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(user,
        System.Security.AccessControl.FileSystemRights.ReadAndExecute, System.Security.AccessControl.AccessControlType.Allow));
    fileAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(admins,
        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
    new FileInfo(target).SetAccessControl(fileAcl);
}
if (args[0] == "permission-failure")
{
    var denied = false;
    try { File.WriteAllText(target, "unexpected"); } catch (UnauthorizedAccessException) { denied = true; }
    if (!denied) throw new Exception("Fixture does not require elevation");
    var group = (await groups.GetAllAsync(default)).Single();
    var plan = await new ApplyPlanner(entries, git).BuildPlanAsync(await items.GetByGroupAsync(group.Id, default),
        new TargetSpec(TargetMode.SpecifiedPath, Path.GetDirectoryName(target), StructureMode.Flatten), ConflictPolicy.Overwrite, default);
    var failed = await executor.ExecuteAsync(plan, null, default);
    if (failed.Status != OperationStatus.PartiallyFailed || File.ReadAllText(target) != "original")
        throw new Exception("Permission failure was not preserved");
    File.WriteAllText(Path.Combine(root, "medium-denied.json"), JsonSerializer.Serialize(new { denied, failed.Id }));
}
if (args[0] == "unprotect-fixture")
{
    var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
    var folder = new DirectoryInfo(Path.GetDirectoryName(target)!);
    var folderAcl = folder.GetAccessControl();
    folderAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(user,
        System.Security.AccessControl.FileSystemRights.FullControl,
        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
    folder.SetAccessControl(folderAcl);
    var file = new FileInfo(target);
    var fileAcl = file.GetAccessControl();
    fileAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(user,
        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
    file.SetAccessControl(fileAcl);
}

sealed class CheckpointWriter(Action<string> stop) : IAtomicFileWriter
{
    private readonly AtomicFileWriter inner = new();
    public string? GetIdentity(string path) => inner.GetIdentity(path);
    public IDisposable? GuardTarget(string path) => inner.GuardTarget(path);
    public async Task<bool> CreateAsync(string temporary, string target, CancellationToken ct)
    {
        stop("BeforeCommit");
        var result = await inner.CreateAsync(temporary, target, ct);
        stop("AfterCommit");
        return result;
    }
    public async Task<bool> ReplaceAsync(string temporary, string target, CancellationToken ct)
    {
        stop("BeforeCommit");
        var result = await inner.ReplaceAsync(temporary, target, ct);
        stop("AfterCommit");
        return result;
    }
}
sealed class CheckpointJournal(IOperationEntryRepository inner, Action<string> stop) : IOperationEntryRepository
{
    public Task AddManyAsync(IReadOnlyList<OperationEntry> entries, CancellationToken ct) => inner.AddManyAsync(entries, ct);
    public Task<IReadOnlyList<OperationEntry>> GetByOperationAsync(string id, CancellationToken ct) => inner.GetByOperationAsync(id, ct);
    public Task<IReadOnlySet<string>> GetReferencedBackupSha256Async(CancellationToken ct) => inner.GetReferencedBackupSha256Async(ct);
    public async Task UpdateAsync(OperationEntry entry, CancellationToken ct)
    {
        if (entry.Undone && !entry.UndoPending) stop("UndoAfterCommit");
        await inner.UpdateAsync(entry, ct);
        if (entry.UndoPending) stop("UndoBeforeCommit");
        else if (entry.Undone) stop("UndoAfterRecorded");
        else if (!entry.Pending && entry.Action is EntryResult.Created or EntryResult.Overwritten) stop("AfterRecorded");
    }
}
'@ | Set-Content -LiteralPath (Join-Path $outRoot 'Program.cs') -Encoding utf8
& dotnet build $project -o (Join-Path $outRoot 'bin') --nologo
if ($LASTEXITCODE) { throw 'Worker build failed' }
$worker = Join-Path $outRoot 'bin/RecoveryWorker.exe'
$results = @()
foreach ($kind in 'create', 'overwrite') {
    foreach ($stage in 'BeforeCommit', 'AfterCommit', 'AfterRecorded', 'UndoBeforeCommit', 'UndoAfterCommit', 'UndoAfterRecorded') {
        $caseRoot = Join-Path $outRoot "$kind-$stage"
        $startInfo = [Diagnostics.ProcessStartInfo]::new($worker)
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        foreach ($argument in @('worker', $caseRoot, $stage, $kind)) { $startInfo.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($startInfo)
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not (Test-Path -LiteralPath (Join-Path $caseRoot 'checkpoint.txt'))) {
                if ($process.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw "Checkpoint failed: $kind/$stage" }
                Start-Sleep -Milliseconds 30
            }
            $process.Kill()
            $process.WaitForExit()
        } finally {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
        $output = & $worker verify $caseRoot $stage $kind
        if ($LASTEXITCODE) { throw "Recovery failed: $kind/$stage" }
        $results += ($output | ConvertFrom-Json)
        Write-Host "PASS $kind/$stage"
    }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outRoot 'results.json') -Encoding utf8
if ($ElevatedRetry) {
    $caseRoot = Join-Path $outRoot 'elevated-fixture'
    & $worker fixture $caseRoot fixture overwrite
    if ($LASTEXITCODE) { throw 'Elevated fixture initialization failed' }
    function Run-ElevatedFixture([string]$mode) {
        $process = Start-Process $worker -ArgumentList @($mode, $caseRoot) -Verb RunAs -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(90000)) { throw 'Elevated fixture timed out' }
        if ($process.ExitCode) { throw "Elevated fixture failed: $mode" }
    }
    try {
        Run-ElevatedFixture 'protect-fixture'
        & $worker permission-failure $caseRoot
        if ($LASTEXITCODE) { throw 'Medium token permission failure was not reproduced' }
        Run-ElevatedFixture 'elevated-check'
        Get-Content -LiteralPath (Join-Path $caseRoot 'elevated-result.json')
    } finally {
        Run-ElevatedFixture 'unprotect-fixture'
    }
}
Write-Host "Results: $outRoot"

