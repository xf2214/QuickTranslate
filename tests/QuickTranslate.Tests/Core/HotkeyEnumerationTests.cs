using QuickTranslate.Core.Options;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class HotkeyEnumerationTests
{
    [Fact]
    public void HotkeyModifiers_Values_MatchWin32()
    {
        Assert.Equal(1, (int)HotkeyModifiers.Alt);
        Assert.Equal(2, (int)HotkeyModifiers.Ctrl);
        Assert.Equal(4, (int)HotkeyModifiers.Shift);
        Assert.Equal(8, (int)HotkeyModifiers.Win);
    }

    [Fact]
    public void KeyboardKey_D1_MatchesVK()
    {
        Assert.Equal(0x31, (int)KeyboardKey.D1);
    }

    [Fact]
    public void KeyboardKey_D2_MatchesVK()
    {
        Assert.Equal(0x32, (int)KeyboardKey.D2);
    }

    [Fact]
    public void KeyboardKey_Escape_MatchesVK()
    {
        Assert.Equal(0x1B, (int)KeyboardKey.Escape);
    }

    [Fact]
    public void KeyboardKey_A_MatchesVK()
    {
        Assert.Equal(0x41, (int)KeyboardKey.A);
    }

    [Fact]
    public void KeyboardKey_F1_MatchesVK()
    {
        Assert.Equal(0x70, (int)KeyboardKey.F1);
    }
}
