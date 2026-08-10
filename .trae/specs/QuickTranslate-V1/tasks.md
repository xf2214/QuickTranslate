# QuickTranslate V1 - 实现计划（里程碑 M0→M7，按顺序执行）

> **规则**：按顺序一次只推进一个任务；每个任务结束时必须更新 `tasks.md`（`[ ]`→`[/]`→`[x]`）和 `checklist.md`，并运行 `dotnet build` + 对应单元测试。涉及 UI/DPI/Overlay 的内容必须实际 Windows 运行验证。

## 里程碑 M0 - Skeleton（工程骨架与基础设施）

### [ ] Task M0.1: 创建 Solution 与项目结构、Git 初始化、.editorconfig/Directory.Build.props
- **Priority**: high
- **Depends On**: None
- **Description**:
  - 在 `e:\翻译\` 下初始化 git 仓库（`git init`）并创建 `.gitignore`（排除 bin/obj/packages/模型本地副本等）。
  - 创建 `QuickTranslate.sln` 与项目结构（按 v0.1 §21，结合"小而精"原则，namespace 边界优先于项目数量）：
    - `src/QuickTranslate.App/`（WPF 可执行项目，含 App.xaml/Bootstrap/Tray/Settings 入口）
    - `src/QuickTranslate.Core/`（Core 层：Ocr/Selection/Translation 抽象与数据结构；不依赖 WPF）
    - `src/QuickTranslate.Platform/`（Win32/Hotkeys/Capture/Dpi/Monitors/Cursor/Windows）
    - `src/QuickTranslate.Infrastructure/`（Persistence/Security/Logging/Cache SQLite）
    - `tests/QuickTranslate.Tests/`（xUnit 单元测试，含 Selection/Translation/Cancellation 等）
  - 添加 `Directory.Build.props`（LangVersion latest、nullable enable、ImplicitUsings、TFM net10.0-windows、Platform x64）与 `.editorconfig`（C# 代码风格统一）。
  - 为所有项目引用 Microsoft.Extensions.DependencyInjection、Logging、Options、Configuration 基础包（统一版本）。
- **Acceptance Criteria Addressed**: AC-1（基础）
- **Test Requirements**:
  - `programmatic` TR-M0.1.1: `dotnet build QuickTranslate.sln -c Debug` 成功，零错误零警告（允许仅针对包版本的信息提示）。
  - `programmatic` TR-M0.1.2: `dotnet new gitignore` + `git status` 仅列出源码/配置，无二进制垃圾。
  - `human-judgement` TR-M0.1.3: 目录结构与 namespace 命名清晰，模块依赖方向单向（App→Infrastructure/Platform→Core）。
- **Notes**: 暂不引入 OCR/ONNX/WPF 以外重型 NuGet；后续里程碑按需添加。优先 `UseWPF=true` 的 SDK 风格项目。

### [ ] Task M0.2: 单实例 SingleInstanceGuard、App 生命周期、DI 容器、Settings/Logging 基础
- **Priority**: high
- **Depends On**: M0.1
- **Description**:
  - 实现 `SingleInstanceGuard`（全局命名 Mutex + 尝试占有），第二次启动立即退出，可选择向已有实例发送 WM_消息（预留）。
  - `App.xaml.cs` OnStartup：不创建 MainWindow；走 Bootstrap；注册 `IServiceProvider`（Microsoft DI）：
    - Settings（IOptions<AppSettings> + JSON 文件 `%APPDATA%\QuickTranslate\settings.json`）
    - Logging（Serilog 或 Microsoft.Extensions.Logging + 文件，滚动到 `%LOCALAPPDATA%\QuickTranslate\Logs\`）
    - Infrastructure 注册（后续 SQLite/Cache/密钥）
  - 定义强类型 `AppSettings`：WordHotkey/BlockHotkey/TargetLanguage/TranslationQuality/StartWithWindows/CloseOnOutsideClick/DebugLogging 等。
  - 提供 `ITrayIconService` / `IAppLifecycle` 接口（具体实现在 M1 完成）。
- **Acceptance Criteria Addressed**: AC-1
- **Test Requirements**:
  - `programmatic` TR-M0.2.1: 启动两个进程实例，第二个 3 秒内退出，ExitCode!=0。
  - `programmatic` TR-M0.2.2: 首次启动后 `settings.json` 生成且默认值与文档一致（Alt+1/Alt+2、zh-CN、Fast 等）。
  - `programmatic` TR-M0.2.3: 日志目录生成 `QuickTranslate-.log`，至少一条 Info 启动日志。
- **Notes**: Settings JSON 中不得包含任何名为 ApiKey 的明文字段；ApiKey 字段留在 M6 引入 Security 模块。

### [ ] Task M0.3: xUnit 测试工程可运行 + 基础桩
- **Priority**: medium
- **Depends On**: M0.1, M0.2
- **Description**:
  - `tests/QuickTranslate.Tests/` 使用 xUnit + Microsoft.NET.Test.Sdk + Moq/NSubstitute。
  - 添加最小 smoke test：例如 `ServiceCollectionBootstrapTests` 能构造 ServiceProvider、解析 `IOptions<AppSettings>`。
  - 建立 tests 共享 `TestOutputHelper` / `ITestOutputHelper` 桥接日志。
- **Acceptance Criteria Addressed**: AC-1（测试基础）
- **Test Requirements**:
  - `programmatic` TR-M0.3.1: `dotnet test tests/QuickTranslate.Tests/` 全部通过（≥ 1 个 smoke test）。
  - `human-judgement` TR-M0.3.2: 测试目录结构清晰（按 Core/Platform/Selection/Translation 分子目录）。
- **Notes**: 后续每个里程碑都要在该工程中追加对应单测。

---

## 里程碑 M1 - Windows Shell（托盘/快捷键/鼠标/DPI/无焦点 Overlay 原型）

### [ ] Task M1.1: Win32 封装层：全局热键、鼠标坐标、显示器枚举、DPI、窗口样式
- **Priority**: high
- **Depends On**: M0.2
- **Description**:
  - 在 QuickTranslate.Platform 中实现：
    - `IGlobalHotkeyService`：`RegisterHotKey(Modifiers, VK, id)` + `UnregisterAll()`，基于隐藏 NativeWindow/HwndSource 接收 WM_HOTKEY。
    - `ICursorService`：`GetPhysicalCursorPos()` 返回 `PhysicalPoint` + `MonitorId`（使用 GetCursorPos + MonitorFromPoint）。
    - `IMonitorService`：枚举所有显示器 Physical Rect + WorkArea + DPI（PerMonitorV2；GetDpiForMonitor）。
    - `IDpiMapper`：Physical ↔ DIP 互转（按指定 Monitor Dpi）；并确保所有 Rect/Point 强类型避免混用。
    - `WindowStyleHelper`：设置 WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW / WS_EX_TOPMOST 以及 ShowWithoutActivation 策略。
  - 所有 P/Invoke 归到 `UnmanagedMethods/` 内联类并使用 `LibraryImport`（.NET 7+ 风格）。
- **Acceptance Criteria Addressed**: AC-1, AC-6, AC-4
- **Test Requirements**:
  - `programmatic` TR-M1.1.1: 单元测试验证 `DpiMapper` 在 100/125/150/200% 下转换对称（roundtrip 误差=0）。
  - `programmatic` TR-M1.1.2: 非 UI 单元测试能构造 `PhysicalPoint/PhysicalRect` 并序列化/相等语义稳定。
  - `human-judgement` TR-M1.1.3: 在双屏不同 DPI 机器上实际跑 Demo，MonitorEnum 正确显示两屏 DPI 与 Rect。
- **Notes**: 本任务优先于实际 UI，确保 Platform 层可独立单测。

### [ ] Task M1.2: TrayIcon + TrayMenu（设置/暂停/启用/退出）、无主窗口启动
- **Priority**: high
- **Depends On**: M1.1
- **Description**:
  - 使用 System.Windows.Forms.NotifyIcon（或 WPF 兼容库 Hardcodet.NotifyIcon.Wpf）实现托盘图标与菜单。
  - 菜单项：打开设置、暂停/启用快捷键、关于（占位）、退出。
  - App Startup：仅显示托盘，不打开主窗；App Exit：释放 Mutex、注销热键、写最后一条 Info 日志。
  - 通过 `IAppLifecycle` 暴露 Pause/Resume/Shutdown 给协调层调用。
- **Acceptance Criteria Addressed**: AC-1
- **Test Requirements**:
  - `human-judgement` TR-M1.2.1: 启动后托盘图标可见，菜单点击"退出"可干净退出无残留。
  - `human-judgement` TR-M1.2.2: 第二次启动无第二托盘/窗口出现，仅原实例继续运行（与 M0.2 联动验证）。
- **Notes**: 图标资源可先用占位 ico；后续 M7 再替换正式图标。

### [ ] Task M1.3: 全局快捷键注册与热键事件管线 + Esc 取消钩子
- **Priority**: high
- **Depends On**: M1.1, M1.2
- **Description**:
  - Bootstrap 在启动时从 AppSettings 读取 WordHotkey/BlockHotkey 并注册两个 id；失败则记录 Warning 并在 Tray Menu 提示。
  - 定义 `IHotkeyBroker` 暴露 `IObservable<HotkeyEvent>`（Word/Block/Esc）；Esc 用 RegisterHotKey(VK_ESCAPE) 或组件级键盘钩子（优先前者，避免全局副作用）。
  - 在 UI STA 线程中处理，所有回调转发到 `InteractionCoordinator`（先定义接口，M2+ 逐步填实现）。
  - Settings 中 Hotkey 修改必须先 Unregister 再 Register；检测冲突并提示用户。
- **Acceptance Criteria Addressed**: AC-1, AC-10
- **Test Requirements**:
  - `programmatic` TR-M1.3.1: 模拟 HotkeyBroker 在测试桩下发送 Word/Block/Esc，Coordinator 能收到正确枚举。
  - `human-judgement` TR-M1.3.2: 实际按 Alt+1/Alt+2/Esc，分别写入日志一条 Info 记录。
- **Notes**: Esc 的全局注册需要注意与其他应用冲突；若后续发现冲突，可切换为仅在 Overlay/Popup 可见时通过 HwndSource 处理。

### [ ] Task M1.4: SelectionOverlay 原型（无焦点、描边、DPI 正确）
- **Priority**: high
- **Depends On**: M1.1, M1.3
- **Description**:
  - WPF Window：`SelectionOverlayWindow`，对应每显示器一个或按需创建；使用 WindowStyle=None、AllowsTransparency=True、Background=Transparent、Topmost=True。
  - 通过 WindowStyleHelper 强制 WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW，重写 `ShowWithoutActivation` 返回 true；禁止任何 `Activate()/Focus()`。
  - 提供 `ISelectionOverlayService`：`Show(Rect physicalBox, MonitorId)`、`Update(Rect)`、`Hide()`、`HideAll()`。内部将 PhysicalRect 按 DPI 转 DIP，再画 1.5-2 DIP 描边 Border + 轻透明填充。
  - Demo：手动/测试工具调用 Show 后，当前前台输入框焦点不丢失。
- **Acceptance Criteria Addressed**: AC-4, AC-6
- **Test Requirements**:
  - `human-judgement` TR-M1.4.1: 在记事本输入文字时按快捷键触发 Overlay Demo，光标仍在记事本且继续打字不被打断。
  - `human-judgement` TR-M1.4.2: 125%/150%/200% DPI 下调用 Show 指定同一个 `PhysicalRect(100,100,200,40)`，实际位置与 Windows Paint 画的同坐标矩形视觉重合。
  - `programmatic` TR-M1.4.3: 通过反射/Win32 读取窗口样式，确认 WS_EX_NOACTIVATE & WS_EX_TOPMOST 存在。
- **Notes**: 本里程碑只做"显示一个测试框"，真实选择框由 M2/M3 传入。

---

## 里程碑 M2 - Capture + OCR（截图 + PP-OCRv6 + 输出 OcrLayoutResult）

### [ ] Task M2.1: IScreenCapture 抽象 + GDI 默认实现（仅内存）
- **Priority**: high
- **Depends On**: M1.1
- **Description**:
  - 定义 `CaptureRegion(PhysicalPoint center, Size dipSize)` 与 `ScreenFrame(Bitmap pixelBuffer, PhysicalRect region, MonitorId)`。
  - `GdiScreenCapture : IScreenCapture`：Word 模式 ~720×320 DIP、Block 模式 ~1200×720 DIP；转像素后 Clamp 到 Monitor PhysicalRect。
  - 使用 Graphics.CopyFromScreen / BitBlt 到内存 Bitmap；**不得 Save**；实现 Dispose 尽快释放像素缓冲区。
  - 提供参数 `ICaptureRegionPolicy` 返回 Word/Block 的 DIP 区域，便于 Benchmark 调参。
- **Acceptance Criteria Addressed**: AC-7
- **Test Requirements**:
  - `programmatic` TR-M2.1.1: 执行 CaptureAsync 100 次，检查 `%TEMP%`、程序目录、`%LOCALAPPDATA%` 下不产生 .png/.bmp/.jpg 文件。
  - `programmatic` TR-M2.1.2: `ScreenFrame.Region` Width/Height 与 Bitmap 像素尺寸一致（无 DIP 混用）。
  - `human-judgement` TR-M2.1.3: 将 Demo Bitmap 显示到调试窗口，肉眼能看到鼠标周围内容（只在 Debug 临时开启）。
- **Notes**: 禁止任何 Debug 代码默认写入文件；需要可视化调试时提供 `#if DEBUG` 且开关化的临时代码。

