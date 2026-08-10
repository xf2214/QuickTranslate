---
project: QuickTranslate
version: v0.1
status: V1 设计基线
platform: Windows 10/11 x64
last_updated: 2026-08-10
---

# QuickTranslate

**Windows 快速翻译工具 · 总项目说明与 Agent 开发规范**

> **文档定位**
>
> 本文件同时承担 PRD、系统设计说明、接口契约和 Agent 开发执行规范。开发 Agent 应优先遵循本文档中标记为“V1 必须”“禁止项”“验收标准”的约束。

| **字段** | **内容**                                             |
|----------|------------------------------------------------------|
| 工作名   | QuickTranslate（可在发行前更名）                     |
| 版本     | v0.1 / V1 设计基线                                   |
| 目标平台 | Windows 10/11 x64，优先 Windows 11                   |
| 核心技术 | C# / .NET 10 / WPF / Win32 / PP-OCRv6 / ONNX Runtime |
| 翻译策略 | 本地词典与缓存优先；Qwen-MT Lite/Flash 在线翻译      |
| 文档日期 | 2026-08-10                                           |
| 核心原则 | 单进程、事件驱动、OCR 本地、无主窗口、低常驻开销     |

**一句话产品定义**

**鼠标指向屏幕文字，通过两个全局快捷键分别“翻译这个词”和“翻译这一块”，无需选中、复制、切换窗口。**

# 0. 阅读方式与开发优先级
Agent 开发时按以下优先级理解本文档：

1. 第 1 优先级：第 3 章“V1 范围”、第 16 章“验收标准”、第 17 章“禁止项”。

2. 第 2 优先级：第 5-12 章的架构、模块和接口约定。

3. 第 3 优先级：第 18 章开发里程碑与交付顺序。

4. 若实现细节与本文档发生冲突，先保留接口边界并记录 ADR，不得擅自换技术栈或扩大范围。

| **章节** | **用途**                                |
|----------|-----------------------------------------|
| 1-4      | 产品目标、用户交互、V1 范围、非功能目标 |
| 5-8      | 总体架构、运行模型、OCR 与选择算法      |
| 9-12     | 翻译、UI、状态机、数据结构与接口        |
| 13-15    | 配置、安全、日志与工程结构              |
| 16-18    | 验收、禁止项、Agent 开发计划            |
| 19-21    | 测试、性能 Benchmark、发布与后续路线    |
| 附录     | Agent 启动指令、术语表与决策摘要        |

# 1. 产品目标
QuickTranslate 是一个常驻 Windows 后台的快速翻译工具。它不依赖用户先选中文字，也不要求从当前应用复制内容；用户只需要把鼠标移动到目标文字附近并按快捷键。

- **功能 A - 单词翻译：**鼠标位于单词上或附近，按“快捷键 1”，翻译该单词。

- **功能 B - 文本块翻译：**鼠标位于一段文字中，按“快捷键 2”，识别并翻译鼠标所在文本块。

- **视觉确认：**翻译前先在原屏幕位置显示识别框，告诉用户程序选中了什么。

- **无侵入：**不切换当前应用、不抢输入焦点、不要求安装浏览器插件。

- **隐私优先：**OCR 全部在本机完成，默认只将 OCR 后的文本发送给翻译服务，不上传截图。

# 2. 核心用户交互
## 2.1 单词模式（Word Mode）
> 用户将鼠标停在单词上
>
> ↓
>
> 按 Hotkey 1（默认建议 Alt + 1，可配置）
>
> ↓
>
> 截取鼠标附近小区域
>
> ↓
>
> PP-OCRv6 本地识别
>
> ↓
>
> 定位鼠标对应 Word Bounding Box
>
> ↓
>
> 立即显示单词选择框
>
> ↓
>
> 缓存 / 本地词典 / 在线 MT
>
> ↓
>
> 显示小型 Word Popup

预期示例：用户指向 “significantly”，弹窗只解释/翻译 significantly；允许使用同一 OCR 行作为上下文进行消歧，但结果主体仍是该单词。

## 2.2 文本块模式（Block Mode）
> 用户将鼠标停在正文段落内
>
> ↓
>
> 按 Hotkey 2（默认建议 Alt + 2，可配置）
>
> ↓
>
> 截取鼠标附近较大区域
>
> ↓
>
> PP-OCRv6 本地识别 Line / Box
>
> ↓
>
> 以鼠标所在文字行为 Anchor 做 Block Growing
>
> ↓
>
> 合并得到文本块 Bounding Box
>
> ↓
>
> 立即显示文本块选择框
>
> ↓
>
> 调用 Qwen-MT Lite/Flash，优先 Streaming
>
> ↓
>
> 在选择框附近显示译文 Popup

## 2.3 通用交互规则
| **操作**     | **行为**                                        |
|--------------|-------------------------------------------------|
| Hotkey 1     | 启动 Word Mode；若已有任务，先取消旧任务。      |
| Hotkey 2     | 启动 Block Mode；若已有任务，先取消旧任务。     |
| Esc          | 取消当前 OCR/翻译任务并关闭 Overlay/Popup。     |
| 再次按快捷键 | 以当前鼠标位置重新发起识别；旧请求必须 Cancel。 |
| 点击 Popup   | Popup 切换为可交互状态，可复制译文。            |
| 点击外部区域 | 可配置为关闭 Popup；V1 默认关闭。               |

