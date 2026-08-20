using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Ocr;

/// <summary>
/// 垂直投影词切分（spec 8.3：空白间隔/垂直投影优先于字符区间估计）。
/// 对 OCR 行裁剪位图做列墨水投影，按空白间隔切出每个词的真实像素边界，
/// 生成屏幕绝对坐标的词框。三级策略由调用方串联：
/// 1) <see cref="TrySegment"/>：精确对齐（含自适应阈值重试 + 多余段合并修复）；
/// 2) <see cref="TrySegmentConstrained"/>：受约束最优切分（DP 在墨水最少处下刀）；
/// 3) 比例法兜底（调用方实现）。
/// </summary>
public static class ProjectionWordSegmenter
{
    // 词间空白列的最小宽度：词间空格 ≈ 0.25 × 字号 ≈ 0.2 × 行框高，
    // 0.16 为首选阈值；对齐失败时按 0.12 → 0.08 逐级放宽重试（下限 GapMinPx），
    // 覆盖小字号/紧排版词间距小于首选阈值的场景。
    private static readonly float[] GapHeightFactors = { 0.16f, 0.12f, 0.08f };
    private const int GapMinPx = 2;

    // 列墨水数低于该阈值视为空列（抑制孤立噪点）：max(1, 行高 × 0.02)
    private const float InkNoiseFactor = 0.02f;

    // 段最小宽度（像素）
    private const int MinSegmentWidth = 2;

    // 置信度区分：投影精确 > 受约束切分（比例法兜底在调用方，置信度更低）
    private const float ProjectionConfidence = 0.9f;
    private const float ConstrainedConfidence = 0.8f;

    // 垂直收紧的上下 padding（像素）
    private const int VerticalPadding = 1;

    // DP 宽度偏离惩罚系数：cost = 切点墨水数 + 0.02×|段宽-期望段宽|。
    // 远小于 1，保证"干净切点（零墨水）永远优于切墨水列"，
    // 仅在同代价切点之间按接近期望宽度择优（处理粘连墨块等无空白可切的场景）。
    private const double WidthDeviationPenalty = 0.02;

    /// <summary>
    /// 精确对齐切分：投影段数与 token 数一致才成功。
    /// 段数偏少时逐级放宽 gap 阈值重试；段数偏多时按最小间隔合并多余段。
    /// </summary>
    public static bool TrySegment(
        Bitmap lineBitmap,
        string recognizedText,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        bool rotated180,
        int lineIndex,
        out IReadOnlyList<OcrWord> words)
    {
        return TrySegment(lineBitmap, recognizedText, localBox, frameRegion, rotated180, lineIndex, out words, out _);
    }

    /// <summary>带诊断信息的重载：detail 描述对齐方式（direct/retry=…/merge…）。</summary>
    public static bool TrySegment(
        Bitmap lineBitmap,
        string recognizedText,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        bool rotated180,
        int lineIndex,
        out IReadOnlyList<OcrWord> words,
        out string detail)
    {
        words = Array.Empty<OcrWord>();
        detail = string.Empty;

        if (!TryPrepare(lineBitmap, recognizedText, out var tokens, out var profile))
            return false;

        return TrySegmentFromProfile(profile, tokens, localBox, frameRegion, rotated180, lineIndex, out words, out detail);
    }

    /// <summary>
    /// 先精确对齐（TrySegment）、失败再受约束切分的组合入口，但墨水投影
    /// （位图全像素遍历，回退场景下的主要开销）只计算一次。
    /// detail 为 direct/retry=…/merge/constrained。
    /// </summary>
    public static bool TrySegmentOrConstrained(
        Bitmap lineBitmap,
        string recognizedText,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        bool rotated180,
        int lineIndex,
        out IReadOnlyList<OcrWord> words,
        out string detail)
    {
        words = Array.Empty<OcrWord>();
        detail = string.Empty;

        if (!TryPrepare(lineBitmap, recognizedText, out var tokens, out var profile))
            return false;

        if (TrySegmentFromProfile(profile, tokens, localBox, frameRegion, rotated180, lineIndex, out words, out detail))
            return true;

        if (TrySegmentConstrainedFromProfile(profile, tokens, localBox, frameRegion, rotated180, lineIndex, out words))
        {
            detail = "constrained";
            return true;
        }

        return false;
    }

