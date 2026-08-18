using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using QuickTranslate.TextToSpeech;

namespace QuickTranslate.App.Windows;

public partial class BlockPopupWindow : Window, IFadeOutHideable
{
    // Apple 风格配色（与 XAML 保持一致）
    private static readonly SolidColorBrush PrimaryTextBrush = new(System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush ErrorTextBrush = new(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly SolidColorBrush CopiedFeedbackBrush = new(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

    private bool _interactive;
    private ITextToSpeechService? _textToSpeech;
    private string? _speechLanguage;
    private DispatcherTimer? _copyFeedbackTimer;

    public string FullText { get; private set; } = "";

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
        FullText += delta;
        TranslationText.Text = FullText;
        ScrollViewer1.ScrollToEnd();
    }

    /// <summary>新一轮展示前清空旧内容（窗口复用时避免上次译文残留/累积）。</summary>
    public void ResetContent(string? sourceText = null)
    {
        FullText = "";
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
        FullText = "";
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

    /// <summary>复制成功微反馈：按钮变绿勾 1.2 秒后还原。</summary>
    private void ShowCopyFeedback()
    {
        _copyFeedbackTimer?.Stop();

        CopyButton.Content = "已复制 ✓";
        CopyButton.Foreground = CopiedFeedbackBrush;

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer!.Stop();
            CopyButton.Content = "复制";
            CopyButton.ClearValue(ForegroundProperty);
        };
        _copyFeedbackTimer.Start();
    }

    private void OnDismissButtonClick(object sender, RoutedEventArgs e)
    {
        HideWithFade();
    }

    public PhysicalSize GetPreferredPhysicalSize(uint dpiX, uint dpiY)
    {
        int w = (int)Math.Round(440.0 * dpiX / 96.0);
        int h = (int)Math.Round(480.0 * dpiY / 96.0);
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
