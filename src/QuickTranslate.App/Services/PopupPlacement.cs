using QuickTranslate.Core.Geometry;

namespace QuickTranslate.App.Services;

/// <summary>弹窗相对锚点的候选方位。</summary>
public enum PopupPlacementSide { Auto, Below, Above, Left, Right }

public static class PopupPlacement
{
    /// <summary>推断粘性方位时的容差，避免高 DPI 下取整抖动误判。</summary>
    private const double SideTolerancePx = 4.0;

    public enum PlacementDirection
    {
        Above,
        Below,
        Left,
        Right
    }

    public static PhysicalRect Place(PhysicalRect anchorBox, PhysicalRect monitorWorkArea, PhysicalSize popupPreferredSize)
        => Place(anchorBox, monitorWorkArea, popupPreferredSize, PopupPlacementSide.Auto);

    public static PhysicalRect Place(PhysicalRect anchorBox, PhysicalRect monitorWorkArea, PhysicalSize popupPreferredSize, PopupPlacementSide preferredSide)
    {
        int popupW = popupPreferredSize.Width;
        int popupH = popupPreferredSize.Height;

        if (popupW > monitorWorkArea.Width)
        {
            popupW = monitorWorkArea.Width;
        }

        if (popupH > monitorWorkArea.Height)
        {
            popupH = monitorWorkArea.Height;
        }

        int spaceAbove = anchorBox.Top - monitorWorkArea.Top;
        int spaceBelow = monitorWorkArea.Bottom - anchorBox.Bottom;
        int spaceLeft = anchorBox.Left - monitorWorkArea.Left;
        int spaceRight = monitorWorkArea.Right - anchorBox.Right;

        bool canAbove = spaceAbove >= popupH;
        bool canBelow = spaceBelow >= popupH;
        bool canLeft = spaceLeft >= popupW;
        bool canRight = spaceRight >= popupW;

        int x, y;

        // 显式指定方位（粘性重定位专用）：把贴锚点的边“钉住”——空间不足时收缩尺寸（内容转滚动），
        // 绝不滑到锚点另一侧遮挡原文。Auto/未知值走自动选址+末尾通用 clamp。
        bool sticky = preferredSide != PopupPlacementSide.Auto;

        if (preferredSide == PopupPlacementSide.Below)
        {
            // 顶边钉在锚点底边：y 固定，高度收缩到下方剩余空间
            x = Math.Clamp((anchorBox.X + anchorBox.Width / 2) - popupW / 2,
                monitorWorkArea.Left, Math.Max(monitorWorkArea.Left, monitorWorkArea.Right - popupW));
            y = anchorBox.Bottom;
            popupH = Math.Min(popupH, Math.Max(1, monitorWorkArea.Bottom - y));
        }
        else if (preferredSide == PopupPlacementSide.Above)
        {
            // 底边钉在锚点顶边：高度收缩到上方剩余空间后整体上移
            x = Math.Clamp((anchorBox.X + anchorBox.Width / 2) - popupW / 2,
                monitorWorkArea.Left, Math.Max(monitorWorkArea.Left, monitorWorkArea.Right - popupW));
            popupH = Math.Min(popupH, Math.Max(1, anchorBox.Top - monitorWorkArea.Top));
            y = anchorBox.Top - popupH;
        }
        else if (preferredSide == PopupPlacementSide.Right)
        {
            // 左边缘钉在锚点右边：宽度收缩到右侧剩余空间
            y = Math.Clamp((anchorBox.Y + anchorBox.Height / 2) - popupH / 2,
                monitorWorkArea.Top, Math.Max(monitorWorkArea.Top, monitorWorkArea.Bottom - popupH));
            x = anchorBox.Right;
            popupW = Math.Min(popupW, Math.Max(1, monitorWorkArea.Right - x));
        }
        else if (preferredSide == PopupPlacementSide.Left)
        {
            // 右边缘钉在锚点左边：宽度收缩到左侧剩余空间
            y = Math.Clamp((anchorBox.Y + anchorBox.Height / 2) - popupH / 2,
                monitorWorkArea.Top, Math.Max(monitorWorkArea.Top, monitorWorkArea.Bottom - popupH));
            popupW = Math.Min(popupW, Math.Max(1, anchorBox.Left - monitorWorkArea.Left));
            x = anchorBox.Left - popupW;
        }
        else
        {
            if (canBelow || canAbove)
            {
                PlacementDirection dir;
                if (canBelow && canAbove)
                {
                    dir = spaceBelow >= spaceAbove ? PlacementDirection.Below : PlacementDirection.Above;
                }
                else if (canBelow)
                {
                    dir = PlacementDirection.Below;
                }
                else
                {
                    dir = PlacementDirection.Above;
                }

                x = (anchorBox.X + anchorBox.Width / 2) - popupW / 2;
                y = dir == PlacementDirection.Below ? anchorBox.Bottom : anchorBox.Top - popupH;
            }
            else if (canRight || canLeft)
            {
                PlacementDirection dir;
                if (canRight && canLeft)
                {
                    dir = spaceRight >= spaceLeft ? PlacementDirection.Right : PlacementDirection.Left;
                }
                else if (canRight)
                {
                    dir = PlacementDirection.Right;
                }
                else
                {
                    dir = PlacementDirection.Left;
                }

                x = dir == PlacementDirection.Right ? anchorBox.Right : anchorBox.Left - popupW;
                y = (anchorBox.Y + anchorBox.Height / 2) - popupH / 2;
            }
            else
            {
                int bestSpace = new[] { spaceAbove, spaceBelow, spaceLeft, spaceRight }.Max();

                if (bestSpace == spaceBelow || bestSpace == spaceAbove)
                {
                    PlacementDirection dir = bestSpace == spaceBelow ? PlacementDirection.Below : PlacementDirection.Above;
                    x = (anchorBox.X + anchorBox.Width / 2) - popupW / 2;
                    y = dir == PlacementDirection.Below ? anchorBox.Bottom : anchorBox.Top - popupH;
                }
                else
                {
                    PlacementDirection dir = bestSpace == spaceRight ? PlacementDirection.Right : PlacementDirection.Left;
                    x = dir == PlacementDirection.Right ? anchorBox.Right : anchorBox.Left - popupW;
                    y = (anchorBox.Y + anchorBox.Height / 2) - popupH / 2;
                }
            }
        }

        if (!sticky)
        {
            // 仅自动选址需要通用边界钳制；粘性分支已在钉边时完成各自的收缩与钳制
            x = Math.Clamp(x, monitorWorkArea.Left, monitorWorkArea.Right - popupW);
            y = Math.Clamp(y, monitorWorkArea.Top, monitorWorkArea.Bottom - popupH);
        }

        int clampedW = Math.Min(popupW, monitorWorkArea.Right - x);
        int clampedH = Math.Min(popupH, monitorWorkArea.Bottom - y);

        return new PhysicalRect(x, y, Math.Max(1, clampedW), Math.Max(1, clampedH));
    }

