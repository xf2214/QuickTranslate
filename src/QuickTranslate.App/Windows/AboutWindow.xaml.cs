using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using QuickTranslate.App.Bootstrap;
using QuickTranslate.Infrastructure.Services;
using QuickTranslate.Platform.Win32;
using WpfMessageBox = System.Windows.MessageBox;

namespace QuickTranslate.App.Windows;

public partial class AboutWindow : Window
{
    private static readonly ILogger _logger = LoggingInteractionCoordinatorFactory.CreateLogger<AboutWindow>();
    private readonly ModelVersionVerifier? _modelVersionVerifier;
    private bool _allowActivate;

    public AboutWindow() : this(null)
    {
    }

    public AboutWindow(ModelVersionVerifier? modelVersionVerifier)
    {
        _modelVersionVerifier = modelVersionVerifier;
        InitializeComponent();

        var asm = Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version!;
        VersionLabel.Text = $"Version v{ver.Major}.{ver.Minor}.{ver.Build}";

        var buildTime = File.GetLastWriteTime(asm.Location);
        BuildLabel.Text = $"Build 时间: {buildTime:yyyy-MM-dd HH:mm:ss}";
        CopyrightLabel.Text = "© 2026 QuickTranslate";

        CheckModelButton.Click += OnCheckModelClick;
        LicenseButton.Click += OnLicenseClick;
        CloseButton.Click += OnCloseClick;
        CheckModelButton.PreviewMouseDown += OnButtonPreviewMouseDown;
        LicenseButton.PreviewMouseDown += OnButtonPreviewMouseDown;
        CloseButton.PreviewMouseDown += OnButtonPreviewMouseDown;
        RootBorder.MouseLeftButtonDown += OnRootBorderMouseLeftButtonDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowStyleHelper.SetNoActivateToolTopMost(hwnd);
    }

    private void OnButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _allowActivate = true;
    }

    private void OnRootBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Button)
        {
            _allowActivate = true;
            if (_allowActivate)
            {
                Activate();
            }
        }
    }

    private void OnCheckModelClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        try
        {
            var summary = _modelVersionVerifier?.GetSummaryText() ?? "ModelVersionVerifier 未初始化";
            WpfMessageBox.Show(this, summary, "模型版本信息", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查模型失败");
            WpfMessageBox.Show(this, $"检查模型失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _allowActivate = false;
        }
    }

    private void OnLicenseClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        try
        {
            var licensesPath = Path.Combine(AppContext.BaseDirectory, "LICENSES.txt");
            if (!File.Exists(licensesPath))
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "LICENSES.txt");
                candidate = Path.GetFullPath(candidate);
                if (File.Exists(candidate)) licensesPath = candidate;
            }

            if (File.Exists(licensesPath))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", licensesPath) { UseShellExecute = true });
            }
            else
            {
                WpfMessageBox.Show(this, $"LICENSES.txt 未找到: {licensesPath}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开 LICENSES.txt 失败");
            WpfMessageBox.Show(this, $"打开 LICENSES.txt 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _allowActivate = false;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
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
}
