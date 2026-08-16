using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;
using QuickTranslate.TextToSpeech;

namespace QuickTranslate.App.Windows;

public partial class BlockPopupWindow : Window
{
    private bool _interactive;
    private ITextToSpeechService? _textToSpeech;
    private string? _speechLanguage;

    public string FullText { get; private set; } = "";

    public BlockPopupWindow()
    {
        InitializeComponent();

        CopyButton.Click += OnCopyButtonClick;
        DismissButton.Click += OnDismissButtonClick;
        SpeakButton.Click += OnSpeakButtonClick;
        RootBorder.PreviewMouseDown += OnRootBorderPreviewMouseDown;
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
        TranslationText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        TranslationText.Text = shortMessage;
        BlockHeader.Visibility = Visibility.Collapsed;
    }

    public void ResetStyle()
    {
        TranslationText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27));
        BlockHeader.Visibility = Visibility.Visible;
    }

    private void OnCopyButtonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(FullText);
        }
        catch
        {
        }
    }

    private void OnDismissButtonClick(object sender, RoutedEventArgs e)
    {
        Hide();
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
