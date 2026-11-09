namespace FileStash.Core.Tests;

/// <summary>临时目录的清理助手。</summary>
internal static class TempDirectory
{
    /// <summary>
    /// 递归删除目录。
    /// </summary>
    /// <remarks>
    /// 必须先清掉只读属性：blob 存完之后是只读的（防止误改），
    /// 而 Directory.Delete(recursive) 删不掉只读文件，会抛 UnauthorizedAccessException。
    ///
    /// 还必须清掉 SQLite 的连接池 —— 池里的连接一直占着文件句柄
    /// （WAL 副文件也是），不清池的话目录根本删不掉：
    /// 实测跑一百次测试能在临时目录里攒下九千个目录、六百兆。
    /// </remarks>
    public static void DeleteRecursively(string root)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch (Exception)
        {
            // 清池失败不影响删除
        }

        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ClearReadOnly(file);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 文件还开着：留着，临时目录由系统清理
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
