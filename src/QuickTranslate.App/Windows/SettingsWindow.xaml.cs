using System.Windows;
using System.Windows.Media.Animation;
using SWC = System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Validation;
using QuickTranslate.App.Windows.Controls;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.Logging;
using QuickTranslate.Infrastructure.Translation;
using WpfMessageBox = System.Windows.MessageBox;

namespace QuickTranslate.App.Windows;

public partial class SettingsWindow : Window
{
    private readonly ISettingsManager _settingsManager;
    private readonly IHotkeyBroker _hotkeyBroker;
    private readonly IStartupRegistrar _startupRegistrar;
    private readonly IOcrEngine? _ocrEngine;
    private readonly CustomOpenAiTranslationProvider? _modelTester;
    private readonly AppSettings _appSettings;
    private bool _apiKeyCleared;

    public SettingsWindow(
        ISettingsManager settingsManager,
        IHotkeyBroker hotkeyBroker,
        IStartupRegistrar startupRegistrar,
        IOptions<AppSettings> appSettingsOptions,
        IOcrEngine? ocrEngine = null,
        CustomOpenAiTranslationProvider? modelTester = null)
    {
        InitializeComponent();

        _settingsManager = settingsManager;
        _hotkeyBroker = hotkeyBroker;
        _startupRegistrar = startupRegistrar;
        _ocrEngine = ocrEngine;
        _modelTester = modelTester;
        _appSettings = appSettingsOptions.Value;

        Loaded += OnLoaded;
        CancelButton.Click += (s, e) => Close();
        SaveButton.Click += OnSaveClicked;
        ClearApiKeyButton.Click += OnClearApiKeyClicked;
        RestoreDefaultsButton.Click += OnRestoreDefaultsClicked;
        TestModelButton.Click += OnTestModelClicked;
        DebugLoggingBox.Checked += (s, e) => LoggingSwitch.SetDebugEnabled(true);
        DebugLoggingBox.Unchecked += (s, e) => LoggingSwitch.SetDebugEnabled(false);
    }

    public static SettingsWindow Create(IServiceProvider sp)
    {
        var settingsManager = sp.GetRequiredService<ISettingsManager>();
        var hotkeyBroker = sp.GetRequiredService<IHotkeyBroker>();
        var startupRegistrar = sp.GetRequiredService<IStartupRegistrar>();
        var appSettings = sp.GetRequiredService<IOptions<AppSettings>>();
        var ocrEngine = sp.GetService<IOcrEngine>();
        var modelTester = sp.GetService<CustomOpenAiTranslationProvider>();
        return new SettingsWindow(settingsManager, hotkeyBroker, startupRegistrar, appSettings, ocrEngine, modelTester);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 翻译引擎 + 自定义大模型
        SelectComboBoxItemByTag(TranslationProviderBox, _appSettings.TranslationProvider.ToString());
        CustomLlmBaseUrlBox.Text = _appSettings.CustomLlmBaseUrl ?? string.Empty;
        CustomLlmModelBox.Text = _appSettings.CustomLlmModel ?? string.Empty;
        UpdateCustomLlmPanelVisibility();

        // API Key 状态
        var existingKey = _settingsManager.GetApiKey();
        if (!string.IsNullOrEmpty(existingKey))
        {
            ApiKeyBox.Password = new string('*', existingKey.Length);
            UpdateApiKeyBadge(true);
        }
        else
        {
            UpdateApiKeyBadge(false);
        }

        // 热键初始化
        var wordHotkey = new HotkeyCombo(_appSettings.WordHotkey.Modifiers, _appSettings.WordHotkey.Key);
        var blockHotkey = new HotkeyCombo(_appSettings.BlockHotkey.Modifiers, _appSettings.BlockHotkey.Key);

        WordHotkeyRecorder.Hotkey = wordHotkey;
        BlockHotkeyRecorder.Hotkey = blockHotkey;

        // 设置 Broker + OtherHotkey（用于实时自冲突探测）
        WordHotkeyRecorder.Broker = _hotkeyBroker;
        BlockHotkeyRecorder.Broker = _hotkeyBroker;
        WordHotkeyRecorder.OtherHotkey = blockHotkey;
        BlockHotkeyRecorder.OtherHotkey = wordHotkey;

        // 热键变化时联动更新 OtherHotkey
        WordHotkeyRecorder.HotkeyChangedByUser += (s, newVal) =>
        {
            BlockHotkeyRecorder.OtherHotkey = new HotkeyCombo(newVal.Modifiers, newVal.Key);
            UpdateHotkeyStatusPreview();
        };
        BlockHotkeyRecorder.HotkeyChangedByUser += (s, newVal) =>
        {
            WordHotkeyRecorder.OtherHotkey = new HotkeyCombo(newVal.Modifiers, newVal.Key);
            UpdateHotkeyStatusPreview();
        };

        // 翻译偏好
        SelectComboBoxItemByTag(TargetLanguageBox, _appSettings.TargetLanguage);
        SetQualityRadio(_appSettings.TranslationQuality);

        // 行为设置
        StartOnBootBox.IsChecked = _startupRegistrar.IsEnabled;
        CloseOutsideBox.IsChecked = _appSettings.CloseOnOutsideClick;
        DebugLoggingBox.IsChecked = _appSettings.DebugLogging;
        EnableReadAloudBox.IsChecked = _appSettings.EnableTextToSpeech;

        // 系统状态
        RefreshSystemStatus();

        _apiKeyCleared = false;
        HideError();
        HideToast();
    }

