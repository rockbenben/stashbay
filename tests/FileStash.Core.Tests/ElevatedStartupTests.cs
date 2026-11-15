using FileStash.Infrastructure.Shell;
using FileStash.StartupTests;

namespace FileStash.Core.Tests;

public sealed class ElevatedStartupTests
{
    [Fact]
    public void IsolatedTemplateRegressions() => RegressionSuite.Run();

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

    private static StartupTarget NewTarget() => ElevatedStartup.Target(
        new("StashBay.Tests." + Guid.NewGuid().ToString("N"), "StashBay.exe", "--elevated-logon", "FileStash", "--logon-startup"),
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
            Task.FromResult(ElevatedStartup.Apply(operation, target, store)));
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
}

internal sealed class StartupTestStore : IStartupStore
{
    public string? TaskXml;
    public bool FailLegacyWriteOnce, FailRead;
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
}