# 3. V1 范围与非目标
> **V1 必须完成**
>
> V1 的目标不是做“完整翻译平台”，而是把“鼠标 + 快捷键 + OCR + 翻译浮窗”这一条链路做到稳定、快速、低资源。

| **类别** | **V1 必须**                                            |
|----------|--------------------------------------------------------|
| 后台     | 托盘常驻、单实例、开机启动可配置、无主窗口。           |
| 快捷键   | 2 个可配置全局快捷键；默认建议 Alt+1 / Alt+2。         |
| 截图     | 按需截取鼠标周围区域，不持续轮询、不持续 OCR。         |
| OCR      | PP-OCRv6 Small，本地 ONNX Runtime 推理。               |
| 选择     | WordSelector + BlockSelector + 屏幕 Overlay。          |
| 翻译     | 本地缓存/词典 + Qwen-MT Provider；Block 支持流式结果。 |
| UI       | Word Popup、Block Popup、Settings、Tray Menu。         |
| 安全     | 截图不落盘；API Key 安全存储；默认日志不记录原文。     |
| 质量     | 多显示器、DPI、取消/竞态、错误处理必须覆盖。           |

## 3.1 V1 明确不做
- 不做 Electron / WebView 主框架。

- 不内置 Python Runtime，不启动本地 Python 服务。

- 不做持续后台截图、鼠标悬停持续 OCR。

- 不做聊天机器人、总结、写作、知识库等扩展 AI 功能。

- 不做账户系统、云同步、历史记录中心。

- 不做 PDF 整页解析、表格解析、公式识别等文档工作台。

- 不在 V1 同时维护 Tiny + Small 两套 OCR 模型。

- 不默认依赖 GPU/NPU；V1 以 CPU baseline 为准。

# 4. 非功能目标与性能预算
以下值是工程目标，不是对所有硬件的绝对承诺。必须在参考设备和多档设备上建立 Benchmark，并保留 P50/P95 数据。

| **指标**               | **V1 目标/预算**             | **说明**                                           |
|------------------------|------------------------------|----------------------------------------------------|
| Idle CPU               | 平均 \< 0.5%                 | 应用稳定后无轮询计算；仅消息循环和被动等待。       |
| Idle Working Set       | 软目标 \< 200 MB             | 包含已加载 PP-OCRv6 Session；\>350 MB 需专项排查。 |
| Tray Ready             | \< 1.0 s                     | 启动后托盘与快捷键应尽快可用。                     |
| Hotkey → Overlay       | P50 \< 180 ms；P95 \< 350 ms | 模型预热后；重点衡量本地链路。                     |
| 缓存单词结果           | OCR 完成后近实时             | Memory/SQLite/Dictionary 命中不得请求网络。        |
| Block Translation TTFT | 网络良好时目标 P50 \< 700 ms | 从请求发送到首个可显示译文片段。                   |
| 任务取消               | \< 100 ms 进入取消态         | 旧翻译结果不得覆盖新任务。                         |
| 截图持久化             | 0                            | 截图只能存在内存 Buffer。                          |

# 5. 总体技术架构
> ┌──────────────────────── QuickTranslate.exe ────────────────────────┐
>
> │ Tray / App Lifecycle / Global Hotkey / Single Instance │
>
> │ │ │
>
> │ ▼ │
>
> │ InteractionCoordinator │
>
> │ │ │
>
> │ ┌────────────┴────────────┐ │
>
> │ ▼ ▼ │
>
> │ Word Mode Block Mode │
>
> │ │ │ │
>
> │ └────────────┬────────────┘ │
>
> │ ▼ │
>
> │ Mouse-centered Screen Capture │
>
> │ ▼ │
>
> │ PP-OCRv6 Small / ONNX Runtime │
>
> │ ▼ │
>
> │ OcrLayoutResult │
>
> │ ┌─────────┴─────────┐ │
>
> │ ▼ ▼ │
>
> │ WordSelector BlockSelector │
>
> │ │ │ │
>
> │ └─────────┬─────────┘ │
>
> │ ▼ │
>
> │ SelectionOverlay │
>
> │ ▼ │
>
> │ TranslationRouter + Cache │
>
> │ ┌─────────┴──────────┐ │
>
> │ ▼ ▼ │
>
> │ Local Dictionary Qwen-MT Provider │
>
> │ └─────────┬──────────┘ │
>
> │ ▼ │
>
> │ WordPopup / BlockPopup │
>
> └───────────────────────────────────────────────────────────────────┘