### [ ] Task M2.2: PP-OCRv6 抽象 + ONNX Runtime 集成、模型资产放置、Warm-up
- **Priority**: high
- **Depends On**: M2.1
- **Description**:
  - 定义 `IOcrEngine`：`ValueTask<OcrLayoutResult> RecognizeAsync(ScreenFrame frame, OcrOptions opts, CancellationToken ct)`。
  - 添加 ONNX Runtime C# NuGet（Microsoft.ML.OnnxRuntime）；实现 `PpOcrV6Engine`：
    - Detector 与 Recognizer 两个 InferenceSession（启动时后台 Task.Run 低优先级创建一次）。
    - 启动后立即 Warm-up 一次小尺寸（随机或内置小图）；Warm-up 不阻塞 Tray Ready；首个用户请求等待同一初始化 Task。
  - 模型资产放入 `src/QuickTranslate.Ocr/Models/`（或 `assets/models/`），在 csproj 中设置 CopyToOutput。版本号写进 `AppEnvironmentModel` 与启动 Info 日志。
  - 预处理：`Bitmap` → 规范化 byte[] 张量；后处理：DB/CTC 输出 → 行级 text + Box + Confidence。
- **Acceptance Criteria Addressed**: AC-2（基础），AC-13
- **Test Requirements**:
  - `programmatic` TR-M2.2.1: 连续两次 RecognizeAsync，第二次不会再次创建 InferenceSession（日志仅一次"Session created"）。
  - `programmatic` TR-M2.2.2: Warm-up 调用与首个 Recognize 调用共享同一个初始化 TaskCompletionSource（单元测试用桩验证）。
  - `human-judgement` TR-M2.2.3: Demo 中对浏览器真实文字截图输出至少非空 OcrLine[]。
