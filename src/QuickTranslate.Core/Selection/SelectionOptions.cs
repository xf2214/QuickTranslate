namespace QuickTranslate.Core.Selection;

public class SelectionOptions
{
    public int MaxAnchorDistanceBase { get; set; } = 36;
    public float MaxAnchorDistanceRowHeightFactor { get; set; } = 1.2f;
    public int MinWordWidth { get; set; } = 4;
    public int MinWordHeight { get; set; } = 3;
    public float ConfidenceFloor { get; set; } = 0.3f;
    public int MaxCandidatesPerLine { get; set; } = 30;

    public float BlockMaxVerticalGapFactor { get; set; } = 0.9f;
    public float BlockMinHeightRatio { get; set; } = 0.65f;
    public float BlockMaxHeightRatio { get; set; } = 1.5f;
    public float BlockMinXOverlap { get; set; } = 0.35f;
    public float BlockMaxLeftEdgeDeltaFactor { get; set; } = 1.5f;
    public int BlockEdgeRetryThreshold { get; set; } = 20;
    public int BlockMaxLinesPerBlock { get; set; } = 200;

    public static SelectionOptions Default { get; } = new();

    public int ComputeEffectiveMax(int lineHeight)
    {
        return Math.Max(MaxAnchorDistanceBase, (int)Math.Round(lineHeight * MaxAnchorDistanceRowHeightFactor));
    }
}
