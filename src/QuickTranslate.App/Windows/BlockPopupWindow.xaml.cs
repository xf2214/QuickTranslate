using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Translation;
using QuickTranslate.Platform.Win32;
using QuickTranslate.TextToSpeech;

// WHY：本项目 ImplicitUsings + UseWindowsForms 会全局引入 System.Windows.Forms 与
// System.Drawing，多个常用类型名二义；显式别名统一锁定 WPF/Media 类型
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace QuickTranslate.App.Windows;

public partial class BlockPopupWindow : Window, IFadeOutHideable
{
    // Apple 风格配色（与 XAML 保持一致）；Freeze 后免去变更跟踪与跨线程检查开销
    private static readonly SolidColorBrush PrimaryTextBrush = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush ErrorTextBrush = Frozen(Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly SolidColorBrush CopiedFeedbackBrush = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));

    private static SolidColorBrush Frozen(Color color)
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

    // 保存 ResetContent 传入的原始源文，供粘性重算时估算宽度（格式化后的 preview 已丢失换行等信息）
    private string? _sourcePreviewText;

    public string? CurrentSourceText => _sourcePreviewText;

    // 显示模式与流式状态
    private bool _isDetailed = true;
    private bool _isStreaming = true;
    private bool _isError;
    private bool _sourceExpanded;
    private int _lineCount;

    // 流式追加后通知宿主可按需重算弹窗尺寸；由 WpfBlockPopupService 节流处理
    public event EventHandler? ContentGrew;

    public BlockPopupWindow()
    {
        InitializeComponent();

        CopyButton.Click += OnCopyButtonClick;
        DismissButton.Click += OnDismissButtonClick;
        SpeakButton.Click += OnSpeakButtonClick;
        RootBorder.PreviewMouseDown += OnRootBorderPreviewMouseDown;

        // Toggle 行点击：折叠/展开原文引用块
        ToggleRow.MouseLeftButtonUp += OnToggleRowClick;
        SourceQuote.MouseLeftButtonUp += OnSourceQuoteClick;
        SourceExpandChevron.MouseLeftButtonUp += OnToggleRowClick;

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

        // 默认详细模式（若未通过 ApplyDisplayMode 指定则为详细）
        ApplyDisplayMode(true);
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

    /// <summary>切换显示模式（简洁/详细），重建所有模式相关视觉。</summary>
    public void ApplyDisplayMode(bool detailed)
    {
        _isDetailed = detailed;
        // WHY：窗口复用时必须重建所有模式相关视觉，避免上次模式的残留（meta 行、引用块展开态、按钮样式等）
        UpdateVisualMode();
        UpdateMeta();
        UpdateSourceSection();
        RenderTranslation();
        PopupChrome.ApplyFooterMode(SpeakButton, CopyButton, DismissButton, detailed);
        // 清理可能残留的复制反馈态，回归当前模式的常规态
        PopupChrome.ApplyCopyFeedback(CopyButton, detailed, false);
    }

    private void UpdateVisualMode()
    {
        // 译文样式按模式切换
        if (_isDetailed)
        {
            TranslationText.FontSize = 14.5;
            TranslationText.LineHeight = 24;
        }
        else
        {
            TranslationText.FontSize = 15;
            TranslationText.LineHeight = 25;
        }
    }

    private void UpdateMeta()
    {
        if (!_isDetailed)
        {
            MetaRow.Visibility = Visibility.Collapsed;
            return;
        }

        MetaRow.Visibility = Visibility.Visible;
        if (_isError)
        {
            PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.DangerRed, "翻译失败");
        }
        else if (_isStreaming)
        {
            PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.AccentBlue, "翻译中…");
        }
        else
        {
            PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.SecondaryText, "完成");
        }
    }

    private void UpdateSourceSection()
    {
        var preview = SourceText.Text;
        bool hasSource = !string.IsNullOrWhiteSpace(_sourcePreviewText) && !string.IsNullOrWhiteSpace(preview);
        // 若 ForSourcePreview 返回空则视为无源文
        if (!hasSource)
        {
            ToggleRow.Visibility = Visibility.Collapsed;
            SourceQuote.Visibility = Visibility.Collapsed;
            SourceDivider.Visibility = Visibility.Collapsed;
            SourceExpandChevron.Visibility = Visibility.Collapsed;
            return;
        }

        SourceDivider.Visibility = Visibility.Visible;
        if (_isDetailed)
        {
            // 详细：引用块常显，chevron 在右下角
            ToggleRow.Visibility = Visibility.Collapsed;
            SourceQuote.Visibility = Visibility.Visible;
            SourceExpandChevron.Visibility = Visibility.Visible;
            SourceQuote.MaxHeight = _sourceExpanded ? 120 : 54;
            SourceText.MaxHeight = _sourceExpanded ? 120 : 54;
            SourceExpandChevron.Text = _sourceExpanded ? PopupChrome.GlyphChevronDown : PopupChrome.GlyphChevronRight;
        }
        else
        {
            // 简洁：Toggle 行显示“原文 (N 行)”，引用块折叠/展开切换
            ToggleRow.Visibility = Visibility.Visible;
            ToggleLabel.Text = $"原文 ({_lineCount} 行)";
            ToggleChevron.Text = _sourceExpanded ? PopupChrome.GlyphChevronDown : PopupChrome.GlyphChevronRight;
            SourceQuote.Visibility = _sourceExpanded ? Visibility.Visible : Visibility.Collapsed;
            SourceQuote.MaxHeight = _sourceExpanded ? 120 : 54;
            SourceText.MaxHeight = _sourceExpanded ? 120 : 54;
            SourceExpandChevron.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>切换原文引用块展开/折叠（真实点击与测试共用同一入口，保证行为一致）。</summary>
    internal void ToggleSourceExpansion()
    {
        _sourceExpanded = !_sourceExpanded;
        UpdateSourceSection();
        ContentGrew?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>当前引用块展开态；服务层重算窗口尺寸时必须传入估算器（展开区域占高可达 120px）。</summary>
    internal bool IsSourceExpanded => _sourceExpanded;

    private void OnToggleRowClick(object sender, MouseButtonEventArgs e)
    {
        ToggleSourceExpansion();
        e.Handled = true;
    }

    private void OnSourceQuoteClick(object sender, MouseButtonEventArgs e)
    {
        // 详细模式点击引用块本身也触发展开/折叠
        if (_isDetailed)
        {
            _sourceExpanded = !_sourceExpanded;
            UpdateSourceSection();
            ContentGrew?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    public void AppendChunk(string delta)
    {
        ResetStyle();
        _fullText.Append(delta);
        _isStreaming = true;
        _isError = false;
        if (_isDetailed)
        {
            // 详细模式流式信号为 meta 行，无需光标；更新 meta 为流式中
            MetaRow.Visibility = Visibility.Visible;
            PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.AccentBlue, "翻译中…");
        }
        RenderTranslation();
    }

    private void RenderTranslation()
    {
        // 错误态已在 ShowError 中直接设置 Text，不在此重绘
        if (_isError)
        {
            return;
        }

        TranslationText.Foreground = PrimaryTextBrush;
        var formatted = TranslationDisplayFormatter.ForBlock(_fullText.ToString());

        if (string.IsNullOrEmpty(formatted))
        {
            TranslationText.Inlines.Clear();
            TranslationText.Text = string.Empty;
            if (_isStreaming && !_isDetailed)
            {
                // 简洁流式且尚无译文时仅显示光标
                TranslationText.Inlines.Clear();
                TranslationText.Inlines.Add(new Run(PopupChrome.StreamingCursor) { Foreground = PopupChrome.AccentBlue });
            }
            AutoScrollToEnd();
            ContentGrew?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isDetailed)
        {
            // 详细：无光标，直接文本
            TranslationText.Inlines.Clear();
            TranslationText.Text = formatted;
        }
        else
        {
            // 简洁：流式时追加蓝色光标
            if (_isStreaming)
            {
                TranslationText.Inlines.Clear();
                TranslationText.Inlines.Add(new Run(formatted));
                TranslationText.Inlines.Add(new Run(PopupChrome.StreamingCursor) { Foreground = PopupChrome.AccentBlue });
            }
            else
            {
                TranslationText.Inlines.Clear();
                TranslationText.Text = formatted;
            }
        }

        AutoScrollToEnd();
        ContentGrew?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>流式结束：移除光标并翻转 meta 为完成态。</summary>
    public void MarkStreamCompleted()
    {
        if (!_isStreaming && !_isError)
        {
            // 已是完成态，幂等
            return;
        }

        _isStreaming = false;
        _isError = false;
        UpdateMeta();
        RenderTranslation();
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
        _sourcePreviewText = sourceText;
        _fullText.Clear();
        _isStreaming = true;
        _isError = false;
        _sourceExpanded = false;
        TranslationText.Inlines.Clear();
        TranslationText.Text = string.Empty;
        TranslationText.Foreground = PrimaryTextBrush;
        // 原文预览经格式化（压缩 OCR 多余空格）后展示
        var preview = TranslationDisplayFormatter.ForSourcePreview(sourceText);
        SourceText.Text = preview;
        SourceText.MaxHeight = 54;
        SourceQuote.MaxHeight = 54;
        // 重建模式相关视觉：meta、引用块、译文样式
        UpdateMeta();
        UpdateSourceSection();
        RenderTranslation();
        ResetStyle();
        // 清理复制反馈态
        PopupChrome.ApplyCopyFeedback(CopyButton, _isDetailed, false);
    }

    public void UpdateHeader(int lineCount)
    {
        _lineCount = lineCount;
        // WHY：0 行时不显示"· 0 行"，避免选中文本探测命中但无行信息时的误导性文案
        if (ToggleLabel != null)
        {
            ToggleLabel.Text = lineCount > 0 ? $"原文 ({_lineCount} 行)" : "原文";
        }
        // 兼容 spec 中的 BlockHeader 命名：若存在 BlockHeader 控件则同步更新
        var blockHeader = FindName("BlockHeader") as System.Windows.Controls.TextBlock;
        if (blockHeader != null)
        {
            blockHeader.Text = lineCount > 0 ? $"块模式 · {lineCount} 行" : "块模式";
        }
        UpdateSourceSection();
    }

    public void ShowError(string shortMessage)
    {
        _fullText.Clear();
        _isStreaming = false;
        _isError = true;
        TranslationText.Inlines.Clear();
        TranslationText.Text = shortMessage;
        TranslationText.Foreground = ErrorTextBrush;
        // 错误态 meta：详细红点，简洁无 meta 行但需移除光标
        if (_isDetailed)
        {
            MetaRow.Visibility = Visibility.Visible;
            PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.DangerRed, "翻译失败");
        }
        else
        {
            MetaRow.Visibility = Visibility.Collapsed;
        }
        // 错误时若无源文则隐藏引用区；若有源文保持当前展开态但标记为错误
        UpdateSourceSection();
    }

    public void ResetStyle()
    {
        // 仅在非错误态时恢复主文本色，避免覆盖 ShowError 的红字
        if (!_isError)
        {
            TranslationText.Foreground = PrimaryTextBrush;
        }
        // 保持 MetaRow 可见性由 UpdateMeta 控制，此处不强制改动
    }

    /// <summary>带淡出退场的隐藏（自动隐藏/服务收起链路统一入口）。</summary>
    public void HideWithFade()
    {
        // 同 Word 弹窗：退场即通知，选区不等自动隐藏
        Dismissed?.Invoke(this, EventArgs.Empty);
        PopupTransition.PlayExit(this, RootBorder);
    }

    /// <summary>弹窗开始退场（关闭按钮/超时/服务收起）：订阅方联动隐藏选区框。</summary>
    public event EventHandler? Dismissed;

    /// <summary>窗口已可见时被复用（新译文顶替）：取消进行中的退场动画并复播进场。</summary>
    public void ReplayEntry() => PopupTransition.PlayEntry(RootBorder);

    private void OnCopyButtonClick(object sender, RoutedEventArgs e)
    {
        bool copied = false;
        try
        {
            // WHY：WYSIWYG 复制——展示层格式化后的译文为准，避免原始 markdown 残留
            var formatted = TranslationDisplayFormatter.ForBlock(FullText);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                System.Windows.Clipboard.SetText(formatted);
                copied = true;
            }
        }
        catch
        {
        }

        if (copied)
        {
            ShowCopyFeedback();
        }
    }

    /// <summary>复制成功微反馈：通过 PopupChrome 按模式切换（详细文字/简洁绿勾）1.2 秒后还原，内容切换带 100ms 淡入。</summary>
    private void ShowCopyFeedback()
    {
        _copyFeedbackTimer?.Stop();

        bool detailed = _isDetailed;
        PopupChrome.ApplyCopyFeedback(CopyButton, detailed, true);
        PlayFeedbackFade(CopyButton);

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer!.Stop();
            PopupChrome.ApplyCopyFeedback(CopyButton, detailed, false);
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
            // 优先朗读展示一致的格式化译文
            var formatted = TranslationDisplayFormatter.ForBlock(FullText);
            var text = !string.IsNullOrWhiteSpace(formatted) ? formatted : FullText;
            if (!string.IsNullOrWhiteSpace(text) && _textToSpeech != null)
            {
                _textToSpeech.Speak(text, _speechLanguage);
            }
        }
        catch
        {
        }
    }
}
