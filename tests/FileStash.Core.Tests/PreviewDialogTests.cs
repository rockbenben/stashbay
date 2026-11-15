using System.Reflection;
using System.Windows;
using FileStash.App.Views;
using FileStash.Core.Models;

namespace FileStash.Core.Tests;

public sealed class PreviewDialogTests
{
    [Fact]
    public void ClosingDuringWriteMustCancelAndWaitForCompletion()
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            Application? application = null;
            PreviewDialog? window = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var resource in new[] { "Themes/Light.xaml", "Controls.xaml" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("/StashBay;component/Resources/" + resource, UriKind.Relative)
                    });
                using var context = new ApplyTestContext();
                window = new PreviewDialog(new ApplyPlan
                {
                    Target = new TargetSpec(TargetMode.OriginalLocation, null, StructureMode.FullRelative),
                    Entries = []
                }, context.Executor, context.BlobStore, context.Settings)
                {
                    ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Show();
                using var cancellation = new CancellationTokenSource();
                var execution = typeof(PreviewDialog).GetField("_execution", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    typeof(PreviewDialog).GetField("_execution", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
                    window.Close();
                }
                application?.Shutdown();
                done.Set();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Null(failure);
    }
}
