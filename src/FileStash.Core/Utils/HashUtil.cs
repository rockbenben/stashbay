using System.Buffers;
using System.Security.Cryptography;

namespace FileStash.Core.Utils;

/// <summary>哈希工具。内容寻址存储用的就是 SHA-256 的小写十六进制。</summary>
public static class HashUtil
{
    /// <summary>缓冲区大小：1 MB（兼顾大文件流式哈希吞吐量与内存占用）。</summary>
    public const int BufferSize = 1024 * 1024;

    /// <summary>计算字节数组的 SHA-256（小写十六进制）。</summary>
    public static string ComputeSha256(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>计算文件的 SHA-256（小写十六进制）。</summary>
    public static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            useAsync: true);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        int read;

        try
        {
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    /// <summary>是否是合法的 blob 名（SHA-256 的小写十六进制，64 个字符）。</summary>
    public static bool IsValidSha256(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
