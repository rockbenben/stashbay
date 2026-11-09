using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;

namespace FileStash.Core.Tests;

/// <summary>临时工作区：每个测试一套独立的目录与数据库。</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "filestash-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root, Root);
        Paths.EnsureDirectories();
        ConnectionFactory = new SqliteConnectionFactory(Paths);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public SqliteConnectionFactory ConnectionFactory { get; }

    public string CreateFile(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        TempDirectory.DeleteRecursively(Root);
    }
}
