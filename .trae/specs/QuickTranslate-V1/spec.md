# QuickTranslate V1 - 产品需求文档

## Overview
- **Summary**: QuickTranslate 是一款常驻 Windows 后台的轻量快捷翻译工具。用户将鼠标指向屏幕文字并按下全局快捷键（Alt+1/Alt+2），程序在本地通过 PP-OCRv6 识别鼠标附近文字，在屏幕上显示选择框，再经本地词典/缓存或 Qwen-MT 在线翻译，最后在原位置附近弹出无焦点浮窗展示翻译结果。
- **Purpose**: 解决传统翻译流程"选中→复制→切窗→粘贴→翻译"的繁琐操作，实现"指哪译哪"的即时体验，同时保护用户隐私（OCR 本地执行、截图不落盘不上传）。
- **Target Users**: 需要频繁在浏览器、IDE、文档、PDF 中阅读英文内容的 Windows 用户；对响应速度和隐私敏感的开发者、学生、研究人员。

## Goals
- G1: 建立两条可稳定运行的核心交互热路径：Word Mode（单词翻译）与 Block Mode（文本块翻译）。
- G2: 达到本地链路低延迟：Hotkey→Overlay P50<180ms，模型预热后 P95<350ms。
- G3: 常驻开销低：Idle CPU<0.5%，Idle Working Set<200MB（软目标），无轮询无持续 OCR。
- G4: 多显示器与 125%/150%/200% DPI 下选择框位置准确、Overlay/Popup 不抢焦点。
- G5: 取消/竞态安全：任何时候新快捷键立即取消旧任务，旧结果不得覆盖新 Popup。
- G6: 隐私安全：截图仅在内存、API Key 加密存储、默认日志不含原文、翻译只发文本。

## Non-Goals (Out of Scope)
- NG1: 不做 Electron/WebView/Python GUI 等主框架切换，主代码统一 C# + WPF。
- NG2: 不内置 Python Runtime，不启动本地 Python 服务，不做 IPC/多进程微服务架构。
- NG3: 不做持续后台截图、鼠标悬停持续 OCR、键盘/鼠标轮询触发。
- NG4: 不做聊天机器人、总结、写作、知识库等扩展 AI 功能。
- NG5: 不做账户系统、云同步、历史记录中心、跨设备同步。
- NG6: 不做 PDF 整页解析、表格识别、公式识别、文档工作台功能。
- NG7: V1 不同时维护 Tiny+Small 两套 OCR 模型，仅 PP-OCRv6 Small。
- NG8: 不默认依赖 GPU/NPU，V1 以 CPU baseline 为准，接口仅预留扩展。

## Background & Context
- 本项目基线文档为 `QuickTranslate_Agent_Project_Spec_v0.1.md`，版本 v0.1/V1 设计基线，目标平台 Windows 10/11 x64（优先 Win11）。
- 核心技术栈固定：C# / .NET 10 / WPF / Win32 P/Invoke / PP-OCRv6 Small / ONNX Runtime（CPU）/ SQLite。
- 架构原则：单进程、事件驱动、无主窗口；模块边界严格（Capture→OCR→Selection→Translation→UI），Provider 通过抽象接口可替换。
- 坐标规范：Core/OCR/Selection 层统一使用 Physical Pixel；WPF UI 渲染前转换为 DIP。应用声明 PerMonitorV2 DPI Aware。
- 执行顺序以里程碑 M0→M7 递增：每次只推进一个里程碑，build+test+运行验证通过再进入下一个。

