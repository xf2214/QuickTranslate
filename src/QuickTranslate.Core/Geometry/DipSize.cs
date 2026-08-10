namespace QuickTranslate.Core.Geometry;

public readonly record struct DipSize(double Width, double Height)
{
    public static DipSize Empty => new(0.0, 0.0);

    public bool IsEmpty => Width <= 0.0 || Height <= 0.0;

    public override string ToString() => $"{Width}x{Height}";
}
