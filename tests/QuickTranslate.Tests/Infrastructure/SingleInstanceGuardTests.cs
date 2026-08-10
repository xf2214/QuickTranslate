using QuickTranslate.Infrastructure.SingleInstance;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class SingleInstanceGuardTests
{
    [Fact]
    public void TryEnsureSingle_FirstInstance_ReturnsTrue()
    {
        var uniqueName = $"QuickTranslate-Test-{Guid.NewGuid():N}";
        using var guard1 = new SingleInstanceGuard(uniqueName);

        Assert.True(guard1.TryEnsureSingle());
        Assert.True(guard1.IsPrimaryInstance);
    }

    [Fact]
    public void TryEnsureSingle_SecondInstance_SameMutex_ReturnsFalse()
    {
        var uniqueName = $"QuickTranslate-Test-{Guid.NewGuid():N}";
        using var guard1 = new SingleInstanceGuard(uniqueName);
        using var guard2 = new SingleInstanceGuard(uniqueName);

        Assert.True(guard1.TryEnsureSingle());
        Assert.False(guard2.TryEnsureSingle());
        Assert.False(guard2.IsPrimaryInstance);
    }

    [Fact]
    public void TryEnsureSingle_DifferentMutexNames_BothReturnTrue()
    {
        var name1 = $"QuickTranslate-Test-A-{Guid.NewGuid():N}";
        var name2 = $"QuickTranslate-Test-B-{Guid.NewGuid():N}";

        using var guard1 = new SingleInstanceGuard(name1);
        using var guard2 = new SingleInstanceGuard(name2);

        Assert.True(guard1.TryEnsureSingle());
        Assert.True(guard2.TryEnsureSingle());
    }

    [Fact]
    public void Dispose_FirstInstanceReleased_SecondCanAcquire()
    {
        var uniqueName = $"QuickTranslate-Test-{Guid.NewGuid():N}";

        using (var guard1 = new SingleInstanceGuard(uniqueName))
        {
            Assert.True(guard1.TryEnsureSingle());
        }

        using var guard2 = new SingleInstanceGuard(uniqueName);
        Assert.True(guard2.TryEnsureSingle());
    }
}
