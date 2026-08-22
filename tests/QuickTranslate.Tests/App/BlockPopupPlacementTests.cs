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

    // 粘性重定位：流式增高时保持象限不抖动

    [Fact]
    public void PlaceSticky_SticksBelow_WhenPreviousWasBelow_EvenIfAboveHasMoreSpace()
    {
        // 锚点贴近底部但下方仍有少量空间，上方空间更大
        var anchor = new PhysicalRect(800, 900, 120, 40);
        var popupSmall = new PhysicalSize(440, 120);
        // 上次弹窗在锚点下方（模拟首次 Below 布局的结果）
        var previousRect = new PhysicalRect(700, anchor.Bottom, 440, 120);

        int spaceAbove = anchor.Top - WorkArea1920.Top;
        int spaceBelow = WorkArea1920.Bottom - anchor.Bottom;
        Assert.True(spaceAbove > spaceBelow, "Sanity: above has more space");
        Assert.True(spaceBelow >= popupSmall.Height, "Sanity: below still fits small popup");

        // Auto 会选上方（空间大），粘性应保持下方
        var autoResult = PopupPlacement.Place(anchor, WorkArea1920, popupSmall);
        Assert.Equal(anchor.Top - popupSmall.Height, autoResult.Top);

        var stickyResult = PopupPlacement.PlaceSticky(anchor, WorkArea1920, popupSmall, previousRect);

        Assert.True(ContainedWithin(stickyResult, WorkArea1920), $"Popup {stickyResult} not within work area");
        // 粘住下方：Y 在锚点底边附近（容差内）
        Assert.True(stickyResult.Y >= anchor.Bottom - 4, $"Expected sticky below but got {stickyResult} anchor {anchor}");
        Assert.Equal(anchor.Bottom, stickyResult.Top);
    }

    [Fact]
    public void PlaceSticky_SticksAbove_WhenPreviousWasAbove_EvenIfBelowHasMoreSpace()
    {
        // 锚点贴近顶部，上次弹窗在锚点上方；下方空间更大
        var anchor = new PhysicalRect(800, 100, 120, 40);
        // 上方剩余空间 100px，弹窗高度取 80 保证完全容纳（钉边语义下 Top == anchor.Top - H）
        var popupSmall = new PhysicalSize(440, 80);
        var previousRect = new PhysicalRect(700, anchor.Top - 80, 440, 80);

        int spaceAbove = anchor.Top - WorkArea1920.Top;
        int spaceBelow = WorkArea1920.Bottom - anchor.Bottom;
        Assert.True(spaceBelow > spaceAbove, "Sanity: below has more space");
        Assert.True(spaceAbove >= popupSmall.Height - 20, "Sanity check");

        var autoResult = PopupPlacement.Place(anchor, WorkArea1920, popupSmall);
        Assert.Equal(anchor.Bottom, autoResult.Top);

        var stickyResult = PopupPlacement.PlaceSticky(anchor, WorkArea1920, popupSmall, previousRect);

        Assert.True(ContainedWithin(stickyResult, WorkArea1920), $"Popup {stickyResult} not within work area");
        // 粘住上方：底边在锚点顶边附近
        Assert.True(stickyResult.Bottom <= anchor.Top + 4, $"Expected sticky above but got {stickyResult} anchor {anchor}");
        Assert.Equal(anchor.Top - popupSmall.Height, stickyResult.Top);
    }

    [Fact]
    public void PlaceSticky_BottomGrowth_DoesNotFlip_ClampsAtWorkAreaBottom()
    {
        // 底边增长：preferred 高度大于下方剩余空间，粘住下方时应 clamp 在 WorkArea 底边，而不是翻到上方
        var anchor = new PhysicalRect(800, 950, 120, 40);
        var previousRect = new PhysicalRect(700, anchor.Bottom, 440, 80);
        var preferredLarge = new PhysicalSize(440, 300);
        int spaceBelow = WorkArea1920.Bottom - anchor.Bottom; // 90
        Assert.True(preferredLarge.Height > spaceBelow, "Sanity: preferred taller than space below");

        var stickyResult = PopupPlacement.PlaceSticky(anchor, WorkArea1920, preferredLarge, previousRect);

        Assert.True(ContainedWithin(stickyResult, WorkArea1920), $"Popup {stickyResult} not within work area");
        // 空间不足时靠末端 clamp 收缩，底边贴 WorkArea 底边，不翻边到上方
        Assert.Equal(WorkArea1920.Bottom, stickyResult.Bottom);
        // 若翻到上方，Top 应为 anchor.Top - Height = 650；粘性下方 clamp 后 Bottom==WorkArea.Bottom 且 Top != 650
        Assert.NotEqual(anchor.Top - preferredLarge.Height, stickyResult.Top);
    }

    [Fact]
    public void PlaceSticky_DefaultPrevious_FallsBackToAuto_SameAsPlace()
    {
        var anchor = new PhysicalRect(740, 100, 440, 40);
        var popup = new PhysicalSize(440, 200);

        var expected = PopupPlacement.Place(anchor, WorkArea1920, popup);
        var sticky = PopupPlacement.PlaceSticky(anchor, WorkArea1920, popup, default);

        Assert.Equal(expected, sticky);
    }

    [Fact]
    public void PlaceSticky_DefaultPrevious_EqualsThreeParamOverload_AtBottomAnchor()
    {
        var anchor = new PhysicalRect(10, 1020, 80, 40);
        var popup = new PhysicalSize(440, 480);

        var expected = PopupPlacement.Place(anchor, WorkArea1920, popup);
        var sticky = PopupPlacement.PlaceSticky(anchor, WorkArea1920, popup, default);

        Assert.Equal(expected, sticky);
    }
}
