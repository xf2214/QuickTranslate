using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;

namespace QuickTranslate.Platform.Automation;

/// <summary>
/// 基于 UI Automation TextPattern 的选中文本探测。
/// 浏览器/编辑器/Office 等支持文本模式的控件可零 OCR 误差取词；
/// 探测核心在 MTA 线程池线程运行，总预算 ProbeBudgetMs（WaitAsync 包裹），
/// 超时/异常一律返回 null 由调用方回退 OCR——绝不让 hung 应用拖住热键管线。
/// 已知局限：WaitAsync 仅放弃等待，超时后内核线程仍驻留至 UIA 跨进程调用返回
/// （目标应用 hung 时最坏数秒），期间占一个线程池线程；UIA 不支持取消，
/// 根治需 STA+CoWaitable 方案，热键低频场景可接受（见安全评审 #3）。
/// </summary>
public sealed class UiAutomationSelectedTextProbe : ISelectedTextProbe
{
    // 单次探测允许的最大行矩形数：正常选区远低于此值；恶意/异常提供者可返回
    // 无界矩形集，截断防内存/CPU 放大（超出仅记 Debug 计数，不记内容）
    private const int MaxLineRects = 256;

    private readonly ILogger<UiAutomationSelectedTextProbe> _logger;

    public UiAutomationSelectedTextProbe(ILogger<UiAutomationSelectedTextProbe>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UiAutomationSelectedTextProbe>.Instance;
    }

    public async Task<SelectedTextProbeResult?> ProbeAsync(PhysicalPoint cursor, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => ProbeCore(cursor), ct)
                .WaitAsync(TimeSpan.FromMilliseconds(SelectedTextProbePolicy.ProbeBudgetMs), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 槽位取消语义保持：调用方按取消处理
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("[UiAProbe] Budget exceeded ({BudgetMs}ms), fall back to OCR", SelectedTextProbePolicy.ProbeBudgetMs);
            return null;
        }
        catch (Exception ex)
        {
            // 隐私红线：只记异常类型与错误码，绝不记录探测到的文本内容
            _logger.LogDebug(ex, "[UiAProbe] Probe failed [ErrorCode=UIA_PROBE_FAIL] {ExType}", ex.GetType().Name);
            return null;
        }
    }

    private SelectedTextProbeResult? ProbeCore(PhysicalPoint cursor)
    {
        // FromPoint 接收物理像素屏幕坐标（与截屏链路同一坐标域）
        var element = AutomationElement.FromPoint(new System.Windows.Point(cursor.X, cursor.Y));
        if (element is null) return null;
        // 托管 UIA 的 TryGetCurrentPattern 是 out 参数形式，需先取再模式匹配
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) ||
            patternObj is not TextPattern textPattern)
        {
            return null;
        }

        var selections = textPattern.GetSelection();
        if (selections is null || selections.Length == 0) return null;

        var range = selections[0];
        // 长度截断在源头做：GetText(maxLength+1)，超限交由策略层 IsAdoptable 拒绝，
        // 避免用户全选整篇文档时无界取文本
        string raw = range.GetText(SelectedTextProbePolicy.BlockModeMaxChars + 1);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var lineRects = new List<PhysicalRect>();
        bool truncated = false;
        foreach (System.Windows.Rect r in range.GetBoundingRectangles())
        {
            if (lineRects.Count >= MaxLineRects)
            {
                truncated = true;
                break;
            }
            if (double.IsInfinity(r.X) || double.IsInfinity(r.Y)) continue;
            if (r.Width <= 0 || r.Height <= 0) continue;
            lineRects.Add(new PhysicalRect(
                (int)Math.Round(r.X), (int)Math.Round(r.Y),
                (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height)));
        }
        if (truncated)
        {
            _logger.LogDebug("[UiAProbe] Line rects truncated at {Max} [ErrorCode=UIA_RECTS_TRUNCATED]", MaxLineRects);
        }

        var union = SelectedTextProbePolicy.UnionOrFallback(lineRects, cursor);
        return new SelectedTextProbeResult(raw, lineRects, union, SelectedTextSource.UiAutomation);
    }
}
