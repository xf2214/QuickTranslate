using System.Text.RegularExpressions;

namespace QuickTranslate.Core.Translation;

/// <summary>
/// 词典结果的结构化视图：从 TargetText 解析出音标与逐行释义。
/// 只做展示层解析，不改动缓存/词典存储的原始字符串；零 WPF 依赖。
/// </summary>
public sealed class DictionaryStructuredView
{
    // 音标前缀：[xxx] + 至少两个空格（与 TranslationDisplayFormatter.PhoneticPrefix 一致）
    private static readonly Regex PhoneticPrefix = new(
        @"^\[[^\[\]\n]+\]\s{2,}", RegexOptions.Compiled);

    // 词性标签：行首小写字母+点+空格，如 n. / vt. / adj. / pron. 等
    private static readonly Regex PosTagRegex = new(
        @"^[a-z]+\.\s+", RegexOptions.Compiled);

    // 领域标签：行首 [xx]，后面不跟两个以上空格（避免误匹配音标）
    private static readonly Regex DomainTagRegex = new(
        @"^\[[^\[\]\n]+\]", RegexOptions.Compiled);

    private const int MaxLines = 8;

    public string? Phonetic { get; }
    public IReadOnlyList<DictionaryLine> Lines { get; }
    public bool IsTruncated { get; }

    /// <summary>当有 2 行以上释义时需要显示行号。</summary>
    public bool ShouldNumberLines => Lines.Count(l => !l.IsTruncationMarker) >= 2;

    public DictionaryStructuredView(string? phonetic, IReadOnlyList<DictionaryLine> lines, bool isTruncated)
    {
        Phonetic = phonetic;
        Lines = lines;
        IsTruncated = isTruncated;
    }

    /// <summary>
    /// 从词典 TargetText 解析结构化视图。
    /// 复用 TranslationDisplayFormatter 的换行归一逻辑：字面量 \n 还原为真实换行。
    /// </summary>
    public static DictionaryStructuredView Parse(string? targetText)
    {
        if (string.IsNullOrWhiteSpace(targetText))
            return new DictionaryStructuredView(null, Array.Empty<DictionaryLine>(), false);

        // 与 TranslationDisplayFormatter.NormalizeNewlines 保持一致的归一
        var text = targetText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(text))
            return new DictionaryStructuredView(null, Array.Empty<DictionaryLine>(), false);

        // 提取音标
        string? phonetic = null;
        var match = PhoneticPrefix.Match(text);
        if (match.Success)
        {
            var closeIdx = text.IndexOf(']', StringComparison.Ordinal);
            // 去掉外层 brackets，只保留内部音标
            if (closeIdx >= 1)
            {
                phonetic = text[1..closeIdx].Trim();
            }
            text = text[match.Length..];
        }

        // 按行拆分、trim、去空行
        var rawLines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) rawLines.Add(line);
        }

        bool isTruncated = false;
        if (rawLines.Count > MaxLines)
        {
            rawLines.RemoveRange(MaxLines, rawLines.Count - MaxLines);
            isTruncated = true;
        }

        var lines = new List<DictionaryLine>(rawLines.Count + (isTruncated ? 1 : 0));
        foreach (var raw in rawLines)
        {
            lines.Add(ParseLine(raw));
        }

        if (isTruncated)
        {
            // 截断标记行：灰色省略号
            lines.Add(new DictionaryLine(null, null, "…", IsTruncationMarker: true));
        }

        return new DictionaryStructuredView(phonetic, lines, isTruncated);
    }

    private static DictionaryLine ParseLine(string line)
    {
        // 优先检测 POS（如 n. / vt. / adj.）
        var posMatch = PosTagRegex.Match(line);
        if (posMatch.Success)
        {
            var posTag = posMatch.Value.TrimEnd(); // "n."
            var body = line[posMatch.Length..].Trim();
            return new DictionaryLine(posTag, null, body, false);
        }

        // 再检测领域标签（如 [计] / [医]）
        var domainMatch = DomainTagRegex.Match(line);
        if (domainMatch.Success)
        {
            var domainTag = domainMatch.Value; // "[计]"
            var body = line[domainMatch.Length..].Trim();
            // 若领域标签后仍有一段内容，body 即为剩余；否则 body 可能为空
            return new DictionaryLine(null, domainTag, body, false);
        }

        // 纯文本
        return new DictionaryLine(null, null, line, false);
    }
}

/// <summary>单行释义的结构化表示。</summary>
public sealed record DictionaryLine(
    string? PosTag,
    string? DomainTag,
    string Body,
    bool IsTruncationMarker);
