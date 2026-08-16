using Microsoft.Extensions.Options;
using QuickTranslate.Core.Options;
using Xunit;

namespace QuickTranslate.Tests.Core;

public class SettingsDefaultsTests
{
    [Fact]
    public void AppSettings_DefaultConstructor_HasExpectedDefaults()
    {
        var settings = new AppSettings();

        Assert.NotNull(settings.WordHotkey);
        Assert.Equal(HotkeyModifiers.Alt, settings.WordHotkey.Modifiers);
        Assert.Equal(KeyboardKey.D1, settings.WordHotkey.Key);

        Assert.NotNull(settings.BlockHotkey);
        Assert.Equal(HotkeyModifiers.Alt, settings.BlockHotkey.Modifiers);
        Assert.Equal(KeyboardKey.D2, settings.BlockHotkey.Key);

        Assert.Equal("zh-CN", settings.TargetLanguage);
        Assert.Equal(TranslationQuality.Fast, settings.TranslationQuality);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.CloseOnOutsideClick);
        Assert.False(settings.DebugLogging);
        Assert.False(settings.EnableTextToSpeech);
    }

    [Fact]
    public void HotkeyCombo_DefaultConstructor_HasNoneValues()
    {
        var combo = new HotkeyCombo();

        Assert.Equal(HotkeyModifiers.None, combo.Modifiers);
        Assert.Equal(KeyboardKey.None, combo.Key);
    }

    [Fact]
    public void HotkeyCombo_ParameterizedConstructor_SetsValues()
    {
        var combo = new HotkeyCombo(HotkeyModifiers.Shift, KeyboardKey.T);

        Assert.Equal(HotkeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(KeyboardKey.T, combo.Key);
    }

    [Fact]
    public void AppSettings_ConfigureOptions_BindsCorrectly()
    {
        var sourceSettings = new AppSettings
        {
            TargetLanguage = "en-US",
            TranslationQuality = TranslationQuality.Fast,
            StartWithWindows = true,
            DebugLogging = true
        };

        var target = new AppSettings();
        var configure = new ConfigureOptions<AppSettings>(opts =>
        {
            opts.WordHotkey = sourceSettings.WordHotkey;
            opts.BlockHotkey = sourceSettings.BlockHotkey;
            opts.TargetLanguage = sourceSettings.TargetLanguage;
            opts.TranslationQuality = sourceSettings.TranslationQuality;
            opts.StartWithWindows = sourceSettings.StartWithWindows;
            opts.CloseOnOutsideClick = sourceSettings.CloseOnOutsideClick;
            opts.DebugLogging = sourceSettings.DebugLogging;
        });

        configure.Configure(target);

        Assert.Equal("en-US", target.TargetLanguage);
        Assert.Equal(TranslationQuality.Fast, target.TranslationQuality);
        Assert.True(target.StartWithWindows);
        Assert.True(target.DebugLogging);
    }
}
