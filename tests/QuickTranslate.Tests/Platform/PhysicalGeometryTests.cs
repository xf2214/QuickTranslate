using QuickTranslate.Core.Geometry;
using Xunit;

namespace QuickTranslate.Tests.Platform;

public class PhysicalGeometryTests
{
    [Fact]
    public void PhysicalRect_Contains_PointInside_ReturnsTrue()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new PhysicalPoint(50, 50);
        Assert.True(rect.Contains(point));
    }

    [Fact]
    public void PhysicalRect_Contains_PointOnLeftEdge_ReturnsTrue()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new PhysicalPoint(0, 50);
        Assert.True(rect.Contains(point));
    }

    [Fact]
    public void PhysicalRect_Contains_PointOnTopEdge_ReturnsTrue()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new PhysicalPoint(50, 0);
        Assert.True(rect.Contains(point));
    }

    [Fact]
    public void PhysicalRect_Contains_PointOnRightEdge_ReturnsFalse()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new PhysicalPoint(100, 50);
        Assert.False(rect.Contains(point));
    }

    [Fact]
    public void PhysicalRect_Contains_PointOnBottomEdge_ReturnsFalse()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new PhysicalPoint(50, 100);
        Assert.False(rect.Contains(point));
    }

    [Fact]
    public void PhysicalRect_Contains_PointOutside_ReturnsFalse()
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var point = new PhysicalPoint(150, 150);
        Assert.False(rect.Contains(point));
    }

    [Fact]
    public void PhysicalRect_Intersects_Overlapping_ReturnsTrue()
    {
        var a = new PhysicalRect(0, 0, 100, 100);
        var b = new PhysicalRect(50, 50, 100, 100);
        Assert.True(a.Intersects(b));
        Assert.True(b.Intersects(a));
    }

    [Fact]
    public void PhysicalRect_Intersects_TouchingLeft_ReturnsFalse()
    {
        var a = new PhysicalRect(0, 0, 100, 100);
        var b = new PhysicalRect(100, 0, 100, 100);
        Assert.False(a.Intersects(b));
        Assert.False(b.Intersects(a));
    }

    [Fact]
    public void PhysicalRect_Intersects_Separate_ReturnsFalse()
    {
        var a = new PhysicalRect(0, 0, 100, 100);
        var b = new PhysicalRect(200, 200, 100, 100);
        Assert.False(a.Intersects(b));
        Assert.False(b.Intersects(a));
    }

    [Fact]
    public void PhysicalRect_LeftTopRightBottom_ComputedCorrectly()
    {
        var rect = new PhysicalRect(10, 20, 30, 40);
        Assert.Equal(10, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
    }

    [Fact]
    public void PhysicalRect_Equality_SameValues_Equal()
    {
        var a = new PhysicalRect(1, 2, 3, 4);
        var b = new PhysicalRect(1, 2, 3, 4);
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void PhysicalRect_Equality_DifferentValues_NotEqual()
    {
        var a = new PhysicalRect(1, 2, 3, 4);
        var b = new PhysicalRect(1, 2, 3, 5);
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void PhysicalRect_ToString_FormatMatches()
    {
        var rect = new PhysicalRect(1, 2, 3, 4);
        Assert.Equal("X=1, Y=2, W=3, H=4", rect.ToString());
    }

    [Fact]
    public void PhysicalPoint_Addition_Works()
    {
        var a = new PhysicalPoint(10, 20);
        var b = new PhysicalPoint(5, 6);
        Assert.Equal(new PhysicalPoint(15, 26), a + b);
    }

    [Fact]
    public void PhysicalPoint_Subtraction_Works()
    {
        var a = new PhysicalPoint(10, 20);
        var b = new PhysicalPoint(5, 6);
        Assert.Equal(new PhysicalPoint(5, 14), a - b);
    }

    [Fact]
    public void PhysicalPoint_ToString_FormatMatches()
    {
        var pt = new PhysicalPoint(10, 20);
        Assert.Equal("(10, 20)", pt.ToString());
    }
}
