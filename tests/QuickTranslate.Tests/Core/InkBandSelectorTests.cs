using QuickTranslate.Core.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Core;

/// <summary>
/// 主墨水带选择（InkBandSelector）：
/// 防止 det 框内"又矮又密"的邻行渗漏带按墨水总量劫持主行选择
/// （日志实测：38px 行框内 6px 致密 CJK 碎片曾把整行词框压成 9px 条带）。
/// </summary>
public class InkBandSelectorTests
{
    private static int[] Profile(int height, params (int Start, int End, int Value)[] bands)
    {
        var rowInk = new int[height];
        foreach (var (start, end, value) in bands)
            for (int y = start; y <= end; y++)
                rowInk[y] = value;
        return rowInk;
    }

    [Fact]
    public void ShortDenseLeakBand_Excluded_TallSparseMainWins()
    {
        // 日志复现：本行拉丁文字带高 20 行（每行墨水 20，稀疏），下方渗漏的
        // 致密 CJK 碎片带高 6 行（每行墨水 200）。总墨水 6×200=1200 > 20×20=400，
        // 旧的纯墨水总量策略会选中渗漏带；新规则按高度过滤后应选主行。
        var rowInk = Profile(38,
            (10, 29, 20),   // 主行：高 20，墨水和 400
            (32, 37, 200)); // 渗漏：高 6，墨水和 1200

        var band = InkBandSelector.SelectDominant(rowInk, noiseFloor: 1);

        Assert.NotNull(band);
        Assert.Equal(10, band!.Value.Top);
        Assert.Equal(30, band.Value.Bottom); // [10,30) = 20 行
    }

    [Fact]
    public void SingleBand_ReturnedAsIs()
    {
        var rowInk = Profile(40, (8, 31, 50));

        var band = InkBandSelector.SelectDominant(rowInk, noiseFloor: 1);

        Assert.NotNull(band);
        Assert.Equal(8, band!.Value.Top);
        Assert.Equal(32, band.Value.Bottom);
    }

    [Fact]
    public void NoInk_ReturnsNull()
    {
        var rowInk = new int[30];

        Assert.Null(InkBandSelector.SelectDominant(rowInk, noiseFloor: 1));
        Assert.Null(InkBandSelector.SelectDominant(Array.Empty<int>(), noiseFloor: 1));
    }

    [Fact]
    public void OneRowBelowNoiseFloor_BridgedIntoSameBand()
    {
        // 字形抗锯齿/细笔画行可能低于噪声阈值：单行缺口应桥接而非拆成两带
        var rowInk = Profile(30, (5, 12, 30), (14, 25, 30)); // 第 13 行为 0

        var band = InkBandSelector.SelectDominant(rowInk, noiseFloor: 1);

        Assert.NotNull(band);
        Assert.Equal(5, band!.Value.Top);
        Assert.Equal(26, band.Value.Bottom);
    }

    [Fact]
    public void TwoEqualHeightBands_PicksMoreInk()
    {
        // 两带都被高度过滤保留（高度相同）时，取墨水总量大者
        var rowInk = Profile(60,
            (5, 24, 30),   // 高 20，和 600
            (30, 49, 50)); // 高 20，和 1000

        var band = InkBandSelector.SelectDominant(rowInk, noiseFloor: 1);

        Assert.NotNull(band);
        Assert.Equal(30, band!.Value.Top);
        Assert.Equal(50, band.Value.Bottom);
    }

    [Fact]
    public void AllBandsBelowMinHeight_FallsBackToMaxInk()
    {
        // 极端场景：所有带都只有 1-2 行高（低于最小高度下限 3），
        // 退回全局墨水最大者（行为不劣于旧的纯墨水总量策略）
        var rowInk = Profile(10,
            (2, 3, 30),   // 高 2，和 60
            (6, 7, 100)); // 高 2，和 200

        var band = InkBandSelector.SelectDominant(rowInk, noiseFloor: 1);

        Assert.NotNull(band);
        Assert.Equal(6, band!.Value.Top);
        Assert.Equal(8, band.Value.Bottom);
    }
}
