using System.IO;

namespace FileStash.App.Diagnostics;

/// <summary>
/// 取景夹具的坐标：<see cref="SeedRunner"/> 记下工作区在哪，截图与探针按它构造真实目标路径。
/// </summary>
/// <remarks>
/// 走临时目录里的一个标记文件，而不是把路径写死在代码里 ——
/// 工作区每轮都是新的临时目录，写死的下一轮就指到别的数据上去了。
/// </remarks>
internal static class Fixture
{
    private static string Marker => Path.Combine(Path.GetTempPath(), "filestash-fixture.txt");

    /// <summary>
    /// 记下工作区与"默认箱"的 id。
    /// </summary>
    /// <remarks>
    /// 箱要按 id 认：默认箱的名字是**跟着界面语言变的**（中文态"默认暂存箱"、英文态是译文），
    /// 按名字选的后果是英文那一轮量到了别的箱 —— 两轮读数对不上，还看不出来。
    /// </remarks>
    public static void Save(string workspace, string defaultBoxId) =>
        File.WriteAllText(Marker, $"{workspace}{Environment.NewLine}{defaultBoxId}");

    private static string? ReadField(int index)
    {
        try
        {
            if (!File.Exists(Marker))
            {
                return null;
            }

            var lines = File.ReadAllText(Marker).Split(Environment.NewLine);

            return lines.Length > index && !string.IsNullOrWhiteSpace(lines[index])
                ? lines[index].Trim()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string? Load()
    {
        var workspace = ReadField(0);

        return workspace is null || !Directory.Exists(workspace) ? null : workspace;
    }

    public static string? DefaultBoxId => ReadField(1);
}
