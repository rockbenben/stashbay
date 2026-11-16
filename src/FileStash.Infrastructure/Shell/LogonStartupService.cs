using System.Security.Principal;

namespace FileStash.Infrastructure.Shell;

public enum LogonStartupState { Disabled, Plain, Elevated, PlainNeedsRepair, ElevatedNeedsRepair, Conflict, Unknown, Suppressed }
public sealed record LogonStartupStatus(LogonStartupState State, string? Detail = null);

public interface ILogonStartupService
{
    bool IsPending { get; }
    Task<LogonStartupStatus> ReadAsync(int? delaySeconds = null);
    Task<StartupResult> ChangeAsync(bool enabled, bool elevated, bool repair = false, int delaySeconds = 3);
}

/// <summary>One owner for normal Run entries / Startup shortcuts and the opt-in administrator logon task.</summary>
public sealed class LogonStartupService : ILogonStartupService
{
    public static readonly StartupOptions Options = new("StashBay", "StashBay.exe",
        "--elevated-logon", "FileStash", "--logon-startup");
    private readonly bool _disabled;
    private readonly Func<int> _delay;
    private readonly IStartupStore _store;
    private readonly StartupTarget? _target;
    private readonly Func<StartupOperation, StartupOptions, Task<StartupResult>> _request;
    private readonly Func<bool> _pending;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StartupOptions _options;

    public LogonStartupService(bool disabled = false, IStartupStore? store = null, StartupTarget? target = null,
        Func<StartupOperation, Task<StartupResult>>? request = null, Func<bool>? pending = null, Func<int>? delay = null,
        Func<StartupOperation, StartupOptions, Task<StartupResult>>? configuredRequest = null,
        StartupOptions? options = null)
    {
        _disabled = disabled;
        _store = store ?? new WindowsStartupStore();
        _target = target;
        _delay = delay ?? (() => 3);
        _request = configuredRequest ?? ((operation, opt) => request is null
            ? ElevatedStartup.ChangeAsync(operation, opt) : request(operation));
        _pending = pending ?? (() => ElevatedStartup.IsHelperPending);
        _options = options ?? Options;
    }

    private StartupTarget Target => TargetFor(_delay());

    private StartupTarget TargetFor(int seconds)
    {
        ElevatedStartup.ValidateDelay(seconds);
        if (_target is not null) return _target with { Options = _target.Options with { LogonDelaySeconds = seconds } };
        using var identity = WindowsIdentity.GetCurrent();
        return ElevatedStartup.Target(_options with { LogonDelaySeconds = seconds }, Environment.ProcessPath ?? "", identity.User!.Value);
    }

    public bool IsPending => !_disabled && (_gate.CurrentCount == 0 || _pending());

    public Task<LogonStartupStatus> ReadAsync(int? delaySeconds = null) => Task.Run(() =>
    {
        if (_disabled) return new LogonStartupStatus(LogonStartupState.Suppressed);
        if (IsPending) return new LogonStartupStatus(LogonStartupState.Unknown, "Startup helper is still running.");
        try
        {
            var target = TargetFor(delaySeconds ?? _delay());
            string? task = _store.ReadTask(target.TaskName);
            string? run = _store.ReadRun(target.Options.AppId);
            string? legacy = target.Options.LegacyRunValueName is { } name ? _store.ReadRun(name) : null;
            var shortcut = _store.ReadShortcut(target.Options.AppId);
            if (task is not null)
            {
                if (!ElevatedStartup.Owned(task, target)) return new(LogonStartupState.Conflict, target.TaskName);
                return new(ElevatedStartup.Matches(task, target) && run is null && legacy is null && shortcut is null
                    ? LogonStartupState.Elevated : LogonStartupState.ElevatedNeedsRepair, target.TaskName);
            }
            if (target.Options.UseStartupShortcut)
            {
                var expectedShortcut = ElevatedStartup.PlainShortcut(target);
                return new(run is null && legacy is null && shortcut is null ? LogonStartupState.Disabled :
                    run is null && legacy is null && shortcut == expectedShortcut ? LogonStartupState.Plain :
                    LogonStartupState.PlainNeedsRepair);
            }
            return new(run is null && legacy is null && shortcut is null ? LogonStartupState.Disabled :
                run == ElevatedStartup.PlainCommand(target) && legacy is null && shortcut is null ? LogonStartupState.Plain :
                LogonStartupState.PlainNeedsRepair);
        }
        catch (Exception ex) { return new(LogonStartupState.Unknown, Diagnostic("query", ex)); }
    });

    public async Task<StartupResult> ChangeAsync(bool enabled, bool elevated, bool repair = false, int delaySeconds = 3)
    {
        if (_disabled) return new(StartupOutcome.Unchanged);
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            return new(StartupOutcome.Uncertain, "Another startup operation is in progress.");
        try
        {
            if (_pending()) return new(StartupOutcome.Uncertain, "Startup helper is still running; refresh status later.");
            return await Task.Run(async () =>
            {
                var target = TargetFor(delaySeconds);
                string? task = _store.ReadTask(target.TaskName);
                if (task is not null && !ElevatedStartup.Owned(task, target))
                    return new StartupResult(StartupOutcome.Failed, "Task ownership conflict: " + target.TaskName + "; executable: " + target.Executable,
                        StartupFailure.OwnershipConflict);
                var operation = !enabled ? StartupOperation.Uninstall : !elevated ? StartupOperation.Plain :
                    repair ? StartupOperation.Repair : StartupOperation.Install;
                // No task means ordinary startup can be changed without UAC. A fully correct
                // elevated task also needs no writes. Every actual task mutation uses the helper.
                bool correctElevated = enabled && elevated && ElevatedStartup.Matches(task, target) &&
                    _store.ReadRun(target.Options.AppId) is null &&
                    (target.Options.LegacyRunValueName is not { } legacy || _store.ReadRun(legacy) is null) &&
                    _store.ReadShortcut(target.Options.AppId) is null;
                if (correctElevated) return new StartupResult(StartupOutcome.Unchanged);
                if (task is null && operation is StartupOperation.Plain or StartupOperation.Uninstall)
                    return ElevatedStartup.Apply(operation, target, _store);
                return await _request(operation, target.Options).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { return new(StartupOutcome.Failed, Diagnostic("change preflight", ex), ElevatedStartup.Classify(ex)); }
        finally { _gate.Release(); }
    }

    private string Diagnostic(string stage, Exception error)
    {
        StartupTarget? target = null;
        try { target = Target; } catch { /* Preserve the original error if identity/path discovery also fails. */ }
        return $"Startup {stage}; task: {target?.TaskName}; executable: {target?.Executable ?? Environment.ProcessPath}; " +
            $"error: 0x{error.HResult:X8}; {error}";
    }
}
