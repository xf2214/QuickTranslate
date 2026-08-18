# 修复：快捷键按了没反应 / 保存成功但热键不生效

> 计划生成时间：2026-08-12
> 适用项目：`e:\翻译`（QuickTranslate v0.1）

---

## 一、问题根因分析（代码调研结论）

### 统一根因：WordInteractionCoordinator 在生产环境从未被实例化

**用户确认的症状**：问题 2「按下快捷键没有反应，也不弹出识别框」+ 问题 1「保存成功但热键仍不生效」。

**代码证据链**：
1. `WordInteractionCoordinator` 在构造函数里订阅 `_hotkeyBroker.HotkeyFired`（[WordInteractionCoordinator.cs:74](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L74)），只有 `Block` 类型留空、`Word` 类型调用 `RunWordPipeline()`（[L93-L96](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L93-L96)）。**因此该对象一旦被构造，Alt+1 就会触发完整识别→翻译→气泡链路。**
2. 但 DI 只对它做了**注册**，没有任何生产代码**解析**它：
   - [AppHost.cs:31-32](file:///e:/翻译/src/QuickTranslate.App/Bootstrap/AppHost.cs#L31-L32) 仅 `AddSingleton<WordInteractionCoordinator>` 和 `AddSingleton<IInteractionCoordinator>(sp => sp.GetRequiredService<WordInteractionCoordinator>())`。
   - [App.xaml.cs:211](file:///e:/翻译/src/QuickTranslate.App/App.xaml.cs#L211) `GetService<WordInteractionCoordinator>()` 只在 `TryCleanupAll`（退出清理）里出现，不是启动路径。
   - `WinFormsTrayIconService` 只解析 `IAppLifecycle / ILogger / IServiceProvider`，从不解析 `WordInteractionCoordinator` 或 `IInteractionCoordinator`。
3. 对比 `BlockInteractionCoordinator`：它在 [AppHost.cs WireBlockHotkey](file:///e:/翻译/src/QuickTranslate.App/Bootstrap/AppHost.cs#L65-L82) 里被**显式解析**并订阅 `broker.HotkeyFired`，所以 Alt+2（Block）能工作。

**结论**：`Block` 热键被正确接线，而 `Word` 热键（Alt+1，主功能）对应的协调器从未被构造 → `HotkeyFired(Word)` 没有订阅者 → 按 Alt+1 完全没反应。同理，用户改完热键点保存后，`SettingsWindow.OnSaveClicked` 里 `RegisterDefaultsFromSettings` 虽然重新注册了新键到 `WordId=0xB001`，但 Word 协调器不接线，新热键依然无效 →「保存成功但热键不生效」。

**为什么之前几次修复没解决**：之前修复的是 `Probe` 自冲突误判、`MockOcrEngine` 坐标系、错误气泡、Dispatcher 跨线程等底层问题，但「Word 协调器从未被实例化」这个最高层的接线问题一直存在，导致即便底层全对，Alt+1 也进不了 `RunWordPipeline`。

### 次要问题：保存后翻译偏好不即时生效

`SettingsManager.SaveAsync` 每次写入 `settings.json` 并把 `_loadedSettings` 指向一个**新实例**（[SettingsManager.cs:50-60](file:///e:/翻译/src/QuickTranslate.Infrastructure/Persistence/SettingsManager.cs#L50-L60)）；而协调器持有的是启动时 `IOptions<AppSettings>` 的**快照**（[WordInteractionCoordinator.cs:37](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L37)），两者不是同一实例。因此目标语言/质量等改动需重启才生效。用户当前聚焦的是热键，此项作为次要加固一并处理。

---

## 二、修改文件 & 模块清单

| # | 文件 | 改动 | 解决 |
|---|------|------|------|
| 1 | `src/QuickTranslate.App/Bootstrap/AppHost.cs` | 新增 `WireWordHotkey`：解析 `WordInteractionCoordinator`（经 `IInteractionCoordinator`）强制构造，使其构造函数订阅 `HotkeyFired(Word)` | 问题1+2 根因 |
| 2 | `src/QuickTranslate.App/Bootstrap/AppHost.cs` | 在 `Build()` 末尾调用 `WireWordHotkey(host.Services)`；统一 Word/Block 的接线风格 | 同上 |
| 3 | `src/QuickTranslate.Infrastructure/Persistence/SettingsManager.cs` | `SaveAsync` 改为**就地更新**当前活跃的 `AppSettings` 单例（字段复制），保证 `IOptions<AppSettings>.Value` 与磁盘一致 | 次要问题 |
| 4 | `src/QuickTranslate.Infrastructure/ServiceCollectionExtensions.cs` | 将 `AppSettings` 注册为**共享可变单例**，`IConfigureOptions` 与 `SettingsManager` 复用同一实例 | 次要问题 |
| 5 | `tests/QuickTranslate.Tests/Coordination/WordCoordinatorWiringTests.cs` | 新增测试：构造 `WordInteractionCoordinator` + 假 `IHotkeyBroker`，触发 `HotkeyFired(Word)` 后断言 `RunWordPipeline` 被调用 / 状态进入 Capturing | 回归验证 |

---

## 三、详细实施步骤

### 步骤 1：接线 Word 协调器（根因修复，最重要）

**改 [AppHost.cs](file:///e:/翻译/src/QuickTranslate.App/Bootstrap/AppHost.cs)**

当前 `Build()` 末尾：
```csharp
RunModelVersionVerification(host.Services);
WireBlockHotkey(host.Services);
return host;
```

`WireBlockHotkey` 里 `blockCoord.Start()` 用的是 `BlockInteractionCoordinator`。Word 协调器在构造函数里**已自行订阅** `_hotkeyBroker.HotkeyFired`，所以只需**强制构造一次**即可：

新增方法：
```csharp
private static void WireWordHotkey(IServiceProvider services)
{
    // 强制构造 WordInteractionCoordinator：其构造函数订阅 broker.HotkeyFired(Word)
    // 若不解析，Alt+1 热键将永远没有订阅者（历史根因：只注册未实例化）。
    var wordCoord = services.GetRequiredService<WordInteractionCoordinator>();
    var logger = services.GetRequiredService<ILogger<WordInteractionCoordinator>>();
    wordCoord.Start(); // 与 Block 协调器对称，便于停机/暂停管理
    logger.LogDebug("WordInteractionCoordinator wired: Alt+1 (Word) hotkey active");
}
```

在 `Build()` 里调用：
```csharp
RunModelVersionVerification(host.Services);
WireBlockHotkey(host.Services);
WireWordHotkey(host.Services);
return host;
```

**说明**：`WordInteractionCoordinator` 的依赖（`IAppLifecycle/IHotkeyBroker/ICursorService/IMonitorService/IScreenCapture/IOcrEngine/IWordSelector/ISelectionOverlayService/ITranslationRouter/IWordPopupService/IOptions<AppSettings>/ILogger`）全部已在 DI 注册（`AddPlatform`/`AddInfrastructure`/`AddSelectionCore`/`AddCacheAndRouter`/AppHost），解析不会失败。`IInteractionCoordinator` 抽象保留（供未来入口），本步骤直接解析具体类，最小改动。

### 步骤 2：保存后翻译偏好即时生效（次要加固）

**改 [SettingsManager.cs](file:///e:/翻译/src/QuickTranslate.Infrastructure/Persistence/SettingsManager.cs)**

确保 `SaveAsync` 更新的是**与 `IOptions<AppSettings>` 同一个实例**，而不是新建一个。做法：`AppSettings` 注册为共享单例，`SettingsManager` 持有并就地更新它。

`SaveAsync` 改为复制字段到活跃单例：
```csharp
public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
{
    var dir = Path.GetDirectoryName(_settingsFilePath);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    var json = JsonSerializer.Serialize(settings, SerializerOptions);
    json = SanitizeSettingsJson(json);
    await File.WriteAllTextAsync(_settingsFilePath, json, ct).ConfigureAwait(false);

    // 就地更新活跃单例，保证 IOptions<AppSettings>.Value 与磁盘一致、无需重启
    if (_loadedSettings != null)
    {
        _loadedSettings.WordHotkey = settings.WordHotkey;
        _loadedSettings.BlockHotkey = settings.BlockHotkey;
        _loadedSettings.TargetLanguage = settings.TargetLanguage;
        _loadedSettings.TranslationQuality = settings.TranslationQuality;
        _loadedSettings.StartWithWindows = settings.StartWithWindows;
        _loadedSettings.CloseOnOutsideClick = settings.CloseOnOutsideClick;
        _loadedSettings.DebugLogging = settings.DebugLogging;
        _loadedSettings.EnableTextToSpeech = settings.EnableTextToSpeech;
        _loadedSettings.ResolvedApiKey = settings.ResolvedApiKey;
    }
    else
    {
        _loadedSettings = settings;
    }
}
```

**改 [ServiceCollectionExtensions.cs](file:///e:/翻译/src/QuickTranslate.Infrastructure/ServiceCollectionExtensions.cs)**
- 注册共享可变单例 `AppSettings`，并让 `ISettingsManager` 的 `LoadAsync` 返回该实例（`LoadAsync` 已把结果赋给 `_loadedSettings`，二者指向同一对象）。
- `IConfigureOptions<AppSettings>` 改为从该单例拷贝一次（启动时合理），或直接复用——优先保证 `IOptions<AppSettings>.Value` 就是 `SettingsManager._loadedSettings` 同一引用。

> 注意：此步骤涉及 DI 单例注册顺序（`ISettingsManager` → `AppSettings` → `IConfigureOptions`），需保证不产生循环依赖。若改动风险偏高，可退化为「仅修步骤 1」，因为用户明确的主要痛点是热键不生效，步骤 2 是让翻译偏好无需重启的增强。

### 步骤 3：回归测试

**新增 `tests/QuickTranslate.Tests/Coordination/WordCoordinatorWiringTests.cs`**
- 用现有 `TestFakes` 构造 `WordInteractionCoordinator`（见 [TestFakes.cs:322 CreateCoordinator](file:///e:/翻译/tests/QuickTranslate.Tests/Coordination/TestFakes.cs#L322)）。
- 触发 `broker.HotkeyFired(Word)`，断言协调器进入 `Capturing` 状态（说明 Word 热键链路激活）。
- 触发 `broker.HotkeyFired(Block)`，断言 Word 协调器**不**重复触发 Block 流程（保持现有语义）。

---

## 四、假设 & 决策

1. **Alt+2（Block）当前可用**：由 `WireBlockHotkey` 显式接线，用户只反馈 Alt+1（Word）没反应，符合本根因。
2. **Word 协调器自订阅**：其构造函数已 `_hotkeyBroker.HotkeyFired += OnHotkeyFired`，因此「强制构造」即完成接线，无需在 AppHost 再加订阅（与 Block 的显式订阅手法不同，但语义等价且更内聚）。
3. **步骤 2 定位为次要**：用户确认症状是「保存成功但热键不生效」，步骤 1 直接解决；步骤 2 让翻译偏好也即时生效，若 DI 改造风险高则以步骤 1 为交付底线。
4. **不新增非必须文件**：仅新增一个回归测试文件；其余均为既有文件就地修改。

---

## 五、验证清单（Checklist）

- [ ] `dotnet build QuickTranslate.sln` 0 错误
- [ ] `dotnet test --no-build` 全绿（含新增 `WordCoordinatorWiringTests`）
- [ ] 启动 exe → 托盘 → 日志出现 `WordInteractionCoordinator wired: Alt+1 active`
- [ ] 鼠标放到英文单词上按 **Alt+1** → 弹出单词翻译气泡（不再「没反应」）
- [ ] 设置页把 Word 热键改为 `Ctrl+Alt+W` → 保存 → 按 `Ctrl+Alt+W` 立即生效（不再「保存成功但热键不生效」）
- [ ] 设置页改目标语言为 `en` → 保存 → 不重启，按热键翻译结果语言即时变化（步骤 2 生效）
- [ ] 按 **Alt+2**（Block）仍正常（回归不受影响）

---

## 六、交付与回滚

- 交付：完成步骤 1 + 步骤 2 + 步骤 3，`dotnet build`/`dotnet test` 通过。
- 若步骤 2 的 DI 单例改造引入循环依赖或测试回归，**回滚步骤 2**（`git checkout` 相关文件），保留步骤 1 作为本次交付核心。
- 若 `WireWordHotkey` 导致启动异常，检查是否 `WordInteractionCoordinator` 依赖解析失败并按 DI 注册补齐。