    private void UpdateHotkeyStatusPreview()
    {
        var w = HotkeyRecorder.FormatHotkey(WordHotkeyRecorder.Hotkey.Modifiers, WordHotkeyRecorder.Hotkey.Key);
        var b = HotkeyRecorder.FormatHotkey(BlockHotkeyRecorder.Hotkey.Modifiers, BlockHotkeyRecorder.Hotkey.Key);
        HotkeyStatusText.Text = $"单词: {w}  区域: {b}";
    }

    private void SetQualityRadio(TranslationQuality q)
    {
        switch (q)
        {
            case TranslationQuality.Fast:
                QualityFast.IsChecked = true;
                break;
            case TranslationQuality.Balanced:
                QualityBalanced.IsChecked = true;
                break;
            case TranslationQuality.Best:
                QualityBest.IsChecked = true;
                break;
        }
    }

    private TranslationQuality GetQualityFromRadio()
    {
        if (QualityBest.IsChecked == true) return TranslationQuality.Best;
        if (QualityBalanced.IsChecked == true) return TranslationQuality.Balanced;
        return TranslationQuality.Fast;
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

    private void UpdateApiKeyBadge(bool configured)
    {
        if (configured)
        {
            ApiKeyStatusBadge.Background = (System.Windows.Media.Brush)FindResource("SuccessBg");
            ApiKeyStatusText.Text = "已配置";
            ApiKeyStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Success");
        }
        else
        {
            ApiKeyStatusBadge.Background = (System.Windows.Media.Brush)FindResource("WarningBg");
            ApiKeyStatusText.Text = "未填";
            ApiKeyStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
        }
    }

    private void RefreshSystemStatus()
    {
        // OCR 引擎状态
        if (_ocrEngine != null)
        {
            OcrEngineName.Text = _ocrEngine.EngineName;
            if (_ocrEngine.IsAvailable)
            {
                OcrEngineBadge.Background = (System.Windows.Media.Brush)FindResource("SuccessBg");
                OcrEngineStatus.Text = "可用";
                OcrEngineStatus.Foreground = (System.Windows.Media.Brush)FindResource("Success");
            }
            else
            {
                OcrEngineBadge.Background = (System.Windows.Media.Brush)FindResource("DangerBg");
                OcrEngineStatus.Text = "不可用";
                OcrEngineStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            }
        }
        else
        {
            OcrEngineName.Text = "未注册";
            OcrEngineBadge.Background = (System.Windows.Media.Brush)FindResource("WarningBg");
            OcrEngineStatus.Text = "未知";
            OcrEngineStatus.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
        }

        // 热键注册状态（从 Broker 探测）
        UpdateHotkeyStatusPreview();

        // 版本号
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionText.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v0.1.0";
    }

    private void OnClearApiKeyClicked(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = string.Empty;
        _apiKeyCleared = true;
        UpdateApiKeyBadge(false);
    }

    private void OnTranslationProviderChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // ComboBox 在 InitializeComponent/初始SelectedItem 绑定阶段就会触发；此时面板可能尚未就绪
        if (CustomLlmPanel != null)
        {
            UpdateCustomLlmPanelVisibility();
        }
    }

