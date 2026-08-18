# 翻译模式命名 + 翻译前选定框可见性/动画修复

> 版本：v1.0 | 更新：2026-08-12 | 适用：`e:\翻译`（QuickTranslate）

---

## 一、需求与现状

用户反馈两点：
1. **命名困惑**：设置页把两个模式叫「Word 取词」（Alt+1）和「Block 划词」（Alt+2），不够直白。用户明确：一个是**单词翻译**，一个是**区域的句子翻译**。→ 要求重命名为中文直白叫法。
2. **翻译前选定框不可见**：翻译之前应出现一个**选定框动画**指示翻译范围，但实际看不到框。→ 要求修复。

### 现状调研结论

**（1）命名出处**（仅 3 处真实出现，其余为英文 Word/Block 用词）：
- [SettingsWindow.xaml:351](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml#L351) `Text="Word 取词"`（Alt+1）
- [SettingsWindow.xaml:368](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml#L368) `Text="Block 划词"`（Alt+2）
- [SettingsWindow.xaml:562](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml#L562) 状态行默认 `Text="Word: Alt+1  Block: Alt+2"`
- [SettingsWindow.xaml.cs:115-120](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml.cs#L115-L120) `UpdateHotkeyStatusPreview` 用 `$"Word: {w}  Block: {b}"`
- [SettingsValidator.cs](file:///e:/翻译/src/QuickTranslate.App/Validation/SettingsValidator.cs#L79-L89) 错误文案含「无法用作 Word 热键 / Block 热键」
- [PaddleOcrV6Engine.cs:278](file:///e:/翻译/src/QuickTranslate.Infrastructure/Ocr/PaddleOcrV6Engine.cs#L278) 代码注释含「取词不可用」（仅注释）

**（2）选定框现状**：
- 覆盖框是 [SelectionOverlayWindow.xaml](file:///e:/翻译/src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml#L18-L25) 的静态 `SelectionBorder`（2px 橙色边框 + 低透明度填充），**无任何动画**。
- 协调器在「选中后、翻译前」调用 `_overlayService.Show(...)`（[WordInteractionCoordinator.cs:168](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L168)、[BlockInteractionCoordinator.cs:201](file:///e:/翻译/src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs#L201)），随后 `await` 翻译，再弹 Popup。
- **根因**：翻译可走本地词典/缓存**瞬时返回**（[DefaultTranslationRouter.cs:57-66](file:///e:/翻译/src/QuickTranslate.Infrastructure/Cache/DefaultTranslationRouter.cs#L57-L66)），于是 `Show` 之后几乎立刻 `Popup.Show`，选定框闪现 <1ms 被 Popup 覆盖 → 用户完全看不到。

---

## 二、修改文件 & 模块清单

| # | 文件 | 改动 | 解决 |
|---|------|------|------|
| 1 | `src/QuickTranslate.App/Windows/SettingsWindow.xaml` | 「Word 取词」→「单词翻译」；「Block 划词」→「区域句子翻译」；状态行「Word:…Block:…」→「单词:…区域:…」 | 命名 |
| 2 | `src/QuickTranslate.App/Windows/SettingsWindow.xaml.cs` | `UpdateHotkeyStatusPreview` 文案同步 | 命名 |
| 3 | `src/QuickTranslate.App/Validation/SettingsValidator.cs` | 错误文案「无法用作 Word/Block 热键」→「单词/区域热键」 | 命名 |
| 4 | `src/QuickTranslate.Infrastructure/Ocr/PaddleOcrV6Engine.cs` | 注释「取词不可用」措辞更新（仅注释） | 命名 |
| 5 | `src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml` + `.xaml.cs` | 新增**淡入 + 边框脉冲**动画，使选定框出现时清晰可见 | 动画 |
| 6 | `src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs` | 在 `Popup.Show` 前加**最小可见时长**（~250ms）保证框被看到 | 可见性 |
| 7 | `src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs` | 同上 | 可见性 |
| 8 | `tests/QuickTranslate.Tests/Coordination/*` | 更新受 250ms hold 影响的断言时延（等待进入 `Displaying`） | 测试 |

---

## 三、详细实施步骤

### 步骤 1：重命名「取词/划词」→「单词翻译 / 区域句子翻译」

**文件 [SettingsWindow.xaml](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml)**
- L351 `<TextBlock Text="Word 取词" .../>` → `<TextBlock Text="单词翻译" .../>`
- L368 `<TextBlock Text="Block 划词" .../>` → `<TextBlock Text="区域句子翻译" .../>`
- L562 `HotkeyStatusText` 默认值 `Text="Word: Alt+1  Block: Alt+2"` → `Text="单词: Alt+1  区域: Alt+2"`

**文件 [SettingsWindow.xaml.cs](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml.cs#L115-L120)**
- `UpdateHotkeyStatusPreview()`：`$"Word: {w}  Block: {b}"` → `$"单词: {w}  区域: {b}"`

**文件 [SettingsValidator.cs](file:///e:/翻译/src/QuickTranslate.App/Validation/SettingsValidator.cs#L79-L89)**
- 「无法用作 Word 热键」→「无法用作单词热键」；「无法用作 Block 热键」→「无法用作区域热键」（两处，保持其余建议文案不变）

**文件 [PaddleOcrV6Engine.cs](file:///e:/翻译/src/QuickTranslate.Infrastructure/Ocr/PaddleOcrV6Engine.cs#L278)**
- 注释内「取词不可用」→「单词识别不可用」（仅注释，无行为影响）

> 注：`SettingsConflictValidationTests` 只断言「相同/冲突/修饰键/字母/系统常用快捷键/Esc/Ctrl+Alt+」，**不断言**「Word 热键/Block 热键」，故改名安全。

### 步骤 2：选定框动画（淡入 + 边框脉冲）

**文件 [SelectionOverlayWindow.xaml](file:///e:/翻译/src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml)**
在 `SelectionBorder` 上加入动画（用窗口 `Loaded` 触发，或以 `Border` 的常规动画实现）：
- **淡入**：窗口 `Opacity` 0→1，200ms，`EaseOut`，让框出现时柔和地被注意到。
- **脉冲**：`SelectionBorder` 的 `BorderThickness` 或边框透明度做一次「2→3→2」的呼吸，300ms，增强「指示范围」的视觉反馈。

建议以窗口为动画作用域（`BeginStoryboard` 到 `Window` 的 `Opacity` 与 `SelectionBorder`），`Loaded` 事件启动；`Hide`/`HideAll` 时动画自然随窗口隐藏。

> 实现要点：动画只做**视觉增强**，不阻塞任何逻辑；`Hide()` 后重 `Show()` 会重新触发 `Loaded` 动画。

### 步骤 3：保证翻译前选定框可见（最小可见时长）

**文件 [WordInteractionCoordinator.cs](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs)**
- 新增常量：`private const int SelectionHoldMs = 250;`
- 在 `_overlayService.Show(sel.Box, mid, dpiX, dpiY);`（L168）后记录 `long overlayShownAt = Environment.TickCount64;`
- 在 `TranslateWordAsync` 完成、`IsStaleOrCanceled` 检查之后、`SetState(Displaying)` 与 `_popupService.Show(...)` 之前插入：
  ```csharp
  long elapsed = Environment.TickCount64 - overlayShownAt;
  int remaining = SelectionHoldMs - (int)elapsed;
  if (remaining > 0)
  {
      try { await Task.Delay(remaining, newSlot.Cts.Token).ConfigureAwait(false); }
      catch (OperationCanceledException) { return; }
      if (IsStaleOrCanceled(newSlot)) return;
  }
  ```
- 效果：本地词典/缓存瞬时翻译时，框至少停留 250ms 再弹 Popup；在线慢翻译时 `elapsed` 已超 250ms，不额外等待。

**文件 [BlockInteractionCoordinator.cs](file:///e:/翻译/src/QuickTranslate.App/Coordination/BlockInteractionCoordinator.cs)**
- 同样新增 `SelectionHoldMs = 250`，在 `_overlayService.Show(block.UnionBox, ...)`（L201）后记录时间，在 `TranslateBlockAsync` 完成后、`SetState(Displaying)` 前插入同样的 hold。

### 步骤 4：更新受影响的协调器测试

新增 hold 后，原「设置翻译结果 → 等 ~50ms → 断言 `Displaying` / `popup.ShowCount==1`」的用例会因 250ms hold 而时序不足。将这类等待改为**等待进入目标状态**（既有 `CoordinatorTestHelpers.WaitForState`），或把 `Task.Delay` 提到 ≥ 300ms：

- [CancelRaceTests.cs:64-69](file:///e:/翻译/tests/QuickTranslate.Tests/Coordination/CancelRaceTests.cs#L64-L69) 与 :129-130：`await Task.Delay(50)` → `await CoordinatorTestHelpers.WaitForState(coord, AppState.Displaying)`（或延迟 ≥300ms）
- [CoordinatorErrorTests.cs:184](file:///e:/翻译/tests/QuickTranslate.Tests/Coordination/CoordinatorErrorTests.cs#L184)：`popup.ShowCount==1` 断言前加对应等待
- [BlockInteractionCoordinatorTests.cs:120](file:///e:/翻译/tests/QuickTranslate.Tests/Coordination/BlockInteractionCoordinatorTests.cs#L120)：同上

> 逐个读文件确认具体等待方式后，用 `WaitForState` 替换裸 `Task.Delay` 即可，不改断言语义。

---

## 四、假设 & 决策

1. **命名**：采用用户原话「单词翻译」「区域句子翻译」。Block 不再叫「段落翻译」，避免与用户措辞不一致。
2. **选定框可见性根因**：确定是「瞬时翻译导致框被 Popup 立即覆盖」，而非 overlay 完全不上屏（`SelectionOverlayTests` 已证明 Show/Hide 正常）。修复用「最小可见时长 + 淡入/脉冲动画」组合。
3. **hold 放在 Popup.Show 之前**：保证「先看到框、再看到结果」的时序；在线慢翻译不产生额外等待。
4. **测试改动**：只改等待时序，不改断言语义；若个别用例不依赖时序则不动。

---

## 五、验证清单（Checklist）

- [ ] `dotnet build QuickTranslate.sln` 0 错误
- [ ] `dotnet test --no-build` 全绿（含改动的协调器测试）
- [ ] 打开设置页：显示「单词翻译 / 区域句子翻译」，状态行显示「单词: Alt+1  区域: Alt+2」
- [ ] 鼠标放英文单词按 **Alt+1**：先出现淡入的橙色选定框（约 250ms），随后弹出翻译结果
- [ ] 鼠标放段落按 **Alt+2**：同样先出现区域选定框，再显示段落翻译
- [ ] 本地词典命中（瞬时翻译）时，选定框仍可见约 250ms（不再一闪而过）
- [ ] 验证错误文案：设置页冲突提示显示「无法用作单词热键 / 区域热键」

---

## 六、交付与回滚

- 交付：步骤 1-4 全部完成，build + test 通过。
- 回滚：若 hold 导致某用例仍偶发失败，优先调整该用例等待时序；若动画影响性能，可仅保留淡入、去掉脉冲。