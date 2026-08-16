# OCR 模型来源与自定义大模型翻译

> 本文档对应 2026-08 轮次的两项核心变更：PP-OCRv6 真实识别链路、
> 自定义 OpenAI 兼容翻译接入。与「关于」窗口（AboutWindow）展示内容保持同步。

---

## 一、OCR 模型

### 模型来源

| 文件 | 来源 | 说明 |
|------|------|------|
| `det.onnx` | [HoVDuc/ppocrv5-onnx](https://github.com/HoVDuc/ppocrv5-onnx) Release v1.1.0（PP-OCRv6 medium zip） | 文本检测 |
| `rec.onnx` | 同上 zip，解压取最大 `.onnx` | 文本识别，输出 **18710** 类（blank + 18708 字典 + 空格） |
| `ppocr_keys.txt` | 由 rec zip 内 `inference.yml` 的 `PostProcess.character_dict` 自动生成 | **18708** 字符，UTF-8 无 BOM |
| `cls.onnx` | `paddleocr.bj.bcebos.com` 直链（可选） | 180° 方向分类，缺失不影响主流程 |
| `version.json` | 随下载自动复制到模型目录 | sha256 校验清单 |

放置目录：开发环境 `assets/models/`；运行目录 `BaseDirectory/assets/models`。

### 关键约定（勿改）

- **CTC 标签布局**：`label 0 = blank`，`1..N = 字典行`，`N+1（末位）= 空格`。
  见 `PaddleOcrV6Engine.BuildCharDictionary` / `CtcGreedyDecode`（附历史 Bug 注释）。
- **det 归一化按 BGR 通道序**（`[0.485, 0.456, 0.406]` 对应 B/G/R），勿"修正"回 RGB。
- 字典行数必须与 rec 输出类别数满足 `C = 字典行数 + 2`，会话初始化时有一致性检查。

### 新装机下载

`ModelDownloader`（或 `scripts/download-v6-final.ps1`）：det/rec 从 HoVDuc GitHub
Release zip 下载解压；`ppocr_keys.txt` 从 rec zip 内 `inference.yml` 生成
（同 URL 只下载一次）；cls 失败仅告警不阻断。

---

## 二、自定义大模型翻译（OpenAI 兼容）

### 配置项（设置窗口 S1 卡片）

| 设置 | 对应字段 | 说明 |
|------|----------|------|
| 翻译引擎 | `TranslationProvider` | `CustomOpenAi`（默认）/ `QwenMt` |
| API 地址 | `CustomLlmBaseUrl` | 如 `https://api.openai.com/v1`、`http://127.0.0.1:11434/v1` |
| 模型名称 | `CustomLlmModel` | 如 `gpt-4o-mini`、`deepseek-chat`、`qwen2.5:7b` |
| API Key | 共用 `ApiKey` | Bearer 鉴权 |

### 行为说明

- 请求 `POST {BaseUrl}/chat/completions`，SSE 流式优先，非 SSE 响应自动回退 JSON 解析。
- **URL 容错**：自动补尾斜杠与 `/chat/completions`；缺 scheme 时补全
  （localhost 默认 `http://`，公网默认 `https://`）。
- **保存即生效**：配置每次从 AppSettings 单例实时读取，无需重启。
- **未配置降级**：URL / 模型 / Key 缺一时返回 Mock 占位流（便于离线试用）。
- **错误映射**：401/429/5xx 映射为带提示的 `TranslationException`。

### 已知限制

- `CustomLlmMaxContextLines`（多轮上下文条数）设置项已建模，provider 尚未使用。

> 注：QwenMt 与自定义引擎的 API Key 均为实时读取 AppSettings 单例，
> 设置窗口保存后立即生效、无需重启。
