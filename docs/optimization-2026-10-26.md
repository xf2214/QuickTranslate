# QuickTranslate 调研与优化记录（2026-10-26）

本轮基于 `4859c4527bda0d582b47a3d0503ded40feb7c9e1`，即已有 [PR #1](https://github.com/xf2214/QuickTranslate/pull/1) 的分支，在独立分支实施。保留 .NET 10 / WPF / Win32 / ONNX Runtime 架构、现有热键和本地 OCR 隐私边界。

## 调研结论

优先优化现有推理链路和存储热路径，并把 **PP-OCRv6 small** 作为下一轮真实屏幕测试的候选。当前 default medium 的 det + rec 权重共 138,587,816 字节；官方 small 对应文件共 31,039,890 字节，约少 77.6%。这是模型文件大小差异，不等同于应用内存或耗时差异。

| 方案 | 适用性 | 本轮决策 |
|------|--------|----------|
| PP-OCRv6 medium + 当前 ONNX Runtime | 已有模型、字符表和预处理路径，迁移成本最低 | 保留默认，去掉输出复制并修复段落截断 |
| PP-OCRv6 small + ONNX Runtime | 官方定位含桌面 / 移动端；det / rec 约 9.88 MB / 21.16 MB | 首选待测候选；未验证真实屏幕准确率前不替换默认权重 |
| PP-OCRv6 tiny | 更偏资源受限设备，模型性能与精度取舍更大 | 可作为低配置机器后续实验，当前不增加配置分支 |
| RapidOCR | 提供 Paddle 模型的 ONNX 部署及多语言实现 | 用作实现和部署参考；当前项目已有同类 C# 推理链路，整套替换的收益尚无实测依据 |
| Windows.Media.Ocr | 系统 OCR API，返回行、词和位置；语言可用性需检查设备 | 适合作为未来备用引擎实验；不能从 API 存在推断在本项目中更快或更准 |

来源：[PaddleOCR v6 官方介绍](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html)、[官方跨平台部署](https://www.paddleocr.ai/latest/en/version3.x/inference_deployment/cross_platform/android_deployment.html)、[RapidOCR 官方文档](https://rapidai.github.io/RapidOCRDocs/main/)、[Microsoft OcrEngine](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)。

PaddleOCR 官方识别评测中，medium / small / tiny 的 Screen 指标分别为 82.5 / 79.7 / 71.2，Print-EN 为 94.1 / 93.3 / 88.4。官方另有 Xeon 8350C、200 张通用 / 文档图片的 ONNX 完整流程平均耗时 3.31 / 0.61 / 0.22 秒，包含 I/O 和前后处理。这些结果说明存在速度和识别能力的取舍，**不是 QuickTranslate 的热键延迟、Windows 实测或本机准确率**。本轮没有把这些数字写成应用性能承诺。

### small 候选的可追溯资产

| 资产 | 官方固定 revision | 字节数 | SHA-256 |
|------|--------------------|-------:|---------|
| [small det](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/tree/28fe5895c24fd108c19eb3e8479f4ab385fbfc62) | `28fe5895c24fd108c19eb3e8479f4ab385fbfc62` | 9,880,512 | `d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e` |
| [small rec](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/tree/b8f84f0b80c529de40b4fbb3544b84fa7233a513) | `b8f84f0b80c529de40b4fbb3544b84fa7233a513` | 21,159,378 | `5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634` |

已读取此 rec revision 的 `inference.yml`：BGR 输入、识别 resize `[3,48,320]`、CTC、18,708 个字符。从 YAML 字符数组生成 UTF-8 / LF 字典后，SHA-256 为 `b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d`，与仓库现有 medium 字典相同。这个结论只针对上述精确版本，不代表任意同名模型可混用字符表。

后续切换默认前应在同一台 Windows 机器、同一批英文 / 中英混排截图上比较 medium 与 small：Word / Block、125% / 150% / 200% DPI、深浅背景、小字号、代码和多行段落；记录文本正确率、词框定位、漏行率、冷 / 热 P50/P95 与峰值内存。CPU 与 DirectML 分开报告。只换模型文件并不足以证明兼容；仍需检查算子、输入输出、归一化与字符表。

## 已实施的改动

### OCR 热路径

`CtcGreedyDecoder` 从大引擎类抽出，直接读取连续 `DenseTensor<float>.Buffer.Span`，并在输出仍存活时解码，消除每个识别行的 `T × C × 4` 字节数组复制。非连续 Tensor 保留按逻辑顺序复制的兼容路径。明确验证 `[1,T,C]` / `[T,C]`、数据长度与标签数。

`OcrRegionSelector` 负责焦点区域筛选，移除原先无条件 `Take(8)`；12 行测试不再只返回 8 行。不通过 CPU / GPU 参数推断用户选择的 Word / Block 模式。

`OcrOperationGate` 串行管理识别、预热和会话重建，等待可取消；关闭时拒绝新操作并等当前租期结束后清理，避免销毁正在推理的会话。被替换但尚未完成的初始化仍有清理责任，部分创建失败的 native session 和 SessionOptions 也会释放。旧初始化的状态不会覆盖新一代会话状态。[ONNX Runtime 官方说明](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html#configuration-options)明确要求同一个 DirectML session 不能并发调用 Run。当前正在执行的同步 ONNX Run 不会被 token 强制中断；本轮没有引入未经 CPU / DirectML 实测的硬终止逻辑。

保留已有 ROI、UIA 取词、输入缓冲复用和会话常驻策略。本轮不声称改成了真正的“首帧推理预热”：现有 WarmUp 主要是初始化会话，首轮模型运行延迟仍需实机测量。

模型下载器此前计算了 SHA-256 却没有和清单比较。现在必需模型及字符字典必须匹配清单，缺失 / 错误清单和损坏文件都会报告失败；可选分类器不再阻止必需文件齐备判断。去掉下载脚本和清单搜索中的开发者固定磁盘路径。官方 YAML 的字符表位于 `PostProcess.character_dict`，本轮同时修复 C# 下载器与 PowerShell 脚本只识别顶层字段的问题，支持两种列表缩进并在同级字段处停止，防止把其他列表混入字符表。

### 内部词典和缓存

实际 packed 词典为 **500,000 条、30,070,636 字节**，其中 224,250 个键包含空格（44.85%）。保留词库覆盖面，不以删除短语换取体积减少。规范化后无重复键、无空释义。

- `ContainsWord` 提供精确成员查询，OCR 词合理性检查不再创建释义、音标和 `TranslationResult` 对象；不引入词干猜测改变几何拆词依据。
- packed 加载重用租借的 64 KiB 缓冲，字段解码不再逐一 `ReadBytes` 分配。加载前限制文件大小、条目数和字段长度，严格检查 UTF-8 与格式版本。
- 完整解析成功才发布该候选；AppData 候选损坏时回退随包词典，避免部分坏词表覆盖完整资产。原有两个 v1 magic 格式和用户 overlay 仍可用。
- Word 路由为 L1 → 本地词典 → L2 SQLite → 在线；本地确定性命中不触盘，不会被较旧的在线 L2 结果遮住。
- SQLite 对新增条目自动裁剪，默认软上限为 `maxKeep + min(256, max(1, maxKeep / 10))`，超过后裁到 `maxKeep`；已有超量库在打开时裁剪。更新已有 key 不增加计数。插入和裁剪同事务，维护失败时回滚新增缓存项，避免无限增长。
- 无效缓存 JSON 被移除并视为未命中；可恢复的缓存读写故障不阻断词典 / 在线翻译。用户取消仍向上传播，错误日志不含原文和完整缓存载荷。

现有 Lazy / FrozenDictionary 快照和启动预载仍被保留，它们在本轮前已经存在。

### API 接入与结构

增加“需要 API Key”“启用流式”“发送 temperature”三个设置，默认均开启。关闭鉴权后，本地匿名服务不会收到已保存的全局 Key。新增开关参与设置快照、配置版本和缓存身份。

拆出 `ChatCompletionsEndpoint`、`TranslationHttp`、`OpenAiChatResponseReader`。UI 校验、连接测试和真实翻译共享端点规则；Custom 与 Qwen 共享 HTTP 生命周期和响应解析，继续保留各自请求语义。支持完整端点、自定义路径前缀和 query，避免重复追加路径或把 query 当作路径。

请求超时覆盖响应头和正文。按 [Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)，`ResponseHeadersRead` 下 `HttpClient.Timeout` 本身只覆盖响应头完成前，因此需要保留独立超时 token 直到读取完正文。

SSE 处理按事件合并多行 data，识别 `[DONE]` / finish_reason 后结束；空内容、异常结构、超限内容和未完成的流不会以成功译文缓存。错误信息只给安全分类，不回显服务原始正文。HTTP 连接池设置 5 分钟寿命以刷新后续连接的 DNS，符合 [HttpClient 官方建议](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines)。代理开关仍需要重启，UI 已明确说明。

完整配置示例和限制见 [接入指南](ocr-models-and-custom-llm.md)。本轮支持 Bearer / 匿名 Chat Completions，不把所有 API 协议或任意自定义 Header 宣称为已适配。

### 可复用验证入口

新增 `QuickTranslate.Engine.Tests`，引用真实 Core / Infrastructure 并链接原测试源，避免为了 Linux 执行复制业务逻辑。原 Windows / WPF 测试保留，CI 增加 Linux 核心回归作业；真实模型和图形桌面测试仍单独运行。

新增 `QuickTranslate.HotPathBenchmarks`，测量实际 50 万条词典加载、查词及 CTC 输出复制 / 直接读取差异，保留原始 JSON 和复现说明。见 [热路径测量](benchmark-results/2026-10-26-hot-paths.md)。

## 验证边界

实际下载并检查了项目默认的 medium det / rec ZIP（压缩包分别 33,633,266 / 66,927,398 字节）。用生产 `ModelDownloader.GenerateDictFromZipYml` 从真实 rec ZIP 生成 18,708 行字符字典，再运行生产 `VerifySha256`，检测、识别、字典三项均与仓库 manifest 匹配，返回成功。这是资产与生成链路校验，不包含 ONNX 推理。

自动化覆盖解码结果、段落行数、词典损坏 / 回退、缓存容量与异常、URL 组合、SSE/JSON、正文挂起、取消、匿名鉴权和参数开关。新增行为测试先在旧实现复现失败，再验证修复。

跨平台测试和源码构建能够验证纯逻辑与编译兼容，不能证明真实 Windows 屏幕截取、WPF 交互、DirectML 驱动或 medium / small 的识别准确率。基准只比较对应方法的 CPU 耗时和当前线程托管分配；不将结果换算成整机内存、端到端 OCR 延迟或识别精度。

README 原有约 25 MB / 110 ms 数字已明确标为历史 Mock OCR 基线，移除把它们直接作为真实模型应用表现的宣传描述。
