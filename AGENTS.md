# AGENTS.md — QuickTranslate Agent 入口

本文件只做路由与最小约束，不复制规范全文。

## 权威规范

开发前先读 [QuickTranslate_Agent_Project_Spec_v0.1.md](QuickTranslate_Agent_Project_Spec_v0.1.md)（PRD + 系统设计 + 接口契约 + Agent 执行规范）：

- 第 1 优先级：第 3 章「V1 范围与非目标」、第 16 章「验收标准」、第 17 章「禁止项」
- 第 2 优先级：第 5–12 章 架构、模块与接口约定
- 实现细节与规范冲突时：先保留接口边界并记录 ADR，不得擅自换技术栈或扩大范围

架构概览、数据流与模块职责表见 [README.md](README.md)。

## 模块边界

| 模块 | 职责 |
|------|------|
| `src/QuickTranslate.Core` | 领域抽象、几何/坐标、OCR 分段、选项模型，不依赖 WPF/平台 API |
| `src/QuickTranslate.App` | WPF 应用层：窗口、协调器、服务装配（AppHost / ServiceCollectionExtensions 为高频改动点） |
| `src/QuickTranslate.Infrastructure` | OCR 引擎、翻译 Provider、缓存、持久化 |
| `src/QuickTranslate.Platform` | Win32/User32 平台互操作 |
| `src/QuickTranslate.TextToSpeech` | 语音朗读 |
| `benchmarks/QuickTranslate.Benchmarks` | 性能基准 |
| `tests/QuickTranslate.Tests` | xUnit 测试（与源码共变提交） |

## 常用命令

```powershell
dotnet build QuickTranslate.sln                                     # 构建
dotnet run --project src/QuickTranslate.App                         # 运行（托盘常驻：Alt+1 词 / Alt+2 块 / Esc 取消）
dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj  # 全量测试（与源码共变提交）
dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj --filter "FullyQualifiedName~RealOcrRecognitionTests"  # 真实模型回归（无模型自动跳过）
dotnet publish src/QuickTranslate.App -c Release -r win-x64 --self-contained true -o ./artifacts/publish  # 发布
```

环境：Windows 10/11 x64，.NET 10 SDK；OCR 模型资产放 `assets/models/`（缺失可用下载脚本）。

## 始终需要的约束

- 改动源码时同步更新对应测试（仓库惯例：源码/测试共变提交）
- 隐私：OCR 本机执行，云端只发 OCR 后文本；API Key 走 DPAPI，绝不明文写入 JSON/日志/源码
- OCR 相关测试依赖进程级环境变量 `QUICKTRANSLATE_DISABLE_DEV_MODEL_PATHS=1` 切换模型路径，勿在并行测试中置位
- 构建失败若因 DLL 被运行中的实例锁定，先停止应用再重试
- 任务进度与验收清单见 `.trae/specs/QuickTranslate-V1/`（tasks.md / checklist.md）
