namespace QuickTranslate.Core.Geometry;

public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public static DipRect Empty => new(0.0, 0.0, 0.0, 0.0);

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0.0 || Height <= 0.0;

    public override string ToString() => $"X={X}, Y={Y}, W={Width}, H={Height}";
}
