# QuickTranslate

**Windows 快速翻译工具 —— 鼠标指向屏幕文字，按下快捷键即翻译。**

无需选中、无需复制、无需切换窗口。把鼠标停在目标文字附近，按全局快捷键，QuickTranslate 会就地识别并显示翻译结果。

> 面向 GitHub 的展示文档，完整实现来自 [QuickTranslate_Agent_Project_Spec_v0.1.md](QuickTranslate_Agent_Project_Spec_v0.1.md)。

## 核心能力

- **Alt+1 · 单词模式** — 翻译鼠标所指的单词，弹出简洁的词义解释。
- **Alt+2 · 文本块模式** — 识别并翻译鼠标所在的一段文字/段落，译文流式输出。
- **Esc** — 随时取消当前操作并关闭浮窗。
- **托盘常驻、无主窗口** — 启动即用，后台低开销常驻。
- **本地 OCR** — 翻译前先在原屏幕位置框出所选区域，所见即所译。

![QuickTranslate](./assets/icons/QuickTranslate.ico)

## 设计目标

| 维度 | 说明 |
|------|------|
| 极速 | Word 缓存/词典/在线路由分层；Hotkey→Overlay < 180ms（P50 目标） |
| 隐私 | OCR 全本地；截图仅存内存、不落盘、不上传；只把识别出的文本发给翻译服务 |
| 少侵入 | 弹窗不抢焦点、无头后台、事件驱动、无轮询 |
| 稳健 | 多显示器高 DPI、取消/竞态防护、错误短提示、可诊断日志 |

## 功能特性

- **单词/文本块两种翻译模式**，均由可配置的全局快捷键触发。
- **多级翻译路由**：L1 内存 LRU → L2 SQLite 持久缓存 → ECDICT-lite 本地词典 → 在线模型，命中即不发网络请求。
- **翻译引擎可切换**：默认接入任意 **OpenAI 兼容端点**（OpenAI / DeepSeek / Moonshot / Ollama / one-api 等，SSE 流式优先、JSON 自动回退），也可切回 Qwen-MT。配置保存即时生效。
- **本地 PP-OCRv6 识别**：ONNX Runtime CPU 推理，det → cls → rec → CTC 解码完整流水线，按需截屏不持续轮询。
- **多显示器 PerMonitorV2 DPI 感知**：125% / 150% / 200% 缩放下框体与弹窗定位准确。
- **隐私与安全**：截图不落盘、不上传；API Key 使用 DPAPI 加密存储；默认日志不记录识别原文。
- **Windows SAPI 朗读**：可朗读译文。
- **弹窗尺寸自适应 + 加载动画**：识别/翻译期间有「正在识别…/正在翻译…」的三点动画；弹窗按文本量自动调节尺寸。

## 工作原理

```
热键 (Alt+1 / Alt+2)
   → 协调器
   → 鼠标为中心按需截屏（物理像素，仅内存）
   → IOcrEngine.RecognizeAsync（PP-OCRv6 ONNX：det→cls→rec→CTC 解码）
   → Word / Block 选择（屏幕坐标命中 + 几何块生长）
   → SelectionOverlay（无焦点识别框）
   → ITranslationRouter（L1/L2 缓存 → ECDICT 词典 → 在线 Provider）
   → ITranslationProvider（自定义 OpenAI SSE 流式 / Qwen-MT）
   → WordPopup / BlockPopup（自动定位 + 自适应尺寸）
```

## 技术栈

| 层 | 技术 |
|----|------|
| 语言 / 运行时 | C# / .NET 10 (net10.0-windows) |
| UI | WPF + Win32 P/Invoke（`WS_EX_NOACTIVATE` 无焦点弹窗） |
| OCR | PP-OCRv6 Small + ONNX Runtime（CPU，Session 常驻并 Warm-up） |
| 截图 | GDI BitBlt，内存 Buffer，不落盘 |
| 缓存 | 内存 LRU + SQLite L2 |
| 词典 | ECDICT-lite（packed 二进制 → 用户 overlay → stub 兜底） |
| 密钥 | Windows DPAPI CurrentUser |
| 日志 | Serilog + 分级过滤（默认不含原文） |
| 朗读 | Windows SAPI（TextToSpeech 项目） |
| 测试 | xUnit，约 370 条用例 |

## 解决方案结构

