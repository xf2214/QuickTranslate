using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Translation;
using QuickTranslate.Platform.Win32;
using QuickTranslate.TextToSpeech;

namespace QuickTranslate.App.Windows;

public partial class BlockPopupWindow : Window, IFadeOutHideable
{
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

    private bool _interactive;
    private ITextToSpeechService? _textToSpeech;
    private string? _speechLanguage;
    private DispatcherTimer? _copyFeedbackTimer;

    // StringBuilder 累积流式译文：避免 FullText += delta 的 O(n²) 拷贝
    private readonly StringBuilder _fullText = new();

    public string FullText => _fullText.ToString();

    public BlockPopupWindow()
    {
        InitializeComponent();

        CopyButton.Click += OnCopyButtonClick;
        DismissButton.Click += OnDismissButtonClick;
        SpeakButton.Click += OnSpeakButtonClick;
        RootBorder.PreviewMouseDown += OnRootBorderPreviewMouseDown;

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
        WindowStyleHelper.ApplyNoActivate(hwnd);
    }

    private void OnRootBorderPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive)
        {
            _interactive = true;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            WindowStyleHelper.SetForegroundWindow(hwnd);
        }
    }

    public void AppendChunk(string delta)
    {
        ResetStyle();
        _fullText.Append(delta);
        // 展示层统一格式化（去首尾空白/多余空行、规范换行）；_fullText 保留原文供复制
        TranslationText.Text = TranslationDisplayFormatter.ForBlock(_fullText.ToString());
        AutoScrollToEnd();
    }

    /// <summary>仅在滚动条已贴近底部时自动跟随（距底 ≤ 24px），避免用户回看时被强制拉回。</summary>
    private void AutoScrollToEnd()
    {
        var sv = ScrollViewer1;
        if (sv.ScrollableHeight - sv.VerticalOffset <= 24)
        {
            sv.ScrollToEnd();
        }
    }

    /// <summary>新一轮展示前清空旧内容（窗口复用时避免上次译文残留/累积）。</summary>
    public void ResetContent(string? sourceText = null)
    {
        _fullText.Clear();
        TranslationText.Text = "";
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            SourceText.Text = "";
            SourceText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SourceText.Text = sourceText;
            SourceText.Visibility = Visibility.Visible;
        }
        ResetStyle();
    }

    public void UpdateHeader(int lineCount)
    {
        BlockHeader.Text = $"块模式 · {lineCount} 行";
    }

    public void ShowError(string shortMessage)
    {
        _fullText.Clear();
        TranslationText.Foreground = ErrorTextBrush;
        TranslationText.Text = shortMessage;
        BlockHeader.Visibility = Visibility.Collapsed;
    }

    public void ResetStyle()
    {
        TranslationText.Foreground = PrimaryTextBrush;
        BlockHeader.Visibility = Visibility.Visible;
    }

    /// <summary>带淡出退场的隐藏（自动隐藏/服务收起链路统一入口）。</summary>
    public void HideWithFade() => PopupTransition.PlayExit(this, RootBorder);

    /// <summary>窗口已可见时被复用（新译文顶替）：取消进行中的退场动画并复播进场。</summary>
    public void ReplayEntry() => PopupTransition.PlayEntry(RootBorder);

    private void OnCopyButtonClick(object sender, RoutedEventArgs e)
    {
        bool copied = false;
        try
        {
            System.Windows.Clipboard.SetText(FullText);
            copied = true;
        }
        catch
        {
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
        HideWithFade();
    }

    public PhysicalSize GetPreferredPhysicalSize(uint dpiX, uint dpiY)
    {
        int w = (int)Math.Round(340.0 * dpiX / 96.0);
        int h = (int)Math.Round(360.0 * dpiY / 96.0);
        return new PhysicalSize(w, h);
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
        try
        {
            if (!string.IsNullOrWhiteSpace(FullText) && _textToSpeech != null)
            {
                _textToSpeech.Speak(FullText, _speechLanguage);
            }
        }
        catch
        {
        }
    }
}
