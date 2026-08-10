using QuickTranslate.Core.Geometry;

namespace QuickTranslate.App.Services;

public static class PopupPlacement
{
    public enum PlacementDirection
    {
        Above,
        Below,
        Left,
        Right
    }

    public static PhysicalRect Place(PhysicalRect anchorBox, PhysicalRect monitorWorkArea, PhysicalSize popupPreferredSize)
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

        x = Math.Clamp(x, monitorWorkArea.Left, monitorWorkArea.Right - popupW);
        y = Math.Clamp(y, monitorWorkArea.Top, monitorWorkArea.Bottom - popupH);

        int clampedW = Math.Min(popupW, monitorWorkArea.Right - x);
        int clampedH = Math.Min(popupH, monitorWorkArea.Bottom - y);

        return new PhysicalRect(x, y, clampedW, clampedH);
    }
}
