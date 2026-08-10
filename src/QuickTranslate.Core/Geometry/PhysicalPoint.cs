namespace QuickTranslate.Core.Geometry;

public readonly record struct PhysicalPoint(int X, int Y)
{
    public static PhysicalPoint Zero => new(0, 0);

    public static PhysicalPoint operator +(PhysicalPoint a, PhysicalPoint b) =>
        new(a.X + b.X, a.Y + b.Y);

    public static PhysicalPoint operator -(PhysicalPoint a, PhysicalPoint b) =>
        new(a.X - b.X, a.Y - b.Y);

    public override string ToString() => $"({X}, {Y})";
}
