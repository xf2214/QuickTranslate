namespace QuickTranslate.Core.Geometry;

public readonly record struct PhysicalSize(int Width, int Height)
{
    public static PhysicalSize Empty => new(0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}x{Height}";
}