## 5.1 固定技术栈
| **层**   | **技术选择**                | **约束**                                         |
|----------|-----------------------------|--------------------------------------------------|
| 语言     | C#                          | 主代码统一 C#，不引入第二主语言。                |
| Runtime  | .NET 10                     | Windows Desktop。                                |
| UI       | WPF                         | 轻量 Overlay/Popup/Settings；结合 Win32。        |
| 系统能力 | Win32 P/Invoke              | RegisterHotKey、GetCursorPos、窗口样式、DPI 等。 |
| OCR      | PP-OCRv6 Small              | V1 唯一 OCR 主模型。                             |
| 推理     | ONNX Runtime                | V1 CPU baseline；接口预留 WinML。                |
| 网络     | HttpClient                  | 单例/连接池/Keep-Alive。                         |
| 缓存     | Memory LRU + SQLite         | 词典与翻译结果缓存。                             |
| 密钥     | DPAPI 或 Credential Manager | 严禁明文配置文件存储 API Key。                   |

> **架构原则**
>
> 逻辑模块可以拆分，但 V1 运行时保持单进程。禁止为了“微服务化”创建本地服务、IPC 或多进程架构。

# 6. 应用生命周期与线程模型
## 6.1 启动流程
> Process Start
>
> ↓
>
> SingleInstanceGuard (Mutex)
>
> ↓
>
> Load Settings
>
> ↓
>
> Create Tray Icon
>
> ↓
>
> Register Hotkeys
>
> ↓
>
> Create shared HttpClient / TranslationProvider
>
> ↓
>
> Load PP-OCRv6 InferenceSession (后台低优先级)
>
> ↓
>
> Warm-up once
>
> ↓
>
> IDLE

托盘和快捷键的可用性优先于模型加载。OCR 模型可在启动后后台低优先级初始化，但第一笔用户请求必须能够等待同一个初始化 Task，而不是重复加载模型。

## 6.2 线程与并发
| **执行域**    | **职责**                                                                     |
|---------------|------------------------------------------------------------------------------|
| UI STA Thread | WPF Dispatcher、Overlay、Popup、Settings、Tray 交互。                        |
| OCR Worker    | 截图预处理 + Detector + Recognizer；V1 同时只允许 1 个用户 OCR 任务。        |
| Async I/O     | 翻译 HTTP、SQLite 异步访问、配置持久化。                                     |
| Cancellation  | 每次新 Hotkey 创建新的 OperationId/CancellationTokenSource，旧任务立即取消。 |

# 7. 屏幕捕获与坐标系统
## 7.1 Capture 抽象
> public interface IScreenCapture
>
> {
>
> ValueTask\<ScreenFrame\> CaptureAsync(
>
> CaptureRegion region,
>
> CancellationToken cancellationToken);
>
> }

V1 默认实现：GDI BitBlt / CopyFromScreen 级方案，目标是依赖少、初始化快、适合小区域截图。接口必须允许未来增加 Windows Graphics Capture 或 DXGI 实现。

## 7.2 自适应截图
| **模式** | **初始区域**             | **边界策略**                                             |
|----------|--------------------------|----------------------------------------------------------|
| Word     | 约 720×320 DIP 等效区域  | 以鼠标为中心，转换为当前显示器物理像素并裁剪到 Monitor。 |
| Block    | 约 1200×720 DIP 等效区域 | 若候选文本块触碰截图边缘，允许扩大区域并最多重试 1 次。  |

上述尺寸是初始调参值，最终以 Benchmark 为准。禁止每次默认截取整块 4K 屏幕。

## 7.3 坐标规范
> **强制规范**
>
> Core/OCR/Selection 层统一使用 Physical Pixel Coordinates。仅在 WPF UI 最终渲染时转换到 DIP。所有 Rect 必须携带其坐标空间或通过强类型结构避免混用。

- 应用声明 PerMonitorV2 DPI Aware。

- 获取鼠标时保存 MonitorId + PhysicalPoint。

- 跨显示器移动时必须按目标显示器 DPI 重新换算。

- Overlay 在不同 DPI 显示器上不得出现“识别正确但框偏移”的问题。

# 8. OCR Pipeline（PP-OCRv6）
## 8.1 V1 OCR 组成
> ScreenFrame
>
> ↓
>
> Preprocess
>
> ↓
>
> PP-OCRv6 Text Detector
>
> ↓
>
> Detected Text Regions
>
> ↓
>
> Crop / Normalize
>
> ↓
>
> PP-OCRv6 Text Recognizer
>
> ↓
>
> OcrLine\[\] + BoundingBox + Confidence
>
> ↓
>
> WordBoxResolver（Word Mode 需要）
>
> ↓
>
> OcrLayoutResult

V1 不启用文档方向分类、文档矫正、弯曲展开等面向扫描件的重型模块。屏幕文字默认是规则、水平、高对比度内容；额外模块必须通过真实错误样本证明必要后再加入。

## 8.2 OCR Session 生命周期
- InferenceSession 创建一次并复用，禁止每次快捷键重新加载模型。

- 启动后执行一次小尺寸 Warm-up，Warm-up 不得阻塞 Tray Ready。

- 模型文件作为只读 Asset，版本号写入 Settings/About 与日志环境信息。

- OCR 结果按 OperationId 归属，取消后不得再更新 UI。

## 8.3 Word Bounding Box 解析策略
Word Mode 对单词级 Bounding Box 的质量要求高。由于不同 PP-OCRv6 导出/运行路径可能提供不同粒度的位置结果，WordBoxResolver 必须作为独立模块，不得把单词定位写死在 OCR Provider 内。

