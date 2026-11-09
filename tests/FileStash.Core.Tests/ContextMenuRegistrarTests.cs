using FileStash.Infrastructure.Shell;
using Microsoft.Win32;

namespace FileStash.Core.Tests;

/// <summary>
/// 资源管理器右键菜单的注册 / 注销。
/// </summary>
/// <remarks>
/// 写的是**临时根**（HKCU\Software\FileStashTest\...），测完删掉 ——
/// 真实路径 Software\Classes 下面挂的是用户的资源管理器菜单，不能拿它当测试对象。
/// </remarks>
public sealed class ContextMenuRegistrarTests : IDisposable
{
    private const string TestRoot = @"Software\FileStashTest";

    private readonly string _executablePath = @"C:\Program Files\StashBay\StashBay.exe";

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    private ContextMenuRegistrar CreateRegistrar() => new(_executablePath, TestRoot);

    [Fact]
    public void Register_ShouldWriteAllMenuItems()
    {
        CreateRegistrar().Register();

        using var key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\*\shell\StashBay.Add");
        Assert.NotNull(key);
        Assert.Equal("加入暂存", key.GetValue(null));
        Assert.Equal($"{_executablePath},0", key.GetValue("Icon"));
        Assert.Equal("Player", key.GetValue("MultiSelectModel"));

        using var command = key.OpenSubKey("command");
        Assert.NotNull(command);
        Assert.Equal($"\"{_executablePath}\" --add \"%1\"", command.GetValue(null));

        // 其余三项
        using var directory = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\Directory\shell\StashBay.Add");
        Assert.NotNull(directory);

        using var applyHere = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\Directory\shell\StashBay.ApplyHere");
        Assert.NotNull(applyHere);

        using var background = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\Directory\Background\shell\StashBay.ApplyHere");
        Assert.NotNull(background);
    }

    [Fact]
    public void Unregister_ShouldRemoveEverything()
    {
        var registrar = CreateRegistrar();
        registrar.Register();
        registrar.Unregister();

        using var key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\*\shell\StashBay.Add");
        Assert.Null(key);
    }

    [Fact]
    public void Register_ShouldBeIdempotent()
    {
        var registrar = CreateRegistrar();
        registrar.Register();
        registrar.Register();

        using var key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\*\shell\StashBay.Add");
        Assert.NotNull(key);
    }

    [Fact]
    public void Register_ShouldCleanUpLegacyKeys()
    {
        using (var legacy = Registry.CurrentUser.CreateSubKey($@"{TestRoot}\*\shell\FileStash.Add"))
        {
            legacy?.SetValue(null, "Legacy");
        }

        var registrar = CreateRegistrar();
        registrar.Register();

        using var keyAfterRegister = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\*\shell\FileStash.Add");
        Assert.Null(keyAfterRegister);
    }
}