## Functional Requirements
- **FR-1 App Lifecycle**: 托盘常驻、单实例互斥（Mutex）、启动后无主窗口、开机启动可配置；Tray Menu 提供设置/暂停/启用/退出。
- **FR-2 Global Hotkeys**: 2 个可配置全局快捷键（默认 Alt+1 单词 / Alt+2 文本块）；支持冲突检测；Esc 取消当前任务并关闭浮窗。
- **FR-3 Screen Capture**: 按模式自适应截图区域（Word~720×320 DIP, Block~1200×720 DIP）；仅内存 Buffer，禁止落盘；Block 触碰边缘允许扩大并重试一次。
- **FR-4 Local OCR**: PP-OCRv6 Small + ONNX Runtime，Session 一次创建并后台 Warm-up；输出 `OcrLayoutResult = Lines[] + BoundingBox + Confidence + InferenceTime`；Word Mode 能通过 WordBoxResolver 产出单词级候选框。
- **FR-5 WordSelector**: 从鼠标锚点在 WordCandidate[] 中定位目标单词（Contains→更小面积/更近中心→最近邻 MaxAnchorDistance 阈值→否则 NoTextFound）。
- **FR-6 BlockSelector**: 以鼠标所在行为 Anchor 按几何启发式（vertical gap、行高比、X overlap、left edge delta）向上/向下 Growing 合并得到文本块框。
- **FR-7 Selection Overlay**: 原屏幕位置显示选中框（轻描边+浅透明填充），快速动画；全程不抢当前应用焦点。
- **FR-8 Translation Router & Cache**: L1 Memory LRU → L2 SQLite → Local Dictionary → Qwen-MT Provider；Word Dictionary/Cache 命中不发网络。
- **FR-9 Qwen-MT Provider**: 通过 ITranslationProvider 抽象，复用单例 HttpClient；Fast=Lite / Balanced=Flash；Block 支持 Streaming 首个 chunk 到达即显示。
- **FR-10 Popup UI**: WordPopup（单词+简短释义+复制）与 BlockPopup（流式译文+复制）；自动选择屏幕空闲象限、Clamp 到 Monitor WorkArea；点击 Popup 后方可交互/复制。
- **FR-11 Settings Window**: 快捷键配置与冲突检测、目标语言、翻译质量（Fast/Balanced）、开机启动、点击外部关闭、Debug 日志开关；API Key 加密存储（DPAPI/Credential Manager）。
- **FR-12 State Machine & Cancellation**: IDLE→CAPTURING→OCR→SELECTING→OVERLAY_VISIBLE→TRANSLATING→DISPLAYING；任意状态收到新 Hotkey/Esc → Cancel 当前 OperationId → 新 Operation → CAPTURING。
- **FR-13 Error Handling**: 分类错误码（NoTextFound / OcrFailed / TranslationTimeout / AuthFailed / NetworkUnavailable / Cancelled）；UI 只给短错误提示，不显示堆栈；本地技术日志完整。
- **FR-14 Persistence & Security**: 普通配置 JSON 持久化；API Key 永不写入明文 JSON/日志/源码；翻译缓存 SQLite；密钥卸载时安全清理策略。
- **FR-15 Logging**: 默认 Info 记录 OperationId/模式/各阶段耗时/字符数/模型版本/成功失败；Debug 显式开启且优先哈希/长度；严禁默认把 OCR 原文写入日志。

## Non-Functional Requirements
- **NFR-1 Latency**: Tray Ready<1.0s；Hotkey→Overlay P50<180ms/P95<350ms（模型预热后）；Cache/Dict 命中近实时；Block TTFT P50<700ms（网络良好）；取消到 Cancel<100ms。
- **NFR-2 Resource**: Idle CPU 平均<0.5%；Idle Working Set 软目标<200MB，>350MB 必须专项排查。
- **NFR-3 Compatibility/DPI**: 多显示器 + 100%/125%/150%/200% DPI 下框位置误差不得超过 2 个物理像素量级（肉眼不可见偏移）。
- **NFR-4 Privacy**: 截图仅内存 Buffer（持久化=0）；翻译请求体仅 OCR 文本+上下文，不含图片；默认日志不含原文。
- **NFR-5 Security**: API Key 加密（DPAPI CurrentUser 或 Credential Manager）；禁止明文配置/明文日志。
- **NFR-6 Concurrency**: 同一时刻仅 1 个 Active Operation；旧任务完成晚于新任务时不得发布结果。
- **NFR-7 Threading**: OCR/网络/IO 不得占用 UI STA 线程；WPF 仅负责 Dispatcher/Overlay/Popup/Settings/Tray。
- **NFR-8 Testability**: 阈值集中配置（SelectionOptions）、模块抽象接口（IScreenCapture/IOcrEngine/ITranslationProvider/IWordSelector/IBlockSelector），便于单元测试与 Benchmark。