- **Notes**: V1 固定 CPU EP；不启用文档方向/弯曲展开。若 PP-OCRv6 只输出 line-level，则 WordBox 在 M3 处理。

### [ ] Task M2.3: OcrLayoutResult 数据结构 + 标准耗时埋点
- **Priority**: medium
- **Depends On**: M2.2
- **Description**:
  - Core 层定义 `PhysicalPoint/PhysicalRect/OcrLine/WordCandidate/OcrLayoutResult` 强类型（见 v0.1 §13）。
  - RecognizeAsync 使用 Stopwatch 记录：PreprocessMs / DetectorMs / RecognizerMs / PostprocessMs 并写入日志。
  - 输出 `OcrLayoutResult.InferenceTime`。
- **Acceptance Criteria Addressed**: AC-13
- **Test Requirements**:
  - `programmatic` TR-M2.3.1: 单元测试能构造并验证数据结构的 Equality/Deconstruction（record 语义）。
  - `programmatic` TR-M2.3.2: 日志断言包含 Preprocess/Detector/Recognizer/Postprocess 四段耗时字段（Structured Logging 属性）。
- **Notes**: 暂不做 WordCandidate[] 填充（除 OCR 自带 word-box 外），留给 M3 WordBoxResolver。

---

## 里程碑 M3 - Word Mode（词选框+Overlay+Popup+本地词典/Cache stub）

