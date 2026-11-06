using System.Text.Json;
using QuickTranslate.Core.Options;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class DragSelectionSettingsTests
{
    [Fact]
    public void AppSettings_Default_EnableDragSelection_IsFalse()
    {
        var s = new AppSettings();
        Assert.False(s.EnableDragSelection);
    }

    [Fact]
    public void AppSettings_ApplyFrom_Copies_EnableDragSelection_True()
    {
        var source = new AppSettings { EnableDragSelection = true };
        var target = new AppSettings { EnableDragSelection = false };
        target.ApplyFrom(source);
        Assert.True(target.EnableDragSelection);
    }

    [Fact]
    public void AppSettings_ApplyFrom_Copies_EnableDragSelection_False()
    {
        var source = new AppSettings { EnableDragSelection = false };
        var target = new AppSettings { EnableDragSelection = true };
        target.ApplyFrom(source);
        Assert.False(target.EnableDragSelection);
    }

    [Fact]
    public void AppSettings_JsonRoundtrip_EnableDragSelection_True_Survives()
    {
        var s = new AppSettings { EnableDragSelection = true };
        var json = JsonSerializer.Serialize(s);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.True(restored!.EnableDragSelection);
        Assert.Contains("EnableDragSelection", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettings_JsonRoundtrip_EnableDragSelection_False_Survives()
    {
        var s = new AppSettings { EnableDragSelection = false };
        var json = JsonSerializer.Serialize(s);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.False(restored!.EnableDragSelection);
    }

    [Fact]
    public void AppSettings_JsonMissingField_DefaultsToFalse()
    {
        var json = "{}";
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.False(restored!.EnableDragSelection);
    }
}
