using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
        FullText += delta;
        TranslationText.Text = FullText;
        ScrollViewer1.ScrollToEnd();
    }

    public void UpdateHeader(int lineCount)
    {
        BlockHeader.Text = $"块模式 · {lineCount} 行";
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
