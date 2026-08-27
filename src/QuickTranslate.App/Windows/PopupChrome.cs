using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

// WHY：本项目 ImplicitUsings + UseWindowsForms 会全局引入 System.Windows.Forms 与
// System.Drawing，多个常用类型名二义；显式别名统一锁定 WPF/Media 类型
// （现有窗口文件因未显式 import 对应命名空间而未触发，新文件必须用别名）。
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace QuickTranslate.App.Windows;

/// <summary>
/// 词/块弹窗共享的外观组件：设计令牌（冻结画刷）、图标字形常量、元信息行更新、页脚模式切换。
/// WHY：「简洁 / 详细」两种显示模式在 Word 与 Block 两个弹窗间复用同一套视觉规格，
/// 集中在此避免颜色/字号/字形在多处硬编码漂移；只含展示层常量与纯 UI 组装，不依赖设置/服务层。
/// </summary>
internal static class PopupChrome
{
    // Segoe Fluent Icons（Win11）回退 Segoe MDL2 Assets（Win10），复合字体名保证两代系统可用
    public const string IconFontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";
    public const string GlyphSpeaker = "\uE767";
    public const string GlyphCopy = "\uE8C8";
    public const string GlyphClose = "\uE711";
    public const string GlyphCheck = "\uE73E";
    public const string GlyphChevronRight = "\uE76C";
    public const string GlyphChevronDown = "\uE70D";

    /// <summary>流式出字期间的尾随光标字符。</summary>
    public const string StreamingCursor = "▍";

    public static readonly SolidColorBrush PrimaryText = Frozen(0xFF1C1C1E);
    public static readonly SolidColorBrush SecondaryText = Frozen(0xFF8E8E93);
    public static readonly SolidColorBrush SecondaryTextDark = Frozen(0xFF6D6D72);
    public static readonly SolidColorBrush AccentBlue = Frozen(0xFF007AFF);
    public static readonly SolidColorBrush SuccessGreen = Frozen(0xFF34C759);
    public static readonly SolidColorBrush DangerRed = Frozen(0xFFFF3B30);
    public static readonly SolidColorBrush QuoteBackground = Frozen(0xFFF5F5F7);
    public static readonly SolidColorBrush Hairline = Frozen(0x1A000000);

    private static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 页脚按钮在两种模式间切换：详细=文字胶囊（朗读/复制/关闭），简洁=图标胶囊。
    /// WHY：按钮仍由各窗口 XAML 声明（保留 x:Name 与事件绑定），这里只换样式与内容，
    /// 避免动态构建可视树破坏测试中脱离 App 资源实例化窗口的惯例。
    /// </summary>
    public static void ApplyFooterMode(Button speak, Button copy, Button close, bool detailed)
    {
        if (detailed)
        {
            SetButton(speak, "PopupActionButton", "朗读", iconGlyph: null);
            SetButton(copy, "PopupActionButton", "复制", iconGlyph: null);
            SetButton(close, "PopupActionButton", "关闭", iconGlyph: null);
        }
        else
        {
            SetButton(speak, "PopupIconActionButton", null, GlyphSpeaker);
            SetButton(copy, "PopupIconActionButton", null, GlyphCopy);
            SetButton(close, "PopupIconActionButton", null, GlyphClose);
        }
    }

    private static void SetButton(Button button, string styleKey, string? text, string? iconGlyph)
    {
        button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        if (iconGlyph != null)
        {
            button.Content = new TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily(IconFontFamily),
                FontSize = 12,
                Foreground = PrimaryText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            button.Content = text;
        }
    }

    /// <summary>复制成功微反馈：详细模式按钮文字变「已复制 ✓」，简洁模式图标变绿色对勾。</summary>
    public static void ApplyCopyFeedback(Button copy, bool detailed, bool copied)
    {
        if (detailed)
        {
            copy.Content = copied ? "已复制 ✓" : "复制";
        }
        else
        {
            copy.Content = new TextBlock
            {
                Text = copied ? GlyphCheck : GlyphCopy,
                FontFamily = new FontFamily(IconFontFamily),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        if (copied)
        {
            copy.Foreground = SuccessGreen;
        }
        else
        {
            copy.ClearValue(Control.ForegroundProperty);
        }
    }

    /// <summary>更新元信息行（详细模式）：彩色圆点 + 说明文字；圆点/文字由调用方传入引用。</summary>
    public static void SetMeta(Ellipse dot, TextBlock text, SolidColorBrush dotColor, string message)
    {
        dot.Fill = dotColor;
        text.Text = message;
    }
}