### [ ] Task M3.1: WordBoxResolver + WordSelector + SelectionOptions 配置
- **Priority**: high
- **Depends On**: M2.3
- **Description**:
  - `WordBoxResolver`：输入 `OcrLine` + 该行原始 crop（若 OCR 不给 word-box），按空白间隔/垂直投影/字符区间估计生成 `WordCandidate[]`；策略可扩展但 V1 仅启用 1 套默认。
  - `WordSelector`：实现 §9.1 算法，集中所有阈值到 `SelectionOptions`（MaxAnchorDistance = max(24px, 1.2 × 行高) 等），避免 magic numbers。
  - 输出 `SelectionResult { Text, Context, Box, Kind=Word, Confidence, OperationId }`。
- **Acceptance Criteria Addressed**: AC-2, AC-11
- **Test Requirements**:
  - `programmatic` TR-M3.1.1: WordSelector 单测覆盖：Contains 命中、多候选选更小/更近、No Contains 且距离 < Max 选最近、超过阈值 → NoTextFound。
  - `programmatic` TR-M3.1.2: SelectionOptions 单测验证默认值与文档一致。
  - `human-judgement` TR-M3.1.3: 人工构造典型 OcrLine+鼠标点样例 ≥ 20，肉眼检查 SelectionResult 是否对齐。
- **Notes**: 测试数据放入 `tests/QuickTranslate.Tests/Selection/TestCases/`（纯对象构造，不依赖真实 OCR）。

### [ ] Task M3.2: InteractionCoordinator 状态机（Word 路径） + OperationId/Cancellation
- **Priority**: high
- **Depends On**: M1.4, M3.1
- **Description**:
  - 实现 InteractionCoordinator：Hotkey → 新 OperationId + CTS → Cancel 旧 CTS → CAPTURING → … → OVERLAY_VISIBLE。
  - 状态机：枚举 `AppState.Idle/Capturing/Ocr/Selecting/OverlayVisible/Translating/Displaying`；任意状态 NewHotkey/Esc 强制切回取消然后回到 Capturing 或 Idle。
  - 每次完成晚于当前 OperationId 的结果必须被丢弃（比较版本号/时间戳）。
- **Acceptance Criteria Addressed**: AC-5, AC-10, AC-2
- **Test Requirements**:
  - `programmatic` TR-M3.2.1: 状态机单测：NewHotkey 在 Ocr/Translating 时能够触发 Cancel 且旧 Task 结果最终被抑制（通过 TaskCompletionSource 模拟迟到结果）。
  - `programmatic` TR-M3.2.2: Esc 从 6 个 Active 状态分别进入 Idle，Overlay/Hidden 状态正确。
  - `human-judgement` TR-M3.2.3: 连续快速按 Alt+1 10 次，最终只显示最后一次 Overlay，不出现前一次内容。
