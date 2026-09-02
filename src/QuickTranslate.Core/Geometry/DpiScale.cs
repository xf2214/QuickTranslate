namespace QuickTranslate.Core.Geometry;

/// <summary>
/// 96-DPI 基准物理像素 → 目标 DPI 物理像素换算。
/// 代码库中大量几何常量（估算行高、初始截图尺寸、触边裕度等）按 96-DPI 物理像素标定；
/// 在 144/192 DPI（150%/200% 缩放）屏上同逻辑大小的文字物理尺寸放大 1.5~2 倍，
/// 不缩放会导致首捕范围与 OCR 焦点带偏小、触发截边重抓与选区不准。
/// dpi=96 时恒等，96-DPI 屏行为零变化。
/// </summary>
public static class DpiScale
{
    /// <summary>96-DPI 基准物理像素值换算为当前 DPI 的物理像素（四舍五入远离零）。</summary>
    public static int Px(int px96, uint dpi)
    {
        double effective = dpi == 0 ? 96.0 : dpi;
        return (int)Math.Round(px96 * effective / 96.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>当前 DPI 相对 96-DPI 的缩放系数（dpi=0 视为 96，系数 1.0）。</summary>
    public static double Factor(uint dpi)
    {
        return (dpi == 0 ? 96.0 : dpi) / 96.0;
    }
}
