using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Native;
using FileStash.Infrastructure.Services;

var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[0]));
var mode = args[1];
if (mode is not ("full" or "undo" or "first-write" or "quick")) throw new ArgumentException("Unknown mode");
var quick = mode == "quick";
var workflowRounds = mode == "undo" ? 12 : quick ? 1 : 3;
var microRounds = mode == "first-write" ? 10 : quick ? 1 : 5;
var largeBytes = quick ? 16L*1024*1024 : 2L*1024*1024*1024+1;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel=true; cancellation.Cancel(); };
var completed = false;
var dataRoot = Path.Combine(output, "data-" + Guid.NewGuid().ToString("N"));
if (Directory.Exists(dataRoot)) throw new IOException("Generated directory already exists");
Directory.CreateDirectory(dataRoot);
var results = new List<Sample>();
using var process = Process.GetCurrentProcess();
var ct = cancellation.Token;
IBlobStore MakeStore(string home, bool current)
{
    var paths = new AppPaths(home, home); paths.EnsureDirectories();
    return current ? new BlobStore(paths) : new BenchmarkBaseline.BlobStore(paths);
}
void DeleteGeneratedDirectory(string directory)
{
    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    var generated = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
    if (!string.Equals(full, generated, StringComparison.OrdinalIgnoreCase)
        && !full.StartsWith(generated + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Cleanup target outside generated fixture");
    foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(full, recursive: true);
}
async Task<Sample> Measure(string scenario, string variant, long bytes, int round, Func<Task> action)
{
    var beforeCpu = process.TotalProcessorTime;
    var ioBefore = ProcessIo.Read();
    var clock = Stopwatch.StartNew();
    await action();
    clock.Stop();
    var ioAfter = ProcessIo.Read();
    var value = new Sample(scenario, variant, bytes, round, clock.Elapsed.TotalMilliseconds,
        (process.TotalProcessorTime - beforeCpu).TotalMilliseconds,
        ioAfter.ReadBytes-ioBefore.ReadBytes, ioAfter.WriteBytes-ioBefore.WriteBytes);
    results.Add(value);
    await File.WriteAllTextAsync(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine($"{scenario} {variant} {bytes} round={round}: {value.Milliseconds:F2} ms cpu={value.CpuMilliseconds:F2} ms");
    return value;
}
async Task<string> Source(long bytes)
{
    var path = Path.Combine(dataRoot, $"source-{bytes}.bin");
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4*1024*1024, true);
    var buffer = new byte[4*1024*1024];
    for (long written=0; written<bytes; )
    {
        RandomNumberGenerator.Fill(buffer);
        var count=(int)Math.Min(buffer.Length,bytes-written);
        ct.ThrowIfCancellationRequested();
        await stream.WriteAsync(buffer.AsMemory(0,count), ct); written+=count;
    }
    return path;
}
try
{
    foreach (var bytes in (mode == "undo" ? Array.Empty<long>() : mode == "first-write" ? new[] { largeBytes } : new[] { quick ? 1024L*1024 : 256L*1024*1024, largeBytes }))
    {
        var source=await Source(bytes);
        var expected=await HashUtil.ComputeFileSha256Async(source,ct);
        var before=MakeStore(Path.Combine(dataRoot,$"before-{bytes}"),false);
        var after=MakeStore(Path.Combine(dataRoot,$"after-{bytes}"),true);
        foreach(var store in new[]{before,after}) await store.PutFileAsync(source,ct);
        // Warm both implementations before collecting five alternating paired rounds.
        foreach(var store in new[]{before,after})
        {
            var warm=Path.Combine(dataRoot,"warm.bin");
            await store.CopyBlobToAsync(expected,warm,ct); File.Delete(warm);
        }
        foreach(var scenario in (mode == "first-write" ? new[]{"new-put"} : new[]{"copy","deduplicated-put","new-put"}))
        {
            for(var round=0;round<microRounds;round++)
            {
                var variants=round%2==0 ? new[]{false,true} : new[]{true,false};
                foreach(var current in variants)
                {
                    var store=current?after:before;
                    var variant=current?"optimized":"before";
                    var destination=Path.Combine(dataRoot,"copy.bin");
                    if(scenario=="new-put")
                    {
                        var blob=store.GetBlobPath(expected);
                        File.SetAttributes(blob,FileAttributes.Normal); File.Delete(blob);
                    }
                    await Measure(scenario,variant,bytes,round,async()=>{
                        if(scenario=="copy") await store.CopyBlobToAsync(expected,destination,ct);
                        else
                        {
                            var stored=await store.PutFileAsync(source,ct);
                            if(stored.Sha256!=expected || stored.Size!=bytes) throw new Exception("Snapshot result mismatch");
                        }
                    });
                    if(scenario=="copy")
                    {
                        if(await HashUtil.ComputeFileSha256Async(destination,ct)!=expected) throw new Exception("Copied file differs");
                        File.Delete(destination);
                    }
                }
            }
            Console.WriteLine($"Completed paired {scenario}/{bytes}");
        }
    }
    if (mode != "first-write")
    {
        var largeSource=Path.Combine(dataRoot,$"source-{largeBytes}.bin");
        if(!File.Exists(largeSource)) await Source(largeBytes);
        for(var round=0;round<workflowRounds;round++)
        {
            foreach(var current in (round%2==0?new[]{false,true}:new[]{true,false}))
            {
                var variant=current?"optimized":"before";
                var home=Path.Combine(dataRoot,$"workflow-{variant}-{round}");
                var paths=new AppPaths(home,home); paths.EnsureDirectories();
                var factory=new SqliteConnectionFactory(paths);
                await new DatabaseMigrator(factory).MigrateAsync(ct);
                var groups=new StashGroupRepository(factory);
                var items=new StashItemRepository(factory);
                var entries=new StashEntryRepository(factory);
                var operations=new OperationRepository(factory);
                var operationEntries=new OperationEntryRepository(factory);
                var store=MakeStore(home,current);
                var settings=new SettingsService(paths); settings.Load();
                var git=new GitService();
                var stash=new StashService(groups,items,entries,store,git,settings);
                var executor=new ApplyExecutor(store,operations,operationEntries,new AtomicFileWriter(),new RestartManagerLockInspector(),settings);
                var group=await stash.CreateGroupAsync("benchmark");
                AddResult? added=null;
                var bytes=new FileInfo(largeSource).Length;
                await Measure("workflow-add",variant,bytes,round,async()=>added=await stash.AddAsync(group.Id,[largeSource],null,ct));
                var item=added!.Added.Single();
                IReadOnlyList<string>? exported=null;
                await Measure("workflow-export",variant,bytes,round,async()=>exported=await new DragExportService(entries,store,paths).MaterializeAsync([item],ct));
                if(await HashUtil.ComputeFileSha256Async(exported!.Single(),ct)!=await HashUtil.ComputeFileSha256Async(largeSource,ct)) throw new Exception("Export mismatch");
                DeleteGeneratedDirectory(Path.GetDirectoryName(exported!.Single())!);
                var targetDirectory=Path.Combine(home,"target"); Directory.CreateDirectory(targetDirectory);
                var target=Path.Combine(targetDirectory,Path.GetFileName(largeSource));
                File.Copy(largeSource,target);
                using(var stream=File.Open(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                { var first=stream.ReadByte();stream.Position=0;stream.WriteByte((byte)(first^255)); }
                var originalHash=await HashUtil.ComputeFileSha256Async(target,ct);
                ApplyPlan? plan=null;
                IApplyPlanner planner=current ? new ApplyPlanner(entries,git) : new BenchmarkPlannerBaseline.ApplyPlanner(entries,git);
                await Measure("workflow-preview",variant,bytes,round,async()=>plan=await planner.BuildPlanAsync([item],new TargetSpec(TargetMode.SpecifiedPath,targetDirectory,StructureMode.Flatten),ConflictPolicy.Overwrite,ct));
                Operation? operation=null;
                await Measure("workflow-apply",variant,bytes,round,async()=>operation=await executor.ExecuteAsync(plan!,null,ct));
                if(operation!.Status!=OperationStatus.Succeeded) throw new Exception("Apply failed");
                UndoResult? undone=null;
                await Measure("workflow-undo",variant,bytes,round,async()=>undone=await executor.UndoAsync(operation.Id,ct));
                if(undone!.HasIssues || await HashUtil.ComputeFileSha256Async(target,ct)!=originalHash) throw new Exception("Undo mismatch");
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteGeneratedDirectory(home);
            }
        }
    }
    Summary.Save(output, results);
    completed = true;
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if(completed && Directory.Exists(dataRoot))
    {
        DeleteGeneratedDirectory(dataRoot);
    }
    if (!completed) Console.Error.WriteLine("Incomplete run; generated fixture retained at " + dataRoot);
}
Console.WriteLine("PASS " + mode + "; validated outputs, generated data cleaned");
record Sample(string Scenario,string Variant,long Bytes,int Round,double Milliseconds,double CpuMilliseconds,ulong ReadBytes,ulong WriteBytes);
static class ProcessIo
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Counters { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetProcessIoCounters(IntPtr process,out Counters counters);
    public static Counters Read()
    {
        using var currentProcess = Process.GetCurrentProcess();
        if(!GetProcessIoCounters(currentProcess.Handle,out var value))
            throw new System.ComponentModel.Win32Exception();
        return value;
    }
}