- **Notes**: 这是后续所有模式共享的核心，务必单测覆盖竞态。

### [ ] Task M3.3: WordPopup（无焦点→点击可交互）+ 本地词典/Cache Stub
- **Priority**: high
- **Depends On**: M3.2
- **Description**:
  - `WordPopupWindow`：WPF + WS_EX_NOACTIVATE，默认尺寸 ~300-340 DIP 宽，圆角 8-12 DIP，轻阴影。
  - Popup 定位：在 SelectionBox 上下左右四象限中选可用空间最大者，Clamp 到 Monitor WorkArea。
  - 内容：单词词头、简短释义区、复制按钮；初始不激活，点击 Popup 内任一处后切换为可交互（可选择文本/按复制）。
  - 翻译 Router 先接 Stub：`ITranslationRouter.TranslateWordAsync` 先走 L1（空）→ 本地 Dictionary（stub 返回占位词条/或空）→ 返回空则标记"需在线"（M5 接真实）。
  - Cache：定义 `ITranslationCache`（Memory LRU 接口 + SQLite 接口），M3 先实现 Memory LRU，SQLite 在 M5/M6。
- **Acceptance Criteria Addressed**: AC-2, AC-4, AC-11
- **Test Requirements**:
  - `human-judgement` TR-M3.3.1: 弹出 Popup 瞬间，前台窗口仍有输入焦点；点击 Popup 后复制按钮可工作。
  - `human-judgement` TR-M3.3.2: Popup 不会超出屏幕工作区；在屏幕四角附近 4 次操作均能完整显示。
  - `programmatic` TR-M3.3.3: LRU Cache 单测：命中/淘汰（容量 N=2，第三项使第一项淘汰）、并发读写无异常。
- **Notes**: 字典词库来源未确定前，可先用内置 1000 高频词最小词典 demo 资源（正式词库 M6 前确认）。

---

## 里程碑 M4 - Block Mode（BlockSelector + 扩大重试 + BlockPopup）

### [ ] Task M4.1: BlockSelector（几何启发式 Growing）+ 边界扩大重试
- **Priority**: high
- **Depends On**: M2.3, M3.2
- **Description**:
  - `BlockSelector` 实现 §9.2：鼠标行 Anchor → 估计中值行高 → Upward/Downward Growing，按 vertical gap ≤ 0.9 × median、height ratio 0.65-1.50、X overlap ≥ 0.35 或 left edge delta ≤ 1.5× 行高启发式评估相邻行；接受则 Union Box。
  - 阈值集中到 `SelectionOptions.Block*`，所有常量进入 Options。
  - 边界扩大重试：若 Union Box 的 Left/Top/Right/Bottom 与 CaptureRegion 边缘距离小于 `EdgeRetryThreshold`，重新调用 `IScreenCapture` 扩大区域并重 OCR，最多重试 1 次。
- **Acceptance Criteria Addressed**: AC-3
- **Test Requirements**:
  - `programmatic` TR-M4.1.1: BlockSelector 单测覆盖：连续正文合并、大标题被排除、脚注行排除、两栏场景不分栏、列表缩进仍可并入、跨段间距被打断（共 ≥ 15 case）。
  - `programmatic` TR-M4.1.2: 边界重试：构造触碰左边缘的 Anchor，断言协调器总共调用 CaptureAsync 2 次。
  - `human-judgement` TR-M4.1.3: 真实网页多段落文档 10 处随机测试，误并率 ≤ 10%。
- **Notes**: 启发式初始值遵循文档；Benchmark 数据集建立后再逐步调参。

### [ ] Task M4.2: BlockPopup（流式显示、复制按钮）+ 定位/自动避让
- **Priority**: high
- **Depends On**: M4.1, M3.3
- **Description**:
  - `BlockPopupWindow`：默认 ~380-480 DIP 宽、最小高 120 DIP、最大高约 600 DIP + 垂直滚动。
  - 定位策略同 WordPopup（空闲象限 + Clamp WorkArea）。
  - 流式渲染：绑定 `ObservableCollection<string>` 或追加到 `StringBuilder` 后基于 INotifyPropertyChanged 驱动 TextBlock 更新，首个 chunk 到达即 Show。
  - 点击 Popup → 可交互模式；复制按钮复制完整译文。
- **Acceptance Criteria Addressed**: AC-3, AC-4, AC-12
- **Test Requirements**:
  - `human-judgement` TR-M4.2.1: 模拟 Streaming 每 100ms 追加 1 段，BlockPopup 首段立即出现，后续滚动流畅。
  - `human-judgement` TR-M4.2.2: 屏幕四角显示，Popup 不被裁切；点击后可选中文本复制。
  - `programmatic` TR-M4.2.3: 单元测试 Popup 定位算法对 Box 在四角时返回的位置均在 WorkArea 内。