1\. 优先使用当前选定 PP-OCRv6 推理路径能够稳定输出的 word-level box。

2\. 若只有 line-level box：对英文行做 tokenization，然后在该行 crop 内使用空白间隔/垂直投影/字符区间估计生成 token boxes。

3\. 若初始估计不稳定，允许用轻量图像分割对目标 token box 进行局部收缩/扩张。

4\. WordSelector 只依赖统一的 WordCandidate\[\]，因此未来更换 word-box 算法不影响交互层。

# 9. Selection Engine
## 9.1 WordSelector
> Input:
>
> MousePhysicalPoint
>
> WordCandidate\[\]
>
> Algorithm:
>
> 1\. 先找 BoundingBox Contains(mouse) 的候选。
>
> 2\. 多候选时选面积更小/中心距离更近者。
>
> 3\. 没有 Contains 时，寻找最近 Word，必须小于 MaxAnchorDistance。
>
> 4\. 超过阈值 → 返回 NoTextFound，不猜远处文字。
>
> Output:
>
> SelectedText
>
> SelectedBox
>
> ContextLine
>
> Confidence

初始 MaxAnchorDistance 可使用 max(24px, 1.2 × 行高)；必须进入 Benchmark 配置，不作为不可变常量。

## 9.2 BlockSelector
BlockSelector 的职责是从 OCR 行结果中找出“鼠标所在正文块”，而不是做通用文档版面分析。算法优先稳定、可解释、可调参。

> Mouse
>
> ↓
>
> Nearest / Containing Word or Line = Anchor
>
> ↓
>
> Estimate median line height around Anchor
>
> ↓
>
> Grow upward + downward
>
> ↓
>
> For each adjacent line evaluate:
>
> \- vertical gap
>
> \- line-height similarity
>
> \- horizontal overlap / left alignment
>
> \- same-column likelihood
>
> \- large whitespace break
>
> ↓
>
> Union accepted line boxes
>
> ↓
>
> Add small visual padding
>
> ↓
>
> SelectedBlock

| **启发式**      | **初始建议**               | **说明**                            |
|-----------------|----------------------------|-------------------------------------|
| Vertical gap    | ≤ 0.9 × median line height | 用于排除明显段落间距/标题间距。     |
| Height ratio    | 0.65 - 1.50                | 防止把大标题/小脚注误并入正文。     |
| X overlap       | ≥ 0.35 或左边缘近似        | 适配换行长度不同的正文。            |
| Left edge delta | ≤ 1.5 × line height        | 列表/缩进场景需后续调参。           |
| Edge retry      | 最多 1 次                  | 文本块触碰 Capture 边界时扩大截图。 |

> **实现注意**
>
> 标点不是 Block 的硬停止条件。一个段落可以包含多个句子；块边界应主要由几何布局决定。

# 10. Translation Engine
## 10.1 路由策略
> Word Mode
>
> ↓
>
> L1 Memory Cache
>
> ↓ miss
>
> Local Dictionary / SQLite
>
> ↓ miss or ambiguity needs context
>
> Qwen-MT Lite (text = word, context = OCR line)
>
> ↓
>
> WordPopup
>
> Block Mode
>
> ↓
>
> L1/L2 Translation Cache
>
> ↓ miss
>
> Qwen-MT Lite (Fast default) / Qwen-MT Flash (Balanced)
>
> ↓ Streaming
>
> BlockPopup

V1 的默认目标是“快速翻译”，因此 Fast 模式优先 Qwen-MT Lite；Settings 可提供 Balanced 选项映射到 Qwen-MT Flash。Provider 名称不必暴露在主交互 UI。

## 10.2 Provider 抽象
> public interface ITranslationProvider
>
> {
>
> IAsyncEnumerable\<TranslationChunk\> TranslateAsync(
>
> TranslationRequest request,
>
> CancellationToken cancellationToken);
>
> }
>
> public sealed record TranslationRequest(
>
> string Text,
>
> string? Context,
>
> string SourceLanguage,
>
> string TargetLanguage,
>
> TranslationMode Mode,
>
> Guid OperationId);

- HTTP Client 复用，禁止 per-request new HttpClient。

- 支持 Streaming；UI 收到第一个有效 chunk 后立即展示文本。

- Provider 不得直接操作 WPF UI。

- 网络失败必须返回可分类 ErrorCode，UI 显示短错误，不显示异常堆栈。

## 10.3 Cache
| **层**           | **用途**                         | **Key 建议**                           |
|------------------|----------------------------------|----------------------------------------|
| L1 Memory LRU    | 当前会话高频重复内容，最快命中。 | src\|dst\|mode\|normalizedText         |
| L2 SQLite        | 跨启动复用翻译和词典结果。       | SHA-256(normalized payload) + metadata |
| Local Dictionary | 英文单词常见释义，避免云请求。   | normalized lemma / token               |

Block 文本 Normalize 仅处理空白、换行和明显 OCR 空格差异；不得为了提高缓存命中而改写原文语义或大小写敏感代码标识。