    private void UpdateCustomLlmPanelVisibility()
    {
        var item = TranslationProviderBox.SelectedItem as SWC.ComboBoxItem;
        var tag = item?.Tag?.ToString();
        CustomLlmPanel.Visibility = tag == "QwenMt"
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private TranslationProviderKind GetProviderKindFromCombo()
    {
        var item = TranslationProviderBox.SelectedItem as SWC.ComboBoxItem;
        return item?.Tag?.ToString() == "QwenMt"
            ? TranslationProviderKind.QwenMt
            : TranslationProviderKind.CustomOpenAi;
    }

    private void OnRestoreDefaultsClicked(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            this,
            "确定要恢复所有默认设置吗？\n（热键将重置为 Alt+1 / Alt+2，翻译偏好和选项也将恢复默认）",
            "恢复默认设置",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.OK)
            return;

        var defaults = new AppSettings();
        var defWord = new HotkeyCombo(defaults.WordHotkey.Modifiers, defaults.WordHotkey.Key);
        var defBlock = new HotkeyCombo(defaults.BlockHotkey.Modifiers, defaults.BlockHotkey.Key);

        WordHotkeyRecorder.Hotkey = defWord;
        BlockHotkeyRecorder.Hotkey = defBlock;
        WordHotkeyRecorder.OtherHotkey = defBlock;
        BlockHotkeyRecorder.OtherHotkey = defWord;

        SelectComboBoxItemByTag(TargetLanguageBox, defaults.TargetLanguage);
        SelectComboBoxItemByTag(TranslationProviderBox, defaults.TranslationProvider.ToString());
        CustomLlmBaseUrlBox.Text = defaults.CustomLlmBaseUrl;
        CustomLlmModelBox.Text = defaults.CustomLlmModel;
        UpdateCustomLlmPanelVisibility();
        SetQualityRadio(defaults.TranslationQuality);
        StartOnBootBox.IsChecked = defaults.StartWithWindows;
        CloseOutsideBox.IsChecked = defaults.CloseOnOutsideClick;
        DebugLoggingBox.IsChecked = defaults.DebugLogging;
        EnableReadAloudBox.IsChecked = defaults.EnableTextToSpeech;

        UpdateHotkeyStatusPreview();
        ShowError("");
        ShowToast("已恢复默认设置，记得点击「保存设置」使其生效。");
    }

