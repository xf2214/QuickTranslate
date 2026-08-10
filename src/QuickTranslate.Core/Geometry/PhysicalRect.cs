namespace QuickTranslate.Core.Geometry;

public readonly record struct PhysicalRect(int X, int Y, int Width, int Height)
{
    public static PhysicalRect Empty => new(0, 0, 0, 0);

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PhysicalPoint point) =>
        point.X >= Left && point.X < Right &&
        point.Y >= Top && point.Y < Bottom;

    public bool Intersects(PhysicalRect other) =>
        Left < other.Right && Right > other.Left &&
        Top < other.Bottom && Bottom > other.Top;

    public override string ToString() => $"X={X}, Y={Y}, W={Width}, H={Height}";
}
