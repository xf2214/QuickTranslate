# Block 长按拖选段落 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 Alt+2 长按(400ms)进入拖动态，向下拖动按 OCR 行实时扩展选区（虚线预览无扫描），拖出 1200×720 底边一次性扩大至 1600×1200 重识别，松开切实线扫描并翻译；点按(<400ms)保持现状单次块识别。

**Architecture:** 在 GlobalHotkeyService 新增 WH_KEYBOARD_LL 钩子检测 Alt+2 按下/抬起时长，DefaultHotkeyBroker 转译为 BlockHoldStart/End 事件；BlockInteractionCoordinator 新增 Dragging 状态，用 16ms 轮询 ICursorService 按 Y 过滤 OcrLayoutResult.Lines 重算 UnionBox 并 Update 浮层；BlockRetryCoordinator 首帧保持 1200×720，仅拖出范围时走 1600×1200 二次截屏。

**Tech Stack:** C# .NET 10, WPF (WS_EX_NOACTIVATE), Win32 WH_KEYBOARD_LL, GDI BitBlt, PP-OCRv6 ONNX, xUnit

**Spec:** `docs/superpowers/specs/2026-08-27-block-drag-design.md`

## Global Constraints

- Windows 10/11 x64, .NET 10 SDK, PerMonitorV2 DPI 96 基准
- 仅向下拖动，选区单调只增，最大 1600×1200
- 拖动期无扫描动画，松开才扫描（700ms sweep + 250ms hold）
- 现有 603 测试 + 8 RealOcr 回归必须保持通过
- 隐私/DPAPI/共变提交约束见 AGENTS.md

---

### Task 1: 热键长按基础设施

**Files:**
- Modify: `src/QuickTranslate.Platform/Win32/GlobalHotkeyService.cs:1-130`
- Modify: `src/QuickTranslate.Core/Abstractions/IGlobalHotkeyService.cs`
- Modify: `src/QuickTranslate.Platform/Hotkeys/DefaultHotkeyBroker.cs:1-80`
- Modify: `src/QuickTranslate.Core/Options/HotkeyEvent.cs`
- Modify: `src/QuickTranslate.Core/Options/HotkeyEventType.cs` (可选新增 Hold 类型或复用 Block + Duration 区分)
- Test: `tests/QuickTranslate.Tests/Platform/HotkeyBrokerHoldTests.cs` (新建)

**Interfaces:**
- Consumes: 现有 RegisterHotKey/WM_HOTKEY, WH_MOUSE_LL 参考实现
- Produces: `event EventHandler<HotkeyHoldEvent> BlockHoldStateChanged` 或 `HotkeyEvent` 扩展 `TimeSpan HoldDuration + bool IsHold` ; `GlobalHotkeyService.KeyStateChanged: Down/Up`

- [ ] **Step 1: Write failing test for hold vs tap**

```csharp
// tests/.../HotkeyBrokerHoldTests.cs
[Fact]
public async Task Block_HoldOver400ms_FiresHoldStartThenHoldEnd()
{
    var svc = new FakeGlobalHotkeyService();
    var broker = new DefaultHotkeyBroker(svc, logger, lifecycle);
    int holdStarts=0, holdEnds=0, fires=0;
    broker.BlockHoldStateChanged += (s,e) => { if(e.Phase=="Start") holdStarts++; if(e.Phase=="End") holdEnds++; };
    broker.HotkeyFired += (s,e) => fires++;
    svc.RaiseKeyDown(BlockId); // Alt+2 down
    await Task.Delay(450);
    svc.RaiseKeyUp(BlockId);
    await Task.Delay(50);
    Assert.Equal(1, holdStarts);
    Assert.Equal(1, holdEnds);
    Assert.Equal(0, fires); // hold 不应再发 Fired
}
[Fact]
public async Task Block_TapUnder400ms_FiresHotkeyFiredOnly()
{
    // Down 200ms Up -> Fired=1, hold=0
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter HotkeyBrokerHoldTests -v`
Expected: FAIL 找不到 BlockHoldStateChanged

- [ ] **Step 3: Implement WH_KEYBOARD_LL hook**