## Constraints
- **Technical**: 固定语言 C# / Runtime .NET 10 / UI WPF + Win32 / OCR PP-OCRv6 Small / 推理 ONNX Runtime（CPU baseline）/ 网络 HttpClient / 缓存 MemoryLRU+SQLite / 密钥 DPAPI 或 Credential Manager；V1 单进程；禁止 Electron、Python Runtime、GPU 默认启用。
- **Business**: V1 目标是热路径稳定+快速+低资源，而非完整翻译平台；所有"额外能力"进入 V2。
- **Dependencies**: PP-OCRv6 Small 模型资产及 ONNX Runtime native assets（需明确版本与校验）；Qwen-MT Lite/Flash HTTP API（需要用户提供 API Key）；Windows 10/11 x64。

## Assumptions
- A1: 用户运行环境为 Windows 10/11 x64，.NET 10 Desktop Runtime 可用（或由发布包 self-contained 提供）。
- A2: Qwen-MT Lite/Flash 提供稳定 HTTP 流式接口，支持 Keep-Alive。
- A3: PP-OCRv6 Small ONNX 模型在目标 CPU 上延迟可满足 Hotkey→Overlay P95<350ms（模型预热后）。
- A4: 屏幕内容多为规则水平高对比度文字；扫描件/弯曲文本等场景不作为 V1 承诺。
- A5: 用户至少有 1 个可用显示器；多显示器配置为 Windows 标准扩展桌面。

## Acceptance Criteria

### AC-1: 托盘常驻与基础生命周期 (P0)
- **Given**: 用户在 Windows 10/11 x64 上正常启动 QuickTranslate.exe
- **When**: 启动完成（Tray Ready<1.0s）
- **Then**: 任务栏可见托盘图标；不弹出主窗口；已注册两个全局快捷键；进程为单实例（再次启动不会出现第二实例）
- **Verification**: `programmatic`（构建后启动可观测托盘+无主窗；第二次启动进程列表仅 1 个；可通过日志/Tray Menu 验证快捷键已注册） + `human-judgment`（实际操作体验）
- **Notes**: 同时覆盖 FR-1 / NFR-1 / FR-14 的单实例部分。

### AC-2: Word Mode 定位并框选目标单词 (P0)
- **Given**: 用户在 Chrome/Edge/VS Code/PDF/Word 任一应用中打开含常规英文单词的界面，鼠标位于目标单词上/附近
- **When**: 按下 Hotkey 1（默认 Alt+1）
- **Then**: Selection Overlay 显示包围目标单词的矩形框；框位置肉眼与单词对齐；随后进入翻译流程（本地或在线）
- **Verification**: `human-judgment`（场景矩阵：浏览器/IDE/PDF/Word/浅色/深色，每类至少 5 个样本） + `programmatic`（WordSelector 单元测试覆盖 Contains/最近邻/阈值/重叠候选）
- **Notes**: 对应 FR-5 / FR-7 / NFR-3。

### AC-3: Block Mode 文本块选择不误并标题/下一段 (P0)
- **Given**: 用户将鼠标停在正文段落中间
- **When**: 按下 Hotkey 2（默认 Alt+2）
- **Then**: Overlay 只覆盖当前段落（或上下文连续正文行）；明显的大标题、小脚注、按钮文本不得频繁误并入；下一段不跨越明显段落间距
- **Verification**: `human-judgment`（典型多段落文章、列表、两栏、代码注释块 ≥ 20 场景，误并率 ≤ 10%） + `programmatic`（BlockSelector 单元测试覆盖连续正文/标题分离/列表/两栏/不同字号/段落间距）
- **Notes**: 对应 FR-6 / FR-7。

