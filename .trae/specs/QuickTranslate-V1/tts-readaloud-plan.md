# 朗读（TTS）功能 - 实现计划

## 目标
- 新增"朗读"功能，作为设置项可选开启（默认关闭）。
- 朗读引擎：Windows 内置 SAPI（`System.Speech.Synthesis`），离线、零新增依赖、隐私友好。
- 朗读内容：以**译文为主**（Word 弹窗读释义，Block 弹窗读整段译文）。
- 单独设立独立模块：新建 `src/QuickTranslate.TextToSpeech/` 项目，边界清晰、可独立测试。

## 架构位置
> 现有 5 层架构（Core / Platform / Infrastructure / App / Tests）。TTS 作为专属横切能力独立成项目，只被 App 层引用，不侵入 Core 与 Infrastructure 的翻译链路。

```
src/QuickTranslate.TextToSpeech/          # 新独立项目
  ├─ ITextToSpeechService.cs              # 抽象：Speak / Stop / IsSupported
  ├─ WindowsSapiTextToSpeechService.cs    # SAPI 实现（System.Speech）
  └─ TextToSpeechServiceCollectionExtensions.cs  # DI 注册扩展
```

## 改动清单

### 1. 新建独立项目 `src/QuickTranslate.TextToSpeech/`
- csproj：`net10.0-windows`（继承 Directory.Build.props），引用 `System.Speech` 包。
- `ITextToSpeechService`：
  - `bool IsSupported { get; }`（SAPI 可用性，含是否至少有可选语音）
  - `void Speak(string text, string? language = null)`（异步朗读，自动打断上一次）
  - `void Stop()`
- `WindowsSapiTextToSpeechService`：
  - 内部持有 `SpeechSynthesizer`，用锁保证线程安全，`SpeakAsync` 不阻塞 UI。
  - 根据 `language`（如 `zh-CN`/`en`/`ja`…）匹配 `InstalledVoices` 的 `CultureInfo` 选择语音；无匹配则回退默认语音。
  - `IsSupported`：`InstalledVoices.Count > 0`。
  - `IDisposable` 释放 `SpeechSynthesizer`。

### 2. `AppSettings` 增加开关
- `Core/Options/AppSettings.cs` 新增 `public bool EnableTextToSpeech { get; set; } = false;`

### 3. Infrastructure 设置映射
- `Infrastructure/ServiceCollectionExtensions.cs` 的 `IConfigureOptions<AppSettings>` 中补 `opts.EnableTextToSpeech = appSettings.EnableTextToSpeech;`

### 4. AppHost 注册 TTS 服务
- `App/Bootstrap/AppHost.cs`：`services.AddSingleton<ITextToSpeechService, WindowsSapiTextToSpeechService>();`

### 5. 弹窗接入 TTS（Word + Block）
- `WpfWordPopupService` / `WpfBlockPopupService` 注入 `IOptions<AppSettings>` 与 `ITextToSpeechService`，创建弹窗时通过方法传入 `(tts, enabled)`。
- `WordPopupWindow` / `BlockPopupWindow`：持有 TTS 引用与开关；`enabled && tts != null` 时显示"朗读"按钮，点击后朗读译文（Word 用 `_lastTranslation.TargetText`，Block 用 `FullText`）。

### 6. Settings 界面新增"启用朗读"勾选
- `SettingsWindow.xaml`：复选行新增 `EnableReadAloudBox` "启用朗读"。
- `SettingsWindow.xaml.cs`：`OnLoaded` 回填，`OnSaveClicked` 写入 `newSettings.EnableTextToSpeech`。

### 7. 测试
- `SettingsDefaultsTests`：断言 `EnableTextToSpeech` 默认 `false`。
- 新增 `TextToSpeechContractTests`：断言 `ITextToSpeechService` 默认实现可解析、`IsSupported` 在无语音环境返回 false 且 `Speak/Stop` 调用不抛异常（SAPI 不可用时安全降级）。
- 设置 Round-trip：`EnableTextToSpeech=true` 保存后重新加载仍为 true。

## 验收标准
- 设置页出现"启用朗读"开关，保存后 `settings.json` 出现 `EnableTextToSpeech: true/false`。
- 开启后，Word/Block 弹窗出现"朗读"按钮；点击可听到译文朗读；再次点击会打断上一次朗读。
- 关闭后按钮隐藏。
- `dotnet build QuickTranslate.sln -c Debug` 零错误；相关单测全部通过。

## 不做（Out of Scope）
- 不做在线 TTS、音色/语速/音量配置、自动朗读、朗读历史。
- 不改变翻译链路；TTS 仅作为弹窗交互能力。