- **Notes**: Overlay 与 Popup 初始均不得抢焦点；复用 M1.4 WindowStyleHelper。

---

## 里程碑 M5 - Translation（Qwen Provider + 缓存 + 错误码）

### [ ] Task M5.1: ITranslationProvider 抽象 + Qwen-MT HTTP（Lite/Flash + Streaming）
- **Priority**: high
- **Depends On**: M3.3, M4.2
- **Description**:
  - 按 §10.2 定义 `ITranslationProvider.TranslateAsync(TranslationRequest) -> IAsyncEnumerable<TranslationChunk>`；`TranslationRequest{ Text, Context, SourceLanguage, TargetLanguage, Mode, OperationId }`。
  - `QwenMtTranslationProvider`：
    - 通过 IHttpClientFactory 或单例 HttpClient（生命周期由 DI 管理）配置 `BaseAddress`、`Timeout`、`DefaultRequestHeaders`（鉴权 Bearer）。
    - Fast → Qwen-MT Lite；Balanced → Qwen-MT Flash。
    - 支持 SSE 或 chunked JSON 流解析（按官方最终约定）；逐块产出 `TranslationChunk.TextDelta`。
    - 异常映射 `TranslationException.ErrorCode`：Timeout/AuthFailed/NetworkUnavailable/InvalidResponse/Unknown。
- **Acceptance Criteria Addressed**: AC-9, AC-12
- **Test Requirements**:
  - `programmatic` TR-M5.1.1: 使用 HttpMessageHandler stub 覆盖：401→AuthFailed、500→Unknown 且不崩溃、超时→Timeout、断网→NetworkUnavailable。
  - `programmatic` TR-M5.1.2: `IAsyncEnumerable` 首个 chunk MoveNextAsync 触发后立即返回（不等待完成）。
  - `human-judgement` TR-M5.1.3: 真实 API Key 下 Block 翻译 ≥ 5 句，肉眼可见流式增量。
- **Notes**: Open Questions 中端点/鉴权未确认前先提供可配置 `QwenMtOptions`，并允许以 mock 模式切换到"演示流"避免阻塞。

### [ ] Task M5.2: TranslationRouter（L1/L2/Dict → Provider）+ SQLite Cache
- **Priority**: high
- **Depends On**: M5.1
- **Description**:
  - `TranslationRouter`：
    - Word：L1 Memory LRU → SQLite L2 → Local Dictionary →（必要时）Qwen-MT；命中任一层即返回，不再发网。
    - Block：L1/L2 → Qwen-MT（Streaming）。
  - 文本 Normalize：仅压缩明显 OCR 多空格/换行空白；区分大小写代码标识保留原文。
  - Cache Key：Word 使用 `src|dst|mode|normalizedText`；SQLite 主键使用 SHA-256(normalized payload) + metadata（创建时间/过期/命中计数）。
  - SQLite：引入 `Microsoft.Data.Sqlite`，建表 `translation_cache(id, key_hash, src_lang, dst_lang, mode, result_json, created_at, last_hit_at, hits)`。
- **Acceptance Criteria Addressed**: AC-11
- **Test Requirements**:
  - `programmatic` TR-M5.2.1: 单测断言：相同 normalized word 第二次请求未触发 Qwen Provider HTTP（mock VerifyNoCalls）。
  - `programmatic` TR-M5.2.2: SQLite 集成测试：写入后重启进程，查询返回同一结果。
  - `human-judgement` TR-M5.2.3: 代理抓包：Cache/Dict 命中时看不到对应 HTTP 请求。
- **Notes**: Local Dictionary 在未拿到正式词库前先用最小 demo 词库并保证接口不变。

### [ ] Task M5.3: UI 错误展示（短消息）+ 取消传播到 HTTP/翻译管线
- **Priority**: medium
- **Depends On**: M5.2
- **Description**:
  - 将 `TranslationException.ErrorCode` 映射到用户友好短消息（中文或本地化资源），绝不显示 Exception.ToString()。
  - 所有 Provider 内部操作传入 CancellationToken；网络层支持 Cancel（CancelPendingRequests / CancellationToken 传递到 SendAsync）。
  - 旧 Operation 取消后，HTTP 响应即使到达也不更新对应 Popup（OperationId 比对）。
- **Acceptance Criteria Addressed**: AC-5, AC-9
- **Test Requirements**:
  - `programmatic` TR-M5.3.1: HTTP 请求中途 Cancel，不会抛到 UI 顶层（日志 Warning 即可）。
  - `human-judgement` TR-M5.3.2: 故意填错 Key 或断网，Popup 仅展示短错误一行，无堆栈泄露。
- **Notes**: 错误提示样式沿用 Popup 的简洁风格。

