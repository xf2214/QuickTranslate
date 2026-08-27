using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.Selection;

public class SelectedTextProbePolicyTests
{
    // ===== IsSpatiallyRelevant（防陈旧选区劫持：选区必须在光标附近才可采纳） =====

    [Fact]
    public void Spatial_WordMode_CursorInsideRect_True()
    {
        var rects = new[] { new PhysicalRect(100, 100, 200, 40) };
        Assert.True(SelectedTextProbePolicy.IsSpatiallyRelevant(
            rects, rects[0], new PhysicalPoint(150, 120), wordMode: true));
    }

    [Fact]
    public void Spatial_WordMode_CursorWithinPad_EdgeTolerance_True()
    {
        var rects = new[] { new PhysicalRect(100, 100, 200, 40) };
        // 左缘 100 - pad 6 = 94，光标 x=95 在容差带内
        Assert.True(SelectedTextProbePolicy.IsSpatiallyRelevant(
            rects, rects[0], new PhysicalPoint(95, 120), wordMode: true));
    }

    [Fact]
    public void Spatial_WordMode_FarSelection_HijackGuard_False()
    {
        // 核心回归场景：文档里残留的旧选区远离光标 → 必须拒绝（否则劫持词查找）
        var rects = new[] { new PhysicalRect(3000, 1800, 200, 40) };
        Assert.False(SelectedTextProbePolicy.IsSpatiallyRelevant(
            rects, rects[0], new PhysicalPoint(100, 100), wordMode: true));
    }

    [Fact]
    public void Spatial_BlockMode_UnionNearCursor_WithinUnionPad_True()
    {
        var rects = new[] { new PhysicalRect(3000, 1800, 200, 40) };
        var union = new PhysicalRect(3000, 1800, 200, 40);
        // 并集右缘 3200 + pad 32 = 3232；光标 3220 在并集容差带内
        Assert.True(SelectedTextProbePolicy.IsSpatiallyRelevant(
            rects, union, new PhysicalPoint(3220, 1820), wordMode: false));
    }

    [Fact]
    public void Spatial_BlockMode_AllFar_False()
    {
        var rects = new[] { new PhysicalRect(3000, 1800, 200, 40) };
        Assert.False(SelectedTextProbePolicy.IsSpatiallyRelevant(
            rects, rects[0], new PhysicalPoint(100, 100), wordMode: false));
    }

    [Fact]
    public void Spatial_EmptyRects_False()
    {
        Assert.False(SelectedTextProbePolicy.IsSpatiallyRelevant(
            Array.Empty<PhysicalRect>(), new PhysicalRect(0, 0, 0, 0),
            new PhysicalPoint(100, 100), wordMode: true));
    }

    // ===== IsAdoptable =====

    [Fact]
    public void IsAdoptable_Null_ReturnsFalse()
    {
        Assert.False(SelectedTextProbePolicy.IsAdoptable(null, SelectedTextProbePolicy.WordModeMaxChars));
    }

    [Fact]
    public void IsAdoptable_Empty_ReturnsFalse()
    {
        Assert.False(SelectedTextProbePolicy.IsAdoptable("", SelectedTextProbePolicy.WordModeMaxChars));
    }

    [Fact]
    public void IsAdoptable_Whitespace_ReturnsFalse()
    {
        Assert.False(SelectedTextProbePolicy.IsAdoptable("   \t\n", SelectedTextProbePolicy.WordModeMaxChars));
    }

    [Fact]
    public void IsAdoptable_NormalText_ReturnsTrue()
    {
        Assert.True(SelectedTextProbePolicy.IsAdoptable("hello", SelectedTextProbePolicy.WordModeMaxChars));
    }

    [Fact]
    public void IsAdoptable_ExceedsMax_ReturnsFalse()
    {
        string text = new string('a', SelectedTextProbePolicy.WordModeMaxChars + 1);
        Assert.False(SelectedTextProbePolicy.IsAdoptable(text, SelectedTextProbePolicy.WordModeMaxChars));
    }

    [Fact]
    public void IsAdoptable_ExactlyAtMax_ReturnsTrue()
    {
        string text = new string('a', SelectedTextProbePolicy.WordModeMaxChars);
        Assert.True(SelectedTextProbePolicy.IsAdoptable(text, SelectedTextProbePolicy.WordModeMaxChars));
    }

    [Fact]
    public void IsAdoptable_BlockMode_Boundary()
    {
        string atLimit = new string('a', SelectedTextProbePolicy.BlockModeMaxChars);
        string overLimit = new string('a', SelectedTextProbePolicy.BlockModeMaxChars + 1);
        Assert.True(SelectedTextProbePolicy.IsAdoptable(atLimit, SelectedTextProbePolicy.BlockModeMaxChars));
        Assert.False(SelectedTextProbePolicy.IsAdoptable(overLimit, SelectedTextProbePolicy.BlockModeMaxChars));
    }

    // ===== Normalize =====

