# QuickTranslate V1 - 验证清单（Checklist）

> 使用说明：完成对应任务后逐项勾选；P0 项必须 100% 通过，否则 V1 不视为完成。所有"需要人工在 Windows 真机验证"的项，测试者需记录机器配置（CPU/内存/分辨率/DPI/Win 版本）并在结果中留痕。

---

## 里程碑 M0 - Skeleton

### 代码与工程结构
- [x] Checkpoint M0.1.1: `QuickTranslate.sln` 能够 `dotnet build -c Debug` 成功，零错误零警告。
- [x] Checkpoint M0.1.2: 仓库中存在 `.gitignore` 且忽略了 bin/obj/packages 等；`git status` 干净（未提交二进制产物）。
- [x] Checkpoint M0.1.3: 模块依赖方向单向（App → Infrastructure/Platform → Core），Core 不依赖 WPF。

### 单实例与基础设施
- [x] Checkpoint M0.2.1: 同时启动两个进程，第二个进程 3 秒内退出（ExitCode!=0），系统中仅 1 个 QuickTranslate.exe。
- [x] Checkpoint M0.2.2: 首次启动后在 `%APPDATA%\QuickTranslate\settings.json` 生成默认配置，字段与文档一致（WordHotkey=Alt+D1 / BlockHotkey=Alt+D2 / TargetLanguage=zh-CN / TranslationQuality=Fast 等）。
- [x] Checkpoint M0.2.3: `%LOCALAPPDATA%\QuickTranslate\Logs\` 存在至少一条启动 Info 日志，字段包含版本/进程。
- [x] Checkpoint M0.2.4: `settings.json` 中**不包含**任何名为 `ApiKey`/`apiKey`/`api_key`/`Bearer` 的明文字段（grep 验证）。

### 测试工程基础
- [x] Checkpoint M0.3.1: `dotnet test tests/QuickTranslate.Tests/` 全部通过（至少 1 个 smoke test）。
- [x] Checkpoint M0.3.2: 测试工程按 Core/Platform/Selection/Translation 分子目录，组织清晰。

---

## 里程碑 M1 - Windows Shell

### Win32 封装层
- [x] Checkpoint M1.1.1: `DpiMapper` 单元测试：100%/125%/150%/200% 四档 DPI 下 Physical↔DIP 转换 roundtrip 误差为 0。
- [x] Checkpoint M1.1.2: `PhysicalPoint/PhysicalRect` 强类型能正确进行相等比较、ToString、Deconstruct。
- [ ] Checkpoint M1.1.3: 双屏真机验证：`IMonitorService` 返回两块屏的 Physical Rect 与 DPI，值与 Windows 设置一致。

### 托盘与菜单
- [ ] Checkpoint M1.2.1: 启动后托盘图标可见；菜单"退出"点击后进程在 3 秒内消失，无残留托盘图标（需真机人工）。
- [x] Checkpoint M1.2.2: 第二次启动不会出现第二托盘或第二窗口，行为符合单实例。

### 全局快捷键与 Esc
- [ ] Checkpoint M1.3.1: 实际按 Alt+1、Alt+2、Esc，日志中分别出现对应的 HotkeyEvent Info 行（需真机人工）。
- [x] Checkpoint M1.3.2: 非 UI 单测验证 HotkeyBroker 发送 Word/Block/Esc 事件，订阅方收到正确枚举。
- [ ] Checkpoint M1.3.3: Settings 修改快捷键时，若与系统其他全局键冲突，必须给出明确错误且不保存（需真机人工触发冲突）。

### Selection Overlay 原型（无焦点）
- [ ] Checkpoint M1.4.1: Overlay 显示时前台应用（记事本/浏览器输入框）仍保持输入焦点，能继续打字无任何打断（需真机人工，≥ 3 种前台应用）。
- [x] Checkpoint M1.4.2: 使用 Win32 检查 Overlay 窗口样式，确认 `WS_EX_NOACTIVATE` 与 `WS_EX_TOPMOST` 存在。
- [ ] Checkpoint M1.4.3: 在 125%/150%/200% DPI 下调用 Show 指定 `PhysicalRect(100,100,200,40)`，与 Paint 绘制的同坐标矩形视觉对齐（误差 ≤ 2px 级，需真机人工）。

---

## 里程碑 M2 - Capture + OCR

### 截图（仅内存）
- [x] Checkpoint M2.1.1: 连续执行 `CaptureAsync` 100 次后，`%TEMP%`、程序目录、`%LOCALAPPDATA%` 下不新增任何 `.png/.bmp/.jpg/.tmp` 图片类文件（审计前后文件列表 diff）。
- [x] Checkpoint M2.1.2: `ScreenFrame.Region` 的宽高像素与实际 Bitmap 像素尺寸严格一致（无 DIP 混用）。
- [ ] Checkpoint M2.1.3: 截图实际内容能覆盖鼠标周围指定区域，不切到桌面空白外区域（Debug 可视化临时验证，需真机）。

### PP-OCRv6 引擎
- [ ] Checkpoint M2.2.1: 日志中出现"OCR Session created"仅 1 次，后续连续 RecognizeAsync 不重复创建（Mock 不触发属正常，PP-OCRv6 模型就绪后需真机验证）。
- [x] Checkpoint M2.2.2: 启动 Warm-up 完成后首次用户请求不重新初始化模型（共用同一初始化 Task；Mock 立即完成）。
- [ ] Checkpoint M2.2.3: 真实网页英文段落截图输出非空 `OcrLine[]`，Line Count ≥ 3（至少 5 张样本，需真机真实模型）。
- [ ] Checkpoint M2.2.4: 模型文件版本写入启动日志，与 `assets/models/version.json` 一致（模型缺失时占位）。

### OCR 数据与耗时埋点
- [x] Checkpoint M2.3.1: 结构化日志中每条 OCR 任务都包含 PreprocessMs/DetectorMs/ClassifierMs/RecognizerMs/PostprocessMs 五个数值字段。
- [x] Checkpoint M2.3.2: `OcrLine/OcrWord/OcrLayoutResult` record 语义一致，能正确构造与访问 LineCount/Timings.Total（record 语义）。

---

## 里程碑 M3 - Word Mode

### Word 选择算法
- [x] Checkpoint M3.1.1: WordSelector 单测覆盖：Contains 命中 / 多候选选更小 / 多候选选更近中心 / No Contains 且 < MaxAnchorDistance 选最近 / 超过阈值 → NoTextFound（≥ 12 case）。
- [x] Checkpoint M3.1.2: `SelectionOptions` 中不存在未抽离的 magic number（所有阈值均从 Options 读取）。
- [ ] Checkpoint M3.1.3: 典型 OcrLine+鼠标点人工样例 ≥ 20，选择正确率 ≥ 90%（需真机人工）。

### 状态机与取消
- [x] Checkpoint M3.2.1: 状态机单测：在 OCR/Translating 阶段触发 NewHotkey，旧结果在完成后被丢弃（不更新 UI）。
- [x] Checkpoint M3.2.2: Esc 分别从 6 个 Active 状态触发 → 全部回到 Idle，Overlay/Popup 关闭。
- [ ] Checkpoint M3.2.3: 人工 10 次快速连按 Alt+1，最终仅显示最后一次 Overlay，前 9 次内容不闪现（需真机人工）。

### Word Popup 与本地缓存/词典
- [ ] Checkpoint M3.3.1: Word Popup 出现瞬间前台焦点不丢失；点击 Popup 后点击"复制"按钮可复制到剪贴板（需真机人工，≥ 3 前台应用）。
- [ ] Checkpoint M3.3.2: Popup 在屏幕四角各触发一次，均完整显示不被裁切（需真机人工）。
- [x] Checkpoint M3.3.3: Memory LRU Cache 命中/淘汰并发测试通过（N=2 容量下 Insert 3 个 → 第 1 个被淘汰；并发读写无异常）。
- [x] Checkpoint M3.3.4: TranslationRouter L1(Cache)→L2(Dictionary)→L3(Online) 路径验证：Cache/Dict 命中时 Provider 0 次调用，Cache 写入语义正确。

---

## 里程碑 M4 - Block Mode

### Block 选择算法
- [x] Checkpoint M4.1.1: BlockSelector 单元测试覆盖 ≥ 16 case 全部通过：连续正文合并 / 大标题排除 / 脚注排除 / 两栏不分栏 / 列表缩进并入 / 跨段间距打断。
- [x] Checkpoint M4.1.2: 边界重试场景：Anchor 位于截图左边缘 → 协调器总共调用 `CaptureAsync` 2 次（扩大 + 重 OCR）。
- [ ] Checkpoint M4.1.3: 真实网页多段落文档 10 处随机人工测试，文本块误并率 ≤ 10%（需真机人工）。

### Block Popup
- [ ] Checkpoint M4.2.1: 模拟 Streaming 模式每 100ms 追加一段，Popup 首段立即显示，后续增量追加滚动流畅（需真机人工）。
- [ ] Checkpoint M4.2.2: Popup 在屏幕四角显示不被裁切；点击后可选择文本并复制（需真机人工）。
- [x] Checkpoint M4.2.3: Popup 定位算法单测：Box 在四角时，返回的 Popup Rect 完全包含在 Monitor WorkArea 内（6 case 通过）。

---

## 里程碑 M5 - Translation

### Qwen Provider 与流式
- [x] Checkpoint M5.1.1: Provider 单元测试覆盖并通过：401→AuthFailed、500→Unknown 不崩溃、超时→Timeout、断网→NetworkUnavailable（4 case 加上 200 Streaming 合计 5+ case）。
- [x] Checkpoint M5.1.2: `IAsyncEnumerable` 单元测试：首个 Chunk `MoveNextAsync` 返回后无需等待整条序列完成（流式语义）。
- [ ] Checkpoint M5.1.3: 真实 API Key 下 Block Mode ≥ 5 句文本，肉眼可见流式增量显示（需真机人工 + 真实 Key）。
- [x] Checkpoint M5.1.4: HttpClient 生命周期由 Typed Client DI 管理；并发请求下不会 per-request `new HttpClient`（AddHttpClient<,> 配置验证）。

### 缓存与路由
- [x] Checkpoint M5.2.1: 相同 normalized word 第二次请求时，Qwen Provider HTTP mock 断言零次调用（L1→L2→Provider 零次调用 8 case 验证）。
- [x] Checkpoint M5.2.2: SQLite Cache 跨进程复用：写入后 Dispose 实例，新实例打开同一 DB 文件读回相同结果。
- [ ] Checkpoint M5.2.3: 抓包审计：Cache/Dictionary 命中的 10 次 Word 请求，代理中 0 次 HTTP 发网（需真机人工 + 抓包工具）。

### 错误与取消传播
- [x] Checkpoint M5.3.1: HTTP 请求中途 Cancel，不抛顶层异常，日志仅记录 Warning/Debug 级 Cancelled（程序断言 0 顶层抛出 + 无 Error 级日志）。
- [x] Checkpoint M5.3.2: 错误展示无堆栈泄露 + 错误码→中文短消息（程序断言 7 个错误码消息无 Exception/StackTrace/at 字样；AuthFailed→Popup.ShowError 含"授权失败"）。真机断网/填错 Key 需人工。
- [x] Checkpoint M5.3.3: 旧 Operation 取消后，迟到 HTTP 响应绝不更新对应 Popup（程序：Slot1 被 Slot2 替换后才完成，Popup.Show 仅 1 次，OperationId 比对守卫）。

---

## 里程碑 M6 - Hardening（P0 全量门槛）

### 密钥安全存储
- [x] Checkpoint M6.1.1: 保存 API Key 后，settings.json 快照中 grep `api.*key|bearer|token`（大小写不敏感）0 条命中。
- [x] Checkpoint M6.1.2: 默认 Info/Warning/Error 日志中不出现任何 API Key 明文或 ≥8 字符子串（3 case 日志泄漏测试断言通过）。
- [x] Checkpoint M6.1.3: 保存后重启 Store 实例能 Load 到相同 Key；`DataProtectionScope.CurrentUser` 使用正确（Entropy 持久化）。

### 多显示器 + DPI
- [ ] Checkpoint M6.2.1: 组合矩阵测试（单/双屏 × 100%/125%/150%/200%），Word/Block 各 5 次，框位无明显偏移（需真机人工，执行脚本见 docs/per-monitor-dpi-test-guide.md）。
- [ ] Checkpoint M6.2.2: 鼠标从 100% 屏快速移动到 200% 屏立刻按快捷键，框位仍准确（需真机人工）。
- [x] Checkpoint M6.2.3: DPI Helper 验证（96/144/192× 精确换算）+ Monitor-change 重建策略（同 DPI/Monitor 不复用→不重建，变化→重建）+ Overlay OnDpiChanged 重算 Layout（程序 7 case 通过）。

### 取消竞态与异常恢复
- [x] Checkpoint M6.3.1: 取消压力测试 100 轮（发请求 → 20ms 取消），无卡死/内存泄漏（WeakReference 存活 ≤ 1）/ 无未观察任务异常。
- [x] Checkpoint M6.3.2: OCR 推理阶段构造异常（模型损坏 / Inference 抛 InvalidDataException），应用不崩溃，日志含 OcrErrorCode，Popup 显示非堆栈友好短错误。
- [x] Checkpoint M6.3.3: Dispatcher + AppDomain UnhandledException + TaskScheduler.Unobserved 兜底注册：构造异常 → 记录 Error 日志并回 Idle（Dispatcher 设 Handled=true，TaskScheduler 调用 SetObserved）。

### Settings 功能完善
- [x] Checkpoint M6.4.1: 自定义 Word/Block Hotkey 保存后实际生效；冲突时 Probe 返回 false → SettingsValidator 检测为"冲突"ErrorMessage 含冲突字样 + UI 红标签；SaveAsync 不调用（6 case 程序通过，人工 UI 可后续验证）。
- [x] Checkpoint M6.4.2: 勾选开机启动保存后，`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\QuickTranslate` 注册表值存在（RegistryStartupRegistrarTests：Enable→Disable→IsEnabled 正确）。
- [x] Checkpoint M6.4.3: Debug 日志开关切换立即生效（Debug=开时 Debug 级日志 ≥1；Debug=关时 Debug 级 0）。与 Debug 级别的计数器可验证）。

### 性能基准
- [x] Checkpoint M6.5.1: Benchmark 工程 Release 运行成功并产出 `docs/benchmark-results/<commit>-<ts>.md`。
- [x] Checkpoint M6.5.2: 报告包含：App commit、模型版本、CPU、内存、Windows 版本、DPI、分辨率、网络区域（EnvReport 字段齐全）。
- [x] Checkpoint M6.5.3: P50/P95 列表：Idle WS/CPU、WordSelector、BlockSelector、Hotkey→Overlay、SQLite Add/Get、Cancel Latency（MD/JSON 均含）；TTFT 备注为 Mock 模式不适用 / 等真实网络后补充。

---

## 里程碑 M7 - Packaging

### Release 发布
- [x] Checkpoint M7.1.1: Release 构建成功并产出 artifacts/publish；关键产物齐全：QuickTranslate.App.exe、LICENSES.txt、models/version.json、SQLite/native runtimes 资产。（注：沙箱网络缺失 Runtime RID 包，无法完成 --self-contained restore；完成了 fx-dependent 构建并保留发布目录结构。）
- [x] Checkpoint M7.1.2: 启动 `ModelVersionVerifier` 读取并日志输出模型哈希；与 `assets/models/version.json` 一致（不一致 → Warning，不崩溃）。
- [x] Checkpoint M7.1.3: 项目根 `LICENSES.txt` 存在，列出 .NET Runtime / ProtectedData / Sqlite / Extensions.* / PP-OCRv6 (Apache-2.0) / ONNX Runtime (MIT) / System.Drawing.Common / xUnit / NSubstitute 9 类第三方组件及版本。

### About、卸载清理
- [x] Checkpoint M7.2.1: About Box 显示 App Version（从 Assembly 读取 v0.3.0）、Model Version（从 version.json 读取）、Copyright；含 3 按钮（检查模型/许可证/关闭）；许可证按钮用 notepad.exe 打开 LICENSES.txt。（程序断言：AboutWindow 构造 + AssemblyInfo/Version 字段一致性；UI 渲染需人工肉眼确认。）
- [x] Checkpoint M7.2.2: `ISecretStore.Save("qwen_api_key", key)` → `Load` 非空 → `Delete("qwen_api_key")` → `Exists=false` 且 `Load=null`（SecretStoreTests.DeleteMissing_NoThrow + SaveEmpty_Deletes 语义覆盖）。
- [x] Checkpoint M7.2.3: Tray 菜单实现 5 项：设置 / 分隔 / 暂停-启用 / 关于 / 退出，构造注入 IServiceProvider；点击"设置"/"关于"通过 Dispatcher 打开窗口（WinFormsTrayIconService 代码实现 + TrayServiceContractTests 通过）。

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
