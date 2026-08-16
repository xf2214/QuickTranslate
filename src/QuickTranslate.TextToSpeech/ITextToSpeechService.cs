namespace QuickTranslate.TextToSpeech;

/// <summary>
/// 文本朗读（Text-to-Speech）服务抽象。独立模块，仅被 App 层调用。
/// </summary>
public interface ITextToSpeechService
{
    /// <summary>系统是否具备可用的朗读能力（安装了至少一个语音）。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 异步朗读指定文本。会自动打断上一次尚未读完的朗读。
    /// </summary>
    /// <param name="text">要朗读的文本。</param>
    /// <param name="language">目标语言（如 zh-CN / en / ja），用于挑选匹配的语音；为空或无可匹配语音时使用默认语音。</param>
    void Speak(string text, string? language = null);

    /// <summary>停止当前朗读。</summary>
    void Stop();
}