---

## 里程碑 M6 - Hardening（取消竞态细节、多屏 DPI 全量覆盖、安全存储、性能优化、异常恢复）

### [ ] Task M6.1: API Key 加密存储（DPAPI 或 Credential Manager）+ Settings 接入
- **Priority**: high
- **Depends On**: M5.3, M1.2
- **Description**:
  - Infrastructure 层实现 `ISecretStore`：`Save(string key, string value)` / `string Load(string key)` / `Delete(string key)`。
  - 默认实现：`DpapiCurrentUserSecretStore`（ProtectedData.Protect + 可选 entropy）。
  - Settings 中不存 ApiKey 明文；Settings 界面输入框用 PasswordBox；保存走 ISecretStore；加载走 ISecretStore；若缺失则提示用户去设置。
- **Acceptance Criteria Addressed**: AC-8
- **Test Requirements**:
  - `programmatic` TR-M6.1.1: 保存后在 `settings.json` 中 grep `api.*key`/`bearer`/明文 token 不匹配（大小写不敏感）。
  - `programmatic` TR-M6.1.2: 日志（Info/Warning/Error）中默认级别下不出现 ApiKey 片段。
  - `programmatic` TR-M6.1.3: 重启进程能 Load 到同一个 Key；其他 Windows 用户读取失败（DPAPI 语义）。
- **Notes**: 若 Credential Manager 实现更合适，可通过接口两种实现并存，默认选 DPAPI CurrentUser。

### [ ] Task M6.2: 多显示器 + 多 DPI 回归 + 跨显示器移动/坐标重新换算
- **Priority**: high
- **Depends On**: M4.1, M6.1
- **Description**:
  - `InteractionCoordinator` 每次 Hotkey 都要：获取 Cursor 时同时保存 MonitorId；跨屏时按目标显示器 DPI 重新换算 Overlay/Popup 位置。
  - SelectionOverlay 和 Popup 必须按其所在 Monitor 的 DPI 创建或更新 DpiChanged 处理逻辑。
  - 建立 `PerMonitorDpiTests`（需要人为在双屏机器执行，输出日志截图）。
- **Acceptance Criteria Addressed**: AC-6
- **Test Requirements**:
  - `human-judgement` TR-M6.2.1: 双屏 100%+200% DPI 组合，每屏分别执行 Word/Block 各 5 次，框位无偏移。
  - `human-judgement` TR-M6.2.2: 鼠标从 100% 屏移动到 200% 屏后立即按快捷键，框位仍准确（不得沿用上一屏 DPI）。
- **Notes**: 若机器环境不具备双屏，需要在 checklist 中注明"待目标机器验证"并附上人工执行指南。

### [ ] Task M6.3: 取消竞态加固 + 错误恢复（OCR 异常、模型加载失败、线程切换异常）
- **Priority**: high
- **Depends On**: M3.2, M5.3
- **Description**:
  - OCR Engine：取消后推理结果丢弃；CTS 异常只包装为 `OcrException.ErrorCode=Cancelled`。
  - Coordinator：所有 async 链均 `ConfigureAwait(true/false)` 明确（UI 切回 STA，非 UI 允许继续任意上下文）。
  - 未处理异常兜底：`AppDomain.UnhandledException` + `Dispatcher.UnhandledException` 记录 Error 日志并（尽可能）回到 Idle，不直接崩溃。
- **Acceptance Criteria Addressed**: AC-5, AC-9, AC-10
- **Test Requirements**:
  - `programmatic` TR-M6.3.1: 高并发取消测试：100 轮"发请求→20ms 后取消"，无内存泄漏/卡死，UI 线程能响应。
  - `programmatic` TR-M6.3.2: 构造 OCR 推理抛异常场景，应用不掉进程，日志含 ErrorCode。
  - `human-judgement` TR-M6.3.3: 高强度使用 5 分钟（频繁切词按快捷键），应用不崩溃且托盘菜单仍可用。
- **Notes**: 可用 BenchmarkDotNet 或自定义脚本做取消压力测试。

