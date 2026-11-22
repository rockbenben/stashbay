using FileStash.Infrastructure.Shell;
using FileStash.StartupTests;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace FileStash.Core.Tests;

public sealed class ElevatedStartupTests
{
    [Fact]
    public void IsolatedTemplateRegressions() => RegressionSuite.Run();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SchedulerAccountNameReadbackMatchesOnlyTheSameSid(bool principalName, bool triggerName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var target = NewTarget() with { UserSid = identity.User!.Value };
        var document = XDocument.Parse(ElevatedStartup.TaskXml(target));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var principal = document.Descendants(ns + "Principal").Single().Element(ns + "UserId")!;
        var trigger = document.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!;
        if (principalName) principal.Value = identity.Name;
        if (triggerName) trigger.Value = identity.Name;
        // Actual registered readback also omits these default values.
        document.Descendants().Where(e => e.Name.LocalName is
            "Enabled" or "RunOnlyIfNetworkAvailable" or "RunOnlyIfIdle").Remove();
        Assert.True(ElevatedStartup.Matches(document.ToString(), target));

        // A valid account belonging to a different SID must still be rejected.
        var otherSid = new SecurityIdentifier(identity.User.Value == "S-1-5-18" ? "S-1-5-19" : "S-1-5-18");
        string otherAccount = ((NTAccount)otherSid.Translate(typeof(NTAccount))).Value;
        principal.Value = otherAccount;
        Assert.False(ElevatedStartup.Matches(document.ToString(), target));
        principal.Value = target.UserSid;
        trigger.Value = otherAccount;
        Assert.False(ElevatedStartup.Matches(document.ToString(), target));
        trigger.Value = "";
        Assert.False(ElevatedStartup.Matches(document.ToString(), target));
    }

    [Theory]
    [InlineData(5, StartupFailure.AccountMismatch)]
    [InlineData(6, StartupFailure.RollbackIncomplete)]
    [InlineData(7, StartupFailure.AccessDenied)]
    [InlineData(8, StartupFailure.OwnershipConflict)]
    public void HelperFailureCodesKeepActionableReasonsWithoutClaimingRollback(int code, StartupFailure reason)
    {
        var result = ElevatedStartup.Request(StartupOperation.Install, NewTarget(), _ => code,
            new StartupTestStore { FailRead = true });
        Assert.Equal(StartupOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.Failure);
    }

    private static StartupTarget NewTarget(bool useStartupShortcut = false) => ElevatedStartup.Target(
        new("StashBay.Tests." + Guid.NewGuid().ToString("N"), "StashBay.exe", "--elevated-logon", "FileStash", "--logon-startup",
            LogonDelaySeconds: 3, UseStartupShortcut: useStartupShortcut),
        @"D:\便携 & apps\StashBay.exe", "S-1-5-21-1-2-3-1001");

    [Fact]
    public async Task ConfiguredDelayIsPassedToHelperAndReadBackAgainstRequestedOrSavedSettings()
    {
        var target = NewTarget();
        var store = new StartupTestStore { TaskXml = ElevatedStartup.TaskXml(target) };
        int savedDelay = 3;
        var service = new LogonStartupService(store: store, target: target, delay: () => savedDelay,
            configuredRequest: (operation, options) => Task.FromResult(
                ElevatedStartup.Apply(operation, target with { Options = options }, store)));
        Assert.Equal(StartupOutcome.Applied, (await service.ChangeAsync(true, true, delaySeconds: 17)).Outcome);
        Assert.Equal(LogonStartupState.Elevated, (await service.ReadAsync(17)).State);
        Assert.Equal(LogonStartupState.ElevatedNeedsRepair, (await service.ReadAsync()).State);
        savedDelay = 17;
        Assert.Equal(LogonStartupState.Elevated, (await service.ReadAsync()).State);
        string? before = store.TaskXml;
        Assert.Equal(StartupOutcome.Failed, (await service.ChangeAsync(true, true, delaySeconds: 301)).Outcome);
        Assert.Equal(before, store.TaskXml);
        Assert.Equal(StartupOutcome.Applied, (await service.ChangeAsync(true, true, delaySeconds: 0)).Outcome);
        Assert.Equal(LogonStartupState.Elevated, (await service.ReadAsync(0)).State);
    }

    [Theory]
    [InlineData(StartupOperation.Plain)]
    [InlineData(StartupOperation.Uninstall)]
    [InlineData(StartupOperation.Install)]
    public void SwitchesAreExclusiveAndCleanBothRunNames(StartupOperation operation)
    {
        var target = NewTarget();
        var store = new StartupTestStore { TaskXml = ElevatedStartup.TaskXml(target) };
        store.Runs[target.Options.AppId] = "old ordinary";
        store.Runs["FileStash"] = "legacy";
        store.Runs["OtherApp"] = "preserve";
        ElevatedStartup.Apply(operation, target, store);
        Assert.Equal(operation == StartupOperation.Plain ? ElevatedStartup.PlainCommand(target) : null,
            store.ReadRun(target.Options.AppId));
        Assert.Null(store.ReadRun("FileStash"));
        Assert.Equal("preserve", store.ReadRun("OtherApp"));
        Assert.Equal(operation == StartupOperation.Install, store.TaskXml is not null);
    }

