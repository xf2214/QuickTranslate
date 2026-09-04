namespace QuickTranslate.Core.Geometry;

public readonly record struct PhysicalRect(int X, int Y, int Width, int Height)
{
    public static PhysicalRect Empty => new(0, 0, 0, 0);

    /// <summary>显示器枚举失败时的回退矩形（1080p 假设），仅用于 MonitorInfo 兜底构造。</summary>
    public static PhysicalRect Fallback1080p => new(0, 0, 1920, 1080);

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
