using QuickTranslate.Core.Geometry;

namespace QuickTranslate.Core.Abstractions;

/// <summary>
/// 状态浮层（"正在识别… / 正在翻译…"加载指示器）。
/// 协调器在 OCR / 翻译等耗时阶段调用 Show/Update 给予即时视觉反馈，
/// 结束（成功/失败/取消）时必须调用 Hide。
/// </summary>
public interface IStatusIndicatorService
{
    void Show(MonitorId monitorId, PhysicalRect anchorBox, uint dpiX, uint dpiY, string message);
    void Update(string message);
    void Hide();
}
