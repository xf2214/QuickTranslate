using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Platform.Win32;

namespace QuickTranslate.App.Windows;

public partial class BlockPopupWindow : Window
{
    private bool _interactive;

    public string FullText { get; private set; } = "";

    public BlockPopupWindow()
    {
        InitializeComponent();

        CopyButton.Click += OnCopyButtonClick;
        DismissButton.Click += OnDismissButtonClick;
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
}
