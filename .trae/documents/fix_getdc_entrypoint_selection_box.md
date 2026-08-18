# 修复：橙色选定框与翻译弹窗完全不出现

## 摘要

按下 Alt+1 / Alt+2 后，橙色选定框、单词翻译弹窗、错误气泡**全部不出现**。

根因：屏幕截图模块 `GdiScreenCapture` 调用 `Gdi32.GetDC`，但该 P/Invoke 声明指向 `gdi32.dll`，而 Windows 实际从 **`user32.dll`** 导出 `GetDC` / `ReleaseDC`。运行时抛出 `EntryPointNotFoundException: Unable to find an entry point named 'GetDC' in DLL 'gdi32.dll'`，管线在第一步（屏幕捕获）即崩溃，后续的 OCR、选定框显示、翻译、弹窗**全部未执行**。

日志证据（`C:\Users\Administrator\AppData\Local\QuickTranslate\Logs\QuickTranslate-20260812.log`，最近会话 PID 3776，20:21:49~51）：
- `Word pipeline error` / `Block pipeline error`
- 堆栈：`Gdi32.GetDC` → `GdiScreenCapture.CaptureGdiInternal:128` → `CaptureAroundAsync:37` → 协调器管线
- 异常被 `WordInteractionCoordinator` / `BlockInteractionCoordinator` 的通用 `catch (Exception)` 接住；此时 `captureRegion`/`selectionBox` 均为 null → `errorBox.HasValue == false` → 既不显示选定框，也不弹错误气泡（仅 `HideAll` + 回 Idle）。这与用户"框和气泡都没有"的现象完全吻合。

## 当前状态分析

### 崩溃链路（已确认）
```
热键 Alt+1/Alt+2 ✓ 接收
  → 协调器 RunPipeline ✓ 进入
  → SetState(Capturing) ✓
  → GdiScreenCapture.CaptureAroundAsync
    → CaptureGdiInternal:128  Gdi32.GetDC(hDesktop)
      ✗ EntryPointNotFoundException（GetDC 不在 gdi32.dll）
  → 通用 catch：HideAll（框从未显示）+ errorBox=null → 不弹气泡
  → SetState(Idle)
```

### 涉及文件
- [Gdi32.cs](file:///e:/翻译/src/QuickTranslate.Platform/UnmanagedMethods/Gdi32.cs) — L38-42：`GetDC` / `ReleaseDC` 用 `[LibraryImport("gdi32.dll")]` 声明（错误）
- [User32.cs](file:///e:/翻译/src/QuickTranslate.Platform/UnmanagedMethods/User32.cs) — L7：`DllName = "user32.dll"`（正确目标 DLL）
- [GdiScreenCapture.cs](file:///e:/翻译/src/QuickTranslate.Platform/Win32/GdiScreenCapture.cs) — L128 `Gdi32.GetDC(...)`、L175 `Gdi32.ReleaseDC(...)`（仅此两处调用点）

### Windows API 事实
`GetDC` / `ReleaseDC` 尽管名称含 "DC"（Device Context），但由 **`user32.dll`** 导出，而非 `gdi32.dll`。`gdi32.dll` 导出的是 `CreateCompatibleDC` / `CreateCompatibleBitmap` / `SelectObject` / `BitBlt` / `DeleteDC` / `DeleteObject` / `GetDeviceCaps` 等，这些已在 `Gdi32.cs` 中正确声明。

## 拟定改动

### 改动 1：将 GetDC / ReleaseDC 从 Gdi32.cs 移至 User32.cs

**文件**：`e:\翻译\src\QuickTranslate.Platform\UnmanagedMethods\Gdi32.cs`

删除 L38-42（`GetDC` 与 `ReleaseDC` 两个声明）：
```csharp
// 删除以下两段：
[LibraryImport(DllName)]
public static partial IntPtr GetDC(IntPtr hWnd);

[LibraryImport(DllName)]
public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);
```

**文件**：`e:\翻译\src\QuickTranslate.Platform\UnmanagedMethods\User32.cs`

在 `User32` 类中（建议放在 `GetDesktopWindow` 附近，语义相关）新增：
```csharp
[LibraryImport(DllName)]
public static partial IntPtr GetDC(IntPtr hWnd);

[LibraryImport(DllName)]
public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);
```

**理由**：`user32.dll` 才是 `GetDC`/`ReleaseDC` 的真实导出方。遵循现有代码组织（按 DLL 分组），移到 `User32` 类最自然，且 `DllName` 常量已指向 `user32.dll`。

### 改动 2：更新 GdiScreenCapture.cs 调用点

**文件**：`e:\翻译\src\QuickTranslate.Platform\Win32\GdiScreenCapture.cs`

- L128：`hdcScreen = Gdi32.GetDC(hDesktop);` → `hdcScreen = User32.GetDC(hDesktop);`
- L175：`Gdi32.ReleaseDC(hDesktop, hdcScreen);` → `User32.ReleaseDC(hDesktop, hdcScreen);`

**理由**：跟随声明位置变更，引用改为 `User32`。`User32` 已在文件头 `using QuickTranslate.Platform.UnmanagedMethods;` 命名空间内，无需新增 using。

## 假设与决策

1. **仅修 GetDC/ReleaseDC 的 DLL 归属**，不动其他 Gdi32 声明（`CreateCompatibleDC` / `BitBlt` 等确实在 gdi32.dll，已验证正确）。
2. **不修 SHA256 不匹配 / MockOcrEngine 回退**：日志显示模型 SHA256 校验失败回退到 MockOcrEngine，但这不是"框/气泡不出现"的原因。修复 GetDC 后，即使走 MockOcrEngine，管线也能跑到选定框/弹窗阶段（MockOcrEngine 会返回模拟文本，触发选定框显示或 NoTextFound 错误气泡）。真实 OCR 的 SHA256 校验问题留待后续单独处理。
3. **不修改协调器与 overlay 服务的逻辑**：选定框显示/隐藏机制本身经分析是正确的（成功路径显示框、NoTextFound 显示错误气泡），只是被 GetDC 崩溃截断而从未触达。

## 验证步骤

1. **编译**：`dotnet build e:\翻译\src\QuickTranslate.App\QuickTranslate.App.csproj -c Debug`，确认 0 错误。
2. **运行测试**：`dotnet test e:\翻译\src\QuickTranslate.Tests\QuickTranslate.Tests.csproj`，确认无回归。
3. **手动验证**：
   - 启动应用 `e:\翻译\src\QuickTranslate.App\bin\Debug\net10.0-windows\QuickTranslate.App.exe`
   - 将鼠标悬停在屏幕上的英文单词上，按 Alt+1
   - 预期：橙色选定框出现 → 弹出翻译结果或"未检测到可翻译的单词"错误气泡
   - 将鼠标悬停在英文段落上，按 Alt+2
   - 预期：橙色区域选定框出现 → 弹出段落翻译或错误气泡
4. **日志核查**：查看 `C:\Users\Administrator\AppData\Local\QuickTranslate\Logs\` 下当日日志，确认不再出现 `GetDC` / `EntryPointNotFoundException`，且出现 `State -> "OverlayVisible"` / `State -> "Displaying"` 等状态流转。