### AC-4: Overlay/Popup 初始不抢焦点 (P0)
- **Given**: 用户正在前台应用（例如 Word/浏览器输入框）中输入文字
- **When**: 按下任一快捷键，Overlay 与 Popup 依次显示
- **Then**: 前台应用输入焦点保持不变，仍可继续输入文字；只有用户用鼠标主动点击 Popup 后 Popup 才可选择/复制
- **Verification**: `human-judgment`（用 WPF+Win32 NoActivate/ToolWindow/TopMost 样式；禁用 Activate/Focus 伪装法）
- **Notes**: 对应 FR-7 / FR-10 / NFR-6 焦点规则。

### AC-5: 新请求取消旧请求 (P0)
- **Given**: 上一快捷键任务仍在 OCR/翻译中（可故意用慢网络或大文本）
- **When**: 用户在新位置再次按下任一快捷键
- **Then**: 上一 OperationId 立即取消（<100ms 进入取消态）；旧的翻译结果绝不能覆盖新 Popup；界面只显示最新任务的 Overlay 和 Popup
- **Verification**: `programmatic`（Cancellation 单元测试：旧 Operation 完成晚于新 Operation 时不得发布结果；日志中 Cancel 完成时间差） + `human-judgment`（人工快速连按两次快捷键验证）
- **Notes**: 对应 FR-12 / NFR-1。

### AC-6: 多屏与多 DPI 下框位准确 (P0)
- **Given**: 用户使用双（多）显示器，分别设置 100%/125%/150%/200% DPI，分辨率 1080p/2K/4K
- **When**: 在每个显示器上执行 Word Mode 与 Block Mode 各 5 次
- **Then**: Overlay 框与目标文本对齐，不出现 DPI 导致的偏移（偏移不得超过肉眼可接受 2px 级）；跨屏移动后选择框仍准确
- **Verification**: `human-judgment`（场景矩阵：100/125/150/200% × 单屏/双屏） + `programmatic`（CoordinateMapper 单元测试）
- **Notes**: 对应 FR-3 / FR-7 / NFR-3。

### AC-7: 截图不落盘、翻译请求无图片 (P0)
- **Given**: 用户执行任意次 Word/Block 翻译
- **When**: 观察磁盘 I/O 与网络请求体
- **Then**: 整个热路径截图不写入任何磁盘文件（仅内存 Buffer）；发往 Qwen-MT 的 HTTP 请求体只包含 OCR 文本/上下文等 JSON 字段，不包含 base64/png/jpg 图像载荷
- **Verification**: `programmatic`（临时文件/抓包审计：检查用户临时目录、程序目录、%TEMP% 新增文件；HTTP 请求体 JSON schema 校验） + `human-judgment`（开启调试代理检查真实请求体）
- **Notes**: 对应 FR-3 / NFR-4 / 禁止项"禁止上传截图给翻译 API"。

### AC-8: API Key 安全存储 (P0)
- **Given**: 用户在 Settings 中填入 API Key 并保存
- **When**: 检查配置 JSON 文件、日志文件、源码中的 API Key 痕迹
- **Then**: 明文配置文件中不存在 API Key；默认日志中不出现 API Key；源码不硬编码任何生产 Key；只能通过 DPAPI/Credential Manager 加密读取
- **Verification**: `programmatic`（grep/扫描 Settings JSON、日志目录、编译产物；单元测试验证密钥存储走 DPAPI/Credential Manager）
- **Notes**: 对应 FR-11 / NFR-5。