    /// <summary>
    /// 流式翻译内容增长时的粘性重定位：保持首次选定的象限方向重新放置（只延伸+clamp），
    /// 防止弹窗从锚点下方跳到上方来回抖动。空间不足时靠末端 clamp 收缩高度（内容转滚动），不翻边。
    /// </summary>
    public static PhysicalRect PlaceSticky(PhysicalRect anchorBox, PhysicalRect monitorWorkArea, PhysicalSize preferredSize, PhysicalRect previousRect)
    {
        // previousRect 为 default 时回退 Auto，保证首次调用与三参 Place 一致
        if (previousRect.Equals(default(PhysicalRect)))
        {
            return Place(anchorBox, monitorWorkArea, preferredSize, PopupPlacementSide.Auto);
        }

        PopupPlacementSide? inferred = null;

        // 判定顺序：先垂直（Below/Above）后水平（Right/Left）
        if (previousRect.Y >= anchorBox.Bottom - SideTolerancePx)
        {
            inferred = PopupPlacementSide.Below;
        }
        else if (previousRect.Bottom <= anchorBox.Top + SideTolerancePx)
        {
            inferred = PopupPlacementSide.Above;
        }
        else if (previousRect.X >= anchorBox.Right - SideTolerancePx)
        {
            inferred = PopupPlacementSide.Right;
        }
        else if (previousRect.Right <= anchorBox.Left + SideTolerancePx)
        {
            inferred = PopupPlacementSide.Left;
        }

        var side = inferred ?? PopupPlacementSide.Auto;
        return Place(anchorBox, monitorWorkArea, preferredSize, side);
    }
}