    private static bool TrySegmentFromProfile(
        InkProfile profile,
        string[] tokens,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        bool rotated180,
        int lineIndex,
        out IReadOnlyList<OcrWord> words,
        out string detail)
    {
        words = Array.Empty<OcrWord>();
        detail = string.Empty;

        int n = tokens.Length;
        int noiseFloor = ComputeNoiseFloor(profile.Height);

        for (int level = 0; level < GapHeightFactors.Length; level++)
        {
            int gapThreshold = Math.Max(GapMinPx, (int)Math.Round(profile.Height * GapHeightFactors[level]));
            var segments = BuildSegmentsFromInk(profile.ColInk, noiseFloor, gapThreshold);
            if (segments.Count == 0)
                continue;

            string retryNote = level == 0 ? string.Empty : $"retry={GapHeightFactors[level]:0.00}";

            if (segments.Count == n)
            {
                words = BuildWordsFromSegments(segments, tokens, profile, rotated180, localBox, frameRegion, lineIndex, ProjectionConfidence);
                detail = string.IsNullOrEmpty(retryNote) ? "direct" : retryNote;
                return true;
            }

            if (segments.Count > n)
            {
                // 伪分裂（噪声/标点粘连）：按相邻段间隔从小到大合并，直到段数一致
                if (MergeSmallestGaps(segments, n))
                {
                    words = BuildWordsFromSegments(segments, tokens, profile, rotated180, localBox, frameRegion, lineIndex, ProjectionConfidence);
                    detail = string.IsNullOrEmpty(retryNote) ? "merge" : $"{retryNote}+merge";
                    return true;
                }
            }
            // segments.Count < n：放宽阈值继续重试；耗尽后交由受约束切分处理
        }

        return false;
    }

    /// <summary>
    /// 受约束最优切分：已知需要 N 个 token 段时，用动态规划在 [firstInk, lastInk]
    /// 内找 N-1 个切点，使切点处墨水总量最小（尽量从空白处下刀）。
    /// 用于粘连词/噪声导致投影段数与 token 数无法对齐、且合并修复失败的场景。
    /// </summary>
    public static bool TrySegmentConstrained(
        Bitmap lineBitmap,
        string recognizedText,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        bool rotated180,
        int lineIndex,
        out IReadOnlyList<OcrWord> words)
    {
        words = Array.Empty<OcrWord>();

        if (!TryPrepare(lineBitmap, recognizedText, out var tokens, out var profile))
            return false;

        return TrySegmentConstrainedFromProfile(profile, tokens, localBox, frameRegion, rotated180, lineIndex, out words);
    }

