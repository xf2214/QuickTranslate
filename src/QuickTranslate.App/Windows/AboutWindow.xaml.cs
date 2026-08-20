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

    // 在部署后稳定暴露的项目主页
    private const string ProjectWebsite = "https://github.com/xf2214/QuickTranslate";

    public AboutWindow() : this(null)
    {
    }

    public AboutWindow(ModelVersionVerifier? modelVersionVerifier)
    {
        _modelVersionVerifier = modelVersionVerifier;
        InitializeComponent();

        var asm = Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version!;
        // AssemblyVersion 使用 0.M.R.0，对外显示语义化版本
        VersionLabel.Text = $"Version v{ver.Major}.{ver.Minor}.{ver.Build}";

        var buildTime = File.GetLastWriteTime(asm.Location);
        BuildLabel.Text = $"Build 时间: {buildTime:yyyy-MM-dd HH:mm:ss}  ·  Runtime: .NET {Environment.Version.ToString(2)}";
        CopyrightLabel.Text = "© 2026 QuickTranslate · MIT License · See LICENSES.txt for 3rd-party licenses";

        // 模型与翻译引擎提示（与 docs/ocr-models-and-custom-llm.md / version.json 保持同步）
        OcrInfoLabel.Text = "OCR：PP-OCRv6 medium（det + rec） · ONNX Runtime CPU 推理 · 字典 18708 字符（blank + 18708 + space = 18710 类）· 缺失 cls.onnx 时跳过方向分类，不影响主流程。源：HoVDuc/ppocrv5-onnx v1.1.0";
        TranslationInfoLabel.Text = "翻译：默认接入任意 OpenAI 兼容端点（SSE 流式，可在设置中切换 Base URL / Model / API Key）；也可切回内置 Qwen-MT 非流式提供者。Word 模式优先查本地 ECDICT。";
        DictionaryInfoLabel.Text = "本地词典：ECDICT-lite（packed 二进制 → 用户自定义 overlay → 59 词 stub 三级回退）。详见设置 → 系统状态。";

        WebsiteButton.Click += OnWebsiteClick;
        CheckModelButton.Click += OnCheckModelClick;
        LicenseButton.Click += OnLicenseClick;
        CloseButton.Click += OnCloseClick;
        WebsiteButton.PreviewMouseDown += OnButtonPreviewMouseDown;
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

    private void OnWebsiteClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        try
        {
            Process.Start(new ProcessStartInfo(ProjectWebsite) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开项目主页失败");
            WpfMessageBox.Show(this, $"打开项目主页失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _allowActivate = false;
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
