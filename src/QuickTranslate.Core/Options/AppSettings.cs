namespace QuickTranslate.Core.Options;

public class HotkeyCombo
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.None;
    public KeyboardKey Key { get; set; } = KeyboardKey.None;

    public HotkeyCombo() { }

    public HotkeyCombo(HotkeyModifiers modifiers, KeyboardKey key)
    {
        Modifiers = modifiers;
        Key = key;
    }
}

public enum TranslationQuality
{
    Fast = 0,
    Balanced = 1,
    Best = 2
}

/// <summary>
/// 在线翻译提供方。CustomOpenAi = 任意 OpenAI 兼容 chat/completions 端点
/// （自建 vLLM/Ollama/one-api 或任意云厂商）；QwenMt = 阿里 DashScope 专用协议。
/// </summary>
public enum TranslationProviderKind
{
    CustomOpenAi = 0,
    QwenMt = 1
}

public class AppSettings
{
    public HotkeyCombo WordHotkey { get; set; } = new(HotkeyModifiers.Alt, KeyboardKey.D1);
    public HotkeyCombo BlockHotkey { get; set; } = new(HotkeyModifiers.Alt, KeyboardKey.D2);
    public string TargetLanguage { get; set; } = "zh-CN";
    public TranslationQuality TranslationQuality { get; set; } = TranslationQuality.Fast;
    public bool StartWithWindows { get; set; } = false;
    public bool CloseOnOutsideClick { get; set; } = true;
    public bool DebugLogging { get; set; } = false;
    public bool EnableTextToSpeech { get; set; } = false;

    // ===== 自定义大模型接入（OpenAI 兼容）=====
    public TranslationProviderKind TranslationProvider { get; set; } = TranslationProviderKind.CustomOpenAi;

    /// <summary>OpenAI 兼容服务基地址，如 https://api.openai.com/v1 或 http://127.0.0.1:11434/v1。</summary>
    public string CustomLlmBaseUrl { get; set; } = "";

    /// <summary>模型名，如 gpt-4o-mini、qwen2.5:7b、deepseek-chat。</summary>
    public string CustomLlmModel { get; set; } = "";

    /// <summary>对话上下文最大条数（提示词之外保留的原文轮次），0 = 不带上下文。</summary>
    public int CustomLlmMaxContextLines { get; set; } = 0;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsApiKeyConfigured => !string.IsNullOrWhiteSpace(ResolvedApiKey);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ResolvedApiKey { get; set; }

    /// <summary>自定义大模型是否已具备真实调用条件（Base URL + 模型名 + API Key 齐全）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustomLlmConfigured =>
        !string.IsNullOrWhiteSpace(CustomLlmBaseUrl) &&
        !string.IsNullOrWhiteSpace(CustomLlmModel);
}
