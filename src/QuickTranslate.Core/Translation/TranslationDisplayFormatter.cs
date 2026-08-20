using System.Text;
using System.Text.RegularExpressions;

namespace QuickTranslate.Core.Translation;

/// <summary>
/// 译文显示格式化：只在展示层使用，不改动缓存/词典存储的 TargetText（旧缓存数据同样受益）。
/// 背景：
///   - ECDICT 的 translation 字段用字面量 "\n"（反斜杠+n 两个字符）分隔词性释义，
///     词典结果又拼成 "[音标]  释义" 前缀，直接展示会让弹窗里出现成串的 \n；
///   - LLM 句子译文可能带首尾空白、\r\n 或连续多余换行。
/// </summary>
public static class TranslationDisplayFormatter
{
    // 词典结果的音标前缀：[音标] + 至少两个空格（EcdictLiteDictionary.BuildResult 的拼接格式）。
    // 释义行首也可能出现 [计]/[医] 等领域标签，但它们后面不会跟两个以上空格，不会误匹配。
    private static readonly Regex PhoneticPrefix = new(
        @"^\[[^\[\]\n]+\]\s{2,}", RegexOptions.Compiled);

    // 词弹窗释义行数上限：多义词（如 set/run）释义很长，超出部分省略，弹窗内保持紧凑
    private const int MaxWordDefinitionLines = 8;

    /// <summary>
    /// 单词译文展示：还原 \n 为真实换行；词典结果把音标拆成独立行、释义逐行排列并限行。
    /// </summary>
    public static string ForWord(string? targetText, bool fromDictionary)
    {
        var text = NormalizeNewlines(targetText);
        if (text.Length == 0) return string.Empty;

        if (!fromDictionary)
        {
            return string.Join('\n', SplitCleanLines(text));
        }

        string? phonetic = null;
        var match = PhoneticPrefix.Match(text);
        if (match.Success)
        {
            var closeIdx = text.IndexOf(']');
            phonetic = text[..(closeIdx + 1)].Trim();
            text = text[match.Length..];
        }

        var lines = SplitCleanLines(text);
        if (lines.Count > MaxWordDefinitionLines)
        {
            lines.RemoveRange(MaxWordDefinitionLines, lines.Count - MaxWordDefinitionLines);
            lines.Add("…");
        }

        if (lines.Count == 0) return phonetic ?? string.Empty;

        var sb = new StringBuilder();
        if (phonetic != null)
        {
            sb.Append(phonetic).Append('\n');
        }
        sb.Append(string.Join('\n', lines));
        return sb.ToString();
    }

    /// <summary>
    /// 句子/块译文展示：规范换行、逐行裁剪、去掉空行，得到紧凑整洁的多行文本。
    /// </summary>
    public static string ForBlock(string? text)
    {
        var normalized = NormalizeNewlines(text);
        return normalized.Length == 0 ? string.Empty : string.Join('\n', SplitCleanLines(normalized));
    }

    /// <summary>\r\n / \r 统一为 \n；词典存储的字面量转义 \n、\r（两个字符）还原为真实换行。</summary>
    private static string NormalizeNewlines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal);
    }

    /// <summary>按换行拆分，每行 trim，丢弃空行。</summary>
    private static List<string> SplitCleanLines(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }
}
