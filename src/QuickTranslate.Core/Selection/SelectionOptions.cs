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

    // 自适应行距护栏：行间距上限在固定比例（BlockMaxVerticalGapFactor × 行高）之外，
    // 再取“中位行间距 × 该因子”的更严值。紧凑排版下段落间距可能小于 0.5×行高，
    // 固定比例会让块跨段落生长；用实测行间距做基线能区分“段内行距”与“段间空隙”。
    public float BlockParagraphGapVsMedianFactor { get; set; } = 1.8f;

    // 自适应行距下限（像素）：行间距极小时 OCR 框边缘抖动可达数像素，
    // 防止把正常的行距抖动误判为段落边界。
    public int BlockParagraphGapMinPx { get; set; } = 4;

    // 段末短行判定：行右缘距中位右缘超过 中位行宽 × 该因子 → 视为段末短行。
    // 正文段落最后一行通常明显短于正文行，是天然的段落边界信号。
    public float BlockShortTailRightMarginFactor { get; set; } = 0.2f;

    // 全宽行判定：行右缘距中位右缘不超过 中位行宽 × 该因子 → 视为全宽正文行。
    // 短行护栏仅在块内已存在全宽行时生效，避免误伤全是短行的题注/列表块。
    public float BlockFullWidthRightMarginFactor { get; set; } = 0.12f;

    // 首行缩进判定（向上生长）：候选行左缘比块左缘右移超过 行高 × 该因子，
    // 视为当前段落的首行（缩进起始），纳入后停止向上生长，不跨入上一段落。
    public float BlockFirstLineIndentFactor { get; set; } = 1.0f;

    // 候选行宽上限 = max(锚点行宽, 中位行宽) × 该因子：
    // 防止把紧邻段落的全宽 UI 栏/标题栏（宽度远超正文行）吸入块。
    public float BlockMaxWidthVsMedianFactor { get; set; } = 2.5f;

    // 核心列宽度上限 = 行宽基准 × 该因子：宽于该值的行虽可纳入块（受上方宽度上限约束），
    // 但不参与“核心列并集”。水平连通性判定基于核心列，防止超宽行（横幅/跨栏标题）
    // 把块的并集撑宽后，将水平上并不连续的附近文本（另一栏/隔开的文本）桥接进来。
    public float BlockCoreWidthFactor { get; set; } = 1.2f;

    // 核心列单次横向增长上限 = 中位行高 × 该因子：吸入候选行使核心列并集宽度
    // 增长超过该值，说明选区正横向跳向另一文本区域（跨区域连通）→ 停在边界前。
    // 正常段落左右缘逐行抖动仅数像素；行首项目符号/行尾标点外伸也远小于一行高。
    public float BlockMaxCoreGrowthFactor { get; set; } = 1.0f;

    // 锚点不在任何行内时，允许锚定最近行的最大距离 = max(MaxAnchorDistanceBase, 行高 × 该因子)。
    // 光标落在空白区时防止把不相近的段落误当目标块。
    public float BlockMaxAnchorDistanceFactor { get; set; } = 2.0f;

    public static SelectionOptions Default { get; } = new();

    public int ComputeEffectiveMax(int lineHeight)
    {
        return Math.Max(MaxAnchorDistanceBase, (int)Math.Round(lineHeight * MaxAnchorDistanceRowHeightFactor));
    }
}
