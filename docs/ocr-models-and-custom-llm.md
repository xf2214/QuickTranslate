# OCR 模型来源与自定义大模型翻译

更新于 2026-10-26。优化决策与其他 OCR 方案见 [调研与优化记录](optimization-2026-10-26.md)。

## OCR 模型

### 当前默认：PP-OCRv6 medium

| 文件 | 来源 | 说明 |
|------|------|------|
| `det.onnx` | [HoVDuc/ppocrv5-onnx](https://github.com/HoVDuc/ppocrv5-onnx) Release v1.1.0 的 medium ZIP | 文本检测，62,032,837 字节 |
| `rec.onnx` | 同一 Release 的 medium recognition ZIP | 文本识别，76,554,979 字节，18,710 个输出类别 |
| `ppocr_keys.txt` | rec ZIP 内 `inference.yml` 的 `PostProcess.character_dict` | 18,708 个字符，UTF-8 无 BOM、LF 换行 |
| `cls.onnx` | `paddleocr.bj.bcebos.com` 直链，可选 | 180° 方向分类；缺失不阻断主流程 |
| `version.json` | 随应用分发、复制到下载目录 | 检测、识别、字符字典的 SHA-256 清单 |

medium 的检测与识别 SHA-256 可对照 PaddlePaddle 官方 [检测模型](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx/blob/main/inference.onnx) 与 [识别模型](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/blob/main/inference.onnx)。当前镜像权重与清单中的这两个官方文件哈希一致。

开发资产放 `assets/models/`；应用随包资产放 `BaseDirectory/assets/models/`；应用下载器写入用户数据目录的 `models/`。引擎保留已有模型路径搜索规则。

### 推理约定

- CTC 标签：`0 = blank`，`1..N = 字典字符`，`N+1 = 空格`。类别数必须满足 `C = 字典行数 + 2`。
- 检测预处理维持模型要求的 BGR 通道序；不以另一模型的 RGB 约定替换。
- `CtcGreedyDecoder` 直接读取连续 Tensor 的内存，并校验 shape 与字符表长度；非连续 Tensor 保留安全回退。
- 识别结果不再因焦点区域选择器的固定 8 行上限而截断。
- 模型在本地运行，屏幕截图不发送到翻译服务。

### 下载和校验

源码环境运行：

```powershell
.\scripts\download-v6-final.ps1
```

脚本默认写入仓库 `assets/models/`，也可传 `-TargetDir`。应用内 `ModelDownloader` 按 URL 复用 rec ZIP，并从配套 YAML 的 `PostProcess.character_dict` 生成字符字典（也兼容历史顶层格式）。C# 与 PowerShell 均处理列表缩进、字符串转义和字段边界。方向分类器下载失败只告警。

应用下载器完成后真实计算 SHA-256，要求必需文件存在、清单有效且哈希匹配；缺失或损坏的清单不能再报告“校验成功”。`AllModelFilesExist()` 只检查必需文件存在，不等同于完整性校验。当前 `cls` 清单未配置哈希，因此对它只执行可选文件策略；不声称它已通过哈希验证。

如已有文件损坏，下载器会返回校验失败；移除对应损坏文件后重试下载。现有下载流程只补缺失文件，不自动覆盖已有模型。命令行脚本完成后可在 Windows 运行 `Category=Model` 的完整性测试，确认实际模型和字符表匹配。

## 自定义 OpenAI 兼容翻译

### 配置项

| 设置 | 字段 | 默认值与用途 |
|------|------|--------------|
| 翻译引擎 | `TranslationProvider` | `CustomOpenAi`；另保留 `QwenMt` |
| API 地址 | `CustomLlmBaseUrl` | 基础地址或完整 `/chat/completions` 地址 |
| 模型名称 | `CustomLlmModel` | 使用服务实际提供的模型 ID |
| 需要 API Key | `CustomLlmRequireApiKey` | `true`；关闭后完全不发送 Authorization，即使之前保存过 Key |
| 启用流式 | `CustomLlmUseStreaming` | `true`；可关闭以请求普通 JSON 回复 |
| 发送 temperature | `CustomLlmSendTemperature` | `true`；关闭时从请求体省略该字段 |
| API Key | `ApiKey` | Bearer 鉴权；仍使用 Windows DPAPI 存储 |

新增三个开关默认均为 `true`，保持原配置的请求方式。设置保存采用同一份翻译配置快照，请求不会混用修改前后的 URL、模型、Key 与开关；配置版本和缓存身份也包含这三个开关。API Key 不进入缓存键或普通 JSON 设置文件。

### 地址拼接示例

| 输入 | 实际请求 URL |
|------|--------------|
| `https://example.com/v1` | `https://example.com/v1/chat/completions` |
| `https://example.com/custom/v1/` | `https://example.com/custom/v1/chat/completions` |
| `https://example.com/custom/v1/chat/completions` | 保留原端点，不重复追加 |
| `https://example.com/v1?api-version=example` | `https://example.com/v1/chat/completions?api-version=example` |
| `127.0.0.1:11434/v1` | `http://127.0.0.1:11434/v1/chat/completions` |
| `example.com` | `https://example.com/chat/completions`；不会猜测并追加 `/v1` |

仅允许 HTTP / HTTPS，拒绝 URL 用户信息和 fragment。省略协议时，精确的 `localhost` 或 loopback IP 使用 HTTP，其余地址使用 HTTPS；`localhost.example.com` 不会被当成本地地址。

本地 Ollama 示例：API 地址 `http://127.0.0.1:11434/v1`，模型填写本地实际已安装的模型 ID，关闭“需要 API Key”。[Ollama 官方说明](https://docs.ollama.com/api/authentication)指出本地 API 不需要鉴权；云端服务需使用对应鉴权设置。

### 响应与超时

- 超时覆盖响应头和完整响应正文；连接返回响应头后挂起也会结束。用户取消与超时分开处理。
- SSE 支持多行 `data:`、CR / LF / CRLF、注释心跳；遇到 `[DONE]` 或正常 `finish_reason` 后立即结束，无需等待服务器关闭连接。
- 按响应的 Content-Type 选择 SSE 或 JSON；服务忽略 `stream` 而返回合法 JSON 时仍可解析。
- 空回复、畸形响应、未正常结束的流、输出被截断或过滤，不作为成功译文写入缓存。
- 响应有大小上限；HTTP 和解析错误只显示安全分类，不回显服务器的任意错误正文。
- “测试连接”使用同一份 URL、模型、鉴权和请求选项。
- URL、模型与上述开关保存后影响新请求；**系统代理设置需要重启应用**。长生命周期 HTTP 连接每 5 分钟轮换，以便后续连接刷新 DNS。

### 范围

此适配器支持文本 `/chat/completions`、Bearer 或匿名鉴权；不是所有厂商协议的通用适配器。不提供任意自定义 Header、Azure 专用 `api-key` 头、Responses API、工具调用或多模态请求。`CustomLlmMaxContextLines` 目前仍未用于多轮上下文。`QwenMt` 保持非流式请求并复用安全的 HTTP / JSON 处理。