    private static bool TrySegmentConstrainedFromProfile(
        InkProfile profile,
        string[] tokens,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        bool rotated180,
        int lineIndex,
        out IReadOnlyList<OcrWord> words)
    {
        words = Array.Empty<OcrWord>();

        int n = tokens.Length;
        var colInk = profile.ColInk;
        int w = profile.Width;

        int firstInk = -1, lastInk = -1;
        int noiseFloor = ComputeNoiseFloor(profile.Height);
        for (int x = 0; x < w; x++)
        {
            if (colInk[x] >= noiseFloor)
            {
                if (firstInk < 0) firstInk = x;
                lastInk = x;
            }
        }
        if (firstInk < 0)
            return false;

        int span = lastInk - firstInk + 1;
        if (n == 1)
        {
            words = BuildWordsFromSegments(
                new List<(int, int)> { (firstInk, lastInk + 1) }, tokens, profile,
                rotated180, localBox, frameRegion, lineIndex, ConstrainedConfidence);
            return true;
        }
        if (span < n * MinSegmentWidth)
            return false;

        // 段宽窗口与期望宽度：按 token 字符数加权，约束 DP 搜索范围（复杂度 O(N × span × window)）
        int[] units = tokens.Select(t => Math.Max(1, t.Length)).ToArray();
        int totalUnits = units.Sum();
        double[] expWidths = units.Select(u => (double)span * u / totalUnits).ToArray();
        int maxSegWidth = Math.Max(MinSegmentWidth + 1, (int)Math.Ceiling((double)span / n * 3));
        int[] inkPrefix = BuildPrefix(colInk);

        double INF = double.MaxValue / 4;
        int cols = w + 1; // 列索引范围 [0, w]（段右端可取到 lastInk+1 == w）
        var dp = new double[(n + 1) * cols];
        var par = new int[(n + 1) * cols];
        Array.Fill(dp, INF);
        Array.Fill(par, -1);
        dp[firstInk] = 0;

        for (int j = 1; j <= n; j++)
        {
            int lo = firstInk + j * MinSegmentWidth;
            int hi = j < n ? lastInk - (n - j) * MinSegmentWidth + 1 : lastInk + 1;
            int maxW = Math.Min(maxSegWidth, Math.Max(MinSegmentWidth, (int)Math.Ceiling(expWidths[j - 1] * 4)));

            for (int i = lo; i <= hi; i++)
            {
                int kMin = Math.Max(j == 1 ? firstInk : firstInk + (j - 1) * MinSegmentWidth, i - maxW);
                int kMax = i - MinSegmentWidth;
                for (int k = kMin; k <= kMax; k++)
                {
                    double prev = dp[(j - 1) * cols + k];
                    if (prev >= INF) continue;
                    if (InkInRange(inkPrefix, k, i) == 0) continue; // 段内必须含墨水
                    double cost = prev
                        + (j == 1 ? 0 : colInk[k])
                        + WidthDeviationPenalty * Math.Abs(i - k - expWidths[j - 1]);
                    int idx = j * cols + i;
                    if (cost < dp[idx])
                    {
                        dp[idx] = cost;
                        par[idx] = k;
                    }
                }
            }
        }

        if (dp[n * cols + lastInk + 1] >= INF)
            return false;

        // 回溯切点，组装段
        var bounds = new List<(int StartX, int EndX)>(n);
        int end = lastInk + 1;
        for (int j = n; j >= 1; j--)
        {
            int k = par[j * cols + end];
            if (j > 1 && k < 0) return false;
            bounds.Add((j == 1 ? firstInk : k, end));
            end = k;
        }
        bounds.Reverse();

        words = BuildWordsFromSegments(bounds, tokens, profile, rotated180, localBox, frameRegion, lineIndex, ConstrainedConfidence);
        return true;
    }

    // ===== 公共准备 / 词框组装 =====

    private static bool TryPrepare(Bitmap lineBitmap, string recognizedText, out string[] tokens, out InkProfile profile)
    {
        tokens = Array.Empty<string>();
        profile = new InkProfile(Array.Empty<int>(), Array.Empty<int>(), 0, 0);

        if (lineBitmap is null || lineBitmap.Width <= 0 || lineBitmap.Height <= 0)
            return false;
        if (string.IsNullOrWhiteSpace(recognizedText))
            return false;

        tokens = recognizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        profile = ComputeInkProfile(lineBitmap);
        return profile.ColInk.Length > 0;
    }

    private static int ComputeNoiseFloor(int height)
    {
        return Math.Max(1, (int)Math.Round(height * InkNoiseFactor));
    }

