using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FileStash.Core.Tests;

/// <summary>
/// 多语言这条线的护栏：资源表不能漂，界面不能绕过资源表写死文案。
/// </summary>
/// <remarks>
/// 这些判据都是纯文本的，扫源码就能查，所以做成测试而不是检查项 ——
/// 中文文案短，看一眼扫不出问题；而"某一行漏了翻译"这种问题，
/// 出事的时候是**英文界面里混出一句中文**，而不是编译错误。
/// </remarks>
public sealed class ResourceTests
{
    /// <summary>
    /// 允许含汉字的键：
    /// 语言名要用母语写（否则看不懂界面的人找不到自己的语言）；
    /// 旧快捷方式的文件名是路径，不是文案 —— 它得一直是那个名字才能删掉它。
    /// </summary>
    /// <remarks>
    /// 18 门语言里只有三种用汉字写：简体中文、繁體中文、日本語。
    /// </remarks>
    private static readonly string[] AllowedChineseKeys =
        ["LanguageChinese", "LanguageTraditionalChinese", "LanguageJapanese", "ShellLegacyShortcutName"];

    /// <summary>
    /// 用汉字书写的语言：它们的译文里本来就有汉字，
    /// "含中文 = 漏翻"这条判据对它们不成立，得换个判据（见 <see cref="TraditionalChineseTable_MustNotContainSimplifiedCharacters"/>）。
    /// </summary>
    private static readonly string[] HanScriptLanguages = ["zh-TW", "ja-JP"];

    /// <summary>
    /// 简体专用字：这些字在繁体里写法不同（设置→設置、文件→檔案、记录→記錄……）。
    /// </summary>
    /// <remarks>
    /// 用来查繁体表里的"漏转换"。这类错最难看：整句话读得通，只是有个字是简体的，
    /// 混在一屏繁体里，不逐字看根本发现不了。
    /// 列表只收本项目词表里真出现过的字 —— 收全了也没用，反而容易把繁体正字
    /// （台、群、里、占、冲、托）误判成简体。
    /// </remarks>
    private const string SimplifiedOnlyCharacters =
        "与个临为仅从仓会体储关写冲击删别动区单历发变后听启员唤处备复夹对将属库应开异当录径态悬执换据择数无时显暂机权条来构标检测浏点状监盖盘确称简类组经结统继续规览计认记设试询详语误请读败贴资载输过运还这进选里铺销错键闭问间随项预";

    /// <summary>不查的目录：这些地方的文案是给开发者看的（日志）或给机器看的（--inspect 的输出格式）。</summary>
    private static readonly string[] ExcludedDirectories = ["Diagnostics"];

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

    private static IEnumerable<(string Project, string Language, Dictionary<string, string> Table)> ResourceTables()
    {
        foreach (var project in new[] { "FileStash.App", "FileStash.Core" })
        {
            var directory = Path.Combine(RepositoryRoot, "src", project, "Resources");

            foreach (var path in Directory.EnumerateFiles(directory, "Strings*.resx"))
            {
                var name = Path.GetFileNameWithoutExtension(path);   // Strings / Strings.en
                var language = name == "Strings" ? "neutral" : name["Strings.".Length..];

                yield return (project, language, ReadResx(path));
            }
        }
    }

    private static Dictionary<string, string> ReadResx(string path)
    {
        var document = XDocument.Load(path);
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var data in document.Root!.Elements("data"))
        {
            var name = (string?)data.Attribute("name");
            var value = (string?)data.Element("value") ?? string.Empty;

            if (name is not null)
            {
                table[name] = value;
            }
        }

