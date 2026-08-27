using System.Text.Json;
using QuickTranslate.Core.Options;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class PopupDisplayStyleSettingsTests
{
    [Fact]
    public void AppSettings_Default_PopupDisplayStyle_IsDetailed()
    {
        var s = new AppSettings();
        Assert.Equal("detailed", s.PopupDisplayStyle);
    }

    [Fact]
    public void AppSettings_JsonRoundtrip_Compact_Survives()
    {
        var s = new AppSettings { PopupDisplayStyle = "compact" };
        var json = JsonSerializer.Serialize(s);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal("compact", restored!.PopupDisplayStyle);
    }

    [Fact]
    public void AppSettings_JsonRoundtrip_Detailed_Survives()
    {
        var s = new AppSettings { PopupDisplayStyle = "detailed" };
        var json = JsonSerializer.Serialize(s);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal("detailed", restored!.PopupDisplayStyle);
    }

    [Fact]
    public void AppSettings_JsonMissingField_DefaultsToDetailed()
    {
        var json = "{}";
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal("detailed", restored!.PopupDisplayStyle);
    }
}
