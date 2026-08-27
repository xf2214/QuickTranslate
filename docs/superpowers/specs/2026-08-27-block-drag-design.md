# Block 长按拖选段落 — 设计稿

- 日期: 2026-08-27
- 路径: Architectural（热键钩子 + 协调器状态机 + 选区生长 + 浮层直播）
- 方案: **A — 单次 OCR + 本地行过滤**（已确认）
- 状态: Approved by user 2026-08-27 "使用a / 准确，请直接完成任务"

## 1. 目标 / 非目标

**目标**
- 点按 Alt+2 (<400ms): 保持现状 — 截屏 1200×720 → OCR → DefaultBlockSelector 启发式生长 → 实线扫描 → 翻译
- 长按 Alt+2 (≥400ms): 进入拖动态，初始识别后鼠标每向下跨过一行 OCR 行框，选区实时扩展（虚线预览，无扫描）；松开切换实线扫描并翻译；拖出初始底边时一次性扩大至 1600×1200 重截+重识别

**非目标**
- 向上/横向拖动、多栏跨列
- 拖动中持续重 OCR
- 可配置阈值（本期硬编码 400ms / 20px 移动阈值）

## 2. 架构总览

```
GlobalHotkeyService (WH_KEYBOARD_LL) ──► DefaultHotkeyBroker ──► BlockInteractionCoordinator
        │                                      │                           │
        │ HotkeyDown/Up + duration             │ BlockHoldStart/Move/End    │ ICursorService polling (16ms)
        │                                      │                           ├──► BlockRetryCoordinator.SelectBlockWithRetryAsync (首帧)
        │                                      │                           ├──► 本地 ExpandSelectedLines(dragY) (过滤已识别 OcrLine[])
        │                                      │                           └──► ISelectionOverlayService.Update(UnionBox) 直播
        │                                      │                                   │
        │                                      │                           拖出底边 ──► CaptureAroundAsync(1600×1200) + RecognizeAsync
        │                                      │                           松开 ──► Show(solid) + 扫描 + TranslateBlockAsync
```

**现有复用**
- `FocusBand`/`BlockRetryCoordinator` 首帧逻辑不变
- `DefaultBlockSelector` 保持纯函数；拖动期不重调启发式，仅对 `OcrLayoutResult.Lines` 做 Y 过滤
- `WpfSelectionOverlayService.Update` 异步直播，已验证 `Inspect()` 可测

## 3. 热键长按检测

**现状**: `RegisterHotKey(WM_HOTKEY, MOD_NOREPEAT)` 仅发按下，无抬起。

**改动**
- `GlobalHotkeyService`: 新增 `WH_KEYBOARD_LL` 钩子（复用现有 `WH_MOUSE_LL` 模式），`KeyboardHookCallback` 监听 `WM_KEYDOWN/WM_SYSKEYDOWN` 与 `WM_KEYUP/WM_SYSKEYUP`，用 `GetAsyncKeyState(VK_MENU)` 判定 Alt 组合，`vk==0x32` (D2) 匹配 Block。记录 `downTime[BlockId]`，抬起时 `duration = now - downTime`。
- `IGlobalHotkeyService`: 新增 `event HotkeyKeyStateChanged (Phase: Down/Up, DurationMs)` 或直接在 `DefaultHotkeyBroker` 内转译。
- `DefaultHotkeyBroker`: 暴露 `event BlockHoldStateChanged (HoldStart/HoldMove/HoldEnd)`。`HoldStart` = 按下后 400ms 计时器触发且 `GetAsyncKeyState` 仍按下且未抬起；`HoldMove` 由协调器轮询光标驱动，无需 broker 再抛；`HoldEnd` = 抬起且 `duration>=400ms`。点按（<400ms 抬起）仍走原 `HotkeyFired Block` 路径，不进入拖动态，兼容现有测试。
- `HotkeyEvent`: 新增 `HoldDurationMs` 可选字段或新增 `HotkeyHoldEvent`，保持 `HotkeyEventType.Block` 不变以兼容 `AppHost.WireBlockHotkey`。