    [Fact]
    public void FailedNormalSwitchRestoresAdminTaskAndBothRunValues()
    {
        var target = NewTarget();
        var xml = ElevatedStartup.TaskXml(target);
        var store = new StartupTestStore { TaskXml = xml, FailLegacyWriteOnce = true };
        store.Runs[target.Options.AppId] = "old ordinary";
        store.Runs["FileStash"] = "legacy";
        var error = Assert.Throws<IOException>(() => ElevatedStartup.Apply(StartupOperation.Plain, target, store));
        Assert.Contains("previous startup restored", error.Message);
        Assert.Equal(xml, store.TaskXml);
        Assert.Equal("old ordinary", store.ReadRun(target.Options.AppId));
        Assert.Equal("legacy", store.ReadRun("FileStash"));
    }

    [Fact]
    public async Task NormalStartupAndDisablingNeedNoUacWhenNoTaskExists()
    {
        var target = NewTarget();
        var store = new StartupTestStore();
        var service = new LogonStartupService(store: store, target: target,
            request: _ => throw new Exception("UAC must not run"));
        Assert.Equal(StartupOutcome.Applied, (await service.ChangeAsync(true, false)).Outcome);
        Assert.Equal(LogonStartupState.Plain, (await service.ReadAsync()).State);
        Assert.Equal(StartupOutcome.Applied, (await service.ChangeAsync(false, false)).Outcome);
        Assert.Equal(LogonStartupState.Disabled, (await service.ReadAsync()).State);
    }

