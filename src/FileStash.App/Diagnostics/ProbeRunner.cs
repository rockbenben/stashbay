using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FileStash.App.Controls;

namespace FileStash.App.Diagnostics;

/// <summary>
/// <c>--probe &lt;目录&gt;</c>：把每个窗口的视觉树量一遍，导出几何 / 配色 / 焦点三张读数表。
/// </summary>
/// <remarks>
/// 为什么不用截图量：截图能看出"这儿不对"，量不出"差多少 DIP、对比度几点几、
/// 键盘走到第几站"。这些数只能从活的视觉树上取。
///
/// 仪器自己也要作证：<see cref="AssertColorMath"/> 拿两组已知色对当证人，
/// 认不出来就整轮作废（解析器只认得一部分写法时，"0 条不达标"是假的）。
/// 认不出来的画刷写法不静默跳过，单列成桶报出来。
/// </remarks>
internal static class ProbeRunner
{
    private const double TightWorkAreaHeight = 768 / 1.50 - 48;   // ≈464 DIP，最紧的一档

    public static async Task<int> RunAsync(string[] args, int probeIndex, IServiceProvider services)
    {
        App.InSmoke = true;

        if (Application.Current is { } app)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var outputDirectory = args.Length > probeIndex + 1
            ? args[probeIndex + 1]
            : Path.Combine(Path.GetTempPath(), "filestash-probe");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "probe-culture.txt"), CultureInfo.CurrentUICulture.Name);

            var witnesses = new List<string>();
            AssertColorMath(witnesses);

            File.WriteAllText(
                Path.Combine(outputDirectory, "probe-witness.txt"),
                string.Join(Environment.NewLine, witnesses));

            var rows = new StringBuilder(ElementHeader);
            var tabRows = new StringBuilder(TabHeader);
            var notes = new StringBuilder();
            var unparsed = new List<string>();
            var failures = new List<string>();
            var warnings = new List<string>();

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Apply(theme);

                foreach (var shot in WindowCatalog.All(services))
                {
                    var window = await shot.Factory();
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -32000;
                    window.Top = -32000;
                    window.ShowActivated = false;
                    window.Show();
                    window.MaxHeight = TightWorkAreaHeight;

                    await UiPump.Settle(window);

                    if (shot.Drive is { } drive)
                    {
                        await drive(window, services);
                        await UiPump.Settle(window);
                    }

                    var stops = 0;

                    try
                    {
                        stops = Walk(window, theme, shot.Name, rows, unparsed, notes, tabRows, failures, warnings);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{shot.Name}.{theme}: {ex.GetType().Name} {ex.Message}");
                    }

                    // 键盘可达性：结构扫描给"每一站有没有焦点表现"，
                    // 真按 Tab 的走查只用来证明"这台机器上走得动"（走不动会自己报）
                    try
                    {
                        TabWalk(window, theme, shot.Name, warnings, stops);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{shot.Name}.{theme} tab: {ex.GetType().Name} {ex.Message}");
                    }

                    window.Close();
                }
            }

            ThemeService.Apply(AppTheme.Light);

            File.WriteAllText(Path.Combine(outputDirectory, "probe-elements.tsv"), rows.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "probe-tabstops.tsv"), tabRows.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "probe-notes.txt"), notes.ToString());
            File.WriteAllText(
                Path.Combine(outputDirectory, "probe-unparsed-brushes.txt"),
                unparsed.Count == 0 ? "none" : string.Join(Environment.NewLine, unparsed));
            File.WriteAllText(
                Path.Combine(outputDirectory, "probe-warnings.txt"),
                warnings.Count == 0 ? "none" : string.Join(Environment.NewLine, warnings));
            File.WriteAllText(
                Path.Combine(outputDirectory, "probe-failures.txt"),
                failures.Count == 0 ? "none" : string.Join(Environment.NewLine, failures));

            var failure = App.SmokeFailure;
            var report = failures.Count == 0 && failure is null
                ? "OK"
                : $"FAIL walkErrors={failures.Count} appFailure={failure ?? "none"}";

            File.WriteAllText(App.SmokeReportPath, report);
            return failures.Count == 0 && failure is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(App.SmokeReportPath, ex.ToString());
            return 1;
        }
    }

    private static string ElementHeader => string.Join('\t',
        "screen", "theme", "kind", "text", "fontSize", "weight", "font",
        "fg", "bg", "contrast", "grade", "ratio", "x", "y", "w", "h",
        "trimmed", "clipped", "tabStop", "focusable", "enabled", "toolTip", "autoName", "autoId");

    private static string TabHeader => string.Join('\t',
        "screen", "theme", "order", "kind", "text", "w", "h", "x", "y", "focusCue", "autoName");

    /// <summary>
    /// 可聚焦性扫描：不靠按 Tab，直接遍历视觉树问"这一站聚焦时屏上会不会变"。
    /// </summary>
    /// <remarks>
    /// 为什么不靠 <see cref="TabWalk"/> 的数就够了：harness 的窗口开在屏幕外且
    /// <c>ShowActivated=false</c>，<c>MoveFocus</c> 走不动（实测每屏都只回到 1 站，
    /// 数字齐得像没有缺陷）。所以焦点表现按结构判，走查另做一条自检。
    /// </remarks>
    private static int FocusScan(
        Window window,
        AppTheme theme,
        string screen,
        StringBuilder tabRows,
        Rect rootBounds)
    {
        var stops = 0;
        var blind = 0;

        foreach (var control in Descendants<Control>(window))
        {
            if (control is not { IsTabStop: true, Focusable: true, IsEnabled: true } || !control.IsVisible)
            {
                continue;
            }

            var bounds = TryBounds(control, window);
            var cue = FocusCue(control);
            var label = TextOf(control);

            if (string.IsNullOrEmpty(label))
            {
                label = GetAuto(control, AutomationProperties.NameProperty);
            }

            stops++;

            if (cue == "none")
            {
                blind++;
            }

            tabRows.Append(string.Join('\t',
                screen, theme, "stop-" + stops, control.GetType().Name, Cell(label),
                Cell(bounds?.Width ?? double.NaN), Cell(bounds?.Height ?? double.NaN),
                Cell(bounds?.X ?? double.NaN), Cell(bounds?.Y ?? double.NaN),
                cue, Cell(GetAuto(control, AutomationProperties.NameProperty))));
            tabRows.AppendLine();
        }

        tabRows.Append(string.Join('\t', screen, theme, "TOTAL", stops, "(站)", "", "", "", "", blind, "(无焦点表现)"));
        tabRows.AppendLine();

        return stops;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// 按 Tab 走一圈：站点顺序。走不动要冒出来，不许报一个像样的小数字。
    /// </summary>
    private static void TabWalk(Window window, AppTheme theme, string screen, List<string> warnings, int stops)
    {
        window.Focus();
        Keyboard.Focus(window);

        var visited = new List<FrameworkElement>();
        FrameworkElement? first = null;

        for (var step = 0; step < 80; step++)
        {
            if (!window.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) ||
                Keyboard.FocusedElement is not FrameworkElement focused ||
                ReferenceEquals(focused, first) ||
                visited.Contains(focused))
            {
                break;
            }

            first ??= focused;
            visited.Add(focused);
        }

        if (stops >= 3 && visited.Count < 2)
        {
            // 自检：这一屏明明有 N 个 tab stop，走查却走不动 —— 是驱动的问题，不是界面的问题，
            // 但绝不能让它悄悄出一个"看起来正常"的站点数
            warnings.Add($"{screen}.{theme}: 真按 Tab 走不动（走到 {visited.Count} 站 / 结构上 {stops} 站）—— 焦点表现按结构判，站点顺序这一维没量到");
        }

        return;
    }


    /// <summary>
    /// 这一站聚焦时到底有没有看得见的变化：模板里的 Focused 视觉态或聚焦触发器。
    /// </summary>
    private static string FocusCue(FrameworkElement element)
    {
        if (element is not Control control)
        {
            return "not-control";
        }

        control.ApplyTemplate();

        // 焦点环也可以由 FocusVisualStyle（墨水瓶模板）提供，与模板触发器是两条平行通路
        if (control.FocusVisualStyle is not null)
        {
            return "inkwell";
        }

        // GetVisualStateGroups 与它里面的集合都是松类型（非泛型），只能逐个 cast
        foreach (var rawGroup in (System.Collections.IEnumerable)VisualStateManager.GetVisualStateGroups(control))
        {
            if (rawGroup is not VisualStateGroup group)
            {
                continue;
            }

            foreach (var rawState in (System.Collections.IEnumerable)group.States)
            {
                if (rawState is VisualState state &&
                    state.Name.Contains("Focus", StringComparison.OrdinalIgnoreCase))
                {
                    return "state";
                }
            }
        }

        if (control.Template is ControlTemplate template)
        {
            foreach (var rawTrigger in (System.Collections.IEnumerable)template.Triggers)
            {
                // PropertyTrigger 这个类型在 WPF 里不是公开的，按名字认，再反射取它关注的属性
                var trigger = rawTrigger as DependencyObject;

                if (trigger?.GetType().Name != "PropertyTrigger")
                {
                    continue;
                }

                var property = trigger.GetType()
                    .GetProperty("Property")?
                    .GetValue(trigger) as DependencyProperty;

                if (property == UIElement.IsKeyboardFocusedProperty ||
                    property == UIElement.IsKeyboardFocusWithinProperty)
                {
                    return "trigger";
                }
            }
        }

        return "none";
    }

    /// <summary>
    /// 证人对表：已知的两对颜色必须算出已知的比值，否则整轮读数不可信。
    /// </summary>
    private static void AssertColorMath(List<string> witnesses)
    {
        // 浅色正文 / 浅底：期望约 15.4
        var body = Contrast(0x1A, 0x20, 0x24, 0xF7, 0xF8, 0xF9);

        // 浅色强调 / 浅底：期望约 4.7
        var accent = Contrast(0x0F, 0x7B, 0x6C, 0xF7, 0xF8, 0xF9);

        witnesses.Add($"witness1 正文/浅底 = {body:0.00}（应接近 15.4）");
        witnesses.Add($"witness2 强调/浅底 = {accent:0.00}（应接近 4.7）");

        if (body is < 14 or > 17 || accent is < 4.2 or > 5.2)
        {
            throw new InvalidOperationException(
                $"对比度计算不认得已知色对（{body:0.00} / {accent:0.00}），本轮读数全部作废");
        }
    }

    private static int Walk(
        Window window,
        AppTheme theme,
        string screen,
        StringBuilder rows,
        List<string> unparsed,
        StringBuilder notes,
        StringBuilder tabRows,
        List<string> failures,
        List<string> warnings)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var rootBounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);

        Visit(window, window, theme, screen, rows, unparsed, notes, dpi, rootBounds, null, 1.0);
        var stops = FocusScan(window, theme, screen, tabRows, rootBounds);
        GeometryChecks(window, screen, rootBounds, failures);

        var overflow = CrossScreen(window, screen, rootBounds);
        failures.AddRange(overflow.Offenders);
        warnings.AddRange(overflow.Scrollers);

        notes.AppendLine($"## overflow {screen}.{theme} measured={overflow.Measured} escaped={overflow.Escaped} exempt={overflow.Exempt} offenders={overflow.Offenders.Count}");

        notes.AppendLine($"## {screen}.{theme} 窗口 {window.ActualWidth:0}x{window.ActualHeight:0}（MaxHeight={window.MaxHeight:0}，tab stop {stops}）");

        return stops;
    }

    /// <summary>
    /// 版面证人：表头列框必须与数据列框重合，行不许撑出窗口。
    /// </summary>
    /// <remarks>
    /// 这两条都是"读代码看不出来"的那类：列宽写在两处、字面一样，
    /// 但树的内缩（展开箭头、滚动条槽）会悄悄把数据列推开；
    /// 而横向滚动没关时，行按无限宽测量，列直接跑到屏外。
    /// </remarks>
    private static void GeometryChecks(
        Window window,
        string screen,
        Rect rootBounds,
        List<string> failures)
    {
        if (!screen.StartsWith("MainWindow.Expanded", StringComparison.Ordinal))
        {
            return;
        }

        const double tolerance = 1.5;

        var tree = FindNamed<TreeView>(window, "ItemTree");
        var header = FindNamed<Border>(window, "ListHeader");

        if (tree is null || header is null)
        {
            failures.Add($"{screen}: 找不到 ItemTree 或 ListHeader，列对齐证人没有量到东西");
            return;
        }

        // 图标网格没有表格列，仍由通用的可视元素越界检查覆盖。
        if (!tree.IsVisible)
        {
            return;
        }

        var headerGrid = Descendants<Grid>(header).FirstOrDefault(IsColumnGrid);
        var rows = Descendants<TreeViewItem>(tree)
            .Where(row => row.IsVisible && TryBounds(row, window) is not null)
            .OrderBy(row => TryBounds(row, window)!.Value.Top)
            .ToList();

        if (headerGrid is null)
        {
            failures.Add($"{screen}: 表头里没有四列的 Grid，列对齐证人没有量到东西");
            return;
        }

        foreach (var row in rows)
        {
            var bounds = TryBounds(row, window)!.Value;

            if (bounds.Right > rootBounds.Right + tolerance || bounds.Left < rootBounds.Left - tolerance)
            {
                failures.Add(
                    $"{screen}: 行右边缘 {bounds.Right:0.#} 出了窗口 {rootBounds.Right:0.#}（列被内容撑出屏外）");
                break;
            }
        }

        var firstRowGrid = rows
            // 对齐只比最外层行：分节视图里嵌套那一层天生缩进 22（那正是"分节"的读法），
            // 拿它跟表头比会永远报"不重合"。下面的溢出检查仍然看所有行。
            .Where(row => ItemsControl.ItemsControlFromItemContainer(row) == tree)
            .Select(OwnColumnGrid)
            .FirstOrDefault(grid => grid is not null);

        if (firstRowGrid is null)
        {
            return; // 空箱没有数据行可对齐
        }

        var headerBounds = TryBounds(headerGrid, window)!.Value;
        var rowBounds = TryBounds(firstRowGrid, window)!.Value;

        if (Math.Abs(headerBounds.Left - rowBounds.Left) > tolerance ||
            Math.Abs(headerBounds.Right - rowBounds.Right) > tolerance)
        {
            failures.Add(
                $"{screen}: 表头列框与数据列框不重合 左 {headerBounds.Left:0.#} vs {rowBounds.Left:0.#}" +
                $"，右 {headerBounds.Right:0.#} vs {rowBounds.Right:0.#}");
        }
    }

    /// <summary>
    /// 跨屏证人：任何一屏，看得见的元素都不许落在窗口外面 —— 除非它所在的容器真能往那个方向滚到它。
    /// </summary>
    /// <remarks>
    /// 为什么摊到所有屏：原先只盯工作台那一格（<see cref="GeometryChecks"/>），
    /// 德语设置页那次溢出是眼睛看见的，不是证人报的。
    ///
    /// 豁免分方向：横向滚得动的容器只放行左右越界，纵向滚得动的只放行上下越界。
    /// "滚得动"用 <see cref="ScrollViewer.ScrollableWidth"/> 判 —— 它是"内容比视口宽多少"，
    /// 横向滚动被关掉时恒为 0，所以"关了滚动还撑出去"不会被豁免。
    ///
    /// 容器自己也是 Control、也进这张表，于是"滚动视口本身高出窗外、内容根本滚不到"照样会红。
    ///
    /// 顺带把"这一屏长出了横向滚动条"报成警告：能滚到 ≠ 应该滚，
    /// 设置页/对话框里冒出一条横滚，多半是某句长文案没折行。
    /// </remarks>
    private static OverflowReport CrossScreen(Window window, string screen, Rect rootBounds)
    {
        const double tolerance = 1.5;

        var offenders = new List<string>();
        var measured = 0;
        var escaped = 0;
        var exempt = 0;
        var offenderCount = 0;

        foreach (var element in Descendants<FrameworkElement>(window))
        {
            if (!IsInteresting(element) || !element.IsVisible || TryBounds(element, window) is not { } rect)
            {
                continue;
            }

            measured++;

            var right = rect.Right - rootBounds.Right;
            var left = rootBounds.Left - rect.Left;
            var bottom = rect.Bottom - rootBounds.Bottom;
            var top = rootBounds.Top - rect.Top;

            // 同一方向只看超出最多的那条边：一个元素因为同一原因出窗，只报一次
            var wide = Math.Max(right, left);
            var tall = Math.Max(bottom, top);

            if (wide <= tolerance && tall <= tolerance)
            {
                continue;
            }

            escaped++;

            var (scrollsX, scrollsY) = ScrollableAxes(element, window, tolerance);
            var cutOff = (wide > tolerance && !scrollsX) || (tall > tolerance && !scrollsY);

            if (!cutOff)
            {
                exempt++;
                continue;
            }

            if (offenders.Count < 5)
            {
                offenders.Add(
                    $"{screen}: {Describe(element, window)} 出窗 {Edges(right, left, bottom, top, tolerance)}" +
                    $"（窗口 {rootBounds.Width:0.#}x{rootBounds.Height:0.#}，元素 {rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}x{rect.Height:0.#}）");
            }

            offenderCount++;
        }

        // 一个可见元素都没量到 = 这一屏没进表，不能让"0 条出窗"冒充通过
        if (measured == 0)
        {
            offenders.Add($"{screen}: 跨屏证人一个可见元素都没量到，这一屏不算通过");
        }

        if (offenderCount > 5)
        {
            offenders.Add($"{screen}: …同屏还有 {offenderCount - 5} 条出窗元素（只列前 5 条）");
        }

        var scrollers = new List<string>();

        foreach (var viewer in Descendants<ScrollViewer>(window))
        {
            if (!viewer.IsVisible || viewer.ScrollableWidth <= tolerance)
            {
                continue;
            }

            var declared = viewer.ReadLocalValue(ScrollViewer.HorizontalScrollBarVisibilityProperty)
                != DependencyProperty.UnsetValue;

            scrollers.Add(
                $"{screen}: 长出横向滚动 {DescribeScrollViewer(viewer)} " +
                $"内容 {viewer.ExtentWidth:0.#} > 视口 {viewer.ViewportWidth:0.#}" +
                $"（横向滚动{(declared ? "是在 XAML/样式里点名的" : "没人声明、吃的默认值")}）");
        }

        scrollers = scrollers.Distinct().ToList();

        return new OverflowReport(measured, escaped, exempt, offenders, scrollers);
    }

    /// <summary>往上找能滚的祖先，按方向分开认。</summary>
    private static (bool Horizontal, bool Vertical) ScrollableAxes(
        FrameworkElement element,
        Window window,
        double tolerance)
    {
        var horizontal = false;
        var vertical = false;

        for (var node = VisualTreeHelper.GetParent(element);
             node is not null && node != window;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer viewer)
            {
                continue;
            }

            horizontal |= viewer.ScrollableWidth > tolerance;
            vertical |= viewer.ScrollableHeight > tolerance;
        }

        return (horizontal, vertical);
    }

    private static string Describe(FrameworkElement element, Window? window = null)
    {
        var auto = GetAuto(element, AutomationProperties.NameProperty);
        var text = TextOf(element);

        var identity = !string.IsNullOrEmpty(element.Name) ? "#" + element.Name
            : !string.IsNullOrEmpty(auto) ? "名称：" + Shorten(auto)
            : !string.IsNullOrEmpty(text) ? "\"" + Shorten(text) + "\""
            : "(无标识)";

        // 没名字也没文字的元素（裸 TextBox、图标）光看类型找不到出处，往上追一层有名字的容器
        var container = NamedAncestor(element, window ?? Window.GetWindow(element));

        return element.GetType().Name + " " + identity + (container is null ? string.Empty : $"（在 {container} 里）");
    }

    private static string? NamedAncestor(FrameworkElement element, Window? window)
    {
        for (var node = VisualTreeHelper.GetParent(element);
             node is not null && node != window;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement ancestor &&
                !string.IsNullOrEmpty(ancestor.Name) &&
                !ancestor.Name.StartsWith("PART_", StringComparison.Ordinal))
            {
                return "#" + ancestor.Name;
            }
        }

        return null;
    }

    private static string DescribeScrollViewer(ScrollViewer viewer)
    {
        var owner = viewer.TemplatedParent?.GetType().Name;
        var named = string.IsNullOrEmpty(viewer.Name) ? string.Empty : "#" + viewer.Name;

        return $"{(owner is null ? "(XAML 里声明的)" : owner + " 模板内的")}ScrollViewer{named}";
    }

    private static string Edges(double right, double left, double bottom, double top, double tolerance)
    {
        var parts = new List<string>();

        if (right > tolerance)
        {
            parts.Add($"右 {right:0.#}");
        }

        if (left > tolerance)
        {
            parts.Add($"左 {left:0.#}");
        }

        if (bottom > tolerance)
        {
            parts.Add($"下 {bottom:0.#}");
        }

        if (top > tolerance)
        {
            parts.Add($"上 {top:0.#}");
        }

        return string.Join(" ", parts);
    }

    private static string Shorten(string value) => value.Length <= 32 ? value : value[..32] + "…";

    private sealed record OverflowReport(
        int Measured,
        int Escaped,
        int Exempt,
        List<string> Offenders,
        List<string> Scrollers);

    private static bool IsColumnGrid(Grid grid) => grid.ColumnDefinitions.Count == 4;

    /// <summary>
    /// 这一行自己模板里的四列网格。
    /// </summary>
    /// <remarks>
    /// 不能直接用 Descendants：分节视图里节头那一行把自己的条目当子树包着，
    /// 那些嵌套条目天生缩进一格（22），拿它们跟表头比会永远报"不重合"。
    /// 遇到下一级的 TreeViewItem 就停住，只认这一行模板里的列框。
    /// </remarks>
    private static Grid? OwnColumnGrid(TreeViewItem row)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(row);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);

                if (child is TreeViewItem)
                {
                    continue;
                }

                if (child is Grid grid && IsColumnGrid(grid))
                {
                    return grid;
                }

                queue.Enqueue(child);
            }
        }

        return null;
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        foreach (var candidate in Descendants<T>(root))
        {
            if (candidate.Name == name)
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Visit(
        DependencyObject node,
        Window window,
        AppTheme theme,
        string screen,
        StringBuilder rows,
        List<string> unparsed,
        StringBuilder notes,
        DpiScale dpi,
        Rect rootBounds,
        Color? inheritedBackground,
        double inheritedOpacity)
    {
        var opacity = inheritedOpacity;

        if (node is UIElement { IsVisible: false })
        {
            return;
        }

        if (node is UIElement visual && node is not Window)
        {
            opacity *= visual.Opacity;
        }

        var ownBackground = BackgroundOf(node);
        var resolvedBackground = ownBackground ?? inheritedBackground;

        if (node is FrameworkElement element)
        {
            var bounds = TryBounds(element, window);

            if (IsInteresting(element) && bounds is { } rect)
            {
                Record(element, rect, theme, screen, rows, unparsed, notes, dpi, rootBounds, resolvedBackground, opacity);
            }
        }

        var count = VisualTreeHelper.GetChildrenCount(node);

        for (var index = 0; index < count; index++)
        {
            Visit(
                VisualTreeHelper.GetChild(node, index),
                window, theme, screen, rows, unparsed, notes, dpi, rootBounds,
                resolvedBackground, opacity);
        }
    }

    /// <summary>只报"用户看得见或点得到"的元素，容器与模板零件不占表。</summary>
    private static bool IsInteresting(FrameworkElement element) =>
        element is TextBlock or Control or Image;

    private static void Record(
        FrameworkElement element,
        Rect bounds,
        AppTheme theme,
        string screen,
        StringBuilder rows,
        List<string> unparsed,
        StringBuilder notes,
        DpiScale dpi,
        Rect rootBounds,
        Color? background,
        double opacity)
    {
        var text = TextOf(element);
        var fontSize = element is Control control ? control.FontSize : double.NaN;
        var weight = element is Control c ? c.FontWeight : FontWeights.Normal;

        if (element is TextBlock block)
        {
            fontSize = block.FontSize;
            weight = block.FontWeight;
        }

        Color? foreground = null;
        var brushKind = string.Empty;

        if (element is TextBlock textBlock)
        {
            (foreground, brushKind) = Resolve(textBlock.Foreground, unparsed);
        }
        else if (element is Control control2)
        {
            (foreground, brushKind) = Resolve(control2.Foreground, unparsed);
        }

        var ratio = double.NaN;
        var grade = string.Empty;

        if (foreground is { } fg && background is { } bg && !string.IsNullOrEmpty(text))
        {
            var composited = Composite(fg, opacity, bg);
            ratio = Contrast(composited, bg);
            grade = Grade(ratio, fontSize, weight);
        }

        var trimmed = IsTrimmed(element, text, dpi);
        var clipped = IsClipped(bounds, rootBounds);

        rows.Append(string.Join('\t',
            screen,
            theme,
            element.GetType().Name,
            Cell(text),
            Cell(fontSize),
            Cell(weight.ToString()),
            Cell(element is Control c3 ? c3.FontFamily.ToString() : string.Empty),
            Cell(foreground is { } f ? Hex(f) : brushKind),
            Cell(background is { } b ? Hex(b) : "unknown"),
            Cell(ratio),
            Cell(grade),
            Cell(ContrastWcag(ratio)),
            Cell(bounds.X),
            Cell(bounds.Y),
            Cell(bounds.Width),
            Cell(bounds.Height),
            Cell(trimmed),
            Cell(clipped),
            Cell(element is Control tabControl ? tabControl.IsTabStop.ToString() : "n/a"),
            Cell(element is UIElement u && u.Focusable),
            Cell(element is not Control c4 || c4.IsEnabled),
            Cell(element.ToolTip is null ? string.Empty : "yes"),
            Cell(GetAuto(element, AutomationProperties.NameProperty)),
            Cell(GetAuto(element, AutomationProperties.AutomationIdProperty))));
        rows.AppendLine();
    }

    private static string ContrastWcag(double ratio) =>
        double.IsNaN(ratio) ? string.Empty : (ratio >= 4.5 ? "AA" : ratio >= 3 ? "AA-large" : "fail");

    private static string Grade(double ratio, double fontSize, FontWeight weight)
    {
        if (double.IsNaN(ratio))
        {
            return string.Empty;
        }

        // WCAG 的"大字"：>=18pt(≈24DIP) 或 >=14pt(≈18.7DIP) 且加粗
        var large = fontSize >= 18 || (fontSize >= 14 && weight >= FontWeights.Bold);

        return ratio >= (large ? 3 : 4.5) ? "ok" : large ? "large-low" : "low";
    }

    /// <summary>文字被省略号截掉：渲染宽度大于可用宽度。</summary>
    private static bool IsTrimmed(FrameworkElement element, string text, DpiScale dpi)
    {
        if (element is not TextBlock block || string.IsNullOrEmpty(text))
        {
            return false;
        }

        // 只有"带省略号且不换行"才叫截断：
        // 可换行的块期望宽度天然大于实际宽度（那正是折行的意思），
        // 按"期望 > 实际"判它，每一条正常折行的文字都会被报成截断（德语设置页实测）。
        // 折行后有没有把内容挤出容器，由 clipped 那一维负责。
        if (block.TextTrimming == TextTrimming.None || block.TextWrapping != TextWrapping.NoWrap)
        {
            return false;
        }

        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);

        var wanted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            block.FlowDirection,
            typeface,
            block.FontSize,
            block.Foreground ?? Brushes.Black,
            dpi.PixelsPerDip)
            .Width;

        return wanted > block.ActualWidth + 0.5;
    }

    /// <summary>盒子有一边落在窗口外（截不到、也点不到）。</summary>
    private static bool IsClipped(Rect bounds, Rect root) =>
        bounds.Left < root.Left - 0.5 || bounds.Top < root.Top - 0.5 ||
        bounds.Right > root.Right + 0.5 || bounds.Bottom > root.Bottom + 0.5;

    private static Rect? TryBounds(FrameworkElement element, Window window)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        try
        {
            var transform = element.TransformToVisual(window);
            return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            // 元素不在同一棵视觉树上（Popup 有自己的根）——单列，不当成"没问题"
            return null;
        }
    }

    private static string TextOf(FrameworkElement element) => element switch
    {
        TextBlock block => block.Text,
        ContentControl content when content.Content is string value => value,
        ContentControl content when content.Content is TextBlock inner => inner.Text,
        ToolTip tip when tip.Content is string value => value,
        _ => string.Empty,
    };

    private static Color? BackgroundOf(DependencyObject node) => node switch
    {
        Border border => Opaque(border.Background),
        Panel panel => Opaque(panel.Background),
        Window window => Opaque(window.Background),
        TextBox textBox => Opaque(textBox.Background),
        Control control => Opaque(control.Background),
        _ => null,
    };

    private static Color? Opaque(Brush? brush) =>
        brush is SolidColorBrush solid && solid.Color.A > 8 ? solid.Color : null;

    private static (Color?, string) Resolve(Brush? brush, List<string> unparsed)
    {
        switch (brush)
        {
            case null:
                return (null, "null");
            case SolidColorBrush solid:
                return (solid.Color, string.Empty);
            default:
                // 认不出来的写法必须冒出来，不能被当成"这一条不用测"
                var name = brush.GetType().Name;

                if (!unparsed.Contains(name))
                {
                    unparsed.Add(name);
                }

                return (null, name);
        }
    }

    private static Color Composite(Color foreground, double opacity, Color background)
    {
        var alpha = (foreground.A / 255.0) * Math.Clamp(opacity, 0, 1);

        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(byte fr, byte fg, byte fb, byte br, byte bg, byte bb) =>
        Contrast(Color.FromRgb(fr, fg, fb), Color.FromRgb(br, bg, bb));

    private static double Contrast(Color foreground, Color background)
    {
        var l1 = Luminance(foreground);
        var l2 = Luminance(background);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);

        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(double value) =>
            value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        return 0.2126 * Channel(color.R / 255.0) + 0.7152 * Channel(color.G / 255.0) + 0.0722 * Channel(color.B / 255.0);
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string GetAuto(FrameworkElement element, DependencyProperty property) =>
        element.GetValue(property) as string ?? string.Empty;

    private static string Cell(object? value) => value switch
    {
        null => string.Empty,
        double number => double.IsNaN(number) ? string.Empty : number.ToString("0.##", CultureInfo.InvariantCulture),
        string text => Sanitize(text),
        _ => Sanitize(value.ToString() ?? string.Empty),
    };

    private static string Sanitize(string value) => value
        .Replace('\t', ' ')
        .Replace('\r', ' ')
        .Replace('\n', ' ');
}
