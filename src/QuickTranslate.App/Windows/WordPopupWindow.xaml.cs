using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.Platform.Win32;
using QuickTranslate.TextToSpeech;

namespace QuickTranslate.App.Windows;

public partial class WordPopupWindow : Window, IFadeOutHideable
{
    private static readonly ILogger _logger = LoggingInteractionCoordinatorFactory.CreateLogger<WordPopupWindow>();

    // Apple 风格配色（与 XAML 保持一致）；Freeze 后免去变更跟踪与跨线程检查开销
    private static readonly SolidColorBrush PrimaryTextBrush = Frozen(System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush SecondaryTextBrush = Frozen(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));
    private static readonly SolidColorBrush PosBrush = Frozen(System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly SolidColorBrush ErrorTextBrush = Frozen(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly SolidColorBrush CopiedFeedbackBrush = Frozen(System.Windows.Media.Color.FromRgb(0x34, 0xC7, 0x59));

    private static SolidColorBrush Frozen(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public DipRect LastLayoutDipRect { get; private set; }

    private SelectionResult? _lastSelection;
    private TranslationResult? _lastTranslation;
    // 展示用的格式化译文（音标独立行、释义逐行）；复制按钮与展示保持一致
    private string _displayText = string.Empty;
    private bool _allowActivate;
    private ITextToSpeechService? _textToSpeech;
    private string? _speechLanguage;
    private DispatcherTimer? _copyFeedbackTimer;
    private bool _detailed = true;

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

    public void ApplyDisplayMode(bool detailed)
    {
        _detailed = detailed;
        PopupChrome.ApplyFooterMode(SpeakButton, CopyButton, DismissButton, detailed);
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

        // _displayText 始终是 ForWord 的原始格式化输出（供复制/TTS 使用，不随富文本样式改变）
        _displayText = TranslationDisplayFormatter.ForWord(trans.TargetText, trans.FromDictionary);

        if (trans.FromDictionary)
        {
            // 词典命中：用结构化视图做富文本渲染，头行合并音标，正文按行着色
            var view = DictionaryStructuredView.Parse(trans.TargetText);
            RenderWordHeader(sel.Text ?? string.Empty, view.Phonetic);
            RenderDictionaryBody(view);
        }
        else
        {
            // 非词典：保持原有纯文本呈现（单一前景色），避免样式残留
            RenderPlainHeader(sel.Text ?? string.Empty);
            RenderPlainBody(_displayText);
        }

        if (_detailed)
        {
            MetaRow.Visibility = Visibility.Visible;
            if (trans.FromDictionary)
            {
                PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.SuccessGreen, "词典");
            }
            else if (trans.FromCache)
            {
                PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.AccentBlue, "缓存");
            }
            else
            {
                PopupChrome.SetMeta(MetaDot, MetaText, PopupChrome.SecondaryText, "在线");
            }
        }
        else
        {
            MetaRow.Visibility = Visibility.Collapsed;
        }
    }

    // 头部：单词(18px Bold #1C1C1E) + 音标(13px Regular #8E8E93，斜杠包裹，去括号)
    private void RenderWordHeader(string word, string? phonetic)
    {
        WordHeader.Inlines.Clear();
        // 先清空 Text 属性，避免 Inlines 与 Text 互斥导致的残留
        WordHeader.Text = string.Empty;
        WordHeader.Inlines.Add(new Run(word)
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = PrimaryTextBrush,
        });
        if (!string.IsNullOrWhiteSpace(phonetic))
        {
            WordHeader.Inlines.Add(new Run("  /" + phonetic + "/")
            {
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                Foreground = SecondaryTextBrush,
            });
        }
    }

    private void RenderPlainHeader(string word)
    {
        WordHeader.Inlines.Clear();
        WordHeader.Text = word;
    }

    private void RenderPlainBody(string text)
    {
        TranslationText.Inlines.Clear();
        TranslationText.Text = text;
        TranslationText.Foreground = PrimaryTextBrush;
    }

    // 正文：每行按 序号(11px #8E8E93) + 词性(13px SemiBold #007AFF) + 领域标签(13px #8E8E93) + 释义(14px #1C1C1E)
    private void RenderDictionaryBody(DictionaryStructuredView view)
    {
        TranslationText.Inlines.Clear();
        TranslationText.Text = string.Empty;
        TranslationText.Foreground = PrimaryTextBrush;

        if (view.Lines.Count == 0) return;

        bool shouldNumber = view.ShouldNumberLines;
        int number = 1;
        for (int i = 0; i < view.Lines.Count; i++)
        {
            var line = view.Lines[i];
            if (i > 0) TranslationText.Inlines.Add(new LineBreak());

            if (line.IsTruncationMarker)
            {
                // 截断省略号：灰色，与序号同色，保持紧凑
                TranslationText.Inlines.Add(new Run(line.Body)
                {
                    FontSize = 14,
                    Foreground = SecondaryTextBrush,
                });
                continue;
            }

            if (shouldNumber)
            {
                TranslationText.Inlines.Add(new Run($"{number}. ")
                {
                    FontSize = 11,
                    Foreground = SecondaryTextBrush,
                });
                number++;
            }

            if (!string.IsNullOrEmpty(line.PosTag))
            {
                TranslationText.Inlines.Add(new Run(line.PosTag + " ")
                {
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PosBrush,
                });
            }

            if (!string.IsNullOrEmpty(line.DomainTag))
            {
                TranslationText.Inlines.Add(new Run(line.DomainTag)
                {
                    FontSize = 13,
                    Foreground = SecondaryTextBrush,
                });
                if (!string.IsNullOrEmpty(line.Body))
                {
                    TranslationText.Inlines.Add(new Run(" ")
                    {
                        FontSize = 14,
                        Foreground = PrimaryTextBrush,
                    });
                }
            }

            if (!string.IsNullOrEmpty(line.Body))
            {
                // 领域标签已处理空格，此处直接追加剩余正文
                TranslationText.Inlines.Add(new Run(line.Body)
                {
                    FontSize = 14,
                    Foreground = PrimaryTextBrush,
                });
            }
        }
    }

