# GitHub 展示文档（README.md）编写计划

## 摘要
为该仓库创建一个面向 GitHub 浏览的展示文档 `README.md`，完整呈现 QuickTranslate 的产品定位、核心功能、架构、技术栈、隐私特性、构建/测试/运行方式与性能数据。文档使用中文（与项目及用户语言一致）。

## 现状分析（来自探索）
- 工作区干净，已全部提交到 `master`（`git status` 显示 clean），无远端配置，无 `README.md`。
- 项目为 Windows WPF 后台划词/截屏翻译工具：`Alt+1` 单词、`Alt+2` 文本块、`Esc` 取消。
- 5 层结构：`QuickTranslate.App`（WPF UI + 协调器）、`Core`（领域抽象/几何/OCR 数据结构）、`Platform`（Win32：截屏/热键/DPI/窗口样式）、`Infrastructure`（PP-OCRv6/翻译/词典/缓存/安全）、`TextToSpeech`（SAPI）。
- 翻译路由：L1 Memory LRU → L2 SQLite → ECDICT-lite 本地词典 → 在线 Provider（默认自定义 OpenAI 兼容 SSE 流式 / 可选 Qwen-MT）。
- 安全隐私：截图仅内存不落盘、不上传；API Key 用 DPAPI CurrentUser 加密存储；默认日志无正文。
- 多显示器 PerMonitorV2 DPI 感知；取消/竞态防护（OperationId + CancellationToken）；无焦点弹窗。
- 测试：约 370 个 `[Fact]/[Theory]`（67 文件），交接文档记录历史为 317+ 全绿。
- Benchmark 项目 `QuickTranslate.Benchmarks` + 报告 `docs/benchmark-results/*.md`：Idle WorkingSet ~24.8 MB、Idle CPU 0%、Hotkey→Overlay P50 ~110 ms、SqliteCache ~6.4ms 等。
- 版本 0.3.0（`Directory.Build.props`）。启动流程：单实例 → 托盘 → 热键 → 后台 Warm-up OCR 会话。

## 变更内容
仅新建一个文件：`e:\翻译\README.md`（GitHub 展示文档）。不修改任何源码/配置。

### README 结构（section → 内容来源）
1. **标题 + 简介标题**：QuickTranslate 一句话价值主张（鼠标指向屏幕文字，按快捷键即翻译，无需选中/复制/切换窗口）+ 徽章行（.NET 10、WPF、PP-OCRv6、317+ tests 等，徽章为文本或 shields.io 静态占位，避免引用不存在的虚拟远端 CI）。
2. **功能特性**：Alt+1 单词模式 / Alt+2 文本块模式 / Esc 取消；托盘常驻无主窗口；本地 OCR；多级缓存；自定义 OpenAI 兼容 + Qwen-MT 可切换；多显示器高 DPI；隐私优先；朗读（TTS）；弹窗尺寸自适应与加载动画。
3. **工作原理 / 数据流**：热键 → 截屏 → PP-OCRv6(det→cls→rec)→ Word/Block 选择 → Overlay → 翻译路由 → Popup（参照 handoff 的数据流 + spec 架构图）。
4. **技术栈**：C# / .NET 10 / WPF / Win32 P/Invoke / ONNX Runtime / PP-OCRv6 / SQLite / DPAPI / Serilog / xUnit / Windows SAPI。
5. **解决方案结构**：项目表格（源自 sln + handoff）。
6. **翻译流水线**：路由级联图 + 本地词典回退链（ECDICT packed → 用户 overlay → stub）。
7. **隐私与安全**：截图不落盘不上传；Key 安全存储；日志默认无正文（源自 spec §15 + 内存）。
8. **性能基准**：引用 `docs/benchmark-results/*.md` 关键指标表 + 说明手工/离线采集口径。
9. **开始使用**：环境要求（Windows 10/11 x64、.NET 10、模型资产）；`dotnet build`、`dotnet run --project src/QuickTranslate.App`、`dotnet test` 命令；模型下载脚本 `scripts/download-v6-final.ps1`；默认热键说明。
10. **测试**：`dotnet test`，真实模型回归测试 `--filter RealOcrRecognitionTests`，说明模型缺失时自动跳过/Mock 回退。
11. **目录/文档链接**：指向 `QuickTranslate_Agent_Project_Spec_v0.1.md`、`docs/`（handoff、benchmark、OCR 模型说明）。
12. **鸣谢/许可**：引用 `LICENSES.txt`；说明第三方组件（PP-OCRv6 模型、ONNX Runtime、ECDICT）许可归属，UI 独立开发。

### 编写约定
- 纯 Markdown，正文中文；代码/命令保持原样。
- 图片：不引入外部图片托管；README 中可引用仓库内 `assets/icons/` 本地路径图标（如 `QuickTranslate.ico`）作标题 logo；不生成虚构截图。
- 所有文件路径/项目名/数字均以实际探索为准，不编造未经验证的事实；测试数写作“约 370”或引用历史“317+”。

## 假设与决策
- 仓库无 CI/远端，故不加入不存在的 GitHub Actions/CI 徽章链接（避免坏链）。
- 不创建 docs/ 下的镜像说明，只在 README 聚合已有文档链接，避免重复维护。
- README 为可执行的产品展示文档（GitHub 首页），语气面向潜在用户/贡献者。

## 验证步骤
1. 在 `e:\翻译` 下 `dotnet build QuickTranslate.sln` 确认不破坏（本轮不改源码，理论无影响）。
2. 通读最终 README，核对每处路径（assets/scripts/docs/src 结构）与本文档中列出的真实文件一致。
3. 检查 README 中 markdown 链接全部指向仓库内存在的路径。
4. （可选）`git add README.md`；是否提交由用户决定，默认不提交。