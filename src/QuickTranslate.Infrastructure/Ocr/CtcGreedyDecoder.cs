using Microsoft.ML.OnnxRuntime.Tensors;

namespace QuickTranslate.Infrastructure.Ocr;

internal static class CtcGreedyDecoder
{
    internal static (string Text, float Confidence) DecodeTensor(Tensor<float> tensor, string[] dictionary)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        var shape = tensor.Dimensions;
        int steps, classes;
        if (shape.Length == 3 && shape[0] == 1) { steps = shape[1]; classes = shape[2]; }
        else if (shape.Length == 2) { steps = shape[0]; classes = shape[1]; }
        else throw new ArgumentException("CTC output must have shape [1, T, C] or [T, C].", nameof(tensor));

        // ORT returns a contiguous DenseTensor backed by the output OrtValue.
        // Consume its memory while the inference outputs are alive, avoiding a
        // T*C managed copy (18710 labels can make that copy many MB per line).
        if (tensor is DenseTensor<float> dense && !dense.IsReversedStride)
            return Decode(dense.Buffer.Span, steps, classes, dictionary);

        // Preserve logical element order for other Tensor implementations.
        return Decode(tensor.ToArray(), steps, classes, dictionary);
    }

    internal static (string Text, float Confidence) Decode(ReadOnlySpan<float> probs, int T, int C, string[] dict)
    {
        ArgumentNullException.ThrowIfNull(dict);
        ArgumentOutOfRangeException.ThrowIfNegative(T);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(C);
        if ((long)T * C != probs.Length)
            throw new ArgumentException("CTC output length does not match T*C.", nameof(probs));
        if (dict.Length != C)
            throw new ArgumentException("CTC dictionary label count must match output classes.", nameof(dict));

        // PaddleOCR CTC 布局：blank 固定在 index 0，字典字符从 1 开始，空格在末位 C-1。
        // dict 数组由 BuildCharDictionary 按同一布局构建（dict[i] 即 label i 的字符）。
        int blankIdx = 0;
        var sb = new System.Text.StringBuilder();
        int prevIdx = -1;

        // 置信度 = 实际输出字符的时间步平均概率（PaddleOCR 同口径）。
        // 部分 ONNX 导出图内含 softmax（行和≈1），部分输出原始 logits；
        // 以首行检测：未归一化时对命中时间步单独做 softmax 取概率。
        bool normalized = IsProbabilityRow(probs, C);
        double confSum = 0;
        int confCount = 0;

        for (int t = 0; t < T; t++)
        {
            int start = t * C;
            int bestIdx = 0;
            float bestVal = float.MinValue;
            for (int c = 0; c < C; c++)
            {
                float v = probs[start + c];
                if (v > bestVal) { bestVal = v; bestIdx = c; }
            }

            if (bestIdx != blankIdx && bestIdx != prevIdx)
            {
                if (bestIdx >= 0 && bestIdx < dict.Length)
                {
                    sb.Append(dict[bestIdx]);
                    float p = normalized ? bestVal : SoftmaxAt(probs, start, C, bestIdx);
                    confSum += p;
                    confCount++;
                }
            }
            prevIdx = bestIdx;
        }

        float confidence = confCount > 0 ? (float)(confSum / confCount) : 0f;
        return (sb.ToString().Trim(), confidence);
    }

    private static bool IsProbabilityRow(ReadOnlySpan<float> probs, int C)
    {
        if (probs.Length < C) return false;
        double sum = 0;
        for (int c = 0; c < C; c++) sum += probs[c];
        return Math.Abs(sum - 1.0) < 0.05;
    }

    private static float SoftmaxAt(ReadOnlySpan<float> probs, int start, int C, int idx)
    {
        // 行内减最大值防溢出；仅在输出未归一化（logits）时调用
        float max = float.MinValue;
        for (int c = 0; c < C; c++)
        {
            float v = probs[start + c];
            if (v > max) max = v;
        }
        double sumExp = 0;
        for (int c = 0; c < C; c++) sumExp += Math.Exp(probs[start + c] - max);
        return (float)(Math.Exp(probs[start + idx] - max) / sumExp);
    }

}
