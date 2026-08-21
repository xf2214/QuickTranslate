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

    // 模型偶发输出的前缀标签（提示词要求只输出译文，但小模型不一定遵守）
    private static readonly Regex LeadingLabel = new(
        @"^(?:翻译结果|译文|翻译|Translation|Translate)\s*[:：]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 行内连续空白压成单个空格（清理 OCR/模型输出的多余空格）
    private static readonly Regex MultipleSpaces = new(" {2,}", RegexOptions.Compiled);

    // 包裹引号对：整段被包裹时才剥离
    private static readonly (string Open, string Close)[] QuotePairs =
    {
        ("“", "”"), ("\"", "\""), ("‘", "’"), ("'", "'"), ("「", "」"), ("『", "』"),
    };

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
            return CleanOnlineTranslation(text);
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
    /// 句子/块译文展示：规范换行、逐行裁剪、去掉空行，并清理模型常见装饰
    /// （“译文：”前缀、整段包裹引号、Markdown 加粗标记、多余空格）。
    /// </summary>
    public static string ForBlock(string? text)
    {
        var normalized = NormalizeNewlines(text);
        return normalized.Length == 0 ? string.Empty : CleanOnlineTranslation(normalized);
    }

    /// <summary>
    /// OCR 原文预览展示：保留换行（便于对照识别范围），逐行裁剪并压缩多余空格。
    /// </summary>
    public static string ForSourcePreview(string? blockText)
    {
        var normalized = NormalizeNewlines(blockText);
        if (normalized.Length == 0) return string.Empty;

        var lines = new List<string>();
        foreach (var raw in normalized.Split('\n'))
        {
            var line = CollapseSpaces(raw.Trim());
            if (line.Length > 0) lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    /// <summary>在线译文清理：去空行、去前缀标签/加粗标记/多余空格、剥离整段包裹引号。</summary>
    private static string CleanOnlineTranslation(string normalizedText)
    {
        var lines = SplitCleanLines(normalizedText);
        if (lines.Count == 0) return string.Empty;

        lines[0] = LeadingLabel.Replace(lines[0], string.Empty);
        if (lines[0].Length == 0) lines.RemoveAt(0);
        if (lines.Count == 0) return string.Empty;

        for (int i = 0; i < lines.Count; i++)
        {
            lines[i] = CollapseSpaces(lines[i].Replace("**", string.Empty).Replace("__", string.Empty));
        }

        return StripWrappingQuotes(string.Join('\n', lines));
    }

    /// <summary>整段被成对引号包裹时剥离（如模型把译文包在 “...” 里）；仅包裹时生效，不影响正文引号。</summary>
    private static string StripWrappingQuotes(string text)
    {
        foreach (var (open, close) in QuotePairs)
        {
            if (text.Length > open.Length + close.Length &&
                text.StartsWith(open, StringComparison.Ordinal) &&
                text.EndsWith(close, StringComparison.Ordinal))
            {
                return text[open.Length..^close.Length].Trim();
            }
        }
        return text;
    }

    private static string CollapseSpaces(string line) =>
        MultipleSpaces.Replace(line.Replace('\t', ' '), " ");

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
