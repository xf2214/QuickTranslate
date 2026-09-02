# 高分辨率 / 高 DPI 选区自适应方案

## Summary

修复"1920x1200 正常、3000x2000 选区异常"的问题。根因不在截图与 OCR（两者全程物理像素、DPI 链路正确），而在 **WPF 显示层的窗口定位方式**与 **96-DPI 基准的几何常量**：

1. **定位根因**：所有 WPF 窗口（选区框、弹窗、状态指示器）通过 `Window.Left/Top`（DIP 属性）+ `EnsureHandle→Show` 时序定位。WPF 用"窗口创建时刻认知的 DPI"（系统 DPI，即进程启动时主屏 DPI）把 DIP 换算为物理像素。当目标屏 DPI ≠ 该认知 DPI（高缩放单屏的启动后改分辨率、混合 DPI 双屏、启动后接拔显示器）时，窗口位置/尺寸按错误比例缩放，且错位后窗口仍落在同一屏时**不会触发 WM_DPICHANGED**，`OnDpiChanged` 兜底失效 → 选区框出现在错误位置或大小错误。
2. **常量根因**：`EstimatedLineHeight=20`、`WordCaptureSize`、块选首捕 `1200x720` 等按 96-DPI 物理像素标定的常量，在 144/192 DPI 屏上对应的"逻辑范围"缩小 1/3~1/2 → 首捕范围与 OCR 焦点带偏小 → 触发截边重抓（延迟翻倍）、选词选块不准。
3. **无启动检测与拓扑自适应**：启动时不感知显示器拓扑；运行中改缩放/接拔屏后只能靠下次 Show 的 DPI 比对被动重建，且无诊断日志。

方案：**窗口定位一律改为 HWND 级 `SetWindowPos` 物理像素**（唯一在单屏任意缩放 / 混合 DPI / 运行中变更下都确定正确的方式）；**启动时自动检测显示器拓扑并预热窗口**（用户显式需求）；**96-DPI 基准常量按监视器 DPI 缩放**；**监听 WM_DISPLAYCHANGE 运行时自适应**。

## Current State Analysis（基于代码探索）

