using FileStash.App.Controls;
using FileStash.App.Startup;

namespace FileStash.Core.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void LogonFlagsDoNotBecomeFileIntakeOrElevatedRetryRequests()
    {
        var plain = CommandLine.Parse(["--logon-startup"]);
        var elevated = CommandLine.Parse(["--elevated-logon"]);
        Assert.True(plain.LogonStartup);
        Assert.False(plain.ElevatedLogon);
        Assert.True(elevated.ElevatedLogon);
        Assert.Empty(elevated.AddPaths);
        Assert.Null(elevated.ElevatedRetryOperationId);
    }
    [Fact]
    public void ElevatedHomeMustOnlyApplyToRetryProcess()
    {
        Assert.Null(CommandLine.Parse(["--elevated-home", @"C:\fixture"]).RetryHome);
        Assert.Equal(@"C:\fixture", CommandLine.Parse(
            ["--elevated-home", @"C:\fixture", "--elevated-retry", "operation"]).RetryHome);
    }

    [Fact]
    public void Parse_WithNoArguments_ShouldBeEmpty()
    {
        var command = CommandLine.Parse([]);

        Assert.True(command.IsEmpty);
        Assert.Empty(command.AddPaths);
        Assert.Null(command.ApplyHerePath);
        Assert.False(command.ApplyOriginal);
    }

    [Fact]
    public void Parse_WithAdd_ShouldCollectPaths()
    {
        var command = CommandLine.Parse(["--add", @"C:\a.txt", @"C:\b.txt"]);

        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], command.AddPaths);
    }

    [Fact]
    public void Parse_WithAddFollowedByOption_ShouldStopAtOption()
    {
        var command = CommandLine.Parse(["--add", @"C:\a.txt", "--group", "我的组"]);

        Assert.Equal([@"C:\a.txt"], command.AddPaths);
        Assert.Equal("我的组", command.GroupName);
    }

    [Fact]
    public void Parse_WithApplyHere_ShouldReadDirectory()
    {
        var command = CommandLine.Parse(["--apply-here", @"D:\target"]);

        Assert.Equal(@"D:\target", command.ApplyHerePath);
    }

    [Fact]
    public void Parse_WithApplyOriginalAndGroup()
    {
        var command = CommandLine.Parse(["--apply-original", "--group", "登录修改"]);

        Assert.True(command.ApplyOriginal);
        Assert.Equal("登录修改", command.GroupName);
    }

    [Fact]
    public void Parse_WithMinimized()
    {
        Assert.True(CommandLine.Parse(["--minimized"]).Minimized);
    }

    [Fact]
    public void Parse_WithElevatedRetry()
    {
        var command = CommandLine.Parse(["--elevated-retry", "op-123"]);

        Assert.Equal("op-123", command.ElevatedRetryOperationId);
    }

    [Fact]
    public void Parse_WithShow()
    {
        Assert.True(CommandLine.Parse(["--show"]).Show);
    }
}

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Win+Alt+S")]
    [InlineData("win+alt+s")]
    [InlineData("Win + Alt + S")]
    [InlineData("Win+Alt+W")]
    [InlineData("Win+Alt+V")]
    [InlineData("Ctrl+Alt+S")]
    [InlineData("ctrl+alt+s")]
    [InlineData("Ctrl + Alt + S")]
    // 日常习惯键名
    [InlineData("Win+Alt+Enter")]
    [InlineData("Ctrl+Shift+Esc")]
    [InlineData("Win+Alt+1")]
    [InlineData("Win+Alt+0")]
    [InlineData("Win+Alt+PageUp")]
    [InlineData("Win+Alt+PgUp")]
    [InlineData("Win+Alt+PageDown")]
    [InlineData("Win+Alt+PgDn")]
    [InlineData("Win+Alt+Backspace")]
    [InlineData("Win+Alt+Delete")]
    [InlineData("Win+Alt+Del")]
    [InlineData("Win+Alt+CapsLock")]
    [InlineData("Win+Alt+Space")]
    [InlineData("Win+Alt+,")]
    [InlineData("Win+Alt+.")]
    [InlineData("Win+Alt+-")]
    [InlineData("Win+Alt+=")]
    [InlineData("Win+Alt+/")]
    [InlineData("Win+Alt+;")]
    [InlineData("Win+Alt+[")]
    [InlineData("Win+Alt+]")]
    [InlineData("Win+Alt+\\")]
    [InlineData("Win+Alt+'")]
    [InlineData("Win+Alt+Num1")]
    // 多语言本地化习惯
    [InlineData("Strg+Alt+S")]         // 德语 Ctrl (Strg)
    [InlineData("Ctrl+Maj+V")]         // 法语 Shift (Maj)
    [InlineData("Ctrl+Mayus+V")]       // 西班牙语 Shift (Mayus)
    [InlineData("Ctrl+Maiusc+V")]      // 意大利语 Shift (Maiusc)
    [InlineData("Win+Alt+Eingabe")]    // 德语 Enter
    [InlineData("Win+Alt+Entrée")]     // 法语 Enter
    [InlineData("Win+Alt+Intro")]      // 西班牙语 Enter
    public void TryParse_ShouldAcceptModifierCombinations(string gesture)
    {
        Assert.True(HotkeyManager.TryParse(gesture, out var modifiers, out var virtualKey), $"Failed to parse gesture: {gesture}");
        Assert.NotEqual(0u, modifiers);
        Assert.NotEqual(0u, virtualKey);
    }

    [Fact]
    public void TryParse_WinAltEnter_ShouldMapToReturnVirtualKey()
    {
        Assert.True(HotkeyManager.TryParse("Win+Alt+Enter", out var modifiers, out var virtualKey));
        Assert.Equal(0x0009u, modifiers);
        Assert.Equal(0x0Du, virtualKey); // VK_RETURN
    }

    [Fact]
    public void TryParse_WinAltDigit_ShouldMapToDigitVirtualKey()
    {
        Assert.True(HotkeyManager.TryParse("Win+Alt+1", out var modifiers, out var virtualKey));
        Assert.Equal(0x0009u, modifiers);
        Assert.Equal(0x31u, virtualKey); // '1'
    }

    [Fact]
    public void FormatKey_ShouldFollowEverydayHabits()
    {
        Assert.Equal("Enter", HotkeyBox.FormatKey(System.Windows.Input.Key.Return));
        Assert.Equal("Esc", HotkeyBox.FormatKey(System.Windows.Input.Key.Escape));
        Assert.Equal("Backspace", HotkeyBox.FormatKey(System.Windows.Input.Key.Back));
        Assert.Equal("Delete", HotkeyBox.FormatKey(System.Windows.Input.Key.Delete));
        Assert.Equal("PageUp", HotkeyBox.FormatKey(System.Windows.Input.Key.Prior));
        Assert.Equal("PageDown", HotkeyBox.FormatKey(System.Windows.Input.Key.Next));
        Assert.Equal("CapsLock", HotkeyBox.FormatKey(System.Windows.Input.Key.Capital));
        Assert.Equal("1", HotkeyBox.FormatKey(System.Windows.Input.Key.D1));
        Assert.Equal("Num1", HotkeyBox.FormatKey(System.Windows.Input.Key.NumPad1));
        Assert.Equal(",", HotkeyBox.FormatKey(System.Windows.Input.Key.OemComma));
        Assert.Equal(".", HotkeyBox.FormatKey(System.Windows.Input.Key.OemPeriod));
    }

    [Fact]
    public void TryParse_WinAltS_ShouldSetCorrectModifiers()
    {
        Assert.True(HotkeyManager.TryParse("Win+Alt+S", out var modifiers, out var virtualKey));
        // MOD_ALT (0x0001) | MOD_WIN (0x0008) = 0x0009
        Assert.Equal(0x0009u, modifiers);
        Assert.NotEqual(0u, virtualKey);
    }

    [Fact]
    public void TryParse_ShouldRejectGestureWithoutModifiers()
    {
        Assert.False(HotkeyManager.TryParse("S", out _, out _));
    }

    [Fact]
    public void TryParse_ShouldRejectEmptyGesture()
    {
        Assert.False(HotkeyManager.TryParse("   ", out _, out _));
    }
}