    [Fact]
    public void Normalize_Crlf_To_Lf()
    {
        Assert.Equal("a\nb", SelectedTextProbePolicy.Normalize("a\r\nb"));
    }

    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("hello", SelectedTextProbePolicy.Normalize("  hello  "));
        Assert.Equal("hello", SelectedTextProbePolicy.Normalize("\n hello \n"));
        Assert.Equal("hello", SelectedTextProbePolicy.Normalize("\r\n hello \r\n"));
    }

    [Fact]
    public void Normalize_FourConsecutiveNewlines_FoldsToTwo()
    {
        Assert.Equal("a\n\nb", SelectedTextProbePolicy.Normalize("a\n\n\n\nb"));
    }

    [Fact]
    public void Normalize_ThreeConsecutiveNewlines_FoldsToTwo()
    {
        Assert.Equal("a\n\nb", SelectedTextProbePolicy.Normalize("a\n\n\nb"));
    }

    [Fact]
    public void Normalize_TwoConsecutiveNewlines_Preserved()
    {
        Assert.Equal("a\n\nb", SelectedTextProbePolicy.Normalize("a\n\nb"));
    }

    [Fact]
    public void Normalize_SingleNewline_Preserved()
    {
        Assert.Equal("a\nb", SelectedTextProbePolicy.Normalize("a\nb"));
    }

    [Fact]
    public void Normalize_NormalText_Unchanged()
    {
        Assert.Equal("hello world", SelectedTextProbePolicy.Normalize("hello world"));
    }

    [Fact]
    public void Normalize_MixedCrlfAndFolding()
    {
        // CRLF 统一后 + 首尾去空 + 折叠
        Assert.Equal("a\n\nb", SelectedTextProbePolicy.Normalize("  a\r\n\r\n\r\n\r\nb  "));
    }

    [Fact]
    public void Normalize_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SelectedTextProbePolicy.Normalize(string.Empty));
    }

    // ===== UnionOrFallback =====

    [Fact]
    public void UnionOrFallback_MultipleRects_CorrectUnion()
    {
        var rects = new[]
        {
            new PhysicalRect(0, 0, 100, 50),
            new PhysicalRect(80, 40, 100, 50),
            new PhysicalRect(10, 60, 20, 20),
        };

        var union = SelectedTextProbePolicy.UnionOrFallback(rects, new PhysicalPoint(0, 0));

        // minX=0, minY=0, maxR=180, maxB=90 => 180x90
        Assert.Equal(new PhysicalRect(0, 0, 180, 90), union);
    }

    [Fact]
    public void UnionOrFallback_Empty_ReturnsFallbackCenteredAtCursor()
    {
        var cursor = new PhysicalPoint(100, 200);
        var result = SelectedTextProbePolicy.UnionOrFallback(Array.Empty<PhysicalRect>(), cursor);

        int expectedX = 100 - SelectedTextProbePolicy.FallbackBoxWidth / 2;
        int expectedY = 200 - SelectedTextProbePolicy.FallbackBoxHeight / 2;
        Assert.Equal(new PhysicalRect(expectedX, expectedY, SelectedTextProbePolicy.FallbackBoxWidth, SelectedTextProbePolicy.FallbackBoxHeight), result);
    }

    [Fact]
    public void UnionOrFallback_Empty_FallbackExactValues()
    {
        // 断言精确值：cursor (0,0) => (-60, -20, 120, 40)
        var result = SelectedTextProbePolicy.UnionOrFallback(Array.Empty<PhysicalRect>(), new PhysicalPoint(0, 0));
        Assert.Equal(new PhysicalRect(-60, -20, 120, 40), result);
    }

    [Fact]
    public void UnionOrFallback_SingleRect_ReturnsSame()
    {
        var rect = new PhysicalRect(10, 20, 30, 40);
        var result = SelectedTextProbePolicy.UnionOrFallback(new[] { rect }, new PhysicalPoint(999, 999));
        Assert.Equal(rect, result);
    }

    [Fact]
    public void UnionOrFallback_TwoNonOverlapping_CorrectUnion()
    {
        var rects = new[]
        {
            new PhysicalRect(0, 0, 10, 10),
            new PhysicalRect(20, 20, 10, 10),
        };
        var result = SelectedTextProbePolicy.UnionOrFallback(rects, new PhysicalPoint(0, 0));
        Assert.Equal(new PhysicalRect(0, 0, 30, 30), result);
    }

    // 阈值常量校验（防止魔数漂移）
    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(150, SelectedTextProbePolicy.ProbeBudgetMs);
        Assert.Equal(240, SelectedTextProbePolicy.WordModeMaxChars);
        Assert.Equal(6000, SelectedTextProbePolicy.BlockModeMaxChars);
        Assert.Equal(120, SelectedTextProbePolicy.FallbackBoxWidth);
        Assert.Equal(40, SelectedTextProbePolicy.FallbackBoxHeight);
    }
}