    private void ShowError(string msg)
    {
        if (string.IsNullOrEmpty(msg))
        {
            HideError();
            return;
        }
        ErrorLabel.Text = msg;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorLabel.Text = string.Empty;
        ErrorBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowToast(string msg)
    {
        ToastText.Text = msg;
        ToastBorder.Opacity = 0;
        ToastBorder.Visibility = Visibility.Visible;

        var sb = new Storyboard();
        var daIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(daIn, ToastBorder);
        Storyboard.SetTargetProperty(daIn, new PropertyPath(OpacityProperty));
        sb.Children.Add(daIn);

        // 3秒后淡出
        var daOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            BeginTime = TimeSpan.FromSeconds(3),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(daOut, ToastBorder);
        Storyboard.SetTargetProperty(daOut, new PropertyPath(OpacityProperty));
        sb.Children.Add(daOut);

        sb.Completed += (s, _) =>
        {
            if (ToastBorder.Opacity <= 0.01)
                ToastBorder.Visibility = Visibility.Collapsed;
        };
        sb.Begin();
    }

    private void HideToast()
    {
        ToastBorder.Visibility = Visibility.Collapsed;
        ToastBorder.Opacity = 0;
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

        // 自定义大模型配置校验：选了自定义引擎时必须给出完整可用的 BaseUrl/Model
        var providerKind = GetProviderKindFromCombo();
        var customLlmValidation = SettingsValidator.ValidateCustomLlm(
            CustomLlmBaseUrlBox.Text, CustomLlmModelBox.Text,
            allowEmpty: providerKind != TranslationProviderKind.CustomOpenAi);
        if (!customLlmValidation.IsSuccess)
        {
            ShowError(customLlmValidation.ErrorMessage ?? "自定义大模型配置无效");
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

            var newSettings = new AppSettings
            {
                WordHotkey = new HotkeyCombo(wordHotkey.Modifiers, wordHotkey.Key),
                BlockHotkey = new HotkeyCombo(blockHotkey.Modifiers, blockHotkey.Key),
                TargetLanguage = targetLangItem?.Tag?.ToString() ?? _appSettings.TargetLanguage,
                TranslationQuality = GetQualityFromRadio(),
                StartWithWindows = StartOnBootBox.IsChecked == true,
                CloseOnOutsideClick = CloseOutsideBox.IsChecked == true,
                DebugLogging = debugEnabled,
                EnableTextToSpeech = EnableReadAloudBox.IsChecked == true,
                TranslationProvider = providerKind,
                CustomLlmBaseUrl = CustomLlmBaseUrlBox.Text.Trim(),
                CustomLlmModel = CustomLlmModelBox.Text.Trim(),
                ResolvedApiKey = _settingsManager.GetApiKey()
            };

            _settingsManager.SaveAsync(newSettings).GetAwaiter().GetResult();

            // 就地更新共享的 IOptions<AppSettings>.Value 实例（协调器/弹窗服务持有同一实例），
            // 使目标语言、热键等改动无需重启即可被运行中的协调器读到。
            _appSettings.WordHotkey = newSettings.WordHotkey;
            _appSettings.BlockHotkey = newSettings.BlockHotkey;
            _appSettings.TargetLanguage = newSettings.TargetLanguage;
            _appSettings.TranslationQuality = newSettings.TranslationQuality;
            _appSettings.StartWithWindows = newSettings.StartWithWindows;
            _appSettings.CloseOnOutsideClick = newSettings.CloseOnOutsideClick;
            _appSettings.DebugLogging = newSettings.DebugLogging;
            _appSettings.EnableTextToSpeech = newSettings.EnableTextToSpeech;
            _appSettings.TranslationProvider = newSettings.TranslationProvider;
            _appSettings.CustomLlmBaseUrl = newSettings.CustomLlmBaseUrl;
            _appSettings.CustomLlmModel = newSettings.CustomLlmModel;
            _appSettings.ResolvedApiKey = newSettings.ResolvedApiKey;

            _hotkeyBroker.UnregisterAll();
            _hotkeyBroker.RegisterDefaultsFromSettings(newSettings);

            // 更新其他 HotkeyRecorder 的 OtherHotkey（指向新保存的）
            WordHotkeyRecorder.OtherHotkey = new HotkeyCombo(newSettings.BlockHotkey.Modifiers, newSettings.BlockHotkey.Key);
            BlockHotkeyRecorder.OtherHotkey = new HotkeyCombo(newSettings.WordHotkey.Modifiers, newSettings.WordHotkey.Key);

            // 更新 API Key 徽章、系统状态
            UpdateApiKeyBadge(!string.IsNullOrEmpty(_settingsManager.GetApiKey()));
            RefreshSystemStatus();

            // Toast 提示，不立即关闭窗口
            SaveButton.IsEnabled = false;
            ShowToast("✔ 保存成功！设置已生效，可关闭窗口。");

            // 恢复按钮
            var dispTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            dispTimer.Tick += (s, _) =>
            {
                SaveButton.IsEnabled = true;
                dispTimer.Stop();
            };
            dispTimer.Start();
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

    // ===== 模型连通性测试 =====

    /// <summary>
    /// 解析测试用 API Key：输入框里的新值优先（全星号为掩码占位），
    /// 其次用已保存的 Key；已点过「清除」则视为无 Key。
    /// </summary>
    private string ResolveApiKeyForTest()
    {
        if (_apiKeyCleared) return string.Empty;
        var typed = ApiKeyBox.Password;
        if (!string.IsNullOrEmpty(typed) && !AllStars(typed)) return typed;
        return _settingsManager.GetApiKey() ?? string.Empty;
    }

    private void SetModelTestStatus(string text, string state)
    {
        ModelTestStatus.Text = text;
        ModelTestStatus.Foreground = state switch
        {
            "ok" => (System.Windows.Media.Brush)FindResource("Success"),
            "fail" => (System.Windows.Media.Brush)FindResource("Danger"),
            "testing" => (System.Windows.Media.Brush)FindResource("Warning"),
            _ => (System.Windows.Media.Brush)FindResource("TextSecondary")
        };
    }

    private async void OnTestModelClicked(object sender, RoutedEventArgs e)
    {
        if (_modelTester == null)
        {
            SetModelTestStatus("测试组件不可用", "fail");
            return;
        }

        var baseUrl = CustomLlmBaseUrlBox.Text.Trim();
        var model = CustomLlmModelBox.Text.Trim();
        var apiKey = ResolveApiKeyForTest();

        TestModelButton.IsEnabled = false;
        SetModelTestStatus("正在测试…", "testing");

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(40));
            var result = await _modelTester.TestConnectionAsync(baseUrl, model, apiKey, cts.Token);

            if (result.Success)
            {
                SetModelTestStatus($"✔ 模型可用（延迟 {(int)result.Latency!.Value.TotalMilliseconds} ms）", "ok");
            }
            else
            {
                SetModelTestStatus("✖ " + result.Message, "fail");
            }
        }
        catch (Exception ex)
        {
            SetModelTestStatus("✖ 测试失败：" + ex.Message, "fail");
        }
        finally
        {
            TestModelButton.IsEnabled = true;
        }
    }
}