### AC-9: 网络失败不崩溃且给出可理解错误 (P0)
- **Given**: Qwen-MT 接口返回 4xx/5xx、断网、超时、API Key 无效四种场景
- **When**: 触发 Block 或 Word 翻译（本地未命中）
- **Then**: 应用不崩溃、不退出；UI 显示可理解的短错误（例如"翻译超时，请重试""API Key 无效，请在设置中检查"）；本地技术日志记录错误码与异常堆栈（不含原文）
- **Verification**: `programmatic`（翻译 Provider 单元/集成测试覆盖超时/401/500/断网） + `human-judgment`（真实断网/填错 Key 观察 UI 表现）
- **Notes**: 对应 FR-13 / FR-9。

### AC-10: Esc 可从任意 Active 状态返回 Idle (P0)
- **Given**: 应用处于 CAPTURING / OCR / SELECTING / OVERLAY_VISIBLE / TRANSLATING / DISPLAYING 任一 Active 状态
- **When**: 用户按下 Esc
- **Then**: 当前任务取消、Overlay 与 Popup 全部关闭，应用回到 IDLE；后续新快捷键可正常工作
- **Verification**: `human-judgment`（在每个阶段按 Esc 做 6 轮验证） + `programmatic`（状态机单测验证转移与 Cancel）
- **Notes**: 对应 FR-12 / FR-2。

### AC-11: Word 本地缓存/词典命中不发网络请求 (P1)
- **Given**: 同一单词在前一次翻译中已命中并写入本地 Dictionary 或 Cache
- **When**: 用户再次对该单词执行 Word Mode
- **Then**: 不产生任何 Qwen-MT HTTP 请求；WordPopup 立即显示结果（近实时）
- **Verification**: `programmatic`（抓包/计数：HttpClient mock 断言请求未被调用；Cache/Dictionary 单测命中） + `human-judgment`（重复翻译同一词观察延迟与代理抓包）
- **Notes**: 对应 FR-8 / NFR-1。

### AC-12: Block 翻译流式输出、首个 Chunk 即显示 (P1)
- **Given**: 网络良好且目标文本块较长（≥ 3 句）
- **When**: 执行 Block Mode 翻译
- **Then**: 首个可显示译文 Chunk 到达后立即在 BlockPopup 中显示，后续 Chunk 增量追加；无需等待完整响应
- **Verification**: `human-judgment`（肉眼观察流式滚动） + `programmatic`（IAsyncEnumerable 单测/集成测试断言首 chunk 到达即触发 UI 渲染事件）
- **Notes**: 对应 FR-9 / FR-10 / NFR-1。

### AC-13: 性能软目标达成或有基准解释 (P1)
- **Given**: 在参考 CPU 设备（至少一台 4C/8T 级以上 x64 笔记本）与至少两档对比设备上运行
- **When**: 采集 Idle CPU / Idle Working Set / Hotkey→Overlay P50/P95 / OCR P50/P95 / TTFT / Cancel Latency
- **Then**: 结果要么达到第 4 章预算，要么在 Benchmark 报告中明确说明偏差原因（硬件/模型版本/系统负载），并给出可执行优化建议
- **Verification**: `programmatic`（Benchmark 控制台输出 P50/P95 数值与环境元数据） + `human-judgment`（审阅 benchmark-results 报告）
- **Notes**: 对应 NFR-1 / NFR-2。

## Open Questions
- [ ] Qwen-MT Lite/Flash 具体 HTTP 端点与鉴权方式（Header 名称、是否支持 SSE/Chunked streaming）以哪个官方版本为准？需最终确认以便固化 Qwen Provider 实现。
- [ ] PP-OCRv6 Small ONNX 的具体导出版本（Detector/Recognizer 输入 shape、后处理约定、是否自带 word-box 输出）是否有指定下载包/MD5？
- [ ] Local Dictionary 的初始词库来源（是否允许使用开源词典数据集 + 体积上限多少 MB）未在 v0.1 文档中指定。
- [ ] 开机启动实现采用 HKCU Run 还是 Task Scheduler 用户级任务？两者在不同 Windows 版本/企业组策略下兼容性差异需要确认。
