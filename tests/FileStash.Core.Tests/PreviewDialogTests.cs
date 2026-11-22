using System.Reflection;
using System.Windows;
using FileStash.App.Views;
using FileStash.Core.Models;

namespace FileStash.Core.Tests;

[Collection(nameof(WpfWindowHost))]
public sealed class PreviewDialogTests(WpfWindowHost host)
{
    [Fact]
    public void ClosingDuringWriteMustCancelAndWaitForCompletion() => host.Dispatcher.Invoke(() =>
    {
        using var context = new ApplyTestContext();
        var window = new PreviewDialog(new ApplyPlan
        {
            Target = new TargetSpec(TargetMode.OriginalLocation, null, StructureMode.FullRelative),
            Entries = []
        }, context.Executor, context.BlobStore, context.Settings)
        {
            ShowActivated = false, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        var execution = typeof(PreviewDialog).GetField("_execution", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            using var cancellation = new CancellationTokenSource();
            execution.SetValue(window, cancellation);
            window.Close();
            var closedDuringWrite = closed;
            var cancelled = cancellation.IsCancellationRequested;
            execution.SetValue(window, null);
            window.Close();
            Assert.False(closedDuringWrite);
            Assert.True(cancelled);
            Assert.True(closed);
        }
        finally
        {
            execution.SetValue(window, null);
            window.Close();
        }
    });
}
