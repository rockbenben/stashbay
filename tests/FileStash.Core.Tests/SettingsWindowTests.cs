using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using FileStash.App.ViewModels;
using FileStash.App.Views;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Infrastructure.Shell;

namespace FileStash.Core.Tests;

[Collection(nameof(WpfWindowHost))]
public sealed class SettingsWindowTests(WpfWindowHost host)
{
    [Fact]
    public async Task SaveOtherSettingsWhileWindowsStartupQueryIsPending()
    {
        var timeout = TimeSpan.FromSeconds(10);
        var cancellation = TestContext.Current.CancellationToken;
        using var context = new ApplyTestContext();
        await context.Migrator.MigrateAsync(cancellation);
        context.Settings.Save(new AppSettings
        {
            BackupBeforeOverwrite = true, Theme = ThemeChoice.Light,
            StartWithWindows = true, StartWithWindowsAsAdministrator = true, AdministratorStartupDelaySeconds = 17
        });

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var target = ElevatedStartup.Target(LogonStartupService.Options, @"C:\StashBay\StashBay.exe", "S-1-5-21-1-2-3-1001");
        var store = new PendingStartupStore(() =>
        {
            entered.TrySetResult(host.Dispatcher.CheckAccess());
            try { release.Wait(cancellation); }
            finally { readFinished.TrySetResult(); }
        }, ElevatedStartup.TaskXml(target));
        int helperCalls = 0;
        var startup = new LogonStartupService(store: store, target: target, pending: () => false, request: _ =>
        {
            Interlocked.Increment(ref helperCalls);
            return Task.FromResult(new StartupResult(StartupOutcome.Unchanged));
        });

        SettingsWindow? window = null;
        try
        {
            await host.Dispatcher.InvokeAsync(() =>
            {
                var cleanup = new CleanupService(context.Entries, context.OperationEntries, context.Operations,
                    context.BlobStore, context.Paths);
                var model = new SettingsViewModel(context.Settings, cleanup, context.BlobStore, context.Operations,
                    new RecordingDialogService(), _ => true, startup);
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(model.HistoryText)) loaded.TrySetResult();
                };
                window = new SettingsWindow(model)
                {
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000
                };
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
            }).Task.WaitAsync(timeout, cancellation);
            Assert.False(await entered.Task.WaitAsync(timeout, cancellation));

            await host.Dispatcher.InvokeAsync(async () =>
            {
                var tabs = (TabControl)window!.FindName("SettingsTabs");
                tabs.SelectedItem = window.FindName("WindowsSettingsTab");
                window.UpdateLayout();
                var repair = (Button)window.FindName("RepairStartupButton");
                Assert.True(repair.IsVisible);
                Assert.False(repair.IsEnabled);
                await ((SettingsViewModel)window.DataContext).RepairStartupCommand.ExecuteAsync(null);

                tabs.SelectedItem = window.FindName("FilesSettingsTab");
                window.UpdateLayout();
                var backup = (CheckBox)window.FindName("BackupBeforeOverwriteCheck");
                Assert.True(backup.IsVisible && backup.IsEnabled);
                Assert.True(backup.IsChecked);
                ((IToggleProvider)new CheckBoxAutomationPeer(backup)).Toggle();
                var save = (Button)window.FindName("SaveSettingsButton");
                Assert.True(save.IsVisible && save.IsEnabled);
                ((IInvokeProvider)new ButtonAutomationPeer(save)).Invoke();
            }).Task.Unwrap().WaitAsync(timeout, cancellation);
            await closed.Task.WaitAsync(timeout, cancellation);
            var reader = new SettingsService(context.Paths);
            reader.Load();
            Assert.False(reader.Current.BackupBeforeOverwrite);
            Assert.True(reader.Current.StartWithWindows);
            Assert.True(reader.Current.StartWithWindowsAsAdministrator);
            Assert.Equal(17, reader.Current.AdministratorStartupDelaySeconds);
            Assert.False(readFinished.Task.IsCompleted);
            Assert.Equal(0, store.Writes);
            Assert.Equal(0, helperCalls);
        }
        finally
        {
            release.Set();
            if (entered.Task.IsCompleted) await loaded.Task.WaitAsync(timeout, cancellation);
            await host.Dispatcher.InvokeAsync(() => window?.Close());
        }
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, helperCalls);
    }

    private sealed class PendingStartupStore(Action waitForRelease, string taskXml) : IStartupStore
    {
        public int Writes;
        public string? ReadTask(string name) { waitForRelease(); return taskXml; }
        public string? ReadRun(string valueName) => null;
        public StartupShortcut? ReadShortcut(string name) => null;
        public void WriteTask(string name, string xml, string userSid) => Interlocked.Increment(ref Writes);
        public void DeleteTask(string name) => Interlocked.Increment(ref Writes);
        public void WriteRun(string valueName, string? command) => Interlocked.Increment(ref Writes);
        public void WriteShortcut(string name, StartupShortcut? shortcut) => Interlocked.Increment(ref Writes);
    }
}
