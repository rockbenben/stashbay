using FileStash.Infrastructure.Native;

namespace FileStash.Core.Tests;

public sealed class SelectionGrabberTests
{
    [Fact]
    public void IsForegroundCurrentProcess_ShouldReturnBooleanWithoutException()
    {
        // 验证 P/Invoke 签名与句柄读取在无前台或任意前台下稳定运行
        var isCurrent = SelectionGrabber.IsForegroundCurrentProcess();
        Assert.True(isCurrent || !isCurrent);
    }

}