### [ ] Task M6.4: Settings Window 完善（快捷键冲突检测、翻译质量开关、开机启动写 HKCU Run）
- **Priority**: medium
- **Depends On**: M6.3
- **Description**:
  - Settings 界面字段：Word/Block Hotkey（热键录制控件 + 冲突检测并报错）、目标语言下拉、Fast/Balanced 下拉、开机启动勾选、点击外部关闭勾选、Debug 日志开关、API Key PasswordBox + 保存/清除。
  - 开机启动：写入/删除 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\QuickTranslate`（不需要管理员）。
  - Settings 修改并保存：热键重新注册、日志级别切换立即生效。
- **Acceptance Criteria Addressed**: AC-1, AC-8
- **Test Requirements**:
  - `human-judgement` TR-M6.4.1: 快捷键与其他应用冲突时 UI 明确显示"与 XXX 冲突"或错误码；无法保存。
  - `human-judgement` TR-M6.4.2: 勾选开机启动并保存，检查注册表 HKCU Run 有对应键值。
  - `programmatic` TR-M6.4.3: Debug 日志开/关，对应日志 Filter 生效（用日志 sink 计数器断言）。
- **Notes**: 开机启动备选实现留 ADR 说明（若企业组策略禁用 Run，则后续切换 Task Scheduler）。

### [ ] Task M6.5: 性能 Benchmark 工程 + 首轮度量（Idel/OCR/Hotkey→Overlay/TTFT）
- **Priority**: medium
- **Depends On**: M6.3
- **Description**:
  - `benchmarks/QuickTranslate.Benchmarks/` 用 BenchmarkDotNet 或自定义控制台采集：
    - Idle CPU / Working Set：静置 10 分钟每 1s 采样。
    - OCR P50/P95：固定 20 张样本截图图片复用 RecognizeAsync。
    - Hotkey→Overlay P50/P95：用合成输入从 `ICursorService` mock 开始测到 Overlay 显示。
    - TTFT：真实网络下 Block Mode 50 次样本。
  - 输出 JSON + Markdown 到 `docs/benchmark-results/<commit-hash>.md`。
- **Acceptance Criteria Addressed**: AC-13
- **Test Requirements**:
  - `programmatic` TR-M6.5.1: Benchmark 工程可 `dotnet run -c Release` 执行且产出 P50/P95 结果文件。
  - `human-judgement` TR-M6.5.2: 报告包含 App commit、模型版本、CPU、内存、Windows 版本、DPI、分辨率、网络区域。
- **Notes**: 若未达标，保留报告并记录问题，后续针对性优化而非一次性重构。

---

## 里程碑 M7 - Packaging（发布/模型资产/开机启动/About/版本/安装包）

### [ ] Task M7.1: Release publish（win-x64 Self-Contained + Single-File 可选） + 模型资产版本
- **Priority**: high
- **Depends On**: M6.4, M6.5
- **Description**:
  - `dotnet publish src/QuickTranslate.App/QuickTranslate.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=false`（先禁用 Single-File，避免 ONNX native 加载坑；后续可评估）。
  - 模型文件版本与校验：在 `assets/models/version.json` 记录 MD5/SHA；启动时校验并日志输出。
  - 第三方许可证/NOTICE：收集 ONNX Runtime/PP-OCRv6/Serilog/SQLite/Http 等条目放入 `LICENSES.txt`。
- **Acceptance Criteria Addressed**: AC-1（发布形式）
- **Test Requirements**:
  - `programmatic` TR-M7.1.1: `dotnet publish` 成功，产物包含模型文件 + native dll 齐全。
  - `programmatic` TR-M7.1.2: 启动日志校验模型 hash 与 version.json 一致。
- **Notes**: 首次发布后考虑 portable zip。

### [ ] Task M7.2: About 信息/版本、Tray 菜单项完善、基础 Uninstall 策略
- **Priority**: medium
- **Depends On**: M7.1
- **Description**:
  - About Box：显示 App 版本（AssemblyInformationalVersion）、模型版本、.NET 版本、Runtime ID、开源许可链接。
  - Tray Menu：打开设置、暂停/启用、About、退出。
  - 卸载策略文档：删除 Settings/Cache 目录可选（默认保留配置）；`ISecretStore.Delete("qwen_api_key")` 显式清理密钥。
- **Acceptance Criteria Addressed**: AC-1
- **Test Requirements**:
  - `human-judgement` TR-M7.2.1: About 信息与 `AssemblyInfo` / `version.json` 一致。
  - `programmatic` TR-M7.2.2: 调用清理脚本/按钮后，SecretStore 读不到 Key。
- **Notes**: 真实 Installer（MSI/Inno）若超出 Agent 单次执行范围，可先交付 portable zip + 说明 ADR。

### [ ] Task M7.3: 在干净 Windows 测试机验证安装/运行/卸载 + 回归 P0 验收
- **Priority**: high
- **Depends On**: M7.2
- **Description**:
  - 在一台无开发环境的 Win10/11 x64 机器：解压/安装 → 启动 → 按文档步骤配置 API Key → 运行完整 Word/Block 路径。
  - 回归 checklist.md 中 AC-1~AC-10 P0 全项；失败项进入 issue 列表，在 release 前至少修复 P0。
- **Acceptance Criteria Addressed**: 全部 AC（P0 必过）
- **Test Requirements**:
  - `human-judgement` TR-M7.3.1: 测试者在 checklist 勾选 P0 全部通过。
  - `human-judgement` TR-M7.3.2: 记录机器配置与任何偏差，附在 `docs/benchmark-results/` 下。
- **Notes**: 若当前 Agent 环境无干净机，可产出"人工执行指南"，将此标记为需用户协助验证的步骤。
