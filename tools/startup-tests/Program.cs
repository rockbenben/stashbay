using FileStash.Infrastructure.Shell;
using FileStash.StartupTests;
using System.Runtime.InteropServices;

if (args.SequenceEqual(["--validate-xml"]))
{
    var target = ElevatedStartup.Target(LogonStartupService.Options, @"D:\便携 & apps\StashBay.exe", "S-1-5-21-1-2-3-1001");
    using (var service = new Microsoft.Win32.TaskScheduler.TaskService())
    {
        foreach (int seconds in new[] { 0, 3, 17, 300 })
        {
            var configured = target with { Options = target.Options with { LogonDelaySeconds = seconds } };
            using var definition = service.NewTask();
            definition.XmlText = ElevatedStartup.TaskXml(configured);
            if (definition.Settings.Priority != System.Diagnostics.ProcessPriorityClass.Normal ||
                !ElevatedStartup.Matches(definition.XmlText, configured))
                throw new Exception("Native XML readback mismatch at delay " + seconds);
            Console.WriteLine("Task Scheduler parsed/read back Normal priority and delay " + seconds + "; no task registered.");
        }
    }
    if (new WindowsStartupStore().ReadTask(LogonStartupService.Options.AppId + ".Missing." + Guid.NewGuid().ToString("N")) is not null)
        throw new Exception("A random missing task was reported as present.");
    Console.WriteLine("TaskScheduler strict named lookup recognized a missing task; no task registered.");
    return;
}
RegressionSuite.Run();
