using System.Windows;
using System.Windows.Threading;

namespace FileStash.Core.Tests;

[CollectionDefinition(nameof(WpfWindowHost), DisableParallelization = true)]
public sealed class WpfWindowCollection : ICollectionFixture<WpfWindowHost> { }

// WPF permits one Application per process. Keep it on a pumping STA for both window tests.
public sealed class WpfWindowHost : IDisposable
{
    private readonly Thread _thread;
    public Dispatcher Dispatcher { get; }

    public WpfWindowHost()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            Application app;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var resource in new[] { "Themes/Light.xaml", "Controls.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("/StashBay;component/Resources/" + resource, UriKind.Relative)
                    });
                ready.SetResult(app.Dispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            app.Run();
        }) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Dispatcher.Invoke(() => Application.Current.Shutdown());
        _thread.Join();
    }
}