**文件**: `GlobalHotkeyService.cs`, `IGlobalHotkeyService.cs`, `DefaultHotkeyBroker.cs`, `HotkeyEvent.cs`

## 4. 选区生长（拖动期）

**数据流**
1. `BlockInteractionCoordinator.RunBlockPipelineAsync` 首帧: `anchor = GetCursorPos`, `frame = CaptureAround(1200×720)`, `ocr = Recognize(frame, band)`, `block = SelectBlock(ocr, anchor)` → `UnionBox` 初选。
2. 若 `duration>=400ms` 进入 `Dragging` 状态: 启动 `DispatcherTimer 16ms` 轮询 `ICursorService.GetPhysicalCursorPos`，`dragY = cursor.Y`。
3. `ExpandSelectedLines(ocr.Lines, anchorY, dragY)`: 选出 `lines.Where(l => l.Box.Top >= anchorLine.Top && l.Box.Bottom <= dragY + lineHeight/2)` 且通过 `SelectedLines` 已有顺序截断（不跨 `HasFullWidthLine` 短尾边界），重算 `UnionBox = UnionRect(selected)`。若 `dragY > frame.Region.Bottom - 20` 且 `frame.Width < 1600` 则 `CaptureAround(1600×1200) + Recognize + SelectBlock` 一次性刷新 `ocr`/`UnionBox`。
4. 每次 `UnionBox` 变化 → `overlay.Update(UnionBox, preview:true)`（虚线预览）。

**松开**: `HoldEnd` → 取消轮询 → `overlay.Show(UnionBox, preview:false)` 触发扫描 → `TranslateAndDisplayBlockAsync`。

**不变量**: 仅向下（`dragY >= anchorY`），`UnionBox` 单调只增不减（`ApplyGrowthResize` 已保证弹窗只增）。

## 5. 浮层动画

- 拖动期: `Show(UnionBox, preview:true)` 首次，之后 `Update(UnionBox)`，`PreviewBorder` 可见，无 `BeginScanSweep`（`ApplyLayout` 中 `preview==true` 分支不重播扫描）。
- 松开: `Show(UnionBox, preview:false)` → `ScanPanel` + `BeginScanSweep 700ms` + 250ms `SelectionHold` 后弹翻译气泡 + 3000ms 自动隐藏。

## 6. 异常与边界

- 长按期间按 Esc → `TryCancelAndClear` 取消 `OperationSlot`，`HideAll`，不翻译
- 拖动移出显示器/跨 DPI → `IMonitorService.TryGetMonitorFromPoint` 重算 `dpiX/dpiY` 后 `Update`
- OCR 空结果 / NoBlockFound → 保持初选单行，不扩展，松开提示“未检测到段落”
- 400ms 内快速抬起 → 不进入拖动，走点按分支
- 1600×1200 已是上限，超出不再扩大

## 7. 测试

- `HotkeyBrokerHoldTests`: FakeGlobalHotkeyService 模拟 Down 400ms 后 Up → 断言 HoldStart/End 触发；Down 200ms Up → 仅 Fired
- `BlockDragSelectionTests`: 给定固定 `OcrLayoutResult` (5 行)，anchor 在第2行，dragY 逐行下移断言 `UnionBox` 逐步包含 2→5 行；超底边断言二次 Capture 发生
- `SelectionOverlayUpdateTests`: 断言拖动序列中 `Inspect()[mid].lastDipRect` 单调扩展且 preview 标志正确
- 回归: `BlockInteractionCoordinatorTests`, `RealOcrRecognitionTests` 8/8, `WordCapturePreviewTests`

## 8. 文件清单

- 修改: `GlobalHotkeyService.cs`, `IGlobalHotkeyService.cs`, `DefaultHotkeyBroker.cs`, `HotkeyEvent.cs`, `BlockInteractionCoordinator.cs`, `BlockRetryCoordinator.cs` (可选), `WpfSelectionOverlayService.cs` (无需改，仅调用方), `SelectionOptions.cs` (可选阈值常量)
- 新增: `BlockDragState.cs` (可选，封装 dragging 上下文)
- 测试: `HotkeyBrokerHoldTests.cs`, `BlockDragSelectionTests.cs`
