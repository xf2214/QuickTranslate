using QuickTranslate.App.Services;
using QuickTranslate.Core.Geometry;
using Xunit;

namespace QuickTranslate.Tests.App;

public class BlockPopupPlacementTests
{
    private static readonly PhysicalRect WorkArea1920 = new(0, 0, 1920, 1080);

    private static bool ContainedWithin(PhysicalRect inner, PhysicalRect outer)
    {
        return inner.Left >= outer.Left
               && inner.Top >= outer.Top
               && inner.Right <= outer.Right
               && inner.Bottom <= outer.Bottom;
    }

    [Fact]
    public void Place_AtTopLeft_ClampsInsideWorkArea()
    {
        var anchor = new PhysicalRect(10, 10, 80, 40);
        var popup = new PhysicalSize(440, 480);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(WorkArea1920.Left, result.Left);
        Assert.Equal(anchor.Bottom, result.Top);
    }

    [Fact]
    public void Place_AtTopRight_ClampsInsideWorkArea()
    {
        var anchor = new PhysicalRect(1850, 10, 60, 40);
        var popup = new PhysicalSize(440, 480);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(WorkArea1920.Right - popup.Width, result.Left);
    }

    [Fact]
    public void Place_AtBottomLeft_PrefersAboveOrClamps()
    {
        var anchor = new PhysicalRect(10, 1020, 80, 40);
        var popup = new PhysicalSize(440, 480);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(WorkArea1920.Left, result.Left);
        Assert.True(result.Right <= WorkArea1920.Right);
    }

    [Fact]
    public void Place_AtBottomRight_ClampsInside()
    {
        var anchor = new PhysicalRect(1850, 1020, 60, 40);
        var popup = new PhysicalSize(440, 480);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.True(result.Right <= WorkArea1920.Right);
        Assert.True(result.Bottom <= WorkArea1920.Bottom);
    }

    [Fact]
    public void Place_HorizontalCenteredOnAnchor_WhenBelowFits()
    {
        var anchor = new PhysicalRect(740, 100, 440, 40);
        var popup = new PhysicalSize(440, 200);

        int spaceBelow = WorkArea1920.Bottom - anchor.Bottom;
        Assert.True(spaceBelow >= popup.Height, "Sanity: enough space below");

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(anchor.Bottom, result.Top);

        int expectedCenterX = anchor.X + anchor.Width / 2;
        int actualCenterX = result.X + result.Width / 2;
        Assert.Equal(expectedCenterX, actualCenterX);
    }

    [Fact]
    public void Place_PrefersLargestSpaceDirection_AboveVsBelow()
    {
        var work = new PhysicalRect(0, 0, 1920, 500);
        var anchor = new PhysicalRect(900, 150, 120, 40);
        var popup = new PhysicalSize(440, 200);

        int spaceAbove = anchor.Top - work.Top;
        int spaceBelow = work.Bottom - anchor.Bottom;
        Assert.True(spaceBelow > spaceAbove, "Sanity: more space below");

        var result = PopupPlacement.Place(anchor, work, popup);

        Assert.True(ContainedWithin(result, work), $"Popup {result} not within work area");
        Assert.Equal(anchor.Bottom, result.Top);
    }
}
