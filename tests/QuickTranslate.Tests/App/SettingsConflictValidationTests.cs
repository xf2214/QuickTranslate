using NSubstitute;
using QuickTranslate.App.Validation;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using Xunit;

namespace QuickTranslate.Tests.App;

public class SettingsConflictValidationTests
{
    [Fact]
    public void WordEqualsBlock_ValidationFail()
    {
        var broker = Substitute.For<IHotkeyBroker>();
        broker.Probe(Arg.Any<HotkeyModifiers>(), Arg.Any<KeyboardKey>()).Returns(true);

        var word = new HotkeyCombo(HotkeyModifiers.Alt, KeyboardKey.D1);
        var block = new HotkeyCombo(HotkeyModifiers.Alt, KeyboardKey.D1);

        var result = SettingsValidator.Validate(word, block, broker);

        Assert.False(result.IsSuccess);
        Assert.Contains("相同", result.ErrorMessage);
        Assert.Contains("冲突", result.ErrorMessage);
    }

    [Fact]
    public void ProbeFail_MapsToUiError()
    {
        var broker = Substitute.For<IHotkeyBroker>();
        broker.Probe(HotkeyModifiers.Alt, KeyboardKey.D1).Returns(false);
        broker.Probe(HotkeyModifiers.Alt, KeyboardKey.D2).Returns(true);

        var word = new HotkeyCombo(HotkeyModifiers.Alt, KeyboardKey.D1);
        var block = new HotkeyCombo(HotkeyModifiers.Alt, KeyboardKey.D2);

        var result = SettingsValidator.Validate(word, block, broker);

        Assert.False(result.IsSuccess);
        Assert.Contains("冲突", result.ErrorMessage);
    }

    [Fact]
    public async Task Save_NoConflict_Success()
    {
        var settingsManager = Substitute.For<ISettingsManager>();
        var broker = Substitute.For<IHotkeyBroker>();
        var startup = Substitute.For<IStartupRegistrar>();

        broker.Probe(Arg.Any<HotkeyModifiers>(), Arg.Any<KeyboardKey>()).Returns(true);

        var word = new HotkeyCombo(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, KeyboardKey.W);
        var block = new HotkeyCombo(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, KeyboardKey.B);

        var validateResult = SettingsValidator.Validate(word, block, broker);
        Assert.True(validateResult.IsSuccess);

        var newSettings = new AppSettings
        {
            WordHotkey = word,
            BlockHotkey = block,
            TargetLanguage = "en",
            TranslationQuality = TranslationQuality.Balanced
        };

        await settingsManager.SaveAsync(newSettings);

        await settingsManager.Received(1).SaveAsync(
            Arg.Is<AppSettings>(s =>
                s.WordHotkey.Key == KeyboardKey.W &&
                s.BlockHotkey.Key == KeyboardKey.B),
            Arg.Any<CancellationToken>());
    }
}
