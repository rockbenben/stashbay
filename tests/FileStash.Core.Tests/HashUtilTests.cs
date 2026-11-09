using System.Security.Cryptography;
using System.Text;
using FileStash.Core.Utils;

namespace FileStash.Core.Tests;

public sealed class HashUtilTests : IDisposable
{
    private readonly string _tempDir;

    public HashUtilTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "filestash-hashutil-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TempDirectory.DeleteRecursively(_tempDir);
    }

    [Fact]
    public void ComputeSha256_EmptySpan_ReturnsStandardEmptySha256()
    {
        var hash = HashUtil.ComputeSha256(ReadOnlySpan<byte>.Empty);
        // SHA-256 of empty string is e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public void ComputeSha256_KnownContent_ReturnsCorrectLowerHex()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var hash = HashUtil.ComputeSha256(data);
        var expected = Convert.ToHexStringLower(SHA256.HashData(data));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public async Task ComputeFileSha256Async_SmallFile_ReturnsCorrectHash()
    {
        var filePath = Path.Combine(_tempDir, "small.txt");
        var data = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
        await File.WriteAllBytesAsync(filePath, data, TestContext.Current.CancellationToken);

        var fileHash = await HashUtil.ComputeFileSha256Async(filePath, TestContext.Current.CancellationToken);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(data));

        Assert.Equal(expectedHash, fileHash);
    }

    [Fact]
    public async Task ComputeFileSha256Async_FileLargerThanBuffer_StreamsCorrectly()
    {
        // Buffer is 1 MB. Write 2.5 MB of deterministic data to exercise multiple buffer fills with ArrayPool.
        var filePath = Path.Combine(_tempDir, "large.bin");
        var largeData = new byte[(int)(2.5 * 1024 * 1024)];
        new Random(42).NextBytes(largeData);
        await File.WriteAllBytesAsync(filePath, largeData, TestContext.Current.CancellationToken);

        var fileHash = await HashUtil.ComputeFileSha256Async(filePath, TestContext.Current.CancellationToken);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(largeData));

        Assert.Equal(expectedHash, fileHash);
    }

    [Theory]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", false)] // must be lowercase
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85", false)]  // 63 chars (too short)
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8555", false)] // 65 chars (too long)
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85g", false)] // 'g' invalid
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85!", false)] // punctuation invalid
    public void IsValidSha256_ValidatesCorrectly(string? input, bool expected)
    {
        Assert.Equal(expected, HashUtil.IsValidSha256(input));
    }
}
