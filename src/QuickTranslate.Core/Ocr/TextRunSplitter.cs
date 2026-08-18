namespace QuickTranslate.Core.Ocr;

/// <summary>
/// 文本 run 拆分：把 OCR 识别出的 token（按空格切分后的片段）按脚本边界再细分。
/// 代码/混排场景中标点与中文常紧贴标识符（如 "MinSegmentWidth;"、"增TrySegmentConstrained)"），
/// 整段作为一个"词"会让选取框覆盖到光标目标之外的字符。
/// 拆分为独立 run 后，选择层只命中光标所指的 run（纯标点 run 会被 WordLike 过滤掉）。
/// </summary>
public static class TextRunSplitter
{
    public enum CharClass { Word, Cjk, Punct }

    public readonly record struct TextRun(int Start, int Length, CharClass Class)
    {
        public string Slice(string source) => source.Substring(Start, Length);
    }

    /// <summary>
    /// 拆分规则：
    /// - CJK 连续段为一个 run；拉丁/数字/下划线连续段为一个 run；其余标点各自成 run；
    /// - 夹在两个 Word 字符之间的撇号/连字符（don't、well-known）并入 Word run，不拆。
    /// </summary>
    public static IReadOnlyList<TextRun> Split(string token)
    {
        var runs = new List<TextRun>();
        if (string.IsNullOrEmpty(token)) return runs;

        int n = token.Length;
        int start = 0;
        var cls = Classify(token[0]);

        for (int i = 1; i <= n; i++)
        {
            if (i < n)
            {
                var c = Classify(token[i]);
                if (c == cls) continue;

                // 夹在 Word 字符间的撇号/连字符：并入当前 Word run（如 don't、well-known）
                if (c == CharClass.Punct && cls == CharClass.Word && i + 1 < n &&
                    (token[i] == '\'' || token[i] == '-' || token[i] == '\u2019') &&
                    Classify(token[i + 1]) == CharClass.Word)
                {
                    continue;
                }

                runs.Add(new TextRun(start, i - start, cls));
                start = i;
                cls = c;
            }
            else
            {
                runs.Add(new TextRun(start, n - start, cls));
            }
        }

        return runs;
    }

    /// <summary>估算字符相对宽度权重：CJK/全角 ≈ 2 份，窄标点 ≈ 0.6 份，其余 1 份。</summary>
    public static double CharWeight(char c)
    {
        if (Classify(c) == CharClass.Cjk) return 2.0;
        if (c >= 0xFF01 && c <= 0xFF60 || c >= 0xFFE0 && c <= 0xFFE6) return 2.0; // 全角标点
        if (Classify(c) == CharClass.Punct) return 0.6;
        return 1.0;
    }

    public static CharClass Classify(char c)
    {
        if (c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF ||
            c >= 0x3040 && c <= 0x30FF || c >= 0xAC00 && c <= 0xD7AF)
            return CharClass.Cjk;
        if (char.IsLetterOrDigit(c) || c == '_')
            return CharClass.Word;
        return CharClass.Punct;
    }

    /// <summary>
    /// 驼峰标识符拆分：LineText → [Line, Text]、HTMLParser → [HTML, Parser]、
    /// getValue2 → [get, Value2]。无大小写边界的纯大写/纯小写串原样返回。
    /// 代码场景下光标通常指向标识符中的某个子词，拆开后选取框只覆盖子词。
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> SplitCamelCase(string text)
    {
        var parts = new List<(int, int)>();
        if (string.IsNullOrEmpty(text)) return parts;

        int start = 0;
        for (int i = 1; i < text.Length; i++)
        {
            bool split = false;
            // 小写/数字后接大写 → 在大写前切（lineText → line|Text）
            if (char.IsUpper(text[i]) && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1])))
            {
                split = true;
            }
            // 缩略词接普通词：大写-大写-小写 → 在最后一个大写前切（HTMLParser → HTML|Parser）
            else if (i + 1 < text.Length && char.IsUpper(text[i]) && char.IsUpper(text[i - 1]) && char.IsLower(text[i + 1]))
            {
                split = true;
            }

            if (split)
            {
                parts.Add((start, i - start));
                start = i;
            }
        }
        parts.Add((start, text.Length - start));
        return parts;
    }
}
