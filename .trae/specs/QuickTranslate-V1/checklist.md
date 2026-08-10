# QuickTranslate V1 - 验证清单（Checklist）

> 使用说明：完成对应任务后逐项勾选；P0 项必须 100% 通过，否则 V1 不视为完成。所有"需要人工在 Windows 真机验证"的项，测试者需记录机器配置（CPU/内存/分辨率/DPI/Win 版本）并在结果中留痕。

---

## 里程碑 M0 - Skeleton

### 代码与工程结构
- [ ] Checkpoint M0.1.1: `QuickTranslate.sln` 能够 `dotnet build -c Debug` 成功，零错误零警告。
- [ ] Checkpoint M0.1.2: 仓库中存在 `.gitignore` 且忽略了 bin/obj/packages 等；`git status` 干净（未提交二进制产物）。
- [ ] Checkpoint M0.1.3: 模块依赖方向单向（App → Infrastructure/Platform → Core），Core 不依赖 WPF。

### 单实例与基础设施
- [ ] Checkpoint M0.2.1: 同时启动两个进程，第二个进程 3 秒内退出（ExitCode!=0），系统中仅 1 个 QuickTranslate.exe。
- [ ] Checkpoint M0.2.2: 首次启动后在 `%APPDATA%\QuickTranslate\settings.json` 生成默认配置，字段与文档一致（WordHotkey=Alt+1 / BlockHotkey=Alt+2 / TargetLanguage=zh-CN / TranslationQuality=Fast 等）。
- [ ] Checkpoint M0.2.3: `%LOCALAPPDATA%\QuickTranslate\Logs\` 存在至少一条启动 Info 日志，字段包含版本/进程。
- [ ] Checkpoint M0.2.4: `settings.json` 中**不包含**任何名为 `ApiKey`/`apiKey`/`api_key`/`Bearer` 的明文字段（grep 验证）。

### 测试工程基础
- [ ] Checkpoint M0.3.1: `dotnet test tests/QuickTranslate.Tests/` 全部通过（至少 1 个 smoke test）。
- [ ] Checkpoint M0.3.2: 测试工程按 Core/Platform/Selection/Translation 分子目录，组织清晰。

---

## 里程碑 M1 - Windows Shell

### Win32 封装层
- [ ] Checkpoint M1.1.1: `DpiMapper` 单元测试：100%/125%/150%/200% 四档 DPI 下 Physical↔DIP 转换 roundtrip 误差为 0。
- [ ] Checkpoint M1.1.2: `PhysicalPoint/PhysicalRect` 强类型能正确进行相等比较、ToString、Deconstruct。
- [ ] Checkpoint M1.1.3: 双屏真机验证：`IMonitorService` 返回两块屏的 Physical Rect 与 DPI，值与 Windows 设置一致。

### 托盘与菜单
- [ ] Checkpoint M1.2.1: 启动后托盘图标可见；菜单"退出"点击后进程在 3 秒内消失，无残留托盘图标。
- [ ] Checkpoint M1.2.2: 第二次启动不会出现第二托盘或第二窗口，行为符合单实例。

### 全局快捷键与 Esc
- [ ] Checkpoint M1.3.1: 实际按 Alt+1、Alt+2、Esc，日志中分别出现对应的 HotkeyEvent Info 行（人工观察）。
- [ ] Checkpoint M1.3.2: 非 UI 单测验证 HotkeyBroker 发送 Word/Block/Esc 事件，订阅方收到正确枚举。
- [ ] Checkpoint M1.3.3: Settings 修改快捷键时，若与系统其他全局键冲突，必须给出明确错误且不保存。

### Selection Overlay 原型（无焦点）
- [ ] Checkpoint M1.4.1: Overlay 显示时前台应用（记事本/浏览器输入框）仍保持输入焦点，能继续打字无任何打断（人工，≥ 3 种前台应用）。
- [ ] Checkpoint M1.4.2: 使用 Win32 检查 Overlay 窗口样式，确认 `WS_EX_NOACTIVATE` 与 `WS_EX_TOPMOST` 存在。
- [ ] Checkpoint M1.4.3: 在 125%/150%/200% DPI 下调用 Show 指定 `PhysicalRect(100,100,200,40)`，与 Paint 绘制的同坐标矩形视觉对齐（误差 ≤ 2px 级）。

---

## 里程碑 M2 - Capture + OCR

### 截图（仅内存）
- [ ] Checkpoint M2.1.1: 连续执行 `CaptureAsync` 100 次后，`%TEMP%`、程序目录、`%LOCALAPPDATA%` 下不新增任何 `.png/.bmp/.jpg/.tmp` 图片类文件（审计前后文件列表 diff）。
- [ ] Checkpoint M2.1.2: `ScreenFrame.Region` 的宽高像素与实际 Bitmap 像素尺寸严格一致（无 DIP 混用）。
- [ ] Checkpoint M2.1.3: 截图实际内容能覆盖鼠标周围指定区域，不切到桌面空白外区域（Debug 可视化临时验证）。

### PP-OCRv6 引擎
- [ ] Checkpoint M2.2.1: 日志中出现"OCR Session created"仅 1 次，后续连续 RecognizeAsync 不重复创建。
- [ ] Checkpoint M2.2.2: 启动 Warm-up 完成后首次用户请求不重新初始化模型（共用同一初始化 Task）。
- [ ] Checkpoint M2.2.3: 真实网页英文段落截图输出非空 `OcrLine[]`，Line Count ≥ 3（至少 5 张样本）。
- [ ] Checkpoint M2.2.4: 模型文件版本写入启动日志，与 `assets/models/version.json` 一致。

### OCR 数据与耗时埋点
- [ ] Checkpoint M2.3.1: 结构化日志中每条 OCR 任务都包含 PreprocessMs/DetectorMs/RecognizerMs/PostprocessMs 四个数值字段。
- [ ] Checkpoint M2.3.2: `OcrLine/WordCandidate/OcrLayoutResult` record 语义与 §13 定义一致，能正确序列化/反序列化（如有）。

---

## 里程碑 M3 - Word Mode

### Word 选择算法
- [ ] Checkpoint M3.1.1: WordSelector 单元测试全通过：Contains 命中 / 多候选选更小 / 多候选选更近中心 / No Contains 且 < MaxAnchorDistance 选最近 / 超过阈值 → NoTextFound（≥ 10 case）。
- [ ] Checkpoint M3.1.2: `SelectionOptions` 中不存在未抽离的 magic number（所有阈值均从 Options 读取）。
- [ ] Checkpoint M3.1.3: 典型 OcrLine+鼠标点人工样例 ≥ 20，选择正确率 ≥ 90%。

### 状态机与取消
- [ ] Checkpoint M3.2.1: 状态机单测：在 OCR/Translating 阶段触发 NewHotkey，旧结果在完成后被丢弃（不更新 UI）。
- [ ] Checkpoint M3.2.2: Esc 分别从 6 个 Active 状态触发 → 全部回到 Idle，Overlay/Popup 关闭。
- [ ] Checkpoint M3.2.3: 人工 10 次快速连按 Alt+1，最终仅显示最后一次 Overlay，前 9 次内容不闪现。

### Word Popup 与本地缓存/词典
- [ ] Checkpoint M3.3.1: Word Popup 出现瞬间前台焦点不丢失；点击 Popup 后点击"复制"按钮可复制到剪贴板（人工验证 ≥ 3 前台应用）。
- [ ] Checkpoint M3.3.2: Popup 在屏幕四角各触发一次，均完整显示不被裁切。
- [ ] Checkpoint M3.3.3: Memory LRU Cache 命中/淘汰并发测试通过（N=2 容量下 Insert 3 个 → 第 1 个被淘汰；并发读写无异常）。
- [ ] Checkpoint M3.3.4: Word Dictionary/Cache 命中时翻译结果近实时显示（< 100ms 级）。

---

## 里程碑 M4 - Block Mode

### Block 选择算法
- [ ] Checkpoint M4.1.1: BlockSelector 单元测试覆盖 ≥ 15 case 全部通过：连续正文合并 / 大标题排除 / 脚注排除 / 两栏不分栏 / 列表缩进并入 / 跨段间距打断。
- [ ] Checkpoint M4.1.2: 边界重试场景：Anchor 位于截图左边缘 → 协调器总共调用 `CaptureAsync` 2 次（扩大 + 重 OCR）。
- [ ] Checkpoint M4.1.3: 真实网页多段落文档 10 处随机人工测试，文本块误并率 ≤ 10%。

### Block Popup
- [ ] Checkpoint M4.2.1: 模拟 Streaming 模式每 100ms 追加一段，Popup 首段立即显示，后续增量追加滚动流畅。
- [ ] Checkpoint M4.2.2: Popup 在屏幕四角显示不被裁切；点击后可选择文本并复制。
- [ ] Checkpoint M4.2.3: Popup 定位算法单测：Box 在四角时，返回的 Popup Rect 完全包含在 Monitor WorkArea 内。

---

## 里程碑 M5 - Translation

### Qwen Provider 与流式
- [ ] Checkpoint M5.1.1: Provider 单元测试覆盖并通过：401→AuthFailed、500→Unknown 不崩溃、超时→Timeout、断网→NetworkUnavailable。
- [ ] Checkpoint M5.1.2: `IAsyncEnumerable` 单元测试：首个 Chunk `MoveNextAsync` 返回后无需等待整条序列完成（流式语义）。
- [ ] Checkpoint M5.1.3: 真实 API Key 下 Block Mode ≥ 5 句文本，肉眼可见流式增量显示。
- [ ] Checkpoint M5.1.4: HttpClient 生命周期单例；并发请求下不会 per-request `new HttpClient`。

### 缓存与路由
- [ ] Checkpoint M5.2.1: 相同 normalized word 第二次请求时，Qwen Provider HTTP mock 断言零次调用。
- [ ] Checkpoint M5.2.2: SQLite Cache 跨进程复用：写入后重启应用读回相同结果。
- [ ] Checkpoint M5.2.3: 抓包审计：Cache/Dictionary 命中的 10 次 Word 请求，代理中 0 次 HTTP 发网。

### 错误与取消传播
- [ ] Checkpoint M5.3.1: HTTP 请求中途 Cancel，不抛顶层异常，日志仅记录 Warning 级别 Cancelled。
- [ ] Checkpoint M5.3.2: 故意断网 / 填错 Key → Popup 只显示一行短错误（无堆栈泄露）。
- [ ] Checkpoint M5.3.3: 旧 Operation 取消后，迟到 HTTP 响应绝不更新对应 Popup（OperationId 比对）。

---

## 里程碑 M6 - Hardening（P0 全量门槛）

### 密钥安全存储
- [ ] Checkpoint M6.1.1: 保存 API Key 后，对 `settings.json` 执行大小写不敏感 grep `api.*key|bearer|token`，结果为 0。
- [ ] Checkpoint M6.1.2: 默认 Info/Warning/Error 日志中不出现任何 API Key 明文或其子串（≥ 8 字符匹配 grep）。
- [ ] Checkpoint M6.1.3: 保存后重启应用 → 能 Load 到相同 Key；切换 Windows 用户（或模拟）无法解密。

### 多显示器 + DPI
- [ ] Checkpoint M6.2.1: 组合矩阵测试（单/双屏 × 100%/125%/150%/200%），Word/Block 各 5 次，框位无明显偏移（肉眼对齐）。
- [ ] Checkpoint M6.2.2: 鼠标从 100% 屏快速移动到 200% 屏立刻按快捷键，框位仍准确（不使用上一屏 DPI 缓存）。
- [ ] Checkpoint M6.2.3: 每屏单独 Overlay/Popup 在不同 DPI 下创建后尺寸正确，无"模糊放大"现象。

### 取消竞态与异常恢复
- [ ] Checkpoint M6.3.1: 取消压力测试 100 轮（发请求 → 20ms 取消），无卡死/内存泄漏/UI 无响应。
- [ ] Checkpoint M6.3.2: OCR 推理阶段构造异常场景（故意抛异常或模型损坏），应用不掉进程，日志含 ErrorCode 技术信息。
- [ ] Checkpoint M6.3.3: Dispatcher 与 AppDomain UnhandledException 兜底：构造未捕获异常 → 记录 Error 日志并尽可能回到 Idle 不崩。

### Settings 功能完善
- [ ] Checkpoint M6.4.1: 自定义 Word/Block Hotkey 保存后实际生效；冲突时不保存并 UI 高亮错误。
- [ ] Checkpoint M6.4.2: 勾选开机启动保存后，`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\QuickTranslate` 注册表值存在。
- [ ] Checkpoint M6.4.3: Debug 日志开关切换立即生效（Info 与 Debug 级别的计数器可验证）。

### 性能基准
- [ ] Checkpoint M6.5.1: Benchmark 工程 Release 运行成功并产出 `docs/benchmark-results/<commit>.md`。
- [ ] Checkpoint M6.5.2: 报告包含：App commit、模型版本、CPU、内存、Windows 版本、DPI、分辨率、网络区域。
- [ ] Checkpoint M6.5.3: 列出 P50/P95：Idle CPU、Idle WS、OCR、Hotkey→Overlay、TTFT、Cancel Latency；或提供偏差原因与优化建议。

---

## 里程碑 M7 - Packaging

### Release 发布
- [ ] Checkpoint M7.1.1: `dotnet publish -c Release -r win-x64 --self-contained` 成功；产物包含：exe、模型文件、ONNX native dll、LICENSES.txt。
- [ ] Checkpoint M7.1.2: 启动时日志输出模型 hash，与 `assets/models/version.json` 一致（不一致则拒绝使用模型或 Warning）。
- [ ] Checkpoint M7.1.3: 发布包中存在 `LICENSES.txt`，列出第三方组件及许可证版本号（ONNX Runtime/PP-OCRv6/Serilog/SQLite 等）。

### About、卸载清理
- [ ] Checkpoint M7.2.1: About Box 显示 App Version、Model Version、.NET Version、Runtime ID、许可证链接；信息与 Assembly/version.json 一致。
- [ ] Checkpoint M7.2.2: 执行卸载/清理按钮后，SecretStore 读取 `qwen_api_key` 返回空（或抛不存在异常）。
- [ ] Checkpoint M7.2.3: Tray 菜单包含：打开设置、暂停/启用、关于、退出，四项均可点击且行为正确。

### 干净机测试（真实 Windows 无开发环境）
- [ ] Checkpoint M7.3.1: 解压/安装后首次启动 → 托盘可见，无主窗，无崩溃弹框。
- [ ] Checkpoint M7.3.2: 在 Settings 中配置 API Key → Word/Block 各 5 次真实翻译成功，Popup 正常。
- [ ] Checkpoint M7.3.3: AC-1 ~ AC-10（P0）在干净机上全部人工通过（清单逐条勾）。
- [ ] Checkpoint M7.3.4: 卸载后（或手动清理）：密钥不存在，托盘/进程退出干净；用户设置/缓存可按策略保留或删除。

---

## P0 必过总闸（V1 发布门槛，全部通过视为 Done）

- [ ] AC-01 ✅ 启动后托盘可见、无主窗、快捷键可用、单实例。
- [ ] AC-02 ✅ Word Mode 在 Chrome/Edge/VS Code/PDF/Word 常规英文文本上能定位目标单词并显示框。
- [ ] AC-03 ✅ Block Mode 能从鼠标锚点选出相邻正文行，标题/按钮/下一段不频繁误并。
- [ ] AC-04 ✅ Selection Overlay 与 Popup 初始显示时不抢当前应用焦点。
- [ ] AC-05 ✅ 新快捷键取消旧请求；旧翻译结果绝不覆盖新 Popup。
- [ ] AC-06 ✅ 多显示器 + 125%/150%/200% DPI 下 Overlay 框位准确。
- [ ] AC-07 ✅ 截图不写磁盘；翻译网络请求不包含图片。
- [ ] AC-08 ✅ API Key 不以明文存在于 Settings JSON、日志、源码。
- [ ] AC-09 ✅ Qwen-MT 网络失败（4xx/5xx/超时/断网/鉴权失败）应用不崩溃，给出可理解短错误。
- [ ] AC-10 ✅ Esc 从任意 Active 状态返回 Idle（含 Overlay/Popup 关闭）。

---

## P1 推荐通过项

- [ ] AC-11 ✅ Word Cache/Dictionary 命中不发网络请求。
- [ ] AC-12 ✅ Block 翻译流式输出，首段内容到达即可显示。
- [ ] AC-13 ✅ 满足性能软目标，或有 Benchmark 报告解释偏差与优化建议。
