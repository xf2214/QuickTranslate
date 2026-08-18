# 快捷键冲突修复 + 设置页面优化 实施计划

> 版本：v1.0 | 更新：2026-08-12

---

## 一、问题根因分析（代码调研结论）

### 🔴 Bug 1：任何快捷键都报冲突（严重）

**位置**：[DefaultHotkeyBroker.cs:110-123](file:///e:/翻译/src/QuickTranslate.Platform/Hotkeys/DefaultHotkeyBroker.cs#L110-L123) Probe 方法

**根因链路**：
1. 启动时 `RegisterDefaultsFromSettings` 用 `WordId=0xB001` 注册了 `Alt+D1`，用 `BlockId=0xB002` 注册了 `Alt+D2`（代码 [DefaultHotkeyBroker.cs:39-70](file:///e:/翻译/src/QuickTranslate.Platform/Hotkeys/DefaultHotkeyBroker.cs#L39-L70)）
2. 用户打开设置窗口，点"保存"时 `SettingsValidator.Validate` 调用 `hotkeyBroker.Probe(Alt, D1)`（代码 [SettingsValidator.cs:22-27](file:///e:/翻译/src/QuickTranslate.App/Validation/SettingsValidator.cs#L22-L27)）
3. `Probe` 方法用新的 `probeId=0xBFFF` 去注册完全相同的 `(mods, vk)` 组合
4. **Win32 RegisterHotKey API 硬性规定**：同一个 Hwnd 句柄下，不同的 ID 注册完全相同的 `(modifiers + vk)` 会返回 FALSE（即使 ID 不同）
5. 结果：Probe 返回 false → 误报为"与其他应用冲突"

**后果**：默认的 Alt+1/Alt+2 永远无法通过 SettingsValidator，"任何快捷键都有冲突"。只有换成从未被自己注册过的全新键位才能通过。

### 🟡 Bug 2：设置页面 UI/UX 缺失

- **[SettingsWindow.xaml](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml)** 仅 520×560 单一无分组布局，6 类配置项（API密钥/热键/语言/质量/启动/日志）混杂，难以理解
- HotkeyRecorder 录完键后**无实时冲突反馈**，只有用户点保存时才报冲突（用户以为录好了，保存才被打回，体验差）
- [Generic.xaml HotkeyRecorder 样式](file:///e:/翻译/src/QuickTranslate.App/Themes/Generic.xaml#L5-L28) 只有裸 TextBox+Button，无视觉状态（可用/冲突/自冲突/非法键 无颜色区分）
- 无状态指示：当前热键是否生效、OCR引擎是否加载、API Key是否有效都不知道
- 保存后直接 Close()，无"已保存"反馈提示，用户不知道改没生效
- 无"恢复默认热键"快捷操作

### 🟡 Bug 3：SettingsValidator 过于宽容

- 未禁止"纯修饰符键"（Ctrl / Ctrl+Shift 无主键）
- 未禁止明显无效键：Ctrl+C/Ctrl+V 等系统键可以被保存（虽不致命但用户实际用不了）
- 冲突错误信息不友好："Word 热键与其他应用冲突，请更换键位"无建议

---

## 二、修改模块与文件清单

| 层级 | 文件 | 修改类型 | 说明 |
|------|------|----------|------|
| **Platform** | [DefaultHotkeyBroker.cs](file:///e:/翻译/src/QuickTranslate.Platform/Hotkeys/DefaultHotkeyBroker.cs) | 🔧 修改 | 修复 Probe：自身已注册组合直接返回 true，不触发实际 Win32 探测 |
| **Platform** | IHotkeyBroker 接口（如在 Core 中） | ➕ 可选扩展 | 增加新方法 `GetRegisteredHotkeys()` 返回当前已注册列表，用于 UI 显示状态 |
| **App.Validation** | [SettingsValidator.cs](file:///e:/翻译/src/QuickTranslate.App/Validation/SettingsValidator.cs) | 🔧 修改 | 增加无效键黑名单/纯修饰符检查/更友好的中文错误信息 |
| **App.Controls** | [HotkeyRecorder.cs](file:///e:/翻译/src/QuickTranslate.App/Windows/Controls/HotkeyRecorder.cs) | 🔧 修改 | 增加实时 Probe（录完键立即探测并更新内部状态）+ 新增 Status 依赖属性 |
| **App.Themes** | [Generic.xaml](file:///e:/翻译/src/QuickTranslate.App/Themes/Generic.xaml) | 🔧 修改 | 重写 HotkeyRecorder 模板：加入状态色边框、状态 Badge（可用/冲突/自冲突）、清除按钮 |
| **App.Windows** | [SettingsWindow.xaml](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml) | 🔧 重写 | 按分组重新排布布局（Section卡片化），增加系统状态区，增加保存 Toast，增加恢复默认按钮，字体/间距/颜色统一设计语言 |
| **App.Windows** | [SettingsWindow.xaml.cs](file:///e:/翻译/src/QuickTranslate.App/Windows/SettingsWindow.xaml.cs) | 🔧 修改 | 绑定新增状态（热键实时状态/OCR引擎/API Key）+ 恢复默认处理 + Toast 显示 + 不立即 Close |
| **Tests** | HotkeyBrokerConflictTests.cs（Platform测试） | ➕ 新增用例 | 验证：RegisterDefaults → Probe(相同Alt+1) 返回 true；Probe(真正冲突的键) 返回 false |
| **Tests** | SettingsConflictValidationTests.cs | ➕ 新增用例 | 验证：纯修饰符被拒、系统保留键给出警告、自冲突检测 |

---

## 三、详细实施步骤

### 阶段 1：修复快捷键冲突根因（优先级最高）

**步骤 1.1** — 修复 `DefaultHotkeyBroker.Probe`
- Probe 入口处新增"自身已注册组合"检查：若 `(mods, key)` 与 `_wordCombo` 或 `_blockCombo` 或 `_escCombo` 任意一个**完全相等** → 直接 `return true`（自己的键，不算冲突）
- 仅在"与自身全部不等"时才调用底层 `Register(probeId, mods, key)` 做真实探测
- 探测完立即 `Unregister(probeId)` 释放

**步骤 1.2** — 增强 `TryUpdateHotkeys` 的回滚逻辑正确性
- 目前代码先 Unregister(WordId) 再 Register(新Word)，若新失败则 Register(旧Word) 回滚
- 确认回滚时失败的保护（理论上旧键刚被解绑，立即重绑不会冲突，但仍加 try/catch + 日志 warning）

### 阶段 2：SettingsValidator 加固

**步骤 2.1** — 新增 IsForbidden 规则
- 纯修饰符：`key == None` 直接报错"请按下除修饰键外的具体按键"
- 明显不宜：`Esc / Tab / Enter / PrintScreen / 左右Ctrl/Alt/Shift单独` 禁止用于Word/Block
- 警告级：`Ctrl+C/V/X/Z/A/S/F/W`（系统常用）不强制拦截，但建议改为 Ctrl+Alt 开头

**步骤 2.2** — 错误信息中文友好化 + 修复建议
- 格式从"Word 热键与其他应用冲突，请更换键位"改为：
  > "Alt+1 已被占用（可能是本软件自身或其他应用），建议尝试 Ctrl+Alt+1 / Ctrl+Shift+Q / Win+F8"

### 阶段 3：HotkeyRecorder 控件增强 + 重写样式

**步骤 3.1** — HotkeyRecorder.cs 新增：
- DP `ProbeStatus`（枚举：Unknown / Available / SelfConflict / ExternalConflict / Invalid）
- DP `StatusMessage`（string，如"可用"、"已被其他应用占用"、"与 Block 热键相同"）
- 录键完成（StopRecording）后，**立即执行实时探测**：
  - 如果 WordHotkeyRecorder → 通知父窗口传入 OtherHotkey（Block的）做自冲突比对，
  - 如果结果非自冲突 → 调用 `IHotkeyBroker.Probe` 真实探测，
  - 更新 `ProbeStatus` 与 `StatusMessage`
- 增加"清除热键"功能（Clear 按钮 → 设置为 None）

**步骤 3.2** — Generic.xaml 重写 HotkeyRecorder 模板
- 外框 Grid：圆角 6px，带边框色映射（可用=绿/冲突=红/自冲突=橙/未知=灰）
- 内部 3 列：`[显示TextBox]  [状态Badge]  [录制Btn | 清除Btn]`
- 状态 Badge：小胶囊，显示 ✓可用 / ✗冲突 / ⚠自冲突 / ⚪未知
- 录制按钮：录制中变暗红色 + 边框脉动动画（可用 Storyboard）
- 字体统一使用 Segoe UI 或更合适的中文无衬线

### 阶段 4：设置页面 UI/UX 重设计（frontend-design 规范）

**步骤 4.1** — SettingsWindow.xaml 卡片分组重构
- 新尺寸：`Width=620 Height=720`
- **Section 1 🔑 翻译服务**：API Key 密码框 + 验证状态指示器（绿=有效/灰=未填/红=无效）+ 清除按钮
- **Section 2 ⌨️ 全局热键**：两行 Word/Block，每行带 HotkeyRecorder + 状态徽章 + "恢复默认"小组件
- **Section 3 🌐 翻译偏好**：目标语言下拉 + 翻译质量三列 Radio（Fast/Balanced/Best，替换原 ComboBox 更直观）
- **Section 4 ⚙️ 行为设置**：CheckBox 组 — 开机启动 / 点击外部关闭 Popup / 启用朗读 / Debug 日志（图标+文字）
- **Section 5 ℹ️ 系统状态**：OCR 引擎（PP-OCRv6-ONNX ✓ 或 MockOcrEngine ⚠）+ 当前热键注册状态 + App 版本
- **底部动作区**：`[恢复默认设置]` 左对齐 · `[取消]` `[保存]` 右对齐；保存成功时出现 Toast Banner（淡入 2s 后淡出）

**设计语言**：
- 主色 `#2563EB` 强调，卡片用 `#F9FAFB` 背景 + `#E5E7EB` 浅边框 1px
- Section 标题：14px Semibold，左边加 3px 彩色竖条（主题色）
- 间距：Section 之间 20px，Section 内 12px
- 保存按钮：`#2563EB` 填充 · `CornerRadius=6px` · 悬停 `#1D4ED8`
- Toast：底部 Slidedown，`#16A34A` 背景 + ✓ 图标 + "设置已保存，热键已生效"

**步骤 4.2** — SettingsWindow.xaml.cs 配套
- OnLoaded 时：新增读取 IOptions<AppSettings> 后初始化系统状态区数据（OCR引擎需要拿到 IOcrEngine.EngineName / IsAvailable）
- SaveClicked 保存后：**不立即 Close()**，先显示 Toast 2 秒，然后再关闭；或允许用户继续改
- 新增 RestoreDefaults 按钮 Click：热键恢复 `Alt+1 / Alt+2`，Target 语言 zh-CN，质量 Fast，勾选全部默认
- 关闭按钮/取消按钮：询问用户"有未保存的修改，是否放弃？"（可选，简单起见第一次版本可不做）

### 阶段 5：测试补全

- **HotkeyBrokerConflictTests.cs** +2 测试：
  1. `Probe_SameAsSelfRegistered_ReturnsTrue`：RegisterDefaults(Alt+1,Alt+2) → Probe(Alt,D1) → true
  2. `Probe_RealExternalConflict_ReturnsFalse`：FakeGlobalHotkeyService 配置第一次相同vk拒绝 → Probe 正确返回 false
- **SettingsConflictValidationTests.cs** +2 测试：
  3. `Validate_PureModifierCombo_Fails`：Key=None → 失败，信息含"具体按键"
  4. `Validate_SameWordBlock_FailsWithChineseHint`：word=block → 失败，信息含"相同"
- 构建并运行测试：`dotnet test -c Debug --filter "Hotkey|Settings|Conflict"` 全绿

---

## 四、依赖与注意事项

### 架构层边界约束
- Core/Platform 层**不得**引用 WPF 控件层（SettingsValidator 已在 App.Validation 命名空间，可以引用 Settings 窗口层但要保持输入输出都是简单类型 POCO）
- `IHotkeyBroker.Probe` 仍为 Platform 层，保持物理像素/Win32 语义不变
- 新增 DP 属性仅存在于 `HotkeyRecorder` (App.Controls)，不要泄漏到 Core/Platform

### 风险与应对
| 风险 | 应对 |
|------|------|
| 修改 Probe 返回 true 可能掩盖真实外部冲突 | 新增自冲突判断**先于**Win32 真实探测；对真冲突（不是自己的键）仍会执行真实 Register 返回 false |
| Settings 窗口改成 720px 高度 DPI 缩放后小屏幕显示不全 | 保留 `ResizeMode=CanResize` + `MinHeight=640 MinWidth=560`，长屏幕可 Scroll 化（或保持 Manual 带 Min） |
| HotkeyRecorder 实时 Probe 调用频繁，可能造成窗口句柄下热键瞬时抖动 | Probe 的 Id 用 0xBFFF，探测完立即 Unregister，窗口在 1ms 内完成解绑；用户按不出这瞬间的冲突 |
| Toast Banner 动画资源不足 | 用 XAML Storyboard DoubleAnimation Opacity 即可，无需额外库 |

---

## 五、验证清单（Checklist）

- [ ] 默认启动后直接打开设置 → 点保存（不改任何键）→ **通过不报错**（验证修复了自身冲突 Bug）
- [ ] Word 改为 Alt+1 再次保存 → 通过；Block 改为 Alt+1 保存 → 报错"与 Word 热键相同"
- [ ] 在系统里先让其他应用（如截图工具）占用 Ctrl+Shift+A → 设置里 Word 录 Ctrl+Shift+A → 录完立即 Badge 显示"外部冲突"红色
- [ ] Word 录 Ctrl+Alt 不按字母 → Badge "无效：请加具体按键"橙色
- [ ] 设置页 Word 显示"Alt+1 ✓ 可用"绿色徽章，Block 显示"Alt+2 ✓ 可用"
- [ ] 系统状态区显示 OCR 引擎：`PP-OCRv6-ONNX（已就绪）` 绿色文字
- [ ] 点保存后底部滑入绿色 Toast "设置已保存"，约 2 秒后淡出
- [ ] 点"恢复默认"按钮 → 热键、语言、质量全部回到出厂值
- [ ] `dotnet build QuickTranslate.sln -c Debug` 0 错误
- [ ] `dotnet test tests/QuickTranslate.Tests --filter "Hotkey|Settings|Conflict"` 全部通过

---

## 六、交付与回滚

- 交付形式：提交以上所有文件改动 + 新测试通过
- 回滚：如改动后设置窗口不显示，可 `git checkout` 回到 SettingsWindow.xaml / .cs 的上一个版本
- HotkeyRecorder 改动如导致 Generic.xaml 模板找不到 PART_ 部件 → 回滚 Generic.xaml 即可恢复原始样式（功能仍可用，只是无视觉）
