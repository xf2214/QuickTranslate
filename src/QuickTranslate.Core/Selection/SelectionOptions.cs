namespace QuickTranslate.Core.Selection;

public class SelectionOptions
{
    public int MaxAnchorDistanceBase { get; set; } = 36;
    public float MaxAnchorDistanceRowHeightFactor { get; set; } = 1.2f;
    public int MinWordWidth { get; set; } = 4;
    public int MinWordHeight { get; set; } = 3;
    public float ConfidenceFloor { get; set; } = 0.3f;
    public int MaxCandidatesPerLine { get; set; } = 30;

    public static SelectionOptions Default { get; } = new();

    public int ComputeEffectiveMax(int lineHeight)
    {
        return Math.Max(MaxAnchorDistanceBase, (int)Math.Round(lineHeight * MaxAnchorDistanceRowHeightFactor));
    }
}