| 项目 | 职责 |
|------|------|
| `src/QuickTranslate.Core` | 领域抽象（OCR/翻译/选择/缓存接口）、物理/DIP 几何类型、AppSettings |
| `src/QuickTranslate.Platform` | Win32 互操作：GDI 截屏、全局热键、PerMonitor DPI、窗口样式 |
| `src/QuickTranslate.Infrastructure` | PP-OCRv6 引擎、翻译 Provider、词典/缓存、SettingsManager、ModelDownloader、安全存储 |
| `src/QuickTranslate.App` | WPF 界面（浮窗/遮罩/加载指示器/设置窗口）、Word/Block 协调器、弹窗定位与尺寸 |
| `src/QuickTranslate.TextToSpeech` | Windows SAPI 朗读 |
| `benchmarks/QuickTranslate.Benchmarks` | 性能基准控制台（采样数据见 `docs/benchmark-results/`） |
| `tests/QuickTranslate.Tests` | xUnit 单元/集成测试 |

## 翻译流水线

```
Word 模式                     Block 模式
  │                              │
  ├→ L1 内存 LRU 命中? ──是→结束  ├→ L1/L2 翻译缓存命中? ──是→结束
  │ miss                          │ miss
  ├→ ECDICT 本地词典（Word）      └→ 在线 Provider
  └→ 在线 Provider（带 OCR 行上下文）    ├→ 自定义 OpenAI（SSE 流式，默认）
                                    └→ Qwen-MT（可选，非流式）
```

## 隐私与安全

- 截图只存在于内存，OCR 完成后立即释放 Buffer，**默认不保存截图**。
- 发送给云端翻译的只有识别出的**文本**与必要上下文，**不含屏幕图像**。
- API Key 使用 DPAPI CurrentUser 加密，**不以明文**存在 Settings JSON、日志或源码。
- 默认日志只记录耗时、长度、错误码等**非内容指标**；开启 Debug 日志才允许更多技术上下文。

## 性能基准

来自 [docs/benchmark-results/cc0c3cb-1786438643301.md](docs/benchmark-results/cc0c3cb-1786438643301.md)（手工采集，OCR 使用 Mock、翻译离线）：

| 指标 | P50 | P95 |
|------|----:|----:|
| Idle Working Set | ~24.8 MB | ~25.2 MB |
| Idle CPU | 0.00% | 0.10% |
| Hotkey → Overlay | ~110 ms | ~110 ms |
| WordSelector | ~32 µs | ~3048 µs |
| Cancel Latency | ~33 µs | ~328 µs |

> 注：OCR 阶段使用固定 100ms 的 Mock 引擎、翻译阶段用 Stub 路由，未涉及真实模型推理与网络请求。实时端到端数据请以真机为准。

## 开始使用

### 环境要求

- Windows 10 / 11 (x64)
- .NET 10 SDK
- OCR 模型资产（默认放入 `assets/models/`，缺失时可用脚本下载）

### 构建

```powershell
dotnet build QuickTranslate.sln
```

### 运行

```powershell
dotnet run --project src/QuickTranslate.App
```

启动后托盘常驻：**Alt+1** 单词翻译，**Alt+2** 文本块翻译，**Esc** 取消。

### 模型资产

OCR 模型（det / cls / rec 的 ONNX + `ppocr_keys.txt` + `version.json`）默认随包复制。新装机无模型时可在设置中触发下载，或使用脚本：

```powershell
# 从 HoVDuc GitHub Release 下载并解压，按 inference.yml 自动生成字典
.\scripts\download-v6-final.ps1
```

> 模型与字典版本需严格匹配（CTC 标签布局、字典长度 vs rec 输出类别数），`ModelVersionVerifier` 会在启动时做 SHA256 校验；模型不可用时自动回退到 Mock OCR。

### 测试

```powershell
# 全量测试（约 370 条用例）
dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj

# 仅真实模型识别回归（需要 assets/models 下有模型；无模型自动跳过）
dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj --filter "FullyQualifiedName~RealOcrRecognitionTests"
```

## 文档

- [QuickTranslate_Agent_Project_Spec_v0.1.md](QuickTranslate_Agent_Project_Spec_v0.1.md) — PRD + 系统设计 + 接口契约 + 验收标准
- [docs/handoff-20260817.md](docs/handoff-20260817.md) — 最近一轮功能迭代与遗留事项交接
- [docs/ocr-models-and-custom-llm.md](docs/ocr-models-and-custom-llm.md) — OCR 模型来源与自定义大模型接入说明
- [docs/benchmark-results/](docs/benchmark-results/) — 性能基准报告
- [docs/per-monitor-dpi-test-guide.md](docs/per-monitor-dpi-test-guide.md) — 多显示器/高 DPI 测试指引

## 许可与致谢

- 第三方组件许可清单见 [LICENSES.txt](LICENSES.txt)，包括 PP-OCRv6 模型权重、ONNX Runtime、ECDICT 词典等。
- 应用界面与基础实现为本仓库独立开发。

> ⚠️ 本项目目前未配置远端仓库，README 中的徽章与仓库链接请于发布时按实际远端补全。