# 11. UI / UX 规范
## 11.1 UI Surface
| **Surface**      | **定位**     | **V1 内容**                             |
|------------------|--------------|-----------------------------------------|
| TrayIcon         | 常驻入口     | 设置、暂停/启用、退出。                 |
| SelectionOverlay | 视觉确认     | 只画选中边界/轻透明底，不承载复杂交互。 |
| WordPopup        | 单词结果     | 单词、词性/简短释义、复制。             |
| BlockPopup       | 段落结果     | 流式译文、复制。                        |
| SettingsWindow   | 唯一普通窗口 | 快捷键、语言、翻译模式、API、开机启动。 |

## 11.2 视觉原则
- 极简、无导航侧栏、无聊天气泡堆叠、无大型 Logo。

- Popup 圆角 8-12 DIP，轻阴影，正文优先；默认宽度 Word 约 300-340 DIP，Block 约 380-480 DIP。

- Overlay 使用 1.5-2 DIP 描边 + 很轻的透明填充；动画总时长建议不超过 120ms。

- Popup 自动选择 SelectionBox 周围空闲象限，避免遮住原文并 Clamp 到当前 Monitor WorkArea。

- Loading 不使用大面积 Spinner；可先显示 Overlay，Popup 在首个翻译 chunk 到达时出现。

## 11.3 焦点规则
> **必须做到**
>
> SelectionOverlay 和翻译 Popup 初始显示时不得抢走用户当前应用焦点。只有用户主动点击 Popup 后，Popup 才进入可交互/可选择文本状态。

实现时使用 WPF + Win32 Window Styles（NoActivate/ToolWindow/TopMost 等）完成；禁止用临时 Activate/Focus 再切回原窗口的方式伪装。

# 12. 状态机、取消与错误处理
> IDLE
>
> │ Hotkey
>
> ▼
>
> CAPTURING
>
> ▼
>
> OCR
>
> ▼
>
> SELECTING
>
> ▼
>
> OVERLAY_VISIBLE
>
> ▼
>
> TRANSLATING
>
> ▼
>
> DISPLAYING
>
> │ Esc / Timeout / New Hotkey
>
> └──────────────────────────► IDLE
>
> New Hotkey at any state:
>
> Cancel current OperationId → Create new OperationId → CAPTURING

| **错误**           | **用户行为**       | **系统处理**                               |
|--------------------|--------------------|--------------------------------------------|
| NoTextFound        | 鼠标附近无可用文字 | 短暂提示“未识别到文字”，不发翻译请求。     |
| OcrFailed          | 模型/推理异常      | 关闭 Overlay，记录技术日志，给短错误提示。 |
| TranslationTimeout | 云服务超时         | 保留已识别框，可显示“翻译超时，重试”。     |
| AuthFailed         | API Key 无效       | 引导打开 Settings，不循环重试。            |
| NetworkUnavailable | 无网络             | Word 仍尝试本地词典；Block 显示离线提示。  |
| Cancelled          | 新快捷键/Esc       | 静默取消，旧结果不得显示。                 |

# 13. 核心数据结构
> public readonly record struct PhysicalPoint(int X, int Y);
>
> public readonly record struct PhysicalRect(int X, int Y, int Width, int Height);
>
> public sealed record OcrLine(
>
> string Text,
>
> PhysicalRect Box,
>
> float Confidence,
>
> IReadOnlyList\<WordCandidate\> Words);
>
> public sealed record WordCandidate(
>
> string Text,
>
> PhysicalRect Box,
>
> float Confidence,
>
> int LineIndex);
>
> public sealed record OcrLayoutResult(
>
> PhysicalRect CaptureRegion,
>
> IReadOnlyList\<OcrLine\> Lines,
>
> TimeSpan InferenceTime);
>
> public sealed record SelectionResult(
>
> string Text,
>
> string? Context,
>
> PhysicalRect Box,
>
> SelectionKind Kind,
>
> float Confidence,
>
> Guid OperationId);

数据结构可以按实现语言细节微调，但模块边界必须保留：Capture 不知道翻译；OCR 不知道 UI；Translation 不知道屏幕坐标；Overlay 不知道模型。

# 14. Settings 与本地持久化
| **设置项**             | **默认/建议** | **说明**                                     |
|------------------------|---------------|----------------------------------------------|
| Word Hotkey            | Alt+1         | 必须可配置并检测冲突。                       |
| Block Hotkey           | Alt+2         | 必须可配置并检测冲突。                       |
| Target Language        | zh-CN         | V1 主路径英语→中文；架构支持扩展。           |
| Translation Quality    | Fast          | Fast=Lite，Balanced=Flash。                  |
| Start with Windows     | Off           | 用户主动启用后写 HKCU Run 或等效用户级方案。 |
| Close on outside click | On            | 可配置。                                     |
| Debug logging          | Off           | 开启前明确说明可能包含技术上下文。           |

普通设置可以 JSON 存储；密钥不得写进普通 JSON。API Key 使用 Windows DPAPI CurrentUser 或 Credential Manager。

# 15. 安全、隐私与日志
## 15.1 隐私边界
- 截图只在内存中存在，OCR 完成后尽快释放 Buffer。

- 默认不保存截图、不保存 OCR 原文到日志。

- 发送云端翻译的只有 OCR 文本和必要上下文，不发送屏幕图像。

