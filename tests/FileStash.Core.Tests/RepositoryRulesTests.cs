using System.Text.RegularExpressions;
using FileStash.Core.Models;
using System.Xml;
using System.Xml.Linq;

namespace FileStash.Core.Tests;

/// <summary>
/// 构建期测试：这些判据是纯文本的，扫源码就能查，不需要跑起来。
/// </summary>
/// <remarks>
/// 之所以做成测试而不是检查项：它们同时躲过三道防线 —— 中文文案短看不出来、
/// 出事的那一行可能没有任何截图状态会渲染到、而源码里它长得完全正常。
/// 校验架构与编码约束守则。
/// </remarks>
public sealed class RepositoryRulesTests
{
    /// <summary>最紧的一档工作区：1366x768 @150% ≈ 464 DIP，减去边距 72。</summary>
    private const double TightestMaxHeight = 392;

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FileStash.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录：从 " + AppContext.BaseDirectory + " 向上找不到 FileStash.sln");
    }

    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "src"),
            "*.xaml",
            SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void InstallerMustKeepFilesOutsideItsInstallManifest()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot, "tools", "installer", "FileStash.iss"));
        Assert.DoesNotContain("Type: filesandordirs; Name: \"{app}", text);
        Assert.Contains("Type: dirifempty; Name: \"{app}\"", text);
        Assert.Contains("UninstallLogMode=overwrite", text);
    }

    [Fact]
    public void ProjectFiles_MustNotContainPublishProperties()
    {
        // 放进 csproj 会有两个后果：每次 dotnet build 都走 RID 专属单文件路径；
        // 自包含工程不能被别的工程引用，测试工程一引用就报 NETSDK1151。
        var forbidden = new[] { "RuntimeIdentifier", "SelfContained", "PublishSingleFile", "RestorePackagesWithLockFile" };

        var offenders = new List<string>();

        foreach (var project in ProjectFiles())
        {
            var text = File.ReadAllText(project);

            foreach (var property in forbidden)
            {
                if (text.Contains($"<{property}", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(RepositoryRoot, project)}: <{property}>");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void GlobalJson_MustPinSdkAndSelectTheMtpTestRunner()
    {
        var path = Path.Combine(RepositoryRoot, "global.json");
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        // 下限 + rollForward：写当前补丁号会让只装着旧 feature band 的机器整个构建跑不起来
        Assert.Equal("latestFeature", json.GetProperty("sdk").GetProperty("rollForward").GetString());
        Assert.False(json.GetProperty("sdk").GetProperty("allowPrerelease").GetBoolean());

        // 这段决定 dotnet test 走 Microsoft Testing Platform。
        // 去掉它不是"回到旧行为"，而是 SDK 换一套 runner 跑，MTP 专有的
        // --minimum-expected-tests 立刻报 MSB1001，CI 的空跑守卫直接失效。
        Assert.Equal(
            "Microsoft.Testing.Platform",
            json.GetProperty("test").GetProperty("runner").GetString());
    }

    [Fact]
    public void Repository_MustNotContainPackageLockFile()
    {
        // 锁文件 + 单文件发布 + SDK 补丁 = 某个补丁日 NU1004
        var locks = Directory
            .EnumerateFiles(RepositoryRoot, "packages.lock.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(locks);
    }

    [Fact]
    public void SizeToContentWindows_MustHaveAConservativeMaxHeightFallback()
    {
        // 探测失败时只有 XAML 里这个值生效，那正是最需要保守值的小屏场景。
        // 内部控件上的 MaxHeight 不算 —— 只看 <Window> 自己那个标签。
        var offenders = new List<string>();

        foreach (var xaml in XamlFiles())
        {
            var document = XDocument.Load(xaml);
            var root = document.Root;

            if (root is null || root.Name.LocalName != "Window")
            {
                continue;
            }

            if (root.Attribute("SizeToContent") is null)
            {
                continue;
            }

            var maxHeight = root.Attribute("MaxHeight");

            if (maxHeight is null)
            {
                offenders.Add($"{Path.GetRelativePath(RepositoryRoot, xaml)}: SizeToContent 窗口没有 MaxHeight 兜底");
                continue;
            }

            if (!double.TryParse(maxHeight.Value, out var value) || value > TightestMaxHeight)
            {
                offenders.Add($"{Path.GetRelativePath(RepositoryRoot, xaml)}: MaxHeight={maxHeight.Value} 超过最紧一档 {TightestMaxHeight}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TrayIcon_MustBeDeclaredAsResourceAndPointAtARealFile()
    {
        // WPF SDK 的默认 Resource 通配不覆盖 .ico，漏了这条的症状是：
        // 运行期托盘图标被创建的那一刻才抛 "找不到资源"（平时跑着看不出来）
        var projectPath = Path.Combine(RepositoryRoot, "src", "FileStash.App", "FileStash.App.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.Contains("<Resource Include=", project, StringComparison.Ordinal);

        var iconPath = Path.Combine(RepositoryRoot, "src", "FileStash.App", "Resources", "app.ico");
        Assert.True(File.Exists(iconPath), $"托盘图标文件不存在：{iconPath}");

        // Tray.xaml 里的 pack URI 必须指向真实存在的文件
        var trayXaml = Path.Combine(RepositoryRoot, "src", "FileStash.App", "Resources", "Tray.xaml");
        var text = File.ReadAllText(trayXaml);

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"pack://application:,,,/(?<path>[^""]+)");

        Assert.True(match.Success, "Tray.xaml 里没有找到 IconSource 的 pack URI");

        var referenced = Path.Combine(
            RepositoryRoot,
            "src",
            "FileStash.App",
            match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(referenced), $"pack URI 指向的文件不存在：{referenced}");
    }

    [Fact]
    public void WrappingTextBlocks_MustNotSitInAnUnboundedHorizontalStackPanel()
    {
        // 水平 StackPanel 给子元素无限宽，TextBlock 于是永远不会换行 ——
        // 写的那个 Wrap 是一句空话。正解是放进 Grid 的 * 列；
        // 退一步也要给 Width 或 MaxWidth。
        var offenders = new List<string>();

        foreach (var xaml in XamlFiles())
        {
            var document = XDocument.Load(xaml, LoadOptions.SetLineInfo);

            foreach (var textBlock in document.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
            {
                var wrapping = (string?)textBlock.Attribute("TextWrapping")?.Value == "Wrap";
                var trimming = textBlock.Attribute("TextTrimming")?.Value is not null;

                if (!wrapping && !trimming)
                {
                    continue;
                }

                if (textBlock.Attribute("Width") is not null || textBlock.Attribute("MaxWidth") is not null)
                {
                    continue;
                }

                var parent = textBlock.Parent;

                if (parent is null || parent.Name.LocalName != "StackPanel")
                {
                    continue;
                }

                var orientation = (string?)parent.Attribute("Orientation")?.Value;

                // StackPanel 的默认朝向是竖的，竖排会给子元素**有界**宽度 —— 只有横排才无限宽
                if (orientation is not null && orientation != "Horizontal")
                {
                    continue;
                }

                if (orientation is null)
                {
                    continue;
                }

                var where = $"{Path.GetRelativePath(RepositoryRoot, xaml)}:{((IXmlLineInfo)textBlock).LineNumber}";
                offenders.Add($"{where}: TextBlock(Wrap/Trimming) 位于水平 StackPanel 内且无 Width/MaxWidth —— 无限宽下两个都永不生效");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void SettingsPageBindings_MustNameAPropertySomebodyActuallyReads()
    {
        // 设置页每个勾都得有人读：绑到模型上没有的属性，WPF 静默不报错，
        // 勾看起来能打上、重启就没了；属性存在但没人消费，同样是摆设。
        var view = Path.Combine(RepositoryRoot, "src", "FileStash.App", "Views", "SettingsWindow.xaml");
        var model = Path.Combine(RepositoryRoot, "src", "FileStash.Core", "Models", "AppSettings.cs");

        var bound = Regex.Matches(File.ReadAllText(view), @"Binding\s+Settings\.([A-Za-z][A-Za-z0-9_]*)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

        var declared = Regex.Matches(File.ReadAllText(model), @"public\s+[A-Za-z0-9_<>?,\s]+\s([A-Z][A-Za-z0-9_]*)\s*\{\s*get;")
            .Select(match => match.Groups[1].Value)
            .ToList();

        // 解析器自己先自证：两边都空说明正则没吃到形状，这条不能算过
        Assert.True(bound.Count >= 6, $"设置页一个绑定都没扫到（{bound.Count}），判据本身坏了");
        Assert.True(declared.Count >= 6, $"AppSettings 一个属性都没扫到（{declared.Count}），判据本身坏了");

        var sources = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !string.Equals(path, model, StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        var offenders = new List<string>();

        foreach (var name in bound)
        {
            if (!declared.Contains(name))
            {
                offenders.Add($"SettingsWindow 绑了 Settings.{name}，AppSettings 上没有这个属性（WPF 静默失败）");
                continue;
            }
        }

        // 反向也要成立：模型上每个设置项都得有人读，否则设置页/存档里它就是死字段
        foreach (var name in declared)
        {
            var read = sources.Any(code => Regex.IsMatch(code, @"(?<![A-Za-z0-9_])" + name + @"(?![A-Za-z0-9_])"));

            if (!read)
            {
                offenders.Add($"AppSettings.{name} 存在，但 src 里除了定义没人读它（设置改了不生效，或存档里长个死字段）");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 分段控件的 ConverterParameter 必须是真枚举成员。
    /// </summary>
    /// <remarks>
    /// 参数写错时 WPF 不报错：那一档永远点不亮，或写回时 Enum.Parse 抛在绑定里。
    /// 更狠的是这条判据曾经管不住"整组档位点了都没反应"——
    /// EnumMatchConverter.ConvertBack 一度无条件返回 Binding.DoNothing，
    /// 写回被丢掉，界面照样动。所以档位名要对，绑定也要真能写回（见 SegmentBindingTests）。
    /// </remarks>
    [Fact]
    public void EnumMatchSegments_ReferenceRealEnumMembers()
    {
        var view = Path.Combine(RepositoryRoot, "src", "FileStash.App", "Views", "MainWindow.xaml");
        var matches = Regex.Matches(
            File.ReadAllText(view),
            @"Binding\s+(WorkbenchViewMode|TargetMode)\s*,\s*Converter=\{StaticResource\s+EnumMatch\},\s*ConverterParameter=([A-Za-z]+)");

        // 解析器自证：扫不到形状就等于没查
        Assert.True(matches.Count >= 6, $"只扫到 {matches.Count} 处档位绑定，判据本身坏了");

        var cases = new[]
        {
            (Property: "WorkbenchViewMode", Members: Enum.GetNames<WorkbenchViewMode>()),
            (Property: "TargetMode", Members: Enum.GetNames<TargetMode>()),
        };

        foreach (var (property, members) in cases)
        {
            var used = matches
                .Where(match => match.Groups[1].Value == property)
                .Select(match => match.Groups[2].Value)
                .ToList();

            Assert.True(used.Count >= 3, $"{property} 只扫到 {used.Count} 个档位");
            Assert.Equal(used.Count, used.Distinct().Count());

            foreach (var name in used)
            {
                Assert.Contains(name, members);
            }
        }

        // 视图三档必须齐：少一档就是那个视图在界面上永远进不去
        Assert.Equal(
            Enum.GetNames<WorkbenchViewMode>(),
            matches
                .Where(match => match.Groups[1].Value == "WorkbenchViewMode")
                .Select(match => match.Groups[2].Value)
                .ToArray());
    }
}
