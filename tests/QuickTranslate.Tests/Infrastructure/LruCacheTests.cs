using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Cache;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class LruCacheTests
{
    private static TranslationResult MakeResult(string text) => new(
        NormalizedKey: text,
        SourceText: text,
        TargetText: "result-" + text,
        TargetLanguage: "zh-CN");

    [Fact]
    public void Case1_BasicAddAndCount()
    {
        var cache = new MemoryLruTranslationCache(capacity: 2);
        cache.Add("a", MakeResult("A"));
        cache.Add("b", MakeResult("B"));

        Assert.Equal(2, cache.Count);
        Assert.Equal(2, cache.Capacity);
    }

    [Fact]
    public void Case2_GetPromotesToMru_ThenLruEvicted()
    {
        var cache = new MemoryLruTranslationCache(capacity: 2);
        cache.Add("a", MakeResult("A"));
        cache.Add("b", MakeResult("B"));

        Assert.True(cache.TryGet("a", out var gotA));
        Assert.Equal("result-A", gotA.TargetText);

        cache.Add("c", MakeResult("C"));

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("b", out _), "b should be evicted (LRU after a was promoted)");
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Case3_EvictionOrderWithoutGet()
    {
        var cache = new MemoryLruTranslationCache(capacity: 2);
        cache.Add("x", MakeResult("X"));
        cache.Add("y", MakeResult("Y"));
        cache.Add("z", MakeResult("Z"));

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("x", out _), "x should be evicted first (oldest)");
        Assert.True(cache.TryGet("y", out _));
        Assert.True(cache.TryGet("z", out _));
    }

    [Fact]
    public void Case4_RemoveAndClear()
    {
        var cache = new MemoryLruTranslationCache(capacity: 5);
        cache.Add("a", MakeResult("A"));
        cache.Add("b", MakeResult("B"));
        cache.Add("c", MakeResult("C"));

        cache.Remove("b");
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("b", out _));

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("c", out _));
    }

    [Fact]
    public void Case5_ConcurrentAdd_NoCrashAndCountWithinCapacity()
    {
        var cache = new MemoryLruTranslationCache(capacity: 100);

        Parallel.For(0, 100, i =>
        {
            cache.Add($"key-{i}", MakeResult($"v{i}"));
        });

        Assert.True(cache.Count <= 100, $"Count {cache.Count} should be <= 100");
        Assert.Equal(100, cache.Capacity);
    }

    [Fact]
    public void Case6_ConcurrentAdd_ManyMoreThanCapacity()
    {
        var cache = new MemoryLruTranslationCache(capacity: 50);

        Parallel.For(0, 200, i =>
        {
            cache.Add($"k{i}", MakeResult($"v{i}"));
        });

        Assert.True(cache.Count <= 50, $"Count {cache.Count} should be <= 50");
    }
}
