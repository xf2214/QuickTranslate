using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Platform.Win32;
using QuickTranslate.TextToSpeech;

namespace QuickTranslate.App.Windows;

public partial class WordPopupWindow : Window, IFadeOutHideable
{
    private static readonly ILogger _logger = LoggingInteractionCoordinatorFactory.CreateLogger<WordPopupWindow>();

    // Apple 风格配色（与 XAML 保持一致）；Freeze 后免去变更跟踪与跨线程检查开销
    private static readonly SolidColorBrush PrimaryTextBrush = Frozen(System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush ErrorTextBrush = Frozen(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly SolidColorBrush CopiedFeedbackBrush = Frozen(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

    private static SolidColorBrush Frozen(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public DipRect LastLayoutDipRect { get; private set; }

    private SelectionResult? _lastSelection;
    private TranslationResult? _lastTranslation;
    // 展示用的格式化译文（音标独立行、释义逐行）；复制按钮与展示保持一致
    private string _displayText = string.Empty;
    private bool _allowActivate;
    private ITextToSpeechService? _textToSpeech;
    private string? _speechLanguage;
    private DispatcherTimer? _copyFeedbackTimer;

    public WordPopupWindow()
    {
        InitializeComponent();

        CopyButton.Click += OnCopyButtonClick;
        DismissButton.Click += OnDismissButtonClick;
        SpeakButton.Click += OnSpeakButtonClick;
        CopyButton.PreviewMouseDown += OnButtonPreviewMouseDown;
        DismissButton.PreviewMouseDown += OnButtonPreviewMouseDown;
        SpeakButton.PreviewMouseDown += OnButtonPreviewMouseDown;
        RootBorder.MouseLeftButtonDown += OnRootBorderMouseLeftButtonDown;

        // 进场动效：每次可见时缩放+淡入（只动画 RootBorder，不动窗口 Opacity）
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                PopupTransition.PlayEntry(RootBorder);
            }
        };

        // 显示策略：翻译结果 5 秒后自动消失（用户点击交互则常驻）
        PopupAutoHide.Attach(this, TimeSpan.FromSeconds(5));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowStyleHelper.SetNoActivateToolTopMost(hwnd);
    }

    public void ApplyContent(SelectionResult sel, TranslationResult trans)
    {
        _lastSelection = sel;
        _lastTranslation = trans;

        ResetStyle();

        WordHeader.Text = sel.Text ?? string.Empty;

        // 在线翻译结果（NeedsOnline=true）与本地词典/缓存结果一样直接展示；
        // 早期 M5 阶段曾在此显示占位文本，导致在线译文被吞掉、看起来像未接入在线 API。
        // 词典 TargetText 含字面量 \n 与 "[音标]  " 前缀，显示前统一格式化（不改缓存内容）。
        _displayText = TranslationDisplayFormatter.ForWord(trans.TargetText, trans.FromDictionary);
        TranslationText.Text = _displayText;

        DictionaryBadge.Visibility = trans.FromDictionary ? Visibility.Visible : Visibility.Hidden;
        CacheBadge.Visibility = trans.FromCache ? Visibility.Visible : Visibility.Hidden;
    }

    public void ShowError(string shortMessage)
    {
        TranslationText.Text = "";
        TranslationText.Foreground = ErrorTextBrush;
        TranslationText.Text = shortMessage;
        WordHeader.Visibility = Visibility.Collapsed;
        DictionaryBadge.Visibility = Visibility.Collapsed;
        CacheBadge.Visibility = Visibility.Collapsed;
    }

    public void ResetStyle()
    {
        TranslationText.Foreground = PrimaryTextBrush;
        WordHeader.Visibility = Visibility.Visible;
        DictionaryBadge.Visibility = Visibility.Hidden;
        CacheBadge.Visibility = Visibility.Hidden;
    }

    /// <summary>带淡出退场的隐藏（自动隐藏/服务收起链路统一入口）。</summary>
    public void HideWithFade() => PopupTransition.PlayExit(this, RootBorder);

    /// <summary>窗口已可见时被复用（新译文顶替）：取消进行中的退场动画并复播进场。</summary>
    public void ReplayEntry() => PopupTransition.PlayEntry(RootBorder);

    private void OnButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _allowActivate = true;
    }

    private void OnRootBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Button)
        {
            _allowActivate = true;
            if (_allowActivate)
            {
                Activate();
            }
        }
    }

    private void OnCopyButtonClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        bool copied = false;
        try
        {
            if (_lastSelection != null && _lastTranslation != null)
            {
                string text = _lastSelection.Text + "\n" + _displayText;
                System.Windows.Clipboard.SetText(text);
                copied = true;
                _logger.LogInformation("Copied translation to clipboard");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy translation to clipboard");
        }
        finally
        {
            _allowActivate = false;
        }

        if (copied)
        {
            ShowCopyFeedback();
        }
    }

    /// <summary>复制成功微反馈：按钮变绿勾 1.2 秒后还原，内容切换带 100ms 淡入。</summary>
    private void ShowCopyFeedback()
    {
        _copyFeedbackTimer?.Stop();

        CopyButton.Content = "已复制 ✓";
        CopyButton.Foreground = CopiedFeedbackBrush;
        PlayFeedbackFade(CopyButton);

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer!.Stop();
            CopyButton.Content = "复制";
            CopyButton.ClearValue(ForegroundProperty);
            PlayFeedbackFade(CopyButton);
        };
        _copyFeedbackTimer.Start();
    }

    /// <summary>反馈内容切换的 100ms 淡入微动效（只动画按钮元素，不碰窗口 Opacity）。</summary>
    private static void PlayFeedbackFade(UIElement element)
    {
        var fade = new DoubleAnimation(0.4, 1.0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void OnDismissButtonClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        try
        {
            HideWithFade();
        }
        finally
        {
            _allowActivate = false;
        }
    }

    public void SetLastLayoutDipRect(DipRect rect)
    {
        LastLayoutDipRect = rect;
    }

    /// <summary>
    /// 配置朗读能力：开启朗读时显示"朗读"按钮，关闭或 TTS 不可用时隐藏。
    /// </summary>
    public void ApplyTextToSpeech(ITextToSpeechService? textToSpeech, bool enabled, string? targetLanguage)
    {
        _textToSpeech = textToSpeech;
        _speechLanguage = targetLanguage;
        SpeakButton.Visibility = enabled && textToSpeech != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSpeakButtonClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        try
        {
            // 优先朗读与展示一致的格式化译文，展示为空时退回原始译文/选中文本
            var text = !string.IsNullOrWhiteSpace(_displayText)
                ? _displayText
                : (_lastTranslation?.TargetText ?? WordHeader.Text);
            if (!string.IsNullOrWhiteSpace(text) && _textToSpeech != null)
            {
                _textToSpeech.Speak(text, _speechLanguage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to speak translation");
        }
        finally
        {
            _allowActivate = false;
        }
    }
}

internal static class LoggingInteractionCoordinatorFactory
{
    private static readonly ILoggerFactory _factory = LoggerFactory.Create(b =>
    {
        b.AddDebug();
    });

    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();
}
