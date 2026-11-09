using System.Reflection;
using System.Windows;
using FileStash.App.Views;

namespace FileStash.Core.Tests;

public sealed class WindowEffectsTests
{
    [Fact]
    public void PlaceNearCursor_WhenWindowExceedsWorkArea_DoesNotThrow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var window = new Window { Width = 30000, Height = 30000 };
            try
            {
                var effects = typeof(MainWindow).Assembly.GetType("FileStash.App.Controls.WindowEffects")!;
                var place = effects.GetMethod("PlaceNearCursor", BindingFlags.Public | BindingFlags.Static)!;
                place.Invoke(null, [window, 16d]);
                Assert.True(double.IsFinite(window.Left));
                Assert.True(double.IsFinite(window.Top));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Window placement timed out");
        Assert.Null(failure);
    }
}
