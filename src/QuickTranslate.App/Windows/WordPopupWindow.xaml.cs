using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Platform.Win32;
using QuickTranslate.TextToSpeech;

namespace QuickTranslate.App.Windows;

public partial class WordPopupWindow : Window
{
    private static readonly ILogger _logger = LoggingInteractionCoordinatorFactory.CreateLogger<WordPopupWindow>();

    public DipRect LastLayoutDipRect { get; private set; }

    private SelectionResult? _lastSelection;
    private TranslationResult? _lastTranslation;
    private bool _allowActivate;
    private ITextToSpeechService? _textToSpeech;
    private string? _speechLanguage;

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

        if (trans.NeedsOnline)
        {
            TranslationText.Text = "在线翻译接入（M5）";
        }
        else
        {
            TranslationText.Text = trans.TargetText ?? string.Empty;
        }

        DictionaryBadge.Visibility = trans.FromDictionary ? Visibility.Visible : Visibility.Hidden;
        CacheBadge.Visibility = trans.FromCache ? Visibility.Visible : Visibility.Hidden;
    }

    public void ShowError(string shortMessage)
    {
        TranslationText.Text = "";
        TranslationText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        TranslationText.Text = shortMessage;
        WordHeader.Visibility = Visibility.Collapsed;
        DictionaryBadge.Visibility = Visibility.Collapsed;
        CacheBadge.Visibility = Visibility.Collapsed;
    }

    public void ResetStyle()
    {
        TranslationText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27));
        WordHeader.Visibility = Visibility.Visible;
        DictionaryBadge.Visibility = Visibility.Hidden;
        CacheBadge.Visibility = Visibility.Hidden;
    }

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
        try
        {
            if (_lastSelection != null && _lastTranslation != null)
            {
                string text = _lastSelection.Text + "\n" + _lastTranslation.TargetText;
                System.Windows.Clipboard.SetText(text);
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
    }

    private void OnDismissButtonClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        try
        {
            Hide();
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
            var text = _lastTranslation?.TargetText ?? WordHeader.Text;
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