```csharp
// GlobalHotkeyService.cs 新增
private const int WH_KEYBOARD_LL = 13;
private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN=0x0104, WM_KEYUP=0x0101, WM_SYSKEYUP=0x0105;
private IntPtr _kbHook; private LowLevelKeyboardProc _kbProc;
private Dictionary<int, DateTime> _downTimes = new();
private event EventHandler<KeyStateEventArgs> KeyStateChanged; // Down/Up + id

private void EnsureKeyboardHookInstalled() => SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, GetModuleHandle(null), 0);
private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
  if(nCode>=0){
    int vk = Marshal.ReadInt32(lParam);
    bool isDown = wParam==(IntPtr)WM_KEYDOWN || wParam==(IntPtr)WM_SYSKEYDOWN;
    bool isUp = wParam==(IntPtr)WM_KEYUP || wParam==(IntPtr)WM_SYSKEYUP;
    // 仅当 Alt 按下(GetAsyncKeyState(VK_MENU)<0) 且 vk==0x32(D2) 触发
  }
  return CallNextHookEx(_kbHook, nCode, wParam, lParam);
}
```

`DefaultHotkeyBroker` 订阅 `KeyStateChanged`, 用 `Task.Delay(400)` + `CancellationTokenSource` 实现阈值计时：Down 启动计时, 400ms 后若仍 Down 且未 Up 则发 `HoldStart`; Up 时若已发 HoldStart 则发 `HoldEnd(duration)`, 否则发 `HotkeyFired(Block)`。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter HotkeyBrokerHoldTests`
Expected: PASS 2/2

- [ ] **Step 5: Commit**

```bash
git add src/QuickTranslate.Platform/Win32/GlobalHotkeyService.cs src/QuickTranslate.Core/Abstractions/IGlobalHotkeyService.cs src/QuickTranslate.Platform/Hotkeys/DefaultHotkeyBroker.cs tests/QuickTranslate.Tests/Platform/HotkeyBrokerHoldTests.cs
git commit -m "feat(hotkey): add WH_KEYBOARD_LL hold detection for Alt+2 400ms"
```

---

### Task 2: Block 协调器拖动态

**Files:**
- Modify: `src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs:14-360`
- Modify: `src/QuickTranslate.App/Bootstrap/AppHost.cs:132-149` (WireBlockHold)
- Test: `tests/QuickTranslate.Tests/Coordination/BlockDragCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task1 Hold events, ICursorService, IMonitorService, BlockRetryCoordinator.SelectBlockWithRetryAsync, ISelectionOverlayService
- Produces: `Dragging` state, `ExpandedUnionBox`, `HoldEnd` triggers translation

- [ ] **Step 1: Write failing test for drag expansion**

```csharp
[Fact]
public async Task Hold_DragDown_ExpandsUnionBox()
{
  var ocr = OcrWith5Lines(y0:100, h:20); // 5 OcrLine 100,120,140,160,180
  blockCoord.StartWithHoldEvents();
  SimulateHoldStart(anchor: (200,110));
  await AdvanceDragTo(y:165); // 跨过3行
  Assert.Equal(UnionOfLines(0..3), overlay.LastBox);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BlockDragCoordinatorTests`
Expected: FAIL 找不到 Hold 逻辑

- [ ] **Step 3: Implement dragging state machine**

```csharp
// BlockInteractionCoordinator.cs 新增字段
private const int HoldThresholdMs = 400;
private const int MaxCaptureWidth = 1600, MaxCaptureHeight=1200;
private DispatcherTimer? _dragTimer;
private PhysicalPoint _anchorPoint;
private OcrLayoutResult? _dragOcr;
private PhysicalRect _dragInitialUnion;
private CancellationTokenSource? _holdCts;

private void OnHoldStart(PhysicalPoint anchor){ _anchorPoint=anchor; _dragOcr=currentOcr; EnterDragging(); }
private void OnHoldEnd(){ _dragTimer?.Stop(); overlay.Show(expandedBox, preview:false); TranslateAndDisplayBlockAsync(...); }
// Drag tick 16ms:
void OnDragTick(){
  var cur = cursor.GetPhysicalCursorPos(out _);
  if(cur.Y <= _anchorPoint.Y) return;
  var expanded = ExpandSelectedLines(_dragOcr.Lines, _anchorPoint.Y, cur.Y);
  if(expanded != _lastExpanded) { overlay.Update(expanded.UnionBox, mid, dpiX,dpiY); }
  if(cur.Y > frame.Region.Bottom-20 && frame.Width <1600) { ExpandCaptureTo1600(); }
}
PhysicalRect ExpandSelectedLines(IReadOnlyList<OcrLine> lines, int anchorY, int dragY){
  var anchorLine = FindAnchorLine(lines, anchorY);
  return UnionOf(lines.Where(l=> l.Box.Top>=anchorLine.Top && l.Box.Bottom <= dragY+lineHeight/2));
}
```