        return table;
    }

    private static bool ContainsChinese(string text) =>
        text.Any(c => c >= '一' && c <= '鿿');

    [Fact]
    public void EveryLanguageTable_MustHaveTheSameKeys()
    {
        // 少一个键 = 那一处界面上会露出键名本身（Get 找不到就返回 key）。
        // 多一个键 = 没人用得到，白留着。
        var problems = new List<string>();

        foreach (var group in ResourceTables().GroupBy(t => t.Project))
        {
            var neutral = group.FirstOrDefault(t => t.Language == "neutral").Table;

            if (neutral.Count == 0)
            {
                problems.Add($"{group.Key}: 没有中性资源表");
                continue;
            }

            foreach (var (project, language, table) in group)
            {
                if (language == "neutral")
                {
                    continue;
                }

                foreach (var missing in neutral.Keys.Except(table.Keys, StringComparer.Ordinal))
                {
                    problems.Add($"{project}/{language}: 缺键 {missing}");
                }

                foreach (var extra in table.Keys.Except(neutral.Keys, StringComparer.Ordinal))
                {
                    problems.Add($"{project}/{language}: 多出键 {extra}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void EveryResourceValue_MustNotBeEmpty()
    {
        // 空值在界面上就是一片空白，比写错更难看出问题。
        var problems = new List<string>();

        foreach (var (project, language, table) in ResourceTables())
        {
            foreach (var (key, value) in table.Where(pair => string.IsNullOrWhiteSpace(pair.Value)))
            {
                problems.Add($"{project}/{language}: {key} 是空的");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void NonHanLanguageTables_MustNotContainChinese()
    {
        // 除了用汉字书写的两种语言，其余语言的译文里出现汉字 = 那一处漏翻了。
        // 一屏德语里混一句中文，用户一眼就看出来了 —— 这类错不会静悄悄地过去。
        var problems = new List<string>();

        foreach (var (project, language, table) in ResourceTables())
        {
            if (language == "neutral" || HanScriptLanguages.Contains(language, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var (key, value) in table)
            {
                if (AllowedChineseKeys.Contains(key, StringComparer.Ordinal))
                {
                    continue;
                }

                if (ContainsChinese(value))
                {
                    problems.Add($"{project}/{language}: {key} = {value}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void TraditionalChineseTable_MustNotContainSimplifiedCharacters()
    {
        // 繁体表的漏译，两种形态：
        // ① 整条没翻（跟简体一模一样）—— 那种一眼看得出来；
        // ② 翻了但漏转了某个字（"設置"写成"设置"）—— 读得通，所以最难发现。
        // 这里查的就是 ②。
        var problems = new List<string>();

        foreach (var (project, language, table) in ResourceTables().Where(t => t.Language == "zh-TW"))
        {
            foreach (var (key, value) in table)
            {
                if (AllowedChineseKeys.Contains(key, StringComparer.Ordinal))
                {
                    continue;
                }

                var simplified = value
                    .Where(c => SimplifiedOnlyCharacters.Contains(c))
                    .Distinct()
                    .ToArray();

                if (simplified.Length > 0)
                {
                    problems.Add($"{project}/zh-TW: {key} = {value}（简体字 {string.Join(",", simplified)}）");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void EveryLanguageTable_MustHaveTheSamePlaceholders()
    {
        // 占位符对不上，那个位置就会显示 "{0}" 或者抛 FormatException。
        // 中文里占位符多在句尾，别的语言里可能跑到句首 —— 位置可以变，
        // 但编号与个数不能变（{0} 和 {1} 对调比少一个更隐蔽：那句话照样通顺）。
        var problems = new List<string>();

        foreach (var group in ResourceTables().GroupBy(t => t.Project))
        {
            var neutral = group.FirstOrDefault(t => t.Language == "neutral").Table;

            foreach (var (project, language, table) in group)
            {
                foreach (var (key, value) in table)
                {
                    if (!neutral.TryGetValue(key, out var source))
                    {
                        continue;
                    }

                    var expected = Placeholders(source).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                    var actual = Placeholders(value).OrderBy(p => p, StringComparer.Ordinal).ToArray();

                    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                    {
                        problems.Add($"{project}/{language}: {key} 占位符 [{string.Join(",", actual)}] ≠ [{string.Join(",", expected)}]");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>取出一段文字里的 {0}、{1} 这类占位符。</summary>
    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).ToArray();

    [Fact]
    public void Views_MustNotHardcodeChineseText()
    {
        // XAML 里写死的中文 = 永远翻不了的那一处。
        var problems = new List<string>();

        foreach (var xaml in SourceFiles("*.xaml"))
        {
            var document = XDocument.Load(xaml);

            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    if (!IsUserFacingAttribute(element.Name.LocalName, attribute.Name.LocalName))
                    {
                        continue;
                    }

                    if (ContainsChinese(attribute.Value))
                    {
                        problems.Add($"{Path.GetRelativePath(RepositoryRoot, xaml)}: <{element.Name.LocalName} {attribute.Name.LocalName}=\"{attribute.Value}\">");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void SourceFiles_MustNotHardcodeChineseText()
    {
        // C# 里写死的中文 = 同上。日志与 --inspect 的输出格式不在范围内：
        // 那是给开发者看的，翻译它只会让人在排查问题时更难找。
        var problems = new List<string>();

        foreach (var source in SourceFiles("*.cs"))
        {
            var text = File.ReadAllText(source);
            var lines = text.Split('\n');
            var logLines = LogCallLines(text, lines.Length);

            for (var index = 0; index < lines.Length; index++)
            {
                if (logLines[index])
                {
                    continue;
                }

                var code = StripComments(lines[index]);

                foreach (var literal in StringLiterals(code))
                {
                    if (ContainsChinese(literal))
                    {
                        problems.Add($"{Path.GetRelativePath(RepositoryRoot, source)}:{index + 1}: \"{literal}\"");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>
    /// 标出落在 Serilog 调用里的行 —— 日志文案不归"界面不能写死中文"这条护栏管。
    /// </summary>
    /// <remarks>
    /// 日志留中文是有道理的：排查问题时按中文关键词搜比按英文搜快。
    /// 但排除规则要按**调用形状**来定，不能按文件名 —— 按文件名排除，漏一次就废一次，
    /// 而且改个文件名就失效。认 <c>Log.方法(</c> 这个形状，改写法也不会漏。
    /// </remarks>
    private static bool[] LogCallLines(string text, int lineCount)
    {
        var result = new bool[lineCount];
        var index = 0;
        var line = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (current == '"')
            {
                index = SkipString(text, index, verbatim: false);
                continue;
            }

            if (current == '@' && index + 1 < text.Length && text[index + 1] == '"')
            {
                index = SkipString(text, index + 1, verbatim: true);
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;

                while (index < text.Length && !(text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/'))
                {
                    if (text[index] == '\n')
                    {
                        line++;
                    }

                    index++;
                }

                index = Math.Min(index + 2, text.Length);
                continue;
            }

            if (IsLoggingCall(text, index))
            {
                index = SkipCall(text, index, result, ref line);
                continue;
            }

            index++;
        }

        return result;
    }

    /// <summary>是不是 Serilog 的调用：Log.方法( / Serilog.Log.方法(。</summary>
    private static bool IsLoggingCall(string text, int index)
    {
        const string marker = "Log.";

        if (index + marker.Length >= text.Length)
        {
            return false;
        }

        if (string.CompareOrdinal(text, index, marker, 0, marker.Length) != 0)
        {
            return false;
        }

        // 前面是标识符字符的话，这是别的名字的一部分（MyLog.Write 之类）
        if (index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_'))
        {
            return false;
        }

        var after = index + marker.Length;

        while (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
        {
            after++;
        }

        if (after == index + marker.Length)
        {
            return false;   // "Log." 后面没有方法名
        }

        while (after < text.Length && char.IsWhiteSpace(text[after]))
        {
            after++;
        }

        return after < text.Length && text[after] == '(';
    }

    /// <summary>跳过整个调用（从方法名到配对的右括号），把途经的行标上。</summary>
    private static int SkipCall(string text, int index, bool[] logLines, ref int line)
    {
        var depth = 0;

        while (index < text.Length)
        {
            Mark(logLines, line);

            var current = text[index];

            if (current == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (current == '"')
            {
                index = SkipString(text, index, verbatim: false);
                continue;
            }

            if (current == '@' && index + 1 < text.Length && text[index + 1] == '"')
            {
                index = SkipString(text, index + 1, verbatim: true);
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return index + 1;
                }
            }

            index++;
        }

        return index;
    }

    private static void Mark(bool[] lines, int line)
    {
        if (line >= 0 && line < lines.Length)
        {
            lines[line] = true;
        }
    }

    /// <summary>只有这些属性上的值会显示给用户。</summary>
    private static bool IsUserFacingAttribute(string elementName, string attributeName) => attributeName switch
    {
        "Text" or "Content" or "Header" or "Title" or "ToolTip" or "Name" => true,
        "AutomationProperties.Name" => true,
        _ => false,
    };

    private static IEnumerable<string> SourceFiles(string pattern)
    {
        var root = Path.Combine(RepositoryRoot, "src");

        return Directory
            .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !ExcludedDirectories.Any(directory => path.Contains($"{Path.DirectorySeparatorChar}{directory}{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            .Where(path => !path.EndsWith("Strings.cs", StringComparison.Ordinal));
    }

    /// <summary>去掉行注释。字符串里的 "//" 不算注释（路径里就有）。</summary>
    private static string StripComments(string line)
    {
        var index = 0;

        while (index < line.Length)
        {
            var current = line[index];

            if (current == '"')
            {
                index = SkipString(line, index, verbatim: false);
                continue;
            }

            if (current == '@' && index + 1 < line.Length && line[index + 1] == '"')
            {
                index = SkipString(line, index + 1, verbatim: true);
                continue;
            }

            if (current == '/' && index + 1 < line.Length && line[index + 1] == '/')
            {
                return line[..index];
            }

            index++;
        }

        return line;
    }

    private static IEnumerable<string> StringLiterals(string code)
    {
        var index = 0;

        while (index < code.Length)
        {
            var current = code[index];

            if (current == '"')
            {
                var (literal, next) = ReadString(code, index, verbatim: false);
                yield return literal;
                index = next;
                continue;
            }

            if (current == '@' && index + 1 < code.Length && code[index + 1] == '"')
            {
                var (literal, next) = ReadString(code, index + 1, verbatim: true);
                yield return literal;
                index = next;
                continue;
            }

            index++;
        }
    }

    private static int SkipString(string line, int quoteIndex, bool verbatim)
    {
        var index = quoteIndex + 1;

        while (index < line.Length)
        {
            if (!verbatim && line[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (line[index] == '"')
            {
                if (verbatim && index + 1 < line.Length && line[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        return index;
    }

    private static (string Literal, int Next) ReadString(string code, int quoteIndex, bool verbatim)
    {
        var start = quoteIndex + 1;
        var index = start;

        while (index < code.Length)
        {
            if (!verbatim && code[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (code[index] == '"')
            {
                if (verbatim && index + 1 < code.Length && code[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                break;
            }

            index++;
        }

        return (code[start..Math.Min(index, code.Length)], index + 1);
    }

    /// <summary>
    /// 每门语言"旧的容器叫法"长什么样（含常见屈折）。
    /// </summary>
    /// <remarks>
    /// 中性表用 "neutral" 这个键（<see cref="ResourceTables"/> 就是这么给的）。
    /// 这张表必须覆盖所有在表的门 —— 新增语言时漏了它的旧词形，测试会直接红，
    /// 而不是安静地放过（见下面的自证断言）。
    /// </remarks>
    private static readonly Dictionary<string, string> RetiredContainerNouns = new(StringComparer.Ordinal)
    {
        ["neutral"] = "组",
        ["zh-TW"] = "組",
        ["en"] = @"\bgroup(s)?\b",
        ["ja-JP"] = "グループ",
        ["ko-KR"] = "그룹",
        ["es-ES"] = @"\bgrupo(s)?\b",
        ["pt-BR"] = @"\bgrupo(s)?\b",
        ["fr-FR"] = @"\bgroupe?s?\b",
        ["de-DE"] = "Gruppe",
        ["it-IT"] = @"\bgruppi?\b",
        ["ru-RU"] = "групп",
        ["ar-SA"] = "مجموعة",
        ["hi-IN"] = "समूह",
        ["id-ID"] = @"\bkumpulan\b",
        ["th-TH"] = "กลุ่ม",
        ["tr-TR"] = "grup",
        ["vi-VN"] = "nhóm",
        ["bn-BD"] = "গ্রুপ",
    };

    /// <summary>
    /// 容器这个概念全站统一叫"箱"（各语言用各自的对应词：Box / ボックス / 보관함 / ящик …），
    /// 旧叫法"组 / group"不许再回到任何可见值里。
    /// </summary>
    /// <remarks>
    /// 为什么值得钉住：改名之前，同一个东西在中文里叫"暂存箱 / 新建箱 / 移动到组 / 选择目标组"，
    /// 德语同一张表里既有 Neue Box 又有 In Gruppe verschieben —— 用户会以为箱和组是两个东西。
    /// 统一之后没有任何判据挡着下一次漂移，而这类漂移是逐条改文案时最容易顺手带回来的。
    /// </remarks>
    [Fact]
    public void RetiredContainerNoun_MustNotReappearInAnyValue()
    {
        var tables = ResourceTables().ToList();

        var languages = tables.Select(t => t.Language).Distinct().ToList();

        // 自证：判据覆盖每一门语言的每一张表，漏一门就不算过
        var uncovered = languages
            .Where(language => tables.Any(table => table.Language == language)
                              && !RetiredContainerNouns.ContainsKey(language))
            .ToList();

        Assert.True(uncovered.Count == 0, $"这些语言还没补上旧词形，判据覆盖不全：{string.Join(", ", uncovered)}");

        var offenders = new List<string>();

        foreach (var (project, language, table) in tables)
        {
            var pattern = RetiredContainerNouns[language];

            foreach (var (key, value) in table)
            {
                if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase))
                {
                    offenders.Add($"{project}/{language} {key}: 「{value}」");
                }
            }
        }

        Assert.True(offenders.Count == 0, "旧叫法回来了：\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 挂在语言表里的"路径类字符串"：它们是文件名，不是文案。
    /// </summary>
    /// <remarks>
    /// <c>ShellLegacyShortcutName</c> 喂给 <c>SendToRegistrar.RemoveLegacyShortcut()</c> ——
    /// 拿它拼出旧快捷方式的路径再删。一旦被哪门语言"顺手翻译"了，
    /// 那一门语言的用户清理时就去找一个从没存在过的文件名，
    /// 旧条目永远留在"发送到"菜单里，而且不报错。
    /// 现有测试只免除了它"含汉字 = 漏翻"的判定（见 AllowedChineseKeys），
    /// 没有钉住"每张表都得一模一样"这条 —— 这条测试补的就是那一步。
    /// </remarks>
    [Fact]
    public void PathLikeValues_MustBeIdenticalInEveryTable()
    {
        string[] pathLikeKeys = ["ShellLegacyShortcutName"];

        var tables = ResourceTables().ToList();

        foreach (var key in pathLikeKeys)
        {
            // 哪些表声明了这个键：按实际读到的算，不预设"两个项目各 18 张"
            var declaring = tables.Where(t => t.Table.ContainsKey(key)).ToList();

            // 解析器自证：读不到键、或只有一张表有键，都说明判据本身坏了
            Assert.True(declaring.Count >= 2, $"{key} 只在 {declaring.Count} 张表里找到，判据没量到东西");

            var offenders = declaring
                .GroupBy(t => t.Project)
                .SelectMany(group =>
                {
                    var expected = group.First(t => t.Language == "neutral").Table[key];

                    return group
                        .Where(t => t.Table[key] != expected)
                        .Select(t => $"{t.Project}/{t.Language}: 「{t.Table[key]}」≠ 中性表「{expected}」");
                })
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"{key} 是文件名，被翻译过的表：\n  " + string.Join("\n  ", offenders));
        }
    }
}
