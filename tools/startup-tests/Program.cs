using FileStash.Infrastructure.Shell;
using FileStash.StartupTests;
using System.Runtime.InteropServices;
if (args.SequenceEqual(["--validate-xml"]))
{
    var target = ElevatedStartup.Target(LogonStartupService.Options, @"D:\便携 & apps\StashBay.exe", "S-1-5-21-1-2-3-1001");
    object? service = null, definition = null;
    try
    {
        service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
        ((dynamic)service!).Connect();
        foreach (int seconds in new[] { 0, 3, 17, 300 })
        {
            var configured = target with { Options = target.Options with { LogonDelaySeconds = seconds } };
            definition = ((dynamic)service).NewTask(0);
            ((dynamic)definition).XmlText = ElevatedStartup.TaskXml(configured);
            if ((int)((dynamic)definition).Settings.Priority != 4 ||
                !ElevatedStartup.Matches((string)((dynamic)definition).XmlText, configured))
                throw new Exception("Native XML readback mismatch at delay " + seconds);
            Console.WriteLine("Task Scheduler parsed/read back Normal priority and delay " + seconds + "; no task registered.");
            Marshal.FinalReleaseComObject(definition);
            definition = null;
        }
    }
    finally
    {
        foreach (var item in new[] { definition, service })
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
    }
    return;
}
RegressionSuite.Run();