    private static IReadOnlyList<OcrWord> BuildWordsFromSegments(
        IReadOnlyList<(int StartX, int EndX)> segments,
        string[] tokens,
        InkProfile profile,
        bool rotated180,
        PhysicalRect localBox,
        PhysicalRect frameRegion,
        int lineIndex,
        float confidence)
    {
        int lineY = frameRegion.Y + localBox.Y;
        int bmpW = profile.Width;
        var (tightTop, tightBottom) = ComputeVerticalTighten(profile);

        var result = new List<OcrWord>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            var (startX, endX) = segments[i];
            var token = tokens[i];

            if (IsPureCjk(token) && token.Length > 1)
            {
                // 纯 CJK token（无空格分隔的连续中文）：CJK 字形等宽，
                // 把段按字符数均分，逐字生成词框，让框精准贴合光标所指的单字。
                int charCount = token.Length;
                int span = endX - startX;
                for (int ci = 0; ci < charCount; ci++)
                {
                    int cx1 = startX + (int)Math.Round((double)span * ci / charCount);
                    int cx2 = startX + (int)Math.Round((double)span * (ci + 1) / charCount);
                    if (cx2 <= cx1) cx2 = cx1 + 1;
                    result.Add(BuildWord(cx1, cx2, token[ci].ToString(), rotated180, bmpW,
                        localBox, frameRegion, lineY, tightTop, tightBottom, confidence, lineIndex));
                }
            }
            else
            {
                // 混排 token（如 "MinSegmentWidth;"、"增TrySegmentConstrained)"）：
                // 按脚本/标点边界拆成独立 run，切点先用宽度权重估算，
                // 再在估算点附近找墨水最少列微调，让选取框只覆盖光标所指的 run。
                var runs = TextRunSplitter.Split(token);
                if (runs.Count <= 1)
                {
                    result.Add(BuildWord(startX, endX, token, rotated180, bmpW,
                        localBox, frameRegion, lineY, tightTop, tightBottom, confidence, lineIndex));
                    continue;
                }
                {
                    // run 拆分失败时降级为整段返回，绝不让词框解析异常击穿整个 OCR 管线
                    IReadOnlyList<(int Start, int End)> bounds;
                    try
                    {
                        bounds = SplitSegmentByRuns(token, runs, startX, endX, profile.ColInk);
                    }
                    catch
                    {
                        bounds = new[] { (startX, endX) };
                        runs = new[] { new TextRunSplitter.TextRun(0, token.Length, TextRunSplitter.CharClass.Word) };
                    }
                    for (int ri = 0; ri < runs.Count; ri++)
                    {
                        var (rx1, rx2) = bounds[ri];
                        result.Add(BuildWord(rx1, rx2, runs[ri].Slice(token), rotated180, bmpW,
                            localBox, frameRegion, lineY, tightTop, tightBottom, confidence, lineIndex));
                    }
                }
            }
        }
        return result;
    }

    private static OcrWord BuildWord(
        int startX, int endX, string text, bool rotated180, int bmpW,
        PhysicalRect localBox, PhysicalRect frameRegion, int lineY,
        int tightTop, int tightBottom, float confidence, int lineIndex)
    {
        // 旋转 180° 时把矫正图的 X 翻回原 det 框坐标
        if (rotated180)
        {
            int t = startX;
            startX = bmpW - endX;
            endX = bmpW - t;
        }

        int screenX1 = frameRegion.X + localBox.X + startX;
        int screenX2 = frameRegion.X + localBox.X + endX;
        int wBox = Math.Max(1, screenX2 - screenX1);
        // 垂直收紧：框高贴合墨水范围（上下各留 VerticalPadding），避免框压邻行
        var box = new PhysicalRect(screenX1, lineY + tightTop, wBox, tightBottom - tightTop);
        return new OcrWord(box, text, confidence, lineIndex);
    }

    /// <summary>
    /// 按 run 权重估算切点并在附近找墨水最少列微调，返回每个 run 的 [startX, endX)。
    /// 估算边界先强制严格单调且每个 run 至少 1px，避免窄段/权重偏差导致非法区间。
    /// </summary>
    private static (int Start, int End)[] SplitSegmentByRuns(
        string token, IReadOnlyList<TextRunSplitter.TextRun> runs,
        int startX, int endX, int[] colInk)
    {
        var weights = new double[runs.Count];
        double total = 0;
        for (int ri = 0; ri < runs.Count; ri++)
        {
            double w = 0;
            var r = runs[ri];
            for (int ci = r.Start; ci < r.Start + r.Length; ci++)
                w += TextRunSplitter.CharWeight(token[ci]);
            weights[ri] = Math.Max(0.5, w);
            total += weights[ri];
        }

        int span = endX - startX;
        var bounds = new (int, int)[runs.Count];
        if (span < runs.Count)
        {
            // 段宽不足以给每个 run 分 1px：按均分退化，不追求精确
            for (int ri = 0; ri < runs.Count; ri++)
            {
                int rx1 = startX + span * ri / runs.Count;
                int rx2 = startX + span * (ri + 1) / runs.Count;
                bounds[ri] = (rx1, Math.Max(rx1 + 1, rx2));
            }
            return bounds;
        }

        // 1) 按累计权重估算 run 边界，强制严格单调递增且后续 run 各留至少 1px
        int cutCount = runs.Count - 1;
        var cuts = new int[cutCount];
        double acc = 0;
        for (int i = 0; i < cutCount; i++)
        {
            acc += weights[i];
            int est = startX + (int)Math.Round(span * acc / total);
            int minCut = (i == 0 ? startX : cuts[i - 1]) + 1;
            int maxCut = endX - (cutCount - i);
            cuts[i] = Math.Clamp(est, minCut, Math.Max(minCut, maxCut));
        }

        // 2) 每个切点在相邻切点围成的单元格内、估算点 ±window 范围找墨水最少列
        int window = Math.Max(2, (int)Math.Round(span * 0.2));
        for (int i = 0; i < cutCount; i++)
        {
            int lo = (i == 0 ? startX : cuts[i - 1]) + 1;
            int hi = (i == cutCount - 1 ? endX : cuts[i + 1]) - 1;
            cuts[i] = RefineCut(colInk, cuts[i], lo, hi, window);
        }

        int cur = startX;
        for (int ri = 0; ri < runs.Count; ri++)
        {
            int rx2 = ri < cutCount ? cuts[ri] : endX;
            bounds[ri] = (cur, Math.Max(cur + 1, rx2));
            cur = rx2;
        }
        return bounds;
    }

    /// <summary>在 [lo,hi] 内、估算点 ±window 范围找墨水最少列作为切点（平局取更接近估算点者）。</summary>
    private static int RefineCut(int[] colInk, int estimate, int lo, int hi, int window)
    {
        if (lo > hi) return lo;
        estimate = Math.Clamp(estimate, lo, hi);
        int wLo = Math.Max(lo, estimate - window);
        int wHi = Math.Min(hi, estimate + window);

        int best = estimate;
        int bestInk = int.MaxValue;
        int bestDist = int.MaxValue;
        for (int x = wLo; x <= wHi; x++)
        {
            int ink = x >= 0 && x < colInk.Length ? colInk[x] : 0;
            int dist = Math.Abs(x - estimate);
            if (ink < bestInk || (ink == bestInk && dist < bestDist))
            {
                best = x;
                bestInk = ink;
                bestDist = dist;
            }
        }
        return best;
    }

    private static bool IsPureCjk(string token)
    {
        foreach (var c in token)
        {
            bool cjk = c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF ||
                       c >= 0x3040 && c <= 0x30FF || c >= 0xAC00 && c <= 0xD7AF;
            if (!cjk) return false;
        }
        return token.Length > 0;
    }

    // ===== 段级修复 =====

    private static bool MergeSmallestGaps(List<(int StartX, int EndX)> segments, int targetCount)
    {
        while (segments.Count > targetCount)
        {
            int bestIdx = -1;
            int bestGap = int.MaxValue;
            for (int i = 0; i < segments.Count - 1; i++)
            {
                int gap = segments[i + 1].StartX - segments[i].EndX;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0) return false;
            segments[bestIdx] = (segments[bestIdx].StartX, segments[bestIdx + 1].EndX);
            segments.RemoveAt(bestIdx + 1);
        }
        return true;
    }

    // ===== 墨水投影分析 =====

    private sealed class InkProfile
    {
        public int[] ColInk { get; }
        public int[] RowInk { get; }
        public int Width { get; }
        public int Height { get; }

        public InkProfile(int[] colInk, int[] rowInk, int width, int height)
        {
            ColInk = colInk;
            RowInk = rowInk;
            Width = width;
            Height = height;
        }
    }

    /// <summary>二值化 + 列/行投影统计，返回墨水分布（位图局部坐标）。</summary>
    private static InkProfile ComputeInkProfile(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return AnalyzePixels(bytes, data.Stride, w, h);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static InkProfile AnalyzePixels(byte[] bytes, int stride, int w, int h)
    {
        // 单遍统计：灰度和、暗像素数、每像素灰度值（第二遍按阈值计数）
        long graySum = 0;
        int totalPixels = w * h;
        int darkCount = 0;
        var grays = new byte[totalPixels];

        for (int y = 0; y < h; y++)
        {
            int rowBase = y * stride;
            for (int x = 0; x < w; x++)
            {
                byte b = bytes[rowBase + x * 4];
                byte g = bytes[rowBase + x * 4 + 1];
                byte r = bytes[rowBase + x * 4 + 2];
                int gray = (r * 299 + g * 587 + b * 114) / 1000;
                grays[y * w + x] = (byte)gray;
                graySum += gray;
                if (gray < 128) darkCount++;
            }
        }

        // 极性检测：暗像素过半 → 暗色主题（暗底亮字），反转前景定义
        bool darkBackground = darkCount * 2 > totalPixels;
        double meanGray = (double)graySum / totalPixels;
        // 阈值 = 均值 × 0.6：文字是少数高对比像素，背景主导均值
        int threshold = Math.Clamp((int)Math.Round(meanGray * 0.6), 8, 247);

        var colInk = new int[w];
        var rowInk = new int[h];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int gray = grays[rowBase + x];
                bool isInk = darkBackground ? gray >= 256 - threshold : gray <= threshold;
                if (isInk)
                {
                    colInk[x]++;
                    rowInk[y]++;
                }
            }
        }

        return new InkProfile(colInk, rowInk, w, h);
    }

    /// <summary>垂直收紧：返回行内墨水范围 [top, bottom)，无墨水时退化为整行。</summary>
    private static (int Top, int Bottom) ComputeVerticalTighten(InkProfile profile)
    {
        int noiseFloor = ComputeNoiseFloor(profile.Height);
        int top = -1, bottom = -1;
        for (int y = 0; y < profile.Height; y++)
        {
            if (profile.RowInk[y] >= noiseFloor)
            {
                if (top < 0) top = y;
                bottom = y;
            }
        }
        if (top < 0) return (0, profile.Height);

        top = Math.Max(0, top - VerticalPadding);
        bottom = Math.Min(profile.Height, bottom + 1 + VerticalPadding);
        return (top, bottom);
    }

    private static List<(int StartX, int EndX)> BuildSegmentsFromInk(int[] colInk, int noiseFloor, int gapThreshold)
    {
        bool HasInk(int x) => colInk[x] >= noiseFloor;

        var segments = new List<(int, int)>();
        int segStart = -1;
        int runEmpty = 0;

        for (int x = 0; x < colInk.Length; x++)
        {
            if (HasInk(x))
            {
                if (segStart < 0) segStart = x;
                runEmpty = 0;
            }
            else if (segStart >= 0)
            {
                runEmpty++;
                // 连续空白达到词间距阈值 → 结束当前段
                if (runEmpty >= gapThreshold)
                {
                    int endX = x - runEmpty + 1;
                    if (endX - segStart >= MinSegmentWidth)
                        segments.Add((segStart, endX));
                    segStart = -1;
                    runEmpty = 0;
                }
            }
        }

        if (segStart >= 0)
        {
            int endX = colInk.Length - runEmpty;
            if (endX - segStart >= MinSegmentWidth)
                segments.Add((segStart, endX));
        }

        return segments;
    }

    private static int[] BuildPrefix(int[] colInk)
    {
        var prefix = new int[colInk.Length + 1];
        for (int x = 0; x < colInk.Length; x++)
            prefix[x + 1] = prefix[x] + (colInk[x] > 0 ? 1 : 0);
        return prefix;
    }

    private static int InkInRange(int[] prefix, int start, int end)
    {
        return prefix[end] - prefix[start];
    }
}
