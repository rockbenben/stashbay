using System.Windows;
using FileStash.App.Controls;

namespace FileStash.Core.Tests;

public sealed class ToastStackLayoutTests
{
    [Fact]
    public void DifferentHeightNoticesStackUpWithoutOverlap()
    {
        var area = new Rect(0, 0, 1920, 1040);
        var positions = ToastStackLayout.Arrange(area, [new Size(380, 88), new Size(380, 180), new Size(380, 120)]);
        Assert.Equal(3, positions.Count);
        Assert.Equal(area.Bottom - 16, positions[0].Bottom);
        for (var i = 0; i < positions.Count - 1; i++)
        {
            Assert.Equal(8, positions[i].Top - positions[i + 1].Bottom);
            Assert.False(positions[i].IntersectsWith(positions[i + 1]));
        }
    }

    [Fact]
    public void RemovingMiddleNoticeCompactsOnlyThoseAboveIt()
    {
        var area = new Rect(0, 0, 1920, 1040);
        var before = ToastStackLayout.Arrange(area, [new Size(380, 88), new Size(380, 180), new Size(380, 120)]);
        var after = ToastStackLayout.Arrange(area, [new Size(380, 88), new Size(380, 120)]);
        Assert.Equal(before[0], after[0]);
        Assert.Equal(188, after[1].Top - before[2].Top);
    }

    [Fact]
    public void OverflowWaitsInsteadOfLeavingTheMonitorAndReturnsWhenSpaceIsFreed()
    {
        var area = new Rect(0, 0, 1280, 464);
        Assert.Equal(2, ToastStackLayout.Arrange(area, [new Size(380, 180), new Size(380, 180), new Size(380, 180)]).Count);
        Assert.Equal(2, ToastStackLayout.Arrange(area, [new Size(380, 180), new Size(380, 180)]).Count);
    }

    [Fact]
    public void SecondaryMonitorCoordinatesAndSmallWidthsAreRespected()
    {
        var area = new Rect(-320, -200, 320, 600);
        var position = Assert.Single(ToastStackLayout.Arrange(area, [new Size(380, 120)]));
        Assert.Equal(-16, position.Right);
        Assert.Equal(384, position.Bottom);
        Assert.True(area.Contains(position));
    }
}
