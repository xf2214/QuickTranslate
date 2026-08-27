namespace QuickTranslate.Core.Ocr;

/// <summary>
/// 行内主墨水带选择：从行墨水剖面（每行墨水像素数）中选出"本行文字"所在的连续墨水带。
///
/// 背景：det 框高度归一化会把矮行框垂直撑大，紧凑行距时邻行墨水会渗入裁剪图。
/// 渗漏带通常又矮又密（如致密 CJK 行渗入稀疏拉丁行的裁剪图），若按"墨水总量最大"
/// 择主会被渗漏带劫持——词框被压进错误条带、rec 读到"一行半"输出乱码
/// （日志实测：38px 行框内 6px 致密碎片劫持收紧，整行词框压成 9px 高）。
/// 因此先按高度过滤（合格带高 ≥ 最高带 × <see cref="HeightRatioFloor"/>），
/// 再在合格带中取墨水总量最大者。
/// </summary>
public static class InkBandSelector
{
    /// <summary>
    /// 合格带的最小高度占比（相对最高带）：渗漏碎片带的高度通常不足本行的 40%；
    /// 正常同一视觉行不会出现干净的 ≥2 空行分隔，因此不会被此规则误拆。
    /// </summary>
    public const float HeightRatioFloor = 0.4f;

    /// <summary>带内允许桥接的连续亚阈值行数：字形抗锯齿/细笔画行可能低于噪声阈值，
    /// 但不会连续 2 行；邻行渗漏与本行之间通常隔 ≥2 空行。</summary>
    public const int MaxBridgeRows = 1;

    /// <summary>默认噪声阈值：max(1, 高度 × 2%)，抑制孤立噪点行。</summary>
    public static int DefaultNoiseFloor(int height)
    {
        return Math.Max(1, (int)Math.Round(height * 0.02));
    }

    /// <summary>
    /// 选择主导墨水带，返回 [Top, Bottom)；无墨水返回 null。
    /// 带定义：RowInk ≥ <paramref name="noiseFloor"/> 的连续行，
    /// 中间允许桥接 ≤ <see cref="MaxBridgeRows"/> 行亚阈值行。
    /// 存在合格带时在合格带中取墨水总量最大者（平局取更高者）；
    /// 所有带都不合格时退回全局墨水总量最大者（行为不劣于旧的纯墨水策略）。
    /// </summary>
    public static (int Top, int Bottom)? SelectDominant(int[] rowInk, int noiseFloor)
    {
        if (rowInk is null || rowInk.Length == 0)
            return null;

        // ===== 收集所有候选带（允许 ≤1 行亚阈值桥接）=====
        var starts = new List<int>(8);
        var ends = new List<int>(8);
        var inks = new List<long>(8);
        int curStart = -1, curEnd = -1, emptyRun = 0;
        long curInk = 0;

        void FlushBand()
        {
            if (curStart >= 0)
            {
                starts.Add(curStart);
                ends.Add(curEnd);
                inks.Add(curInk);
            }
            curStart = -1;
            curInk = 0;
        }

        for (int y = 0; y < rowInk.Length; y++)
        {
            if (rowInk[y] >= noiseFloor)
            {
                if (curStart < 0) curStart = y;
                curEnd = y;
                curInk += rowInk[y];
                emptyRun = 0;
            }
            else if (curStart >= 0)
            {
                emptyRun++;
                if (emptyRun > MaxBridgeRows)
                    FlushBand();
            }
        }
        FlushBand();

        if (starts.Count == 0)
            return null;

        // ===== 高度过滤 + 墨水总量择主 =====
        int maxHeight = 0;
        for (int i = 0; i < starts.Count; i++)
            maxHeight = Math.Max(maxHeight, ends[i] - starts[i] + 1);

        int minHeight = Math.Max(3, (int)Math.Ceiling(maxHeight * HeightRatioFloor));

        bool anyEligible = false;
        for (int i = 0; i < starts.Count; i++)
        {
            if (ends[i] - starts[i] + 1 >= minHeight)
            {
                anyEligible = true;
                break;
            }
        }

        int best = -1;
        long bestInk = -1;
        int bestHeight = -1;
        for (int i = 0; i < starts.Count; i++)
        {
            int h = ends[i] - starts[i] + 1;
            if (anyEligible && h < minHeight)
                continue;

            if (inks[i] > bestInk || (inks[i] == bestInk && h > bestHeight))
            {
                best = i;
                bestInk = inks[i];
                bestHeight = h;
            }
        }

        return (starts[best], ends[best] + 1);
    }
}