- 若未来增加云 OCR，必须作为独立显式功能，不得静默切换。

## 15.2 日志
| **日志级别** | **默认记录**                                                   |
|--------------|----------------------------------------------------------------|
| Info         | OperationId、模式、各阶段耗时、字符长度、模型版本、成功/失败。 |
| Warning      | 超时、低 OCR confidence、Hotkey 冲突、Capture fallback。       |
| Error        | 异常类型、错误码、stack trace（本地文件），不附 OCR 原文。     |
| Debug        | 仅用户显式开启；仍优先哈希/长度而非明文。                      |

# 16. V1 验收标准（Definition of Done）
> **发布门槛**
>
> 所有下列 P0 项必须通过；任何一项失败都不能把 V1 标记为完成。

| **ID** | **级别** | **验收条件**                                                                           |
|--------|----------|----------------------------------------------------------------------------------------|
| AC-01  | P0       | 应用启动后无主窗口，托盘可见，全局快捷键可用。                                         |
| AC-02  | P0       | Word Mode 在 Chrome/Edge/VS Code/PDF/Word 的常规英文文本上能定位鼠标目标单词并显示框。 |
| AC-03  | P0       | Block Mode 能从鼠标锚点选出相邻正文行，明显标题/按钮/下一段不得频繁误并。              |
| AC-04  | P0       | SelectionOverlay 显示时不抢当前应用焦点。                                              |
| AC-05  | P0       | 新的快捷键请求可以取消旧请求，旧翻译结果不得覆盖新 Popup。                             |
| AC-06  | P0       | 多显示器 + 125%/150%/200% DPI 下框位置准确。                                           |
| AC-07  | P0       | 截图不写磁盘；网络请求中不包含图片。                                                   |
| AC-08  | P0       | API Key 不以明文形式存在于 Settings JSON、日志或源码。                                 |
| AC-09  | P0       | Qwen-MT 网络失败时程序不崩溃，并给出可理解错误。                                       |
| AC-10  | P0       | Esc 可从任意 active 状态返回 Idle。                                                    |
| AC-11  | P1       | Word Cache/Dictionary 命中时不发网络请求。                                             |
| AC-12  | P1       | Block 翻译流式输出，首段内容到达即可显示。                                             |
| AC-13  | P1       | 参考设备满足第 4 章性能软目标，或有明确 Benchmark 解释偏差。                           |

# 17. 禁止项与架构护栏
| **禁止项**                           | **原因**                                       |
|--------------------------------------|------------------------------------------------|
| 禁止把截图上传给翻译 API             | 违背本地 OCR 与隐私边界。                      |
| 禁止使用轮询键盘/鼠标作为主触发机制  | 增加常驻 CPU；使用 RegisterHotKey + 事件驱动。 |
| 禁止每次快捷键重新加载 OCR 模型      | 严重破坏首响应时间。                           |
| 禁止在 UI 线程执行 OCR/网络调用      | 会冻结 Overlay/Popup。                         |
| 禁止 TranslationProvider 直接操作 UI | 破坏模块边界和可测试性。                       |
| 禁止在 V1 换成 Electron / Python GUI | 与轻量目标冲突。                               |
| 禁止无需求增加账户/云同步/历史记录   | 防止范围膨胀。                                 |
| 禁止把阈值散落为 magic numbers       | Selection 参数必须集中到可测配置。             |
| 禁止吞异常                           | 任何技术失败必须有 ErrorCode + 可诊断日志。    |
| 禁止使用 OCR 文本作为默认遥测内容    | 只上报耗时、长度、错误码等非内容指标。         |

# 18. Agent 开发计划与里程碑
开发顺序必须先完成本地交互闭环，再接翻译。每个里程碑应能单独构建、运行、测试，不允许一次性堆完所有模块后再调试。

| **里程碑**         | **主要工作**                                                                     | **完成标志**                                         |
|--------------------|----------------------------------------------------------------------------------|------------------------------------------------------|
| M0 - Skeleton      | 创建 WPF/.NET 10 工程、DI/服务注册、日志、Settings、单实例；建立 solution 结构。 | 应用可启动/退出；自动化测试工程可运行。              |
| M1 - Windows Shell | Tray、RegisterHotKey、GetCursorPos、PerMonitorV2 DPI、无焦点 Overlay 原型。      | 按快捷键能在鼠标附近显示测试框且不抢焦点。           |
| M2 - Capture + OCR | IScreenCapture、PP-OCRv6 ONNX Session、预处理/后处理、Warm-up、OcrLayoutResult。 | 能对鼠标附近截图输出文本行、box、confidence 和耗时。 |
| M3 - Word Mode     | WordBoxResolver、WordSelector、Word Overlay、WordPopup、本地词典/Cache stub。    | 可稳定完成“指向词 → 框词 → 显示本地结果”。           |
| M4 - Block Mode    | BlockSelector、边界扩大重试、Block Overlay、BlockPopup。                         | 可完成“指向段落 → 框文本块”。                        |
| M5 - Translation   | ITranslationProvider、Qwen-MT、Streaming、HTTP Keep-Alive、错误码、缓存。        | Word fallback 与 Block 在线翻译闭环完成。            |
| M6 - Hardening     | 取消竞态、多屏 DPI、性能优化、安全存储、异常恢复。                               | P0 验收项全部通过。                                  |
| M7 - Packaging     | Release publish、模型资产、开机启动、About/版本、基础 installer/portable 包。    | 可在干净 Windows 测试机安装/运行/卸载。              |

