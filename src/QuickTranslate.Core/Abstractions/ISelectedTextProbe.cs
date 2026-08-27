using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

/// <summary>选中文本的来源。当前仅 UI Automation；剪贴板回退为未来扩展预留。</summary>
public enum SelectedTextSource
{
    UiAutomation = 0,
}

/// <summary>
/// 选中文本探测结果：文本 + 屏幕物理像素矩形（每行一个）。
/// 坐标与截屏链路同一物理像素域，可直接喂给 Overlay/Popup 定位。
/// </summary>
public sealed record SelectedTextProbeResult(
    string Text,
    IReadOnlyList<PhysicalRect> LineRects,
    PhysicalRect UnionBox,
    SelectedTextSource Source);

/// <summary>
/// OCR 前的"软选区"预探测：光标处若存在可选中文本（浏览器/编辑器等），
/// 直接取精确文本跳过 OCR。实现方必须自带预算控制，任何失败/超时返回 null，
/// 调用方静默回退既有 OCR 链路。
/// </summary>
public interface ISelectedTextProbe
{
    /// <summary>预算内探测光标处的选中文本；未命中/超时/异常一律返回 null。</summary>
    Task<SelectedTextProbeResult?> ProbeAsync(PhysicalPoint cursor, CancellationToken ct);
}
