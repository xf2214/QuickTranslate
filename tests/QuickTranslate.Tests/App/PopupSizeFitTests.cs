using System.Threading;
using System.Windows;
using System.Windows.Controls;
using QuickTranslate.App.Services;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.App;

/// <summary>
/// 回归锁：块/词弹窗的窗口高度必须 ≥ 内容自然高度，否则 Grid 末行（按钮区）被裁切。
/// 背景：简洁模式展开"原文"引用块时 PopupSizeEstimator 未计入源文区域高度，
/// 导致底部按钮被推出窗口外（实测 gap=-35px）。
/// </summary>
public class PopupSizeFitTests
{
    private const string SampleSource =
        "Google's search engine rose rapidly in the late 1990s,\nbecoming the most popular tool in the world.";
    private const string SampleTranslation = "谷歌的搜索引擎在九十年代后期迅速崛起，成为全球最受欢迎的工具。";

    public static IEnumerable<object[]> BlockModeData()
    {
        yield return new object[] { true, false };
        yield return new object[] { true, true };
        yield return new object[] { false, false };
        yield return new object[] { false, true };
    }

    [Theory]
    [MemberData(nameof(BlockModeData))]
    public void BlockPopup_EstimatedHeight_MustCoverNaturalHeight(bool detailed, bool expanded)
    {
        bool passed = false;
        Exception? error = null;
        double natural = -1, estimated = -1;

        var thread = new Thread(() =>
        {
            try
            {
                var window = new BlockPopupWindow();
                window.ApplyDisplayMode(detailed);
                window.ResetContent(SampleSource);
                window.AppendChunk(SampleTranslation);
                window.UpdateHeader(2);
                window.MarkStreamCompleted();
                if (expanded)
                {
                    window.ToggleSourceExpansion();
                }

                var (estW, estH) = PopupSizeEstimator.EstimateBlockPopupSize(
                    SampleSource, SampleTranslation, 1920, 1080, detailed, expanded);
                estimated = estH;

                var border = (Border)window.Content;
                border.Width = estW;
                border.Measure(new Size(estW, double.PositiveInfinity));
                natural = border.DesiredSize.Height;

                passed = estimated >= natural;
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
            throw error;
        }

        Assert.True(passed,
            $"detailed={detailed} expanded={expanded}: 估算高度 {estimated} < 自然高度 {natural}（底部按钮区会被裁切）");
    }

    [Fact]
    public void WordPopup_LongWordWithPhonetic_EstimatedHeight_MustCoverNaturalHeight()
    {
        bool passed = false;
        Exception? error = null;
        double natural = -1, estimated = -1;

        var thread = new Thread(() =>
        {
            try
            {
                const string word = "significantly";
                var sel = new SelectionResult(
                    Text: word,
                    ContextLine: word,
                    Box: new PhysicalRect(0, 0, 100, 24),
                    Kind: SelectionKind.Word,
                    Confidence: 0.9f,
                    OperationId: Guid.NewGuid());
                var trans = new TranslationResult(
                    "significantly||zh-cn", word,
                    "[sɪɡˈnɪfɪkəntli]  adv. 意味深长地；显著地",
                    "zh-CN", FromCache: false, FromDictionary: true);

                var window = new WordPopupWindow();
                window.ApplyDisplayMode(true);
                window.ApplyContent(sel, trans);

                // 与服务层一致：词头估算需包含音标后缀（词典命中时展示在词头行）
                var displayText = TranslationDisplayFormatter.ForWord(trans.TargetText, trans.FromDictionary);
                var headerForEstimate = BuildHeaderForEstimate(sel.Text!, displayText, trans.FromDictionary);
                var (estW, estH) = PopupSizeEstimator.EstimateWordPopupSize(
                    headerForEstimate, displayText, 1920, 1080);
                estimated = estH;

                var border = (Border)window.Content;
                border.Width = estW;
                border.Measure(new Size(estW, double.PositiveInfinity));
                natural = border.DesiredSize.Height;

                passed = estimated >= natural;
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
            throw error;
        }

        Assert.True(passed,
            $"词头含音标时估算高度 {estimated} < 自然高度 {natural}");
    }

    /// <summary>与服务层将采用的同一规则：词典命中的音标行并入词头估宽文本。</summary>
    private static string BuildHeaderForEstimate(string word, string displayText, bool fromDictionary)
    {
        if (!fromDictionary)
        {
            return word;
        }

        var nl = displayText.IndexOf('\n');
        return nl > 0 ? $"{word}  {displayText[..nl].Trim()}" : $"{word}  {displayText}";
    }
}
