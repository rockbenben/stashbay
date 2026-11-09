using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FileStash.App.Controls;
using FileStash.App.Converters;
using FileStash.App.ViewModels;
using FileStash.Core.Models;

namespace FileStash.Core.Tests;

/// <summary>
/// 分段控件（RadioButton + EnumMatch）的写回。
/// 这里钉的是"点了真的算"：以前 ConvertBack 无条件交回 Binding.DoNothing，
/// 三档写回模式点下去只动界面不动模型，而没有任何测试点过它。
/// </summary>
public sealed class SegmentBindingTests
{
    [Fact]
    public void WheelOverListBorder_ReachesItsScrollableTemplate()
    {
        RunOnStaThread(() =>
        {
            var list = new ListBox { ItemsSource = Enumerable.Range(1, 100) };
            WheelScroll.SetIsEnabled(list, true);
            // Give the template a presentation source, just as the live ListBox has.
            using var source = new System.Windows.Interop.HwndSource(new("WheelScrollTest")
            {
                Width = 200, Height = 100, WindowStyle = 0, PositionX = -32000, PositionY = -32000,
            }) { RootVisual = list };
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            list.UpdateLayout();

            // A hit on the list itself never bubbles through its child ScrollViewer.
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent,
            };
            list.RaiseEvent(wheel);
            Assert.True(wheel.Handled);
            return true;
        });
    }

    [Fact]
    public void GroupedItemHit_ShouldResolveTheNestedItemAndIgnoreTheHeader()
    {
        RunOnStaThread(() =>
        {
            var model = new ItemViewModel(new StashItem());
            var tree = new TreeView();
            var header = new TreeViewItem { DataContext = new SourceFolderRowViewModel(@"C:\source"), IsExpanded = true };
            var label = new TextBlock { Text = "file.txt" };
            var child = new TreeViewItem { DataContext = model, Header = label };
            header.Items.Add(child);
            tree.Items.Add(header);
            tree.Measure(new Size(600, 400));
            tree.Arrange(new Rect(0, 0, 600, 400));
            tree.UpdateLayout();
            var hit = typeof(FileStash.App.Views.MainWindow).GetMethod("ItemUnder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Assert.Same(model, hit.Invoke(null, [tree, label]));
            Assert.Null(hit.Invoke(null, [tree, header]));
            return true;
        });
    }

    [Fact]
    public void ConvertBack_WhenChecked_WritesTheNamedEnumMember()
    {
        var converter = new EnumMatchConverter();

        var written = converter.ConvertBack(true, typeof(WorkbenchViewMode), "BySourceFolder", CultureInfo.InvariantCulture);

        Assert.Equal(WorkbenchViewMode.BySourceFolder, written);
    }

    /// <summary>取消选中不写回：否则同一组里后点的每一档都会先把源抹一次。</summary>
    [Fact]
    public void ConvertBack_WhenUnchecked_WritesNothing()
    {
        var converter = new EnumMatchConverter();

        var written = converter.ConvertBack(false, typeof(WorkbenchViewMode), "List", CultureInfo.InvariantCulture);

        Assert.Same(Binding.DoNothing, written);
    }

    /// <summary>
    /// 真走一遍绑定：点"按来源"，ViewModel 的档位与投影都要跟着变。
    /// 单测转换器证不了 RadioButton.IsChecked 的双向写回，那要碰真的依赖属性。
    /// </summary>
    [Fact]
    public void RadioButtonClick_ShouldChangeWorkbenchViewMode()
    {
        var observed = RunOnStaThread(() =>
        {
            var viewModel = new ModeOnlyViewModel();

            var radio = new RadioButton();
            radio.SetBinding(
                ToggleButton.IsCheckedProperty,
                new Binding(nameof(ModeOnlyViewModel.WorkbenchViewMode))
                {
                    Source = viewModel,
                    Mode = BindingMode.TwoWay,
                    Converter = new EnumMatchConverter(),
                    ConverterParameter = nameof(WorkbenchViewMode.BySourceFolder),
                });

            // 初始由源决定：Tree ≠ BySourceFolder，所以这档是灭的
            Assert.False(radio.IsChecked);

            // 模拟点下去
            radio.IsChecked = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            return viewModel.WorkbenchViewMode;
        });

        Assert.Equal(WorkbenchViewMode.BySourceFolder, observed);
    }

    /// <summary>WPF 控件要 STA；这条只建控件、不显示窗口，跑完就收。</summary>
    private static T RunOnStaThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.Null(failure);

        return result;
    }

    /// <summary>只带档位的那一小截 MainViewModel：整只 VM 的装配在 MainViewModelTests 里。</summary>
    private sealed class ModeOnlyViewModel
    {
        public WorkbenchViewMode WorkbenchViewMode { get; set; } = WorkbenchViewMode.Tree;
    }
}