## 18.1 每个里程碑的 Agent 行为要求
1\. 先写或更新对应接口与最小测试，再实现具体 Provider。

2\. 每完成一个模块，运行 dotnet build + unit tests；不得积累编译错误到下一阶段。

3\. 涉及 UI/DPI/Overlay 的内容必须做实际 Windows 运行验证，不能只做单元测试。

4\. 任何技术栈变更（例如 WPF→WinUI、ONNX Runtime→其他）必须先生成 ADR 并说明收益、成本、回滚路径。

5\. 如果性能不达标，先用阶段耗时数据定位瓶颈，再优化；禁止凭感觉重写架构。

# 19. 测试策略
## 19.1 单元测试
- WordSelector：Contains、最近距离、边界阈值、重叠候选。

- BlockSelector：连续正文、标题分离、列表、两栏、不同字号、段落间距。

- CoordinateMapper：100/125/150/200% DPI，跨显示器。

- TranslationCache：Normalize、Key、过期、并发访问。

- Cancellation：旧 Operation 完成晚于新 Operation 时不得发布结果。

## 19.2 集成/场景测试矩阵
| **场景** | **必须覆盖**                                        |
|----------|-----------------------------------------------------|
| 浏览器   | Chrome / Edge，浅色/深色，常规网页正文。            |
| 开发工具 | VS Code，等宽字体，小字号，深色主题。               |
| 办公     | Word、PowerPoint 阅读视图中的英文文本。             |
| PDF      | Edge/Adobe 等 PDF 阅读器中的正文。                  |
| 多媒体   | 视频字幕/截图文字作为兼容性测试，不作为 V1 硬承诺。 |
| 显示     | 1080p / 2K / 4K；100/125/150/200% DPI；双显示器。   |

# 20. OCR 与交互 Benchmark
项目必须维护一套真实 Windows 屏幕样本集，公开 OCR 榜单只能作为参考。建议开发期先建立 200-500 个内部样本，后续扩展。

| **指标**               | **定义**                                            |
|------------------------|-----------------------------------------------------|
| Word Exact Match       | 鼠标目标单词是否完全识别正确。                      |
| Word Box Hit Rate      | 鼠标落在目标词时，WordSelector 是否选择正确 token。 |
| Line CER/WER           | 文本块文字识别字符/词错误率。                       |
| Block Boundary Success | 选择框是否只覆盖用户意图文本块。                    |
| OCR P50/P95            | 从 ScreenFrame 到 OcrLayoutResult 的延迟。          |
| Hotkey→Overlay P50/P95 | 真实端到端本地反馈延迟。                            |
| Translation TTFT       | 网络请求到首个可显示译文片段。                      |
| Idle Resource          | 稳定 10 分钟后的 CPU、Working Set。                 |

Benchmark 报告必须记录：App commit、模型版本、CPU、内存、Windows 版本、DPI、显示分辨率和网络区域。

# 21. 工程目录建议
> QuickTranslate.sln
>
> src/
>
> QuickTranslate.App/
>
> App.xaml
>
> Bootstrap/
>
> Tray/
>
> Settings/
>
> QuickTranslate.Platform/
>
> Hotkeys/
>
> Capture/
>
> Cursor/
>
> Dpi/
>
> Monitors/
>
> Windows/
>
> QuickTranslate.Ocr/
>
> Abstractions/
>
> PaddleV6/
>
> WordBoxes/
>
> Models/
>
> QuickTranslate.Selection/
>
> WordSelector.cs
>
> BlockSelector.cs
>
> SelectionOptions.cs
>
> QuickTranslate.Translation/
>
> Abstractions/
>
> Qwen/
>
> Cache/
>
> Dictionary/
>
> QuickTranslate.UI/
>
> Overlay/
>
> WordPopup/
>
> BlockPopup/
>
> Settings/
>
> QuickTranslate.Infrastructure/
>
> Persistence/
>
> Security/
>
> Logging/
>
> tests/
>
> QuickTranslate.Selection.Tests/
>
> QuickTranslate.Translation.Tests/
>
> QuickTranslate.Platform.Tests/
>
> benchmarks/
>
> QuickTranslate.Ocr.Benchmarks/
>
> datasets/
>
> docs/
>
> adr/
>
> benchmark-results/

若开发规模较小，可将多个 Class Library 合并为单项目目录，但 namespace/module 边界必须保留。不要为了“看起来架构完整”强制生成过多空项目。

# 22. Release 构建与部署原则
- 优先产出 win-x64 Release；V1 不要求 ARM64。

- 模型文件与 ONNX Runtime native assets 必须有明确版本和校验。

- 开发阶段可 Portable 发布；稳定后再选择 MSI/WiX/Inno 等 installer。

- 开机启动使用用户级配置，不要求管理员权限。

- 卸载时不删除用户缓存/设置前应有明确策略；密钥需要安全清理。

