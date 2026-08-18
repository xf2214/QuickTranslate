namespace QuickTranslate.Core.Selection;

public class SelectionOptions
{
    public int MaxAnchorDistanceBase { get; set; } = 36;
    public float MaxAnchorDistanceRowHeightFactor { get; set; } = 1.2f;
    public int MinWordWidth { get; set; } = 4;
    public int MinWordHeight { get; set; } = 3;
    public float ConfidenceFloor { get; set; } = 0.3f;
    public int MaxCandidatesPerLine { get; set; } = 30;

    // 词框宽度合理性上限：单字符宽不应超过行高的该倍数。
    // 比例法兜底在“识别文本短于检测行框”时会把几个字母摊到整行宽
    // （如 Text='y' Box=650x60），此类异常宽框直接拒绝，避免画出超大选框。
    public float MaxWordWidthPerCharHeightFactor { get; set; } = 1.3f;

    public float BlockMaxVerticalGapFactor { get; set; } = 0.5f;
    public float BlockMinHeightRatio { get; set; } = 0.65f;
    public float BlockMaxHeightRatio { get; set; } = 1.5f;
    public float BlockMinXOverlap { get; set; } = 0.5f;
    public float BlockMaxLeftEdgeDeltaFactor { get; set; } = 1.5f;
    public int BlockEdgeRetryThreshold { get; set; } = 20;
    public int BlockMaxLinesPerBlock { get; set; } = 30;

    // 候选行宽上限 = max(锚点行宽, 中位行宽) × 该因子：
    // 防止把紧邻段落的全宽 UI 栏/标题栏（宽度远超正文行）吸入块。
    public float BlockMaxWidthVsMedianFactor { get; set; } = 2.5f;

    // 锚点不在任何行内时，允许锚定最近行的最大距离 = max(MaxAnchorDistanceBase, 行高 × 该因子)。
    // 光标落在空白区时防止把不相近的段落误当目标块。
    public float BlockMaxAnchorDistanceFactor { get; set; } = 2.0f;

    public static SelectionOptions Default { get; } = new();

    public int ComputeEffectiveMax(int lineHeight)
    {
        return Math.Max(MaxAnchorDistanceBase, (int)Math.Round(lineHeight * MaxAnchorDistanceRowHeightFactor));
    }
}