    [Fact]
    public async Task CancellationAndPendingHelperDoNotReplayOrDowngrade()
    {
        var target = NewTarget();
        var store = new StartupTestStore { TaskXml = ElevatedStartup.TaskXml(target) };
        int calls = 0;
        bool pending = false;
        var service = new LogonStartupService(store: store, target: target, pending: () => pending,
            request: _ => { calls++; return Task.FromResult(new StartupResult(StartupOutcome.Cancelled)); });
        Assert.Equal(StartupOutcome.Cancelled, (await service.ChangeAsync(true, false)).Outcome);
        Assert.Equal(LogonStartupState.Elevated, (await service.ReadAsync()).State);
        Assert.Empty(store.Runs);
        pending = true;
        Assert.Equal(StartupOutcome.Uncertain, (await service.ChangeAsync(false, false)).Outcome);
        Assert.Equal(LogonStartupState.Unknown, (await service.ReadAsync()).State);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MovedOrDisabledTasksNeedRepairAndReadFailuresStayUnknown()
    {
        var target = NewTarget();
        var store = new StartupTestStore
        {
            TaskXml = ElevatedStartup.TaskXml(target).Replace("HighestAvailable", "LeastPrivilege")
        };
        var service = new LogonStartupService(store: store, target: target, request: operation =>
        {
            Assert.Equal(StartupOperation.Repair, operation);
            return Task.FromResult(ElevatedStartup.Apply(operation, target, store));
        });
        Assert.Equal(LogonStartupState.ElevatedNeedsRepair, (await service.ReadAsync()).State);
        Assert.Equal(StartupOutcome.Applied, (await service.ChangeAsync(true, true, repair: true)).Outcome);
        Assert.Equal(LogonStartupState.Elevated, (await service.ReadAsync()).State);
        store.FailRead = true;
        Assert.Equal(LogonStartupState.Unknown, (await service.ReadAsync()).State);
        Assert.Equal(StartupOutcome.Failed, (await service.ChangeAsync(false, false)).Outcome);
        Assert.NotNull(store.TaskXml);
    }

    [Fact]
    public async Task SandboxSwitchNeverReadsOrWritesStartupState()
    {
        var service = new LogonStartupService(disabled: true, store: new StartupTestStore { FailRead = true },
            request: _ => throw new Exception("Must never launch"));
        Assert.Equal(LogonStartupState.Suppressed, (await service.ReadAsync()).State);
        Assert.Equal(StartupOutcome.Unchanged, (await service.ChangeAsync(true, true)).Outcome);
    }

    [Fact]
    public void IsAdministratorAccountDoesNotThrowAndReturnsBoolean()
    {
        bool isAdmin = ElevatedStartup.IsAdministratorAccount();
        Assert.True(isAdmin || !isAdmin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainRepairUsesTheDeploymentEntryWithoutCreatingATask(bool useStartupShortcut)
    {
        var target = NewTarget(useStartupShortcut);
        var store = new StartupTestStore();
        store.Runs[target.Options.AppId] = "old run";
        var service = new LogonStartupService(store: store, target: target,
            request: _ => throw new Exception("UAC must not run"));

        Assert.Equal(LogonStartupState.PlainNeedsRepair, (await service.ReadAsync()).State);
        var applied = await service.ChangeAsync(true, false, repair: true);
        Assert.Equal(StartupOutcome.Applied, applied.Outcome);
        Assert.Equal(useStartupShortcut ? ElevatedStartup.PlainShortcut(target) : null, store.Shortcut);
        Assert.Equal(useStartupShortcut ? null : ElevatedStartup.PlainCommand(target), store.ReadRun(target.Options.AppId));
        Assert.Null(store.TaskXml);

        var status = await service.ReadAsync();
        Assert.Equal(LogonStartupState.Plain, status.State);

        var uninstalled = await service.ChangeAsync(false, false);
        Assert.Equal(StartupOutcome.Applied, uninstalled.Outcome);
        Assert.Null(store.Shortcut);
        Assert.Null(store.ReadRun(target.Options.AppId));

        var disabledStatus = await service.ReadAsync();
        Assert.Equal(LogonStartupState.Disabled, disabledStatus.State);
    }

    [Fact]
    public async Task ShortcutReadOrWriteFailurePropagatesOrRestoresState()
    {
        var target = NewTarget(useStartupShortcut: true);
        var store = new StartupTestStore { FailShortcutRead = true };
        var service = new LogonStartupService(store: store, target: target);

        var status = await service.ReadAsync();
        Assert.Equal(LogonStartupState.Unknown, status.State);

        store.FailShortcutRead = false;
        store.FailShortcutWriteOnce = true;
        var changeResult = await service.ChangeAsync(true, false);
        Assert.Equal(StartupOutcome.Failed, changeResult.Outcome);
        Assert.Null(store.Shortcut);
    }

    [Fact]
    public async Task StrayShortcutOrRunCausesPlainNeedsRepair()
    {
        var target = NewTarget(useStartupShortcut: false);
        var store = new StartupTestStore();
        store.WriteRun(target.Options.AppId, ElevatedStartup.PlainCommand(target));
        store.Shortcut = new StartupShortcut(@"C:\other\app.exe", "", @"C:\other");
        var service = new LogonStartupService(store: store, target: target);

        var status = await service.ReadAsync();
        Assert.Equal(LogonStartupState.PlainNeedsRepair, status.State);
    }

    [Fact]
    public void TaskLookupRecognizesMissingTaskAndPreservesErrors()
    {
        Assert.Null(WindowsStartupStore.ReadTaskXml(() => throw new FileNotFoundException()));
        Assert.Null(WindowsStartupStore.ReadTaskXml(() => throw new COMException("missing", unchecked((int)0x80070002))));
        Assert.Null(WindowsStartupStore.ReadTaskXml(() => throw new COMException("missing path", unchecked((int)0x80070003))));

        var denied = new UnauthorizedAccessException("denied");
        var error = Assert.Throws<UnauthorizedAccessException>(() => WindowsStartupStore.ReadTaskXml(() => throw denied));
        Assert.Same(denied, error);
    }
}

internal sealed class StartupTestStore : IStartupStore
{
    public string? TaskXml;
    public bool FailLegacyWriteOnce, FailRead;
    public StartupShortcut? Shortcut;
    public bool FailShortcutRead, FailShortcutWriteOnce;
    public Dictionary<string, string> Runs { get; } = [];
    public string? ReadTask(string name) => FailRead ? throw new UnauthorizedAccessException("query denied") : TaskXml;
    public void WriteTask(string name, string xml, string userSid) => TaskXml = xml;
    public void DeleteTask(string name) => TaskXml = null;
    public string? ReadRun(string valueName) => Runs.GetValueOrDefault(valueName);
    public void WriteRun(string valueName, string? command)
    {
        if (command is null) Runs.Remove(valueName);
        else Runs[valueName] = command;
        if (valueName == "FileStash" && FailLegacyWriteOnce)
        {
            FailLegacyWriteOnce = false;
            throw new IOException("legacy write failed after mutation");
        }
    }
    public StartupShortcut? ReadShortcut(string name) => FailShortcutRead ? throw new UnauthorizedAccessException("shortcut query denied") : Shortcut;
    public void WriteShortcut(string name, StartupShortcut? shortcut)
    {
        if (FailShortcutWriteOnce)
        {
            FailShortcutWriteOnce = false;
            throw new IOException("shortcut write failed after mutation");
        }
        Shortcut = shortcut;
    }
}