数据流（已验证正确的部分，不动）：
- [CursorService.cs](file:///e:/翻译/src/QuickTranslate.Platform/Win32/CursorService.cs)：`GetCursorPos` 物理像素 ✓
- [GdiScreenCapture.cs](file:///e:/翻译/src/QuickTranslate.Platform/Win32/GdiScreenCapture.cs)：GDI `BitBlt` 物理坐标截图 ✓
- [MonitorService.cs](file:///e:/翻译/src/QuickTranslate.Platform/Win32/MonitorService.cs)：`GetMonitorInfoW` 物理边界 + `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` ✓
- 协调器每次热键触发时**实时**查询光标所在屏 DPI（`TryGetMonitorFromPoint`），并非只在启动时 →"启动时检查"真正缺的是显示层的确定性定位与预热，不是 DPI 数据源。

出问题的部分：
- [SelectionOverlayWindow.xaml.cs#L183-L236](file:///e:/翻译/src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml.cs#L183-L236)：`ApplyLayout` 设 `this.Left/Top`（DIP）为权威布局；`ApplyPhysicalLayout` = `ToDip` 后走 DIP 属性。
- [WpfSelectionOverlayService.cs#L59-L109](file:///e:/翻译/src/QuickTranslate.App/Services/WpfSelectionOverlayService.cs#L59-L109)：`ShowCore` 顺序为 `new → ApplyPhysicalLayout → EnsureHandle → Show`，DIP 定位依赖 WPF 创建时 DPI 认知。
- [WpfWordPopupService.cs#L113-L148](file:///e:/翻译/src/QuickTranslate.App/Services/WpfWordPopupService.cs#L113-L148)、[WpfStatusIndicatorService.cs#L33-L77](file:///e:/翻译/src/QuickTranslate.App/Services/WpfStatusIndicatorService.cs#L33-L77)、WpfBlockPopupService：同一 DIP 定位模式。
- [WordInteractionCoordinator.cs#L53-L65](file:///e:/翻译/src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs#L53-L65)：`EstimatedLineHeight=20`、`EdgeClipMargin=10` 为 96-DPI 物理常量。
- [BlockRetryCoordinator.cs#L37](file:///e:/翻译/src/QuickTranslate.Infrastructure/Coordination/BlockRetryCoordinator.cs#L37)：首捕 `PhysicalSize(1200, 720)` 同为 96-DPI 物理常量（该文件执行时需通读确认全部常量点）。
- 进程 DPI 感知：.NET 10 WPF 默认 PerMonitorV2（csproj 无 manifest 覆盖，保持现状，不新增 app.manifest）。
- 现有测试以 `Inspect().lastDipRect` 断言 DIP 布局（[SelectionOverlayTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/SelectionOverlayTests.cs)、[PopupDipLayoutTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/PopupDipLayoutTests.cs)、[OverlayDpiRecreatePolicyTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/OverlayDpiRecreatePolicyTests.cs)）→ **保留 `LastLayoutRect` 的 DIP 语义不变**，物理定位为叠加层，测试兼容。

## Proposed Changes

### P0-1 新增物理定位 helper（新文件）

`src/QuickTranslate.App/Windows/WindowPhysicalPlacement.cs`：

```csharp
public static class WindowPhysicalPlacement
{
    // 物理 padding 与现有 ApplyLayout 的 DIP padding（Left-2, Top-2, W+4, H+4）对齐
    public static void SetPhysicalBounds(
        System.Windows.Window window, PhysicalRect physicalBounds,
        uint dpiX, uint dpiY, int padPx = 2)
    {
        // 1) EnsureHandle（无 HWND 时创建；EnsureHandle 不显示窗口）
        // 2) User32.SetWindowPos(hwnd, HWND_TOPMOST,
        //      physicalBounds.X - padPx, physicalBounds.Y - padPx,
        //      physicalBounds.Width + 2*padPx, physicalBounds.Height + 2*padPx,
        //      SWP_NOACTIVATE)  ← 物理像素一步到位，绕开 WPF DIP 换算时序
        // 3) 同步 WPF DIP 属性（ToDip 结果，供内部元素布局与 Inspect 测试）
        //    设置 Width/Height 前临时挂 IgnoreDpiChanged 标志或直接赋值——
        //    简单起见：DIP 属性直接赋值，WPF 随后的 WM_DPICHANGED 由 OnDpiChanged 收敛
    }
}
```

依赖已有 P/Invoke：`User32.SetWindowPos`（[WpfSelectionOverlayService.cs#L126](file:///e:/翻译/src/QuickTranslate.App/Services/WpfSelectionOverlayService.cs#L126) 已在用）、`PerMonitorDpiHelpers.ToDip`。

### P0-2 SelectionOverlayWindow 接入物理定位

`src/QuickTranslate.App/Windows/SelectionOverlayWindow.xaml.cs`：

- `ApplyPhysicalLayout(physicalBox, dpiX, dpiY)`：
  - 保留 `LastPhysicalBox/LastDpiX/LastDpiY` 与 `LastLayoutRect = ToDip(...)`（测试语义不变）
  - **新增**：调用 `WindowPhysicalPlacement.SetPhysicalBounds(this, physicalBox, dpiX, dpiY, padPx: 2)`；若 HWND 为 Zero（尚未 EnsureHandle，纯测试场景）则退化为现有 DIP 路径
  - 内部元素（SelectionBorder 尺寸、扫描动画）仍按 DIP 布局，窗口 DPI 与目标屏一致后渲染物理尺寸自然正确
- `OnDpiChanged`：现有"以 `LastPhysicalBox` 重算 DIP 属性"逻辑保留（窗口 DPI 已变更后 DIP 换算自洽）；追加 `SetWindowPos` 物理重定位，防止 WPF 对 WM_DPICHANGED 的默认自动缩放残留错误几何。

### P0-3 WpfSelectionOverlayService 顺序调整 + 失效监视器清理

`src/QuickTranslate.App/Services/WpfSelectionOverlayService.cs`：

- `ShowCore`：改为 `new → EnsureHandle → ApplyPhysicalLayout（内部 SetWindowPos）→ Show`（把现有 `EnsureHandle` 从 `if (!window.IsVisible)` 块提前；`Show()` 保持其后）
- `UpdateCore`：无需改（`ApplyPhysicalLayout` 已物理化）
- 新增 `public void PruneExcept(IReadOnlyCollection<MonitorId> activeIds)`：关闭并移除不在当前拓扑中的缓存窗口（运行中拔屏/换屏后防隐藏窗口泄漏与陈旧 DPI 窗口）。**不改 `ISelectionOverlayService` 接口**（Core 抽象零改动，仅 App 内部 watcher 直接引用具体类）。

### P0-4 启动时显示器检测 + 预热 + 拓扑监听（新文件，用户显式需求）

`src/QuickTranslate.App/Bootstrap/StartupDisplayProbe.cs`：

```
public static class StartupDisplayProbe
{
    public static void Run(IServiceProvider services)   // UI 线程调用（AppHost.Build 在 OnStartup 中）
    // 1) EnumerateMonitors → 日志（LoggerFactory "StartupDisplayProbe"）：
    //    每屏 deviceName / Bounds / WorkArea / DpiX,DpiY / IsPrimary；GetDpiForMonitor 失败回退 96 时告警
    // 2) 预热 overlay：为每块屏 new SelectionOverlayWindow + EnsureHandle +
    //    SetWindowPos(该屏 Bounds 左上角, 1x1, SWP_NOACTIVATE)（不可见，仅为让 WPF 窗口 DPI
    //    与该屏同步、首次热键零创建延迟零错位）→ 挂入 WpfSelectionOverlayService（新增内部
    //    方法 WarmupForMonitors(monitorInfos)，与 ShowCore 的缓存/DPI 比对逻辑复用）
    // 3) 拓扑监听：隐藏 HwndSource（HandleRef 消息窗口）AddHook 捕获 WM_DISPLAYCHANGE(0x007E)：
    //    重新枚举 → 日志 → WpfSelectionOverlayService.PruneExcept(新拓扑 ids)
    //    （同一屏仅改缩放时 MonitorId 不变、DPI 变，由既有 AreClose 比对在下次 Show 时重建，无需额外处理）
}
```

`src/QuickTranslate.App/Bootstrap/AppHost.cs`：在 `Build()` 的 `WireWordHotkey(...)` 之前插入 `StartupDisplayProbe.Run(host.Services);`。

### P0-5 状态指示器 / 弹窗统一物理定位

- `src/QuickTranslate.App/Services/WpfStatusIndicatorService.cs` `Show`：`window.Left/Top = dip` 改为 `WindowPhysicalPlacement.SetPhysicalBounds(window, 物理rect(已有 phyX/phyY/estW/estH), dpiX, dpiY, padPx: 0)`；EnsureHandle 提前到定位前。
- `src/QuickTranslate.App/Services/WpfWordPopupService.cs` `ShowCore`：`Place` 已产出 `physicalRect` → `SetPhysicalBounds(window, physicalRect, dpiX, dpiY, padPx: 0)` 替代 `window.Left/Top/Width/Height` 赋值；`window.Height` 的测量校正逻辑保留（DIP 属性照常维护，`SetLastLayoutDipRect` 语义不变）。EnsureHandle 提前。
- `src/QuickTranslate.App/Services/WpfBlockPopupService.cs`：同模式（执行时先通读该文件，确认与 WordPopup 同构后套用；`PopupPlacement.Place` 输出物理 rect → SetPhysicalBounds）。

### P0-6 96-DPI 基准常量按 DPI 缩放（新 helper + 两处接入）

新文件 `src/QuickTranslate.Core/Geometry/DpiScale.cs`：

```csharp
public static class DpiScale
{
    // 96-DPI 基准物理像素 → 当前 DPI 物理像素（dpi=96 时恒等，行为零变化）
    public static int Px(int px96, uint dpi) => (int)Math.Round(px96 * (dpi == 0 ? 96.0 : dpi) / 96.0, MidpointRounding.AwayFromZero);
    public static double Scale(uint dpi) => (dpi == 0 ? 96.0 : dpi) / 96.0;
}
```

- `src/QuickTranslate.App/Coordination/WordInteractionCoordinator.cs`：
  - 管线入口处计算 `int estLineHeight = DpiScale.Px(EstimatedLineHeight, dpiY)`，替换 `RunWordPipeline` 中所有 `EstimatedLineHeight` 直接使用点（`WordCaptureSize` 首捕、`WordFocusBand`、`GetLineHeightAtAnchor` 兜底值）
  - `EdgeClipMargin` 的使用点（`TouchesHorizontalEdge` 等）改为 `DpiScale.Px(EdgeClipMargin, dpiX)`——**实现方式**：把 4 个 `Touches*` 静态方法增加 `int margin` 参数（或 `uint dpi` 参数），调用点传缩放值；保持方法纯函数可测
  - 常量注释标注"96-DPI 基准"
- `src/QuickTranslate.Infrastructure/Coordination/BlockRetryCoordinator.cs`：`PhysicalSize(1200, 720)` → `PhysicalSize(DpiScale.Px(1200, dpiX), DpiScale.Px(720, dpiY))`；`MakeBand(_anchorPoint, 600)` 的 600 同理（执行时通读该文件，把所有 96-DPI 物理常量统一处理；比率类常量如 1.4x 扩大系数不动）

### P0-7 测试（源码/测试共变，仓库惯例）

新建：
- `tests/QuickTranslate.Tests/App/WindowPhysicalPlacementTests.cs`（STA 线程模式，照 [SelectionOverlayTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/SelectionOverlayTests.cs) 的 Thread+STA 风格）：
  - 96/144/192 下 `SetPhysicalBounds` 后 `User32.GetWindowRect` 断言物理边界 == 预期（±1px 取整容差）
  - `LastLayoutRect` DIP 断言保持 == `ToDip` 结果（兼容现有语义）
  - 需补 `GetWindowRect` P/Invoke（若 User32 未声明则在该测试内声明或加到 UnmanagedMethods）
- `tests/QuickTranslate.Tests/Core/DpiScaleTests.cs`：`Px(20,96)=20`、`Px(20,144)=30`、`Px(20,192)=40`、dpi=0 回退 96、负/零输入防御

更新（预期少量适配）：
- [SelectionOverlayTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/SelectionOverlayTests.cs)、[OverlayDpiRecreatePolicyTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/OverlayDpiRecreatePolicyTests.cs)、[PopupDipLayoutTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/PopupDipLayoutTests.cs)、[PopupSizeFitTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/PopupSizeFitTests.cs)、[WordPopupPlacementTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/WordPopupPlacementTests.cs)、[BlockPopupPlacementTests.cs](file:///e:/翻译/tests/QuickTranslate.Tests/App/BlockPopupPlacementTests.cs) —— DIP 断言语义保留应全绿；若 popup 测试断言 `window.Left` 则改为断言 `GetWindowRect` 或 `SetLastLayoutDipRect` 值
- `WarmupForMonitors` / `PruneExcept` 补测试：预热后 `_windows` 每屏一项且 DPI 与屏一致；Prune 后失效项移除（照 OverlayDpiRecreatePolicyTests 反射访问 `_windows` 的既有风格）

## Assumptions & Decisions

1. **进程 DPI 感知保持 .NET 默认 PMv2**，不新增 app.manifest（现状已正确，显式声明无增益）。
2. **用户跳过范围确认 → 按全场景全量修复**：选区框 + 弹窗 + 指示器统一物理定位；验证矩阵覆盖单屏高缩放、混合 DPI、运行中变更。
3. **`LastLayoutRect`/`lastDipRect` 的 DIP 语义保留**（现有测试基线不动摇）；物理定位为叠加权威层。
4. **Core 接口零改动**（`ISelectionOverlayService` 不加方法）：`StartupDisplayProbe`/Watcher 为 App 层细节，直接引用 `WpfSelectionOverlayService` 具体类；符合 AGENTS.md"保留接口边界"约束，无需 ADR。
5. **96 DPI 行为零变化**：`DpiScale` 在 dpi=96 时恒等，1920x1200@100% 的现有正常行为作为回归基线。
6. 执行顺序内先通读 `WpfBlockPopupService.cs` 与 `BlockRetryCoordinator.cs` 全文后再动手（本次计划基于同模式推断，未逐行读这两份）。
7. `WM_DPICHANGED` 与 WPF 默认自动缩放的短暂交互闪烁可接受（终态由 `OnDpiChanged`/`SetPhysicalBounds` 收敛），不引入同步等待消息泵的复杂度。

## Verification

1. `dotnet build QuickTranslate.sln`（若 DLL 被运行实例锁定，先停应用再重试）
2. `dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj`（全量；含新 STA 物理定位测试）
3. `dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj --filter "FullyQualifiedName~RealOcrRecognitionTests"`（无模型自动跳过）
4. 手动矩阵（`dotnet run --project src/QuickTranslate.App`，开启 `DebugOverlayMode` 实线框对照截图范围）：
   - 1920x1200@100% 回归：Alt+1/Alt+2 行为与改动前一致
   - 单屏 3000x2000@150%/200%：虚线预览框与实际截图范围逐像素重合；扫描框贴合选词/选块；弹窗锚定选区下方
   - 混合 DPI 双屏（高 DPI + 100% 外接）：光标分别在两屏触发，框位置精确无比例偏移
   - 运行中改缩放 100%↔150% 后立即热键：位置正确，日志出现 `StartupDisplayProbe` 拓扑刷新记录
5. 日志检查：启动时输出 `StartupDisplayProbe` 每屏 Bounds/DPI 记录（用户侧可自行回传诊断）
