namespace QuickTranslate.Core.Geometry;

public readonly record struct DipPoint(double X, double Y)
{
    public static DipPoint Zero => new(0.0, 0.0);

    public override string ToString() => $"({X}, {Y})";
}
