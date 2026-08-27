using System.Globalization;
using System.Threading;
using QuickTranslate.App.Services;
using QuickTranslate.Core.Selection;
using Xunit;

namespace QuickTranslate.Tests.App;

/// <summary>
/// 弹窗"按钮显示不全"回归：估算器给出的高度必须 ≥ 真实渲染所需高度。
/// 用 STA 线程实例化真实窗口、灌入内容后测量 RootBorder 的期望高度，
/// 与 EstimateBlockPopupSize 的输出对比——估算偏低即为裁切根因的量化证据。
/// </summary>
public class PopupFitRegressionTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var passed = false;
        var thread = new Thread(() =>
        {
            try
            {
                action();
                passed = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
        }
        Assert.True(passed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void BlockPopup_EstimatedHeight_CoversRealRenderedHeight(bool detailed, bool sourceExpanded)
    {
        RunOnSta(() =>
        {
            var window = new QuickTranslate.App.Windows.BlockPopupWindow();
            try
            {
                const double widthDip = 440;
                window.ApplyDisplayMode(detailed);
                window.ResetContent("The quick brown fox jumps over the lazy dog. " +
                                    "Pack my box with five dozen liquor jugs.");
                if (sourceExpanded)
                {
                    window.ToggleSourceExpansion();
                }
                var translation =
                    "这是一段用于回归验证的中文译文，长度足够触发多行折行，" +
                    "用来检验估算器的行高常数是否与 XAML 实际值同步。\n" +
                    "Second paragraph with some english words to mix scripts.\n" +
                    "第三段落，继续填充行数直到接近滚动上限之前的自然高度。";
                window.AppendChunk(translation);
                window.MarkStreamCompleted();

                // 以固定宽度做两遍测量（先给约束让 TextBlock 完成一次排版，再取期望尺寸）
                window.RootBorder.Measure(new System.Windows.Size(widthDip, double.PositiveInfinity));
                window.RootBorder.Measure(new System.Windows.Size(widthDip, double.PositiveInfinity));
                double desiredHeight = window.RootBorder.DesiredSize.Height;

                var (estW, estH) = PopupSizeEstimator.EstimateBlockPopupSize(
                    "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs.",
                    translation,
                    1920, 1080,
                    detailed, sourceExpanded);

                // 允许 2px 的测量/取整噪声；低于此即视为会裁掉底部按钮区
                Assert.True(
                    estH >= desiredHeight - 2,
                    $"[detailed={detailed} expanded={sourceExpanded}] 估算高度 {estH} < 真实渲染需求 {desiredHeight:0.##}（差 {desiredHeight - estH:0.##}px），底部按钮将被推出窗口");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WordPopup_MetaAllowance_CoversRealRenderedHeight(bool detailed)
    {
        RunOnSta(() =>
        {
            var window = new QuickTranslate.App.Windows.WordPopupWindow();
            try
            {
                window.ApplyDisplayMode(detailed);
                var sel = new QuickTranslate.Core.Selection.SelectionResult(
                    "significant", null,
                    new QuickTranslate.Core.Geometry.PhysicalRect(100, 100, 120, 30),
                    QuickTranslate.Core.Selection.SelectionKind.Word, 0.99f, Guid.NewGuid());
                var trans = new QuickTranslate.Core.Translation.TranslationResult(
                    "significant", "significant",
                    "adj. 重要的；有意义的\nadv. 显著地\n值得注意的释义补充以触发多行折行效果",
                    "zh-CN", false, false, false);
                window.Width = 340;
                window.ApplyContent(sel, trans);

                double desired = window.MeasureDesiredContentHeight();
                var (estW, estH) = PopupSizeEstimator.EstimateWordPopupSize(
                    "significant", trans.TargetText, 1920, 1080,
                    metaVisible: detailed);

                // 详细模式必须计入元信息行余量：估算高度 ≥ 真实渲染需求（2px 噪声容差）
                Assert.True(
                    estH >= desired - 2,
                    $"[detailed={detailed}] 词弹窗估算 {estH} < 真实需求 {desired:0.##}（差 {desired - estH:0.##}px），底部按钮将被裁切");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