    public void ShowError(string shortMessage)
    {
        // 清理富文本残留，避免复用时旧 Inlines 串色
        WordHeader.Inlines.Clear();
        WordHeader.Text = string.Empty;
        TranslationText.Inlines.Clear();
        TranslationText.Text = string.Empty;
        TranslationText.Foreground = ErrorTextBrush;
        TranslationText.Text = shortMessage;
        WordHeader.Visibility = Visibility.Collapsed;
        MetaRow.Visibility = Visibility.Collapsed;
    }

    public void ResetStyle()
    {
        // 清理上一次富文本的 Inlines 与颜色，避免复用窗口时样式串台（窗口实例跨多次 Show 复用）
        WordHeader.Inlines.Clear();
        WordHeader.Text = string.Empty;
        TranslationText.Inlines.Clear();
        TranslationText.Text = string.Empty;
        TranslationText.Foreground = PrimaryTextBrush;
        WordHeader.Visibility = Visibility.Visible;
        MetaRow.Visibility = Visibility.Collapsed;
        // 重置复制反馈，避免复用时绿色/已复制状态串台
        _copyFeedbackTimer?.Stop();
        PopupChrome.ApplyCopyFeedback(CopyButton, _detailed, false);
        PlayFeedbackFade(CopyButton);
    }

    /// <summary>带淡出退场的隐藏（自动隐藏/服务收起链路统一入口）。</summary>
    public void HideWithFade()
    {
        // 弹窗消失即联动藏选区：Dismiss/超时/服务收起全走这里，选区不等 3s 自动隐藏
        Dismissed?.Invoke(this, EventArgs.Empty);
        PopupTransition.PlayExit(this, RootBorder);
    }

    /// <summary>弹窗开始退场（关闭按钮/超时/服务收起）：订阅方联动隐藏选区框。</summary>
    public event EventHandler? Dismissed;

    /// <summary>窗口已可见时被复用（新译文顶替）：取消进行中的退场动画并复播进场。</summary>
    public void ReplayEntry() => PopupTransition.PlayEntry(RootBorder);

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
        bool copied = false;
        try
        {
            if (_lastSelection != null && _lastTranslation != null)
            {
                string text = _lastSelection.Text + "\n" + _displayText;
                System.Windows.Clipboard.SetText(text);
                copied = true;
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

        if (copied)
        {
            ShowCopyFeedback();
        }
    }

    /// <summary>复制成功微反馈：详细模式按钮文字变「已复制 ✓」，简洁模式图标变绿色对勾，1.2 秒后还原，内容切换带 100ms 淡入。</summary>
    private void ShowCopyFeedback()
    {
        _copyFeedbackTimer?.Stop();

        PopupChrome.ApplyCopyFeedback(CopyButton, _detailed, true);
        PlayFeedbackFade(CopyButton);

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer!.Stop();
            PopupChrome.ApplyCopyFeedback(CopyButton, _detailed, false);
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
        _allowActivate = true;
        try
        {
            HideWithFade();
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

    /// <summary>
    /// 以当前窗口宽度对内容做无限高测量，返回真实所需内容高度（DIP）。
    /// 服务层在 ApplyContent 之后调用：用真实测量校正估算高度，
    /// 兜住结构化词典视图/元信息行等动态布局与静态常数的偏差（防底部按钮裁切的最终防线）。
    /// </summary>
    public double MeasureDesiredContentHeight()
    {
        double width = double.IsFinite(ActualWidth) && ActualWidth > 0 ? ActualWidth : Width;
        RootBorder.UpdateLayout();
        RootBorder.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        return RootBorder.DesiredSize.Height;
    }

    private void OnSpeakButtonClick(object sender, RoutedEventArgs e)
    {
        _allowActivate = true;
        try
        {
            // 优先朗读与展示一致的格式化译文，展示为空时退回原始译文/选中文本
            var text = !string.IsNullOrWhiteSpace(_displayText)
                ? _displayText
                : (_lastTranslation?.TargetText ?? WordHeader.Text);
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
