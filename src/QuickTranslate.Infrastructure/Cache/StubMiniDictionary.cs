using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Cache;

public class StubMiniDictionary : ILocalDictionary
{
    private static readonly Dictionary<string, string> _entries = new()
    {
        ["quick"] = "快速的",
        ["translate"] = "翻译",
        ["hello"] = "你好",
        ["world"] = "世界",
        ["focus"] = "焦点",
        ["book"] = "书",
        ["computer"] = "电脑",
        ["language"] = "语言",
        ["window"] = "窗户",
        ["mouse"] = "鼠标",
        ["keyboard"] = "键盘",
        ["screen"] = "屏幕",
        ["memory"] = "记忆",
        ["cache"] = "缓存",
        ["result"] = "结果",
        ["source"] = "来源",
        ["target"] = "目标",
        ["button"] = "按钮",
        ["click"] = "点击",
        ["open"] = "打开",
        ["close"] = "关闭",
        ["copy"] = "复制",
        ["paste"] = "粘贴",
        ["cut"] = "剪切",
        ["save"] = "保存",
        ["delete"] = "删除",
        ["search"] = "搜索",
        ["find"] = "找到",
        ["replace"] = "替换",
        ["file"] = "文件",
        ["folder"] = "文件夹",
        ["document"] = "文档",
        ["application"] = "应用程序",
        ["system"] = "系统",
        ["network"] = "网络",
        ["server"] = "服务器",
        ["client"] = "客户端",
        ["database"] = "数据库",
        ["image"] = "图片",
        ["video"] = "视频",
        ["audio"] = "音频",
        ["text"] = "文本",
        ["number"] = "数字",
        ["word"] = "单词",
        ["sentence"] = "句子",
        ["paragraph"] = "段落",
        ["page"] = "页面",
        ["line"] = "线条",
        ["point"] = "点",
        ["area"] = "区域"
    };

    public int Count => _entries.Count;

    public bool TryLookup(string word, string targetLang, out TranslationResult result)
    {
        var key = word?.Trim().ToLowerInvariant() ?? string.Empty;
        if (_entries.TryGetValue(key, out var translation))
        {
            var normalizedKey = $"{key}||{targetLang?.Trim().ToLowerInvariant() ?? string.Empty}";
            result = new TranslationResult(
                NormalizedKey: normalizedKey,
                SourceText: word ?? string.Empty,
                TargetText: translation,
                TargetLanguage: targetLang,
                FromCache: false,
                FromDictionary: true,
                NeedsOnline: false);
            return true;
        }

        result = default!;
        return false;
    }
}
