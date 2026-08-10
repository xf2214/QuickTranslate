using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Platform.Win32;

namespace QuickTranslate.App.Windows;

public partial class WordPopupWindow : Window
{
    private static readonly ILogger _logger = LoggingInteractionCoordinatorFactory.CreateLogger<WordPopupWindow>();

    public DipRect LastLayoutDipRect { get; private set; }

    private SelectionResult? _lastSelection;
    private TranslationResult? _lastTranslation;
    private bool _allowActivate;

    public WordPopupWindow()
    {
        InitializeComponent();

        CopyButton.Click += OnCopyButtonClick;
        DismissButton.Click += OnDismissButtonClick;
        CopyButton.PreviewMouseDown += OnButtonPreviewMouseDown;
        DismissButton.PreviewMouseDown += OnButtonPreviewMouseDown;
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
}

internal static class LoggingInteractionCoordinatorFactory
{
    private static readonly ILoggerFactory _factory = LoggerFactory.Create(b =>
    {
        b.AddDebug();
    });

    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();
}
