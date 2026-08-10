using QuickTranslate.App.Services;
using QuickTranslate.Core.Geometry;
using Xunit;

namespace QuickTranslate.Tests.App;

public class WordPopupPlacementTests
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
    public void Case1_MiddleAnchor_BelowSpaceIsLargest_PopupAppearsBelow_CenteredHorizontally()
    {
        var anchor = new PhysicalRect(800, 100, 100, 30);
        var popup = new PhysicalSize(320, 150);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(anchor.Bottom, result.Y);

        int expectedCenterX = anchor.X + anchor.Width / 2;
        int actualCenterX = result.X + result.Width / 2;
        Assert.Equal(expectedCenterX, actualCenterX);
    }

    [Fact]
    public void Case2_AnchorNearBottom_PopupAppearsAbove()
    {
        var anchor = new PhysicalRect(500, 950, 200, 40);
        var popup = new PhysicalSize(320, 150);

        int spaceAbove = anchor.Top - WorkArea1920.Top;
        int spaceBelow = WorkArea1920.Bottom - anchor.Bottom;
        Assert.True(spaceAbove > spaceBelow, "Sanity: more space above");

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(anchor.Top - popup.Height, result.Y);

        int expectedCenterX = anchor.X + anchor.Width / 2;
        int actualCenterX = result.X + result.Width / 2;
        Assert.Equal(expectedCenterX, actualCenterX);
    }

    [Fact]
    public void Case3_AnchorNearRightEdge_PopupClampedToWorkArea()
    {
        var anchor = new PhysicalRect(1800, 500, 100, 30);
        var popup = new PhysicalSize(320, 150);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(WorkArea1920.Right - popup.Width, result.X);
    }

    [Fact]
    public void Case4_AnchorNearTopLeftCorner_BelowAndClamped()
    {
        var anchor = new PhysicalRect(10, 10, 50, 20);
        var popup = new PhysicalSize(320, 150);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.True(ContainedWithin(result, WorkArea1920), $"Popup {result} not within work area");
        Assert.Equal(WorkArea1920.Left, result.X);
        Assert.Equal(anchor.Bottom, result.Y);
    }

    [Fact]
    public void Case5_HugePopup_ClampedToWorkArea_AtTopLeft()
    {
        var anchor = new PhysicalRect(100, 100, 100, 30);
        var popup = new PhysicalSize(5000, 5000);

        var result = PopupPlacement.Place(anchor, WorkArea1920, popup);

        Assert.Equal(WorkArea1920.Left, result.Left);
        Assert.Equal(WorkArea1920.Top, result.Top);
        Assert.Equal(WorkArea1920.Right, result.Right);
        Assert.Equal(WorkArea1920.Bottom, result.Bottom);
    }

    [Fact]
    public void Case6_NoSpaceVertical_UsesRightSideSpace()
    {
        var work = new PhysicalRect(0, 0, 1920, 200);
        var anchor = new PhysicalRect(100, 30, 100, 150);
        var popup = new PhysicalSize(320, 150);

        int spaceAbove = anchor.Top - work.Top;
        int spaceBelow = work.Bottom - anchor.Bottom;
        Assert.True(spaceAbove < popup.Height, "sanity no vertical space above");
        Assert.True(spaceBelow < popup.Height, "sanity no vertical space below");

        var result = PopupPlacement.Place(anchor, work, popup);

        Assert.True(ContainedWithin(result, work), $"Popup {result} not within work area");
        Assert.True(result.X >= anchor.Right || result.Right <= anchor.Left,
            $"Popup should be horizontally beside anchor. result={result}, anchor={anchor}");
    }
}
