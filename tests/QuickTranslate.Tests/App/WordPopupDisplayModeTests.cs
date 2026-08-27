using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using QuickTranslate.Core.Geometry;
using QuickTranslate.Core.Selection;
using QuickTranslate.Core.Translation;
using QuickTranslate.App.Windows;
using Xunit;

namespace QuickTranslate.Tests.App;

public class WordPopupDisplayModeTests
{
    private static SelectionResult MakeSel(string text = "hello") => new(
        Text: text,
        ContextLine: text,
        Box: new PhysicalRect(0, 0, 10, 10),
        Kind: SelectionKind.Word,
        Confidence: 0.9f,
        OperationId: Guid.NewGuid(),
        NoTextFound: false);

    private static TranslationResult MakeTrans(bool fromDict = false, bool fromCache = false, string target = "你好") => new(
        NormalizedKey: "hello||zh-cn",
        SourceText: "hello",
        TargetText: target,
        TargetLanguage: "zh-CN",
        FromCache: fromCache,
        FromDictionary: fromDict,
        NeedsOnline: false);

    private static void RunSta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { a(); } catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException($"STA thread exception: {error.Message}", error);
    }

    [Fact]
    public void Detailed_Dictionary_ShowsGreenDotAndWordLabel()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            w.ApplyDisplayMode(true);
            w.ApplyContent(MakeSel("hello"), MakeTrans(fromDict: true));

            var metaRow = (StackPanel)w.FindName("MetaRow");
            var dot = (Ellipse)w.FindName("MetaDot");
            var txt = (TextBlock)w.FindName("MetaText");
            Assert.NotNull(metaRow);
            Assert.NotNull(dot);
            Assert.NotNull(txt);
            Assert.Equal(Visibility.Visible, metaRow!.Visibility);
            var fill = (SolidColorBrush)dot!.Fill;
            Assert.Equal(Color.FromRgb(0x34, 0xC7, 0x59), fill.Color);
            Assert.Equal("词典", txt!.Text);
            // footer should be text mode
            var copy = (Button)w.FindName("CopyButton");
            Assert.Equal("复制", copy!.Content as string);
            w.Close();
        });
    }

    [Fact]
    public void Detailed_Cache_ShowsBlueDotAndCacheLabel()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            w.ApplyDisplayMode(true);
            w.ApplyContent(MakeSel(), MakeTrans(fromDict: false, fromCache: true));
            var metaRow = (StackPanel)w.FindName("MetaRow");
            var dot = (Ellipse)w.FindName("MetaDot");
            var txt = (TextBlock)w.FindName("MetaText");
            Assert.Equal(Visibility.Visible, metaRow!.Visibility);
            var fill = (SolidColorBrush)dot!.Fill;
            Assert.Equal(Color.FromRgb(0x00, 0x7A, 0xFF), fill.Color);
            Assert.Equal("缓存", txt!.Text);
            w.Close();
        });
    }

    [Fact]
    public void Detailed_Online_ShowsGrayDotAndOnlineLabel()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            w.ApplyDisplayMode(true);
            w.ApplyContent(MakeSel(), MakeTrans(fromDict: false, fromCache: false));
            var metaRow = (StackPanel)w.FindName("MetaRow");
            var dot = (Ellipse)w.FindName("MetaDot");
            var txt = (TextBlock)w.FindName("MetaText");
            Assert.Equal(Visibility.Visible, metaRow!.Visibility);
            var fill = (SolidColorBrush)dot!.Fill;
            Assert.Equal(Color.FromRgb(0x8E, 0x8E, 0x93), fill.Color);
            Assert.Equal("在线", txt!.Text);
            w.Close();
        });
    }

    [Fact]
    public void Compact_HidesMetaRow_AndUsesIconButtons()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            w.ApplyDisplayMode(false);
            w.ApplyContent(MakeSel(), MakeTrans(fromDict: true));
            var metaRow = (StackPanel)w.FindName("MetaRow");
            Assert.Equal(Visibility.Collapsed, metaRow!.Visibility);
            var copy = (Button)w.FindName("CopyButton");
            // compact copy button should be TextBlock with glyph
            var tb = copy!.Content as TextBlock;
            Assert.NotNull(tb);
            Assert.Equal("\uE8C8", tb!.Text);
            w.Close();
        });
    }

    [Fact]
    public void Compact_MetaHiddenEvenWhenCache()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            w.ApplyDisplayMode(false);
            w.ApplyContent(MakeSel(), MakeTrans(fromDict: false, fromCache: true));
            var metaRow = (StackPanel)w.FindName("MetaRow");
            Assert.Equal(Visibility.Collapsed, metaRow!.Visibility);
            w.Close();
        });
    }

    [Fact]
    public void ShowError_HidesMetaRow()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            w.ApplyDisplayMode(true);
            w.ApplyContent(MakeSel(), MakeTrans(fromDict: true));
            w.ShowError("网络错误");
            var metaRow = (StackPanel)w.FindName("MetaRow");
            Assert.Equal(Visibility.Collapsed, metaRow!.Visibility);
            w.Close();
        });
    }

    [Fact]
    public void ResetStyle_HidesMetaRow()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            w.ApplyDisplayMode(true);
            w.ApplyContent(MakeSel(), MakeTrans(fromDict: true));
            w.ResetStyle();
            var metaRow = (StackPanel)w.FindName("MetaRow");
            Assert.Equal(Visibility.Collapsed, metaRow!.Visibility);
            w.Close();
        });
    }

    [Fact]
    public void OldBadges_Removed()
    {
        RunSta(() =>
        {
            var w = new WordPopupWindow();
            // old badges must not exist
            Assert.Null(w.FindName("DictionaryBadge"));
            Assert.Null(w.FindName("CacheBadge"));
            w.Close();
        });
    }
}
