using FileStash.App.Controls;

namespace FileStash.Core.Tests;

public sealed class ToastCountdownTests
{
    [Fact]
    public void ReadingAndKeyboardFocusMustNotSpendRemainingTime()
    {
        var countdown = new ToastCountdown(TimeSpan.FromSeconds(6));
        countdown.Advance(TimeSpan.FromSeconds(2), paused: false);
        countdown.Advance(TimeSpan.FromMinutes(1), paused: true);
        Assert.Equal(TimeSpan.FromSeconds(4), countdown.Remaining);
        Assert.False(countdown.IsElapsed);
        countdown.Advance(TimeSpan.FromSeconds(3.5), paused: false);
        Assert.Equal(TimeSpan.FromMilliseconds(500), countdown.Remaining);
        countdown.Advance(TimeSpan.FromSeconds(1), paused: false);
        Assert.True(countdown.IsElapsed);
        Assert.Equal(0, countdown.Fraction);
    }

    [Fact]
    public void IndependentNoticesMustNotShareTheirCountdown()
    {
        var notice = new ToastCountdown(TimeSpan.FromSeconds(6));
        var result = new ToastCountdown(TimeSpan.FromSeconds(10));
        notice.Advance(TimeSpan.FromSeconds(6), paused: false);
        Assert.True(notice.IsElapsed);
        Assert.Equal(1, result.Fraction);
    }
}
