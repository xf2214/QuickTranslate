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
    /// <summary>核显加速：开启后尝试使用 AMD/Intel 核显 DirectML 加速 OCR 推理，关闭则强制 CPU。</summary>
    public bool UseHardwareAcceleration { get; set; } = false;
    public bool DebugLogging { get; set; } = false;

    /// <summary>调试模式：开启后选中区域以实线框显示（便于排查选词/OCR 定位），关闭时显示扫描式动画。</summary>
    public bool DebugOverlayMode { get; set; } = false;
    public bool EnableTextToSpeech { get; set; } = false;

    /// <summary>热键触发时优先尝试 UIA 选中文本探测（可选中文本场景零 OCR 误差），失败静默回退 OCR。</summary>
    public bool EnableSelectedTextProbe { get; set; } = true;

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

    /// <summary>弹窗显示样式：detailed 详细 / compact 简洁；持久化到 settings.json，随弹窗每次 Show 读取。</summary>
    // WHY：详细模式展示元信息彩点与文字按钮，简洁模式隐藏元信息行并用图标按钮以节省纵向空间。
    public string PopupDisplayStyle { get; set; } = "detailed";

    /// <summary>
    /// 把落盘加载值的全部字段复制到当前实例（原地 mutation，保持 IOptions 引用同一性）。
    /// 新增持久化字段只改这里：调用方（HostedService 补丁 / IConfigureOptions）不再各自手写赋值，避免漏同步。
    /// </summary>
    public void ApplyFrom(AppSettings source)
    {
        WordHotkey = source.WordHotkey;
        BlockHotkey = source.BlockHotkey;
        TargetLanguage = source.TargetLanguage;
        TranslationQuality = source.TranslationQuality;
        StartWithWindows = source.StartWithWindows;
        CloseOnOutsideClick = source.CloseOnOutsideClick;
        UseHardwareAcceleration = source.UseHardwareAcceleration;
        DebugLogging = source.DebugLogging;
        DebugOverlayMode = source.DebugOverlayMode;
        EnableTextToSpeech = source.EnableTextToSpeech;
        EnableSelectedTextProbe = source.EnableSelectedTextProbe;
        TranslationProvider = source.TranslationProvider;
        CustomLlmBaseUrl = source.CustomLlmBaseUrl;
        CustomLlmModel = source.CustomLlmModel;
        CustomLlmMaxContextLines = source.CustomLlmMaxContextLines;
        PopupDisplayStyle = source.PopupDisplayStyle;
        ResolvedApiKey = source.ResolvedApiKey;
    }
}
