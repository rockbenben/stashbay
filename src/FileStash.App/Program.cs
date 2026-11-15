using System.IO;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Shell;

namespace FileStash.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.SequenceEqual(["--install-startup"]))
        {
            if (App.ShellIntegrationDisabled) return;
            try
            {
                var service = new LogonStartupService();
                var status = service.ReadAsync().GetAwaiter().GetResult();
                // Installing/upgrading must preserve an existing administrator choice.
                if (status.State is LogonStartupState.Elevated or LogonStartupState.ElevatedNeedsRepair) return;
                if (status.State is LogonStartupState.Unknown or LogonStartupState.Conflict)
                    throw new IOException("Installer could not confirm startup state: " + status.Detail);
                var result = service.ChangeAsync(true, false).GetAwaiter().GetResult();
                if (result.Outcome is not (StartupOutcome.Applied or StartupOutcome.Unchanged))
                    throw new IOException("Installer startup failed: " + result.Detail);
            }
            catch (Exception ex)
            {
                try { LogHelperError(ex); } catch { /* Still return a failure if the log is unwritable. */ }
                Environment.ExitCode = 4;
            }
            return;
        }
        // Keep registration entirely outside WPF, settings, IPC, tray and hook initialization.
        if (args.Length > 0 && args[0] == ElevatedStartup.HelperSwitch)
        {
            if (App.ShellIntegrationDisabled)
            {
                Environment.ExitCode = 4;
                return;
            }
            ElevatedStartup.TryHandleHelper(args, LogonStartupService.Options, LogHelperError, out int code);
            Environment.ExitCode = code;
            return;
        }
        var application = new App();
        application.InitializeComponent();
        // The generated WPF Main discards Run's result. Uninstall must observe cancellation
        // and cleanup failures before deciding whether it can remove the executable.
        Environment.ExitCode = application.Run();
    }

    private static void LogHelperError(Exception error)
    {
        var paths = new AppPaths();
        Directory.CreateDirectory(paths.LogDirectory);
        File.AppendAllText(Path.Combine(paths.LogDirectory, "startup-helper.log"),
            $"{DateTimeOffset.Now:O} executable: {Environment.ProcessPath}; {error}{Environment.NewLine}");
    }
}
