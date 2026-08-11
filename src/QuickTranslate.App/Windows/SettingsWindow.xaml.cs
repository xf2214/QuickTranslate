using System.Windows;
using SWC = System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Validation;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.Logging;

namespace QuickTranslate.App.Windows;

public partial class SettingsWindow : Window
{
    private readonly ISettingsManager _settingsManager;
    private readonly IHotkeyBroker _hotkeyBroker;
    private readonly IStartupRegistrar _startupRegistrar;
    private readonly AppSettings _appSettings;
    private bool _apiKeyCleared;

    public SettingsWindow(
        ISettingsManager settingsManager,
        IHotkeyBroker hotkeyBroker,
        IStartupRegistrar startupRegistrar,
        IOptions<AppSettings> appSettingsOptions)
    {
        InitializeComponent();

        _settingsManager = settingsManager;
        _hotkeyBroker = hotkeyBroker;
        _startupRegistrar = startupRegistrar;
        _appSettings = appSettingsOptions.Value;

        Loaded += OnLoaded;
        CloseButton.Click += (s, e) => Close();
        CancelButton.Click += (s, e) => Close();
        SaveButton.Click += OnSaveClicked;
        ClearApiKeyButton.Click += OnClearApiKeyClicked;
        DebugLoggingBox.Checked += (s, e) => LoggingSwitch.SetDebugEnabled(true);
        DebugLoggingBox.Unchecked += (s, e) => LoggingSwitch.SetDebugEnabled(false);
    }

    public static SettingsWindow Create(IServiceProvider sp)
    {
        var settingsManager = sp.GetRequiredService<ISettingsManager>();
        var hotkeyBroker = sp.GetRequiredService<IHotkeyBroker>();
        var startupRegistrar = sp.GetRequiredService<IStartupRegistrar>();
        var appSettings = sp.GetRequiredService<IOptions<AppSettings>>();
        return new SettingsWindow(settingsManager, hotkeyBroker, startupRegistrar, appSettings);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var existingKey = _settingsManager.GetApiKey();
        if (!string.IsNullOrEmpty(existingKey))
        {
            ApiKeyBox.Password = new string('*', existingKey.Length);
        }

        WordHotkeyRecorder.Hotkey = new HotkeyCombo(_appSettings.WordHotkey.Modifiers, _appSettings.WordHotkey.Key);
        BlockHotkeyRecorder.Hotkey = new HotkeyCombo(_appSettings.BlockHotkey.Modifiers, _appSettings.BlockHotkey.Key);

        SelectComboBoxItemByTag(TargetLanguageBox, _appSettings.TargetLanguage);
        SelectComboBoxItemByTag(TranslationQualityBox, _appSettings.TranslationQuality.ToString());

        StartOnBootBox.IsChecked = _startupRegistrar.IsEnabled;
        CloseOutsideBox.IsChecked = _appSettings.CloseOnOutsideClick;
        DebugLoggingBox.IsChecked = _appSettings.DebugLogging;

        _apiKeyCleared = false;
        HideError();
    }

    private static void SelectComboBoxItemByTag(SWC.ComboBox combo, string? tag)
    {
        if (tag == null) return;
        foreach (var item in combo.Items)
        {
            if (item is SWC.ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
    }

    private void OnClearApiKeyClicked(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = string.Empty;
        _apiKeyCleared = true;
    }

    private void ShowError(string msg)
    {
        ErrorLabel.Text = msg;
        ErrorLabel.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorLabel.Text = string.Empty;
        ErrorLabel.Visibility = Visibility.Collapsed;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        HideError();

        var wordHotkey = WordHotkeyRecorder.Hotkey;
        var blockHotkey = BlockHotkeyRecorder.Hotkey;

        var validation = SettingsValidator.Validate(wordHotkey, blockHotkey, _hotkeyBroker);
        if (!validation.IsSuccess)
        {
            ShowError(validation.ErrorMessage ?? "验证失败");
            return;
        }

        try
        {
            if (_apiKeyCleared)
            {
                _settingsManager.SetApiKey(null);
            }
            else if (!string.IsNullOrEmpty(ApiKeyBox.Password) && !AllStars(ApiKeyBox.Password))
            {
                _settingsManager.SetApiKey(ApiKeyBox.Password);
            }

            if (StartOnBootBox.IsChecked == true)
                _startupRegistrar.Enable();
            else
                _startupRegistrar.Disable();

            bool debugEnabled = DebugLoggingBox.IsChecked == true;
            LoggingSwitch.SetDebugEnabled(debugEnabled);

            var targetLangItem = TargetLanguageBox.SelectedItem as SWC.ComboBoxItem;
            var qualityItem = TranslationQualityBox.SelectedItem as SWC.ComboBoxItem;

            var newSettings = new AppSettings
            {
                WordHotkey = new HotkeyCombo(wordHotkey.Modifiers, wordHotkey.Key),
                BlockHotkey = new HotkeyCombo(blockHotkey.Modifiers, blockHotkey.Key),
                TargetLanguage = targetLangItem?.Tag?.ToString() ?? _appSettings.TargetLanguage,
                TranslationQuality = ParseQuality(qualityItem?.Tag?.ToString()),
                StartWithWindows = StartOnBootBox.IsChecked == true,
                CloseOnOutsideClick = CloseOutsideBox.IsChecked == true,
                DebugLogging = debugEnabled,
                ResolvedApiKey = _settingsManager.GetApiKey()
            };

            _settingsManager.SaveAsync(newSettings).GetAwaiter().GetResult();

            _hotkeyBroker.UnregisterAll();
            _hotkeyBroker.RegisterDefaultsFromSettings(newSettings);

            Close();
        }
        catch (Exception ex)
        {
            ShowError($"保存失败：{ex.Message}");
        }
    }

    private static bool AllStars(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (c != '*') return false;
        }
        return true;
    }

    private static TranslationQuality ParseQuality(string? tag)
    {
        if (tag == null) return TranslationQuality.Fast;
        if (Enum.TryParse<TranslationQuality>(tag, true, out var q))
            return q;
        return TranslationQuality.Fast;
    }
}
