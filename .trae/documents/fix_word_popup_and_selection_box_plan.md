# 修复：单词翻译不弹框、句子翻译弹框、两者都无选定框

> 版本：v1.0 | 更新：2026-08-12 | 适用：`e:\翻译`（QuickTranslate）

---

## 一、当前状态分析（代码 + 运行日志调研）

用户症状：**单词翻译（Alt+1）不弹框，句子翻译（Alt+2）弹框，但两者都没有选定框**。

### 关键线索：运行日志里的异常
[全局运行日志](file:///C:/Users/Administrator/AppData/Local/QuickTranslate/Logs/QuickTranslate-20260812.log) 反复出现：
```
[ERR] Dispatcher Unhandled Exception
System.ArgumentException: Enum underlying type and the object must be same type or object must be a String.
   Type passed in was 'System.UInt32'; the enum underlying type was 'System.Int32'.
   at QuickTranslate.Platform.Win32.GlobalHotkeyService.WndProc(...) GlobalHotkeyService.cs:line 52
```

**根因结论**：`GlobalHotkeyService.WndProc` 里用 `Enum.IsDefined(typeof(KeyboardKey), vk)` 判断按键合法性，但**当 `vk` 为 `uint` 时**（旧构建），`KeyboardKey` 底层类型是 `int`，`Enum.IsDefined` 直接抛 `ArgumentException`。异常在 `HotkeyPressed?.Invoke` 之前抛出，被 App 的 `DispatcherUnhandledException` 捕获（`e.Handled=true`，不崩溃），**热键事件永远到不了协调器** → 按了没反应、不弹框、无选定框。

> 注：当前源码 [GlobalHotkeyService.cs:49](file:///e:/翻译/src/QuickTranslate.Platform/Win32/GlobalHotkeyService.cs#L49) 已是 `int vk`（已修复），但需**防御性加固**避免此类问题复现，并确保用户运行的是修正后的构建。

### 选定框不可见：本会话新增的动画把框搞隐形
[SelectionOverlayWindow.xaml.cs:33-52](file:///e:/翻译/src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml.cs#L33-L52) 的 `BeginEntryAnimation` 在每次可见时执行 `this.Opacity = 0;` 再 `BeginAnimation(Opacity, 0→1)`。若动画未正常推进（或瞬间被 Popup 打断），**窗口 Opacity 停在 0 → 选定框完全不可见**。这解释了“两个模式都没有选定框”。

### 单词 vs 句子差异
- 句子（Block）能弹框 → Block 热键能触发、OCR/选段/翻译链路 OK。
- 单词（Word）不弹框 → Word 热键触发后，在**选词/翻译**环节失败或未给出可见反馈（最可能是 OCR 没能把鼠标所指的单个英文词框出来 → `NoTextFound` → 只弹错误气泡，用户不视为“翻译弹框”；或翻译环节报错）。

---

## 二、修改文件 & 模块清单

| # | 文件 | 改动 | 解决 |
|---|------|------|------|
| 1 | `src/QuickTranslate.Platform/Win32/GlobalHotkeyService.cs` | `WndProc` 里按键合法性判断改为**绝不抛异常**（`int` 转换 + `try/catch` 兜底） | 热键分发（根因） |
| 2 | `src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml.cs` | **移除 `Opacity=0` 窗口动画**，只保留不隐藏框的边框脉冲 | 选定框可见 |
| 3 | `src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs` | 排查并确保 Word 链路给出可见反馈（选词失败/翻译失败都弹错误气泡，成功弹结果） | 单词弹框 |
| 4 | 重建 + 重启应用 | 让用户运行含以上修复的构建 | 全部 |

---

## 三、详细实施步骤

### 步骤 1：修复 `GlobalHotkeyService.WndProc` 热键分发的异常（根因）

**文件 [GlobalHotkeyService.cs](file:///e:/翻译/src/QuickTranslate.Platform/Win32/GlobalHotkeyService.cs#L45-L75)**

把按键合法性判断改为防御式，任何情况下都不抛异常：
```csharp
KeyboardKey key = KeyboardKey.None;
try
{
    // vk 来自 Win32 WM_HOTKEY（始终在 0..255），转 int 后安全判断；
    // Enum.IsDefined 在类型不匹配（如 uint）时会抛，这里兜底避免热键分发中断。
    key = vk >= 0 && Enum.IsDefined(typeof(KeyboardKey), vk)
        ? (KeyboardKey)vk
        : KeyboardKey.None;
}
catch (ArgumentException)
{
    key = KeyboardKey.None;
}
```
> 同时确认 `vk` 在 [L49](file:///e:/翻译/src/QuickTranslate.Platform/Win32/GlobalHotkeyService.cs#L49) 保持为 `int`（已是）。这样即使未来某处误传 `uint`，也不会让热键分发整体失效。

### 步骤 2：让选定框可见（移除隐形动画）

**文件 [SelectionOverlayWindow.xaml.cs:33-52](file:///e:/翻译/src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml.cs#L33-L52)**

`BeginEntryAnimation` 改为：
- **删除** `this.Opacity = 0;` 与窗口 `OpacityProperty` 的淡入 `BeginAnimation`（这是让框隐形的元凶）。
- **保留**（或改为更安全）仅对 `SelectionBorder` 的边框脉冲：`SelectionBorder.BeginAnimation(Border.OpacityProperty, 0.6→1.0 AutoReverse)`——边框最低 0.6 透明度，框始终可见，只是呼吸增强。
- 若不想要任何动画，直接清空 `BeginEntryAnimation` 或让 `IsVisibleChanged` 不再触发动画。

> 目标：选定框在 250ms 可见窗口内（[WordInteractionCoordinator.cs:183-197](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L183-L197) 的 hold）清晰显示，不再隐形。

### 步骤 3：确保 Word 链路给出可见反馈 + 排查单词不弹框

**文件 [WordInteractionCoordinator.cs](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L112-L246)**

在热键分发修复后，运行期按以下排查：
1. 给 Word 流水线关键点补结构化日志（`State ->` 已有；确认 `NoTextFound` 分支、翻译分支都被走到）。
2. 若 Word 因 OCR 选词 `NoTextFound` 失败：确认 `_popupService.ShowError(...)` 错误气泡能正常显示（`WpfWordPopupService.ShowError` 与 Block 同构，应可行）。
3. 若 Word 翻译报错：确认 `ShowError` 弹出（`TranslationErrorMessage.ForCode`）。
4. 成功路径：`_popupService.Show(sel, trans, ...)` 弹出结果。

> 若排查发现 Word 在真实 OCR 下总选不中鼠标所指单词（OCR 词框偏移），则在 `BuildWords`/`WordSelector` 侧做容错（如增大 `MaxDistance`、对 `NoTextFound` 时用整行文本兜底），以保证“单词翻译”至少能弹出结果或明确错误。

（此步骤以验证为主：先跑通热键分发 + 框可见，再按日志定位 Word 具体失效环节。）

### 步骤 4：重建 + 重启

- 结束正在运行的 `QuickTranslate.App` 进程（释放 DLL 锁）。
- `dotnet build` 后重新启动应用，让用户运行含修复的构建。

---

## 四、假设 & 决策

1. **热键分发是根因**：`WndProc` 的 `Enum.IsDefined(uint)` 异常会让每次按键都中断分发（旧构建）。当前源码已 `int vk`，但做防御加固 + 重启修正构建，确保用户拿到可用版本。
2. **选定框隐形由动画导致**：移除 `Opacity=0` 窗口动画；保留不隐藏框的边框脉冲。
3. **单词不弹框需运行期验证**：先修复分发 + 框可见，再按日志定位 Word 具体失败环节；若属 OCR 选词容错，则做最小兜底。
4. **不新增功能**：只修复“弹框/不弹框/无选定框”这三件事。

---

## 五、验证清单（Checklist）

- [ ] `dotnet build QuickTranslate.sln` 0 错误
- [ ] `dotnet test --no-build` 全绿
- [ ] 运行日志中不再出现 `Enum underlying type...` 的 `Dispatcher Unhandled Exception`
- [ ] 按 **Alt+1**（单词翻译）：出现**选定框** → 弹出单词翻译结果（或明确错误气泡）
- [ ] 按 **Alt+2**（句子翻译）：出现**区域选定框** → 弹出句子翻译结果
- [ ] 确认 Word 流水线日志走到 `State -> OverlayVisible / Translating / Displaying`（不再中途静默退出）

---

## 六、交付与回滚

- 交付：完成步骤 1-4，build + test 通过，应用重启。
- 回滚：若步骤 3 的 Word 容错改动有风险，可仅保留步骤 1+2（热键分发 + 框可见）作为本次核心交付，Word 弹框问题在拿到日志后单独跟进。