点按路径保持原 `RunBlockPipelineAsync` 不变；长按路径复用同一 `OperationSlot` 但跳过自动 250ms hold 直到松开。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter BlockDrag`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs src/QuickTranslate.App/Bootstrap/AppHost.cs
git commit -m "feat(block): add hold-drag state machine with live expand"
```

---

### Task 3: 浮层直播更新

**Files:**
- Modify: `src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs` (调用点)
- Test: 已在 Task2 覆盖，增加 `SelectionOverlayUpdateTests` 断言

**Interfaces:**
- Consumes: ISelectionOverlayService.Update
- Produces: 拖动期虚线预览，松开实线扫描

- [ ] **Step 1: Write failing test for preview vs scan**

```csharp
[Fact]
public void Drag_ShowsPreview_HoldEndShowsScan(){
  // 拖动中 Inspect().preview == true, 松开后 preview==false 且触发扫描
}
```

- [ ] **Step 2: Run test to verify it fails**

- [ ] **Step 3: Implement**

```csharp
// 进入拖动
overlay.Show(initialUnion, mid, dpiX,dpiY, preview:true);
// 轮询中
overlay.Update(expandedUnion, mid, dpiX,dpiY); // Update 保持 preview 模式（WpfSelectionOverlayService 需支持 preview 标志透传，或用 Show(...,preview:true) 替代）
// 松开
overlay.Show(finalUnion, mid, dpiX,dpiY, preview:false);
```

若 `Update` 不支持 preview 参数，改为 `Show(...,preview:true)` 重调用（已验证重调用不重建窗口，仅 ApplyLayout + 重播扫描需抑制，见 SelectionOverlayWindow 199 行）。

- [ ] **Step 4: Run test**

- [ ] **Step 5: Commit**

---

### Task 4: 截屏扩大兜底

**Files:**
- Modify: `src/QuickTranslate.Infrastructure/Coordination/BlockRetryCoordinator.cs` (或直接在 BlockInteractionCoordinator 内二次 Capture)
- Test: 扩展 Task2 测试用例 `DragBeyondFrame_ExpandsTo1600`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task DragBeyondInitialFrame_ExpandsCapture(){
  // 初始 1200x720, dragY 超出底边 50px -> 断言 capture.CaptureAroundSizes[1]==1600x1200
}
```

- [ ] **Step 2: Run test**

- [ ] **Step 3: Implement**

```csharp
if(dragY > currentFrame.Region.Bottom - 20 && currentFrame.Region.Width < 1600){
  var newSize = new PhysicalSize(1600,1200);
  using var newFrame = await capture.CaptureAroundAsync(_anchorPoint, newSize, ct);
  var newOcr = await ocrEngine.RecognizeAsync(newFrame, MakeBand(...), ct);
  _dragOcr = newOcr; currentFrame = newFrame;
}
```

- [ ] **Step 4: Run test**

- [ ] **Step 5: Commit**

---

### Task 5: 回归与抛光

**Files:**
- All modified files

- [ ] **Step 1: Run full suite**

Run: `dotnet test --filter "FullyQualifiedName~Block|FullyQualifiedName~Hotkey|FullyQualifiedName~SelectionOverlay" -v`
Expected: 603+新增 6-8 通过

- [ ] **Step 2: Run RealOcr regression**

Run: `dotnet test --filter RealOcrRecognitionTests`
Expected: 8/8 PASS

- [ ] **Step 3: Manual smoke**

Run: `dotnet run --project src/QuickTranslate.App` → Alt+2 点按单行扫描；长按 400ms 后向下拖动虚线扩展，松开扫描并翻译；Esc 取消

- [ ] **Step 4: Commit docs**

```bash
git add docs/superpowers/specs/2026-08-27-block-drag-design.md docs/superpowers/plans/2026-08-27-block-drag-impl.md
git commit -m "docs: add block drag design and plan"
```
