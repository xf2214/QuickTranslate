# 修复：按下快捷键没有反应 + 没有识别

> 计划生成时间：2026-08-12  
> 适用项目：`e:\翻译`（QuickTranslate v0.1）

---

## 一、Repo 研究结论（根因 4 条，按影响权重排序）

### 根因 #1（最关键，90% 概率）：PP‑OCRv6 模型缺失 → 回退 MockOcrEngine → 输出与真实屏幕无关
**现场证据**：
- 目录 [assets/models](file:///E:/翻译/assets/models) **仅有** `version.json`，缺少 `det.onnx / rec.onnx / ppocr_keys.txt`（`cls.onnx` 可选）。
- [ServiceCollectionExtensions.cs AddOcrEngines](file:///e:/翻译/src/QuickTranslate.Infrastructure/ServiceCollectionExtensions.cs#L183-L204) 的 DI 回退逻辑：
  ```csharp
  if (paddleEngine.IsAvailable) return paddleEngine;
  logger.LogWarning("PP-OCRv6 models not found, MockOcrEngine fallback.");
  return sp.GetRequiredService<MockOcrEngine>();
  ```
- [MockOcrEngine.RecognizeAsync](file:///e:/翻译/src/QuickTranslate.Infrastructure/Ocr/MockOcrEngine.cs#L33-L110) 内部**硬编码 5 行固定英文**，坐标按 `frame.Region` 比例分布（`lineTop = lineIdx * lineHeight`），和用户屏幕真实文本**完全无关**。

**最终表现**：
- 鼠标随便放在桌面真实文本上方 → WordSelector/BlockSelector 在 5 行 Mock 坐标里找不到光标 → 返回 `NoTextFound=true / NoBlockFound=true`。
- [WordInteractionCoordinator L147-151](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L147-L151)、[BlockInteractionCoordinator L183-187](file:///e:/翻译/src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs#L183-L187) 直接 `HideAll()`，**不弹任何 UI 反馈**。
- 最终用户感知："按下 Alt+1 / Alt+2 没反应，也没有识别"。

### 根因 #2：NoTextFound / NoBlockFound 分支静默 HideAll，无 UI 反馈
即使用 Mock 引擎，如果光标正好落在 5 行 Mock 分布区域（比如鼠标接近屏幕中间），也能弹出正确结果。但绝大多数光标位置都会直接 HideAll，用户看不到任何提示。  
这会把"实际执行了但没命中"误判为"完全没反应"。

### 根因 #3：WordInteractionCoordinator.HandleHotkey 中 Block 分支是空壳（埋雷）
[WordInteractionCoordinator L96-97](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L96-L97)：
```csharp
case HotkeyEventType.Block:
    break;  // 空分支
```
当前 Block 热键在 [AppHost.WireBlockHotkey](file:///e:/翻译/src/QuickTranslate.App/Bootstrap/AppHost.cs#L74-L81) 里独立订阅 `broker.HotkeyFired`，所以 Alt+2 仍然能跑。但空分支对后续维护者是极大的误导坑，需要清理或注释说明。

### 根因 #4（潜在死锁风险）：WPF UI 服务在构造函数里缓存 Dispatcher
3 个文件全部在构造函数写了 `_dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher`：
- [WpfWordPopupService L26](file:///e:/翻译/src/QuickTranslate.App/Services/WpfWordPopupService.cs#L26)
- [WpfBlockPopupService L29](file:///e:/翻译/src/QuickTranslate.App/Services/WpfBlockPopupService.cs#L29)
- [WpfSelectionOverlayService L20](file:///e:/翻译/src/QuickTranslate.App/Services/WpfSelectionOverlayService.cs#L20)

当前 DI 调用链是 `App.OnStartup(UI 线程)` → `AppHost.Build()` → 构造这些 Singleton，因此 `Application.Current?.Dispatcher` 确实取到 UI 线程。**但一旦未来 Hosting/DI 结构有调整**（比如宿主工厂切换线程），`?? Dispatcher.CurrentDispatcher` 会取**第一次访问时的后台线程 Dispatcher**，然后 `RunOnUi()` 的 `_dispatcher.Invoke` 将永久阻塞（没人 Pump 后台 Dispatcher）。属于防患未然的加固项。

---

## 二、修改文件 & 模块清单

| # | 文件 | 改动 |
|---|------|------|
| 1 | `src/QuickTranslate.Infrastructure/Ocr/MockOcrEngine.cs` | **让 Mock 输出跟随屏幕光标锚点**（anchor 相对 frame 中心对齐，确保光标总能落在 Mock 文本区域，快速验证整条热键→识别→翻译链路） |
| 2 | `src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs` | NoTextFound 分支新增"未检测到文字"的错误气泡；清理 Block 空分支为注释；增加 try/catch 外层 RunWordPipeline 的静默吞日志（fire-and-forget 的异常要落 Unobserved 之外的日志通道） |
| 3 | `src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs` | NoBlockFound 分支新增"未检测到段落"错误气泡 |
| 4 | `src/QuickTranslate.App/Services/WpfWordPopupService.cs` | `_dispatcher` 字段移除缓存；`RunOnUi` 内部每次取 `Application.Current.Dispatcher` |
| 5 | `src/QuickTranslate.App/Services/WpfBlockPopupService.cs` | 同上 |
| 6 | `src/QuickTranslate.App/Services/WpfSelectionOverlayService.cs` | 同上 |
| 7 | `src/QuickTranslate.Infrastructure/ServiceCollectionExtensions.cs` | AddOcrEngines 在 fallback 到 Mock 时输出 **Warning 到日志 + 在 AppSettings 注入标记**，以便设置页显示 "当前引擎=Mock(因为 PP‑OCRv6 模型缺失)"（可选，若设置页已有 OcrEngine 状态徽章可复用） |

> 注：PP‑OCRv6 的真实模型文件下载不包含在此计划代码修改内（根因 #1 有两条解决路径：a. 下载真实 onnx 放到 assets/models；b. 让 mock 能命中）。鉴于用户已经明确反馈"按了没反应"，**先做 (b)** 保证整条链路跑通，再引导用户去托盘设置页里点"下载模型"按钮即可恢复真实 OCR。

---

## 三、修改步骤（共 6 步，按顺序执行）

### Step 1：MockOcrEngine — 把硬编码文本行锚定到光标 anchor
**目标**：即便回退到 Mock，用户按 Alt+1/Alt+2 也**一定能**在光标附近识别出文字并弹出翻译。

**做法**：
- 在 `MockOcrEngine.RecognizeAsync(ScreenFrame frame, CancellationToken ct)` 签名处不改动；但是**从 frame.Region 推导中心**：
  - `frameCenter = frame.Region.Center`（PhysicalRect 已有 Center 计算）。
  - 5 行硬编码文本行现在以 `frameCenter` 为中心上下排布：
    - `lineHeight = Math.Max(frameHeight / (lineTexts.Length + 2), 28)`；
    - `firstLineTop = frameCenter.Y - (lineTexts.Length * lineHeight / 2)`；
    - `lineTop = firstLineTop + lineIdx * lineHeight`。
  - 单词水平位置保持 frameWidth 比例，但起始 X 改为 `frame.Region.Left + 20`（相对 frame 坐标系）。
- 保持 OcrWord 的 Confidence=0.95，OcrLine 的 IsStartOfBlock 逻辑不变。
- **关键**：`frame.Region` 是**屏幕绝对坐标**（GdiScreenCapture 返回的 Region 是屏幕坐标），因此 Mock 结果的 Box 也必须是**屏幕绝对坐标**（不能是 frame 内相对坐标）。检查现有 Mock：
  ```
  var lineBox = new PhysicalRect(X: 10, Y: lineTop, ...)
  ```
  这里的 `X: 10` 实际上是**相对 frame 左上角**还是屏幕绝对坐标？需要再读 GdiScreenCapture 实现里 `frame.Region =` 到底是屏幕绝对还是 capture 局部 0-based。验证后做对应偏移：`X: frame.Region.Left + 10`、`Y: frame.Region.Top + lineTop`（如果现在是局部坐标）。

### Step 2：WordInteractionCoordinator — NoTextFound 分支给用户气泡反馈
修改 [L147-151](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L147-L151)：
```csharp
if (sel.NoTextFound)
{
    _overlayService.HideAll();
    // 提示"附近没检测到英文单词"。框大小：captureRegion 若可用就用它，否则自己在 cursor 周围画 300x120
    var hintBox = captureRegion ?? new PhysicalRect(cursor.X - 150, cursor.Y - 60, 300, 120);
    if (!IsStaleOrCanceled(newSlot))
    {
        _popupService.ShowError(mid, hintBox, dpiX, dpiY, "未检测到可翻译的单词，请将鼠标移到英文单词上再按 Alt+1", newSlot.Id);
    }
    SetState(null, AppState.Idle);  // 之前漏了
    return;
}
```
同时在 `L96 case HotkeyEventType.Block:` 的空 break 前加注释：
```csharp
case HotkeyEventType.Block:
    // Block 热键独立由 AppHost.WireBlockHotkey 订阅 broker.HotkeyFired 并调用 BlockInteractionCoordinator.RunBlockPipeline()
    // 这里留空以避免在 WordCoord 中重复触发 Block 流程
    break;
```
同时在 RunWordPipeline 顶部 `_ = RunWordPipeline()` 的 fire-and-forget 调用链**包装一个 async Task + Unobserved 外层 try/catch**（现在已有 catch(Exception ex) 在 L217），确认不缺。然后加一条 `Hotkey fired → RunWordPipeline started` 的结构化日志。

### Step 3：BlockInteractionCoordinator — NoBlockFound 给用户气泡反馈
修改 [L183-187](file:///e:/翻译/src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs#L183-L187)：
```csharp
if (block.NoBlockFound)
{
    _overlayService.HideAll();
    if (!IsStaleOrCanceled(newSlot))
    {
        _popupService.ShowError(mid, fallbackBox, dpiX, dpiY,
            "未检测到段落文本，请将鼠标移到英文段落文字上再按 Alt+2", newSlot.Id);
    }
    else
    {
        _popupService.HideAll();
    }
    SetState(null, AppState.Idle);  // 之前漏了
    return;
}
```

### Step 4：三个 WPF UI 服务移除构造函数缓存的 Dispatcher
统一改造 `WpfWordPopupService / WpfBlockPopupService / WpfSelectionOverlayService`：
- 删除 `private readonly Dispatcher _dispatcher;` 字段；
- 删除构造函数 `_dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;` 赋值；
- RunOnUi 改为：
  ```csharp
  private static void RunOnUi(Action a)
  {
      var dispatcher = System.Windows.Application.Current?.Dispatcher
                       ?? throw new InvalidOperationException("Application.Current unavailable, cannot marshal UI work");
      if (dispatcher.CheckAccess()) a();
      else dispatcher.Invoke(DispatcherPriority.Normal, a);
  }
  ```

### Step 5：AddOcrEngines — fallback 日志升级 + 可选的 Lifecycle 通知
当前已经 `logger.LogWarning("PP-OCRv6 models not found, MockOcrEngine fallback.")`。但因为 `LoggingSwitch.IsDebugEnabled = appSettings.DebugLogging`，默认 `DebugLogging=false` 时 Warning **仍会输出**（FilteringLoggerProvider L89-94 的 filter 是 `IsDebugEnabled ? true : level >= Information`，Warning 是高于 Information 的，所以 OK）。无需改过滤条件。

额外在 AddOcrEngines 里**加一个 Debug 级别的详细日志**，列出 candidateDirs 的实际命中路径和缺失文件名，方便排查：
```csharp
logger.LogDebug("PaddleOcrV6Engine candidate dirs: {Dirs}; missing required files: {Missing}",
    candidateDirs, new[] { "det.onnx", "rec.onnx", "ppocr_keys.txt" }.Where(f => ...));
```
如果 candidateDirs 拿不到 sp 里的 PaddleOcrV6Engine，可以直接在 PaddleOcrV6Engine 构造函数内部加 `_logger.LogDebug(...)` 记录 "Not found model files in {dir}"，这样更符合封装。

**更简单的做法**：直接在 PaddleOcrV6Engine 的循环 `CheckModelFiles(dir)` 里为每个 dir 输出一条 Debug 日志：
```csharp
foreach (var dir in candidateDirs)
{
    if (CheckModelFiles(dir)) { /* 命中 */ }
    else _logger.LogDebug("Model directory {Dir} check failed, skipping", dir);
}
```
本计划选择后者（更易实现，不涉及 ServiceCollectionExtensions 里拿候选目录的反射）。

### Step 6：Build + 验证清单
- `dotnet build` 全量通过（0 错误 0 警告）。
- `dotnet test --no-build`：除 MissingModels 相关的已知失败，其他全部通过。
- 手工验证路径：
  1. 启动 exe，托盘右键 → 设置 → 热键确认 Alt+1/Alt+2 均为"就绪"绿徽章。
  2. 打开记事本写 `The quick brown fox` 放在屏幕任意位置 → 鼠标放到中间 → 按 Alt+1：
     - ✅ 弹出红色选择框覆盖到 fox/brown 某个单词
     - ✅ 弹出 Word 翻译气泡（Mock 会命中本地词典或 Qwen 调用 TranslationException，最终能在气泡看到结果/错误提示）
     - ✅ 失败时也能看到错误气泡（"附近没检测到文字"不应再出现，因为 Mock 已对齐）
  3. 鼠标放在桌面空白处（真正没文字的地方）→ 按 Alt+1：应弹出友好错误气泡 "未检测到可翻译的单词..."。
  4. Alt+2 同样验证两条分支。
  5. 数字键 1 / 2 在设置页 API Key 输入框里正常输入，不被吞键（复现旧 bug 已修复）。

---

## 四、依赖 & 注意事项

1. **模型文件路径选择**：PaddleOcrV6Engine 的 3 个 candidateDirs 分别是
   - `%APPDATA%\QuickTranslate\models`
   - `{BaseDirectory}\assets\models`
   - 硬编码 `E:\翻译\assets\models`
   
   目前 `E:\翻译\assets\models` 下只有 version.json。如果用户通过"托盘设置页 → 下载模型"按钮下载，会落到上面第 1 或第 2 个路径，PaddleOcrV6Engine 即会自动命中，不需要改代码。
2. **Mock → Real OCR 的切换**：由 DI 启动时一次性判定，因此下载完模型后需要**重启应用**，Mock 才会被替换为真实 PP‑OCRv6。
3. **TranslationException**（翻译 API Key 未配置 / 网络失败）会走 ShowError 气泡，本计划不改动这条链路，但因为 NoTextFound / NoBlockFound 之前没气泡而现在有了，**用户会感觉"突然弹出错误"**，这是正确反馈。
4. **GdiScreenCapture 返回的 ScreenFrame.Region**：需要确认是屏幕绝对坐标还是 0-based 局部坐标 —— MockOcrEngine 在 Step 1 做此偏移时**务必对齐**，否则即使 Mock 锚定到光标，WordSelector 仍会因坐标系错配而 NoTextFound。

---

## 五、风险处理

| 风险 | 发生概率 | 应对策略 |
|------|----------|----------|
| Mock 改坐标系后仍不命中（Region 理解错） | 中 | 读 GdiScreenCapture 的实现确认 ScreenFrame.Region / Buffer 坐标含义；失败时回退 "Mock 里 OcrWord 全部框定在 frame.Center 周围 200×200 像素，且 Box 一定覆盖 frame.Center 这个点"，保证 100% 命中 |
| ShowError 气泡布局在小屏 / 高 DPI / 多屏外溢 | 低 | `PerMonitorDpiHelpers.AreClose` 已在 EnsureWindow 里做，本计划不改布局，气泡位置由现有 ShowErrorCore 保证在 anchorBox 附近 |
| RunOnUi 去掉 ?? CurrentDispatcher 后在单元测试里抛 NullRef | 低 | 现有 UI 服务没直接进单元测试；如后续测试注入可 mock Application.Dispatcher 或在测试基类启动 WPF Application |
| 托盘设置页默认 DebugLogging=false 无法看 Warning | 低 | FilteringLoggerProvider 已经允许 Warning+，不影响；要开 Debug 在设置页勾 "启用调试日志" |
| 用户仍认为"识别不对"（因为 Mock 文本不是真实屏幕文字） | 中 | 在友好错误气泡 / 设置页 OCR 引擎徽章中**显式标明当前引擎**（PP‑OCRv6 / Mock），避免混淆。这超出本计划主范围，可作为 follow-up 任务 |