- 发布包中包含第三方许可证/NOTICE 清单，尤其核对 OCR 模型权重和 Runtime。

# 23. V2+ 可扩展项（不进入 V1）
| **能力**        | **扩展方式**                                                               |
|-----------------|----------------------------------------------------------------------------|
| GPU/NPU OCR     | 新增 IOcrExecutionProvider / WinML backend；默认仍按 Auto/Benchmark 选择。 |
| 更多 OCR 模型   | 通过 IOcrEngine Provider 替换，不修改 Selection/UI。                       |
| 离线 MT         | 新增 LocalMtProvider，例如 CTranslate2/NMT；由 TranslationRouter 选择。    |
| 更多语言        | 扩充模型/词典/MT Provider；UI 仍保持两个快捷键。                           |
| OCR Hover Cache | 鼠标静止后低频预识别；必须严格控制 CPU/隐私并由用户开启。                  |
| 历史记录        | 独立模块，不得让主快捷链路依赖历史数据库。                                 |
| 高级解释        | 单独 LLM Provider；不能替换极速翻译热路径。                                |

# 24. Agent 启动指令（可直接交给 Coding Agent）
> **执行要求**
>
> 把本章作为开发 Agent 的第一条任务上下文。Agent 必须以“能运行的最小闭环”为目标逐步实现，不得一次性扩展到 V2。

> 你正在开发 QuickTranslate V1，一个轻量 Windows 后台快捷翻译工具。
>
> 必须遵循本项目说明书：
>
> 1\. 技术栈固定：C# + .NET 10 + WPF + Win32 + PP-OCRv6 Small + ONNX Runtime。
>
> 2\. 单进程、事件驱动、无主窗口；托盘 + 两个全局快捷键是主入口。
>
> 3\. Hotkey 1 = 翻译鼠标对应单词；Hotkey 2 = 翻译鼠标所在文本块。
>
> 4\. OCR 全本地，截图不落盘、不上传；翻译只发送 OCR 文本。
>
> 5\. 每个新 Hotkey 必须取消旧 Operation，旧结果绝不能覆盖新结果。
>
> 6\. Overlay/Popup 初始不得抢焦点；Core 坐标统一使用 Physical Pixels。
>
> 7\. 按 M0 → M7 顺序开发。每个里程碑结束后必须 build/test/运行验证。
>
> 8\. 不得擅自增加账户、历史、聊天、云 OCR、Electron、Python 服务等 V1 外功能。
>
> 9\. 不得修改核心技术栈；确需修改必须先写 ADR。
>
> 10\. 优先建立可测接口和阶段耗时指标，再做性能优化。
>
> 首先执行 M0：创建 solution / projects（或等价 namespace 模块）、基础依赖注入、
>
> Settings、Logging、SingleInstanceGuard，并给出可运行的最小 WPF Tray App。
>
> 完成 M0 后列出文件变更、运行方式、测试结果和下一步 M1 计划。

# 附录 A. 关键决策摘要
| **决策**   | **结论**                                                       |
|------------|----------------------------------------------------------------|
| OCR        | PP-OCRv6 Small，V1 唯一主 OCR。                                |
| OCR 运行   | ONNX Runtime CPU baseline，Session 常驻并 Warm-up。            |
| UI         | WPF + Win32；Tray + Overlay + Popup + Settings。               |
| 截图       | 按快捷键按需截图；V1 GDI 实现，接口预留 WGC/DXGI。             |
| Word 模式  | WordBoxResolver + WordSelector；本地词典/缓存优先。            |
| Block 模式 | 几何 Block Growing；先 Overlay，再在线流式翻译。               |
| MT         | Fast=Qwen-MT Lite；Balanced=Qwen-MT Flash；Provider 可替换。   |
| 常驻       | 无轮询、无持续 OCR，Idle 只保留必要 Session/Cache/HttpClient。 |
| 隐私       | 截图永不上传；默认日志无正文；密钥安全存储。                   |
| 并发       | 单 active Operation + CancellationToken + OperationId。        |

# 附录 B. 术语表
| **术语**               | **含义**                                                     |
|------------------------|--------------------------------------------------------------|
| Word Mode              | 鼠标锚定单词并翻译单词。                                     |
| Block Mode             | 鼠标锚定正文区域并翻译整个文本块。                           |
| SelectionOverlay       | 覆盖在原应用上方的无焦点识别框。                             |
| WordPopup / BlockPopup | 显示翻译结果的小型浮窗。                                     |
| TTFT                   | Time To First Translation，首次可显示译文的时间。            |
| OperationId            | 一次快捷键触发的唯一任务 ID，用于取消和防止旧结果覆盖。      |
| Physical Pixel         | 屏幕真实像素坐标；Core 层统一使用。                          |
| DIP                    | WPF Device Independent Pixel，仅用于 UI 渲染转换。           |
| Provider               | 可替换的具体实现，例如 OCR Provider / Translation Provider。 |
| ADR                    | Architecture Decision Record，记录架构变更原因与影响。       |

**文档结束。V1 开发应以“Hotkey → Local OCR → Selection Overlay → Translation Popup”这条热路径为唯一核心。**
