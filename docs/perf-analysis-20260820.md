# QuickTranslate 性能分析与优化方案（2026-08-20）

_基于本机（AMD Zen3 / 27GB RAM / Windows 25H2）真实 PP-OCRv6 模型实测_
_前置状态：首轮优化已完成（启动预热、L2 缓存接入、SQLite WAL、词框切分合并入口）_

## 1. 实测数据（预热后稳态，临时 xunit 探针采集后已删除）

| 场景 | det（每帧一次） | rec（每行） | 总耗时 |
|---|---:|---:|---:|
| Word 起捕 300×100，1 行 | 374ms | 74ms | ~455ms |
| Word 扩抓 900×300，5 行 | 233ms | 75ms×5 | ~365ms |
| Block 1600×900，15 行 | 540ms | 65ms×15 | ~1000ms |
| Block + 焦点带（3 行） | 540ms（首帧） | 75ms×3 | 首帧 ~640ms；同帧复测 194ms（det 缓存命中） |
| GDI 截图 900×300 | — | — | 6~7ms |
| WarmUp（会话已建） | — | — | 554ms |

其他已测数据（首轮优化后）：SQLite L2 Add ~394µs、Get ~139µs；WordSelector 32µs；
HotkeyToOverlay 非 OCR 开销 ~10ms（Mock OCR 100ms 时总 110ms）。

## 2. 瓶颈排序

1. **det 推理 374–540ms/帧** —— 固定成本，当前最大单项开销
2. **rec ~75ms/行** —— 随识别行数线性增长
3. 其余均可忽略：截图 7ms、SQLite 0.4ms、选词 32µs、det 预处理 ~20ms

结论：首轮优化后翻译侧/截图侧/缓存侧已无瓶颈，剩余问题全部集中在 OCR 推理。

## 3. 关键代码事实（优化落点依据）

- Word 管线不传焦点带：`WordInteractionCoordinator` 调 `RecognizeAsync(frame, ct)`，
  扩抓后捕获区内所有行都跑 rec（5 行 ≈ 375ms），但取词只用光标所在行
- Block det 全帧跑：`BlockRetryCoordinator` 传焦点带过滤了 rec，但 det 仍在整帧
  1600×900 上推理（540ms），带外检测结果全部浪费
- 块生长无 rec 复用：触带扩展后新进带的行全部重跑 rec；现有缓存只在 det 层（同帧位图引用）
- `MaxLinesToRecognize = 12`：最坏 12 行 × 75ms ≈ 900ms，上限过高
- cls 步骤：本机无 cls.onnx 自动跳过（实测 cls=0ms）；若目标机器携带，每行 +10–30ms

## 4. 优化方案（按优先级）

### P0-1 Word 模式只识别光标最近行
`WordInteractionCoordinator` 传入以光标为中心、±1 行高的焦点带（或引擎对 Word 场景
只 rec 距光标 Y 最近的 1–2 行）。取词语义上从不用远行；带容差 ±1 行高覆盖行高估值误差。
- 预期：Word 扩抓 365ms → ~150ms；起捕场景不变（本就 1 行）
- 风险：低。需回归「锚点行无文字时 NoTextFound 重抓」路径

### P0-2 Block 首次识别按锚点裁剪 det 输入
det 前把帧按「锚点 ± 慷慨边距」（建议帧高 40%，不是精确焦点带）裁剪后再送 det，
输入面积降 60%+，det ≈ 150–200ms。裁剪区刻意留大使触带扩展仍落在裁剪区内，
同帧 det 缓存继续命中。坐标需平移回帧内坐标系，det 缓存 key 绑定（位图, 裁剪区）。
- 预期：Block 首识 640ms → ~400ms
- 风险：中。需验证裁剪区对超高行/带边界的余量；块选择坐标系回归

### P1-3 rec 行级缓存（块生长收益最大）
引擎内行级缓存：key =（帧位图引用 + 裁剪框），value =（文本 + 词框）。
块生长带扩展时已识别行直接命中，只跑新进行。与 det 同帧缓存同生命周期管理。
- 预期：块生长每轮迭代 rec 成本 −50% 以上（194ms → ~100ms）

### P1-4 MaxLinesToRecognize 12 → 6~8
一行改动。注释已说明远离光标的行识别了也不会被选中。
- 预期：最坏情况上限 900ms → 450–600ms

### P1-5 刷新基准报告
`docs/benchmark-results/cc0c3cb-1786438643301.md` 为 2026-08-11 优化前数据
（SqliteCacheAdd 6404µs，现 ~394µs），重跑 benchmarks 更新基线。

### P2（需先验证，不盲目上）
- `DetMaxSideLen` 960 → 800：Block 大图 det 再省 ~30%，需真实屏幕内容回归检测召回率
- `PreprocessDet` HWC→CHW 循环融合（现 ~20ms，收益小、改动便宜）
- cls 策略配置化：目标机器若带 cls.onnx，每行 +10–30ms，做成设置项默认关

## 5. 明确不建议（附理由）

| 方案 | 否决理由 |
|---|---|
| 多行 rec 并行 | 代码注释有实测记录：ORT 单推理已用满核，线程超订使 rec 恶化 3–4 倍 |
| 截图零拷贝 | 已验证回归：FromHbitmap 返回 32bppRgb 违反 ScreenFrame Argb 契约 |
| DirectML/GPU EP | 小输入下 CPU→GPU 传输开销可能抵消收益，引入驱动兼容面，属独立探索项 |
| 改路由查询顺序 | RouterL2CacheTests.Case1 断言 L2 命中不调词典，属测试契约 |

## 6. 预期总收益

| 场景 | 现状 | 方案后 |
|---|---:|---:|
| Word 取词（典型，无重抓） | ~455ms | ~455ms（det 主导；P2 侧降可再 −70ms） |
| Word 扩抓重抓 | ~365ms | ~150ms |
| Block 首次识别 | ~640ms | ~400ms |
| Block 生长迭代（每轮） | ~194ms | ~100ms |

## 7. 实施建议顺序

1. P0-1 + P0-2 + P1-4（改动集中、收益明确，一次提交并配真实 OCR 回归）
2. P1-3（单独提交，需新增缓存失效测试）
3. P1-5 重跑基准更新报告
4. P2 各项逐个做 A/B 验证后再决定

## 8. 落地记录（2026-08-21，P0-1 + P0-2 + P1-4 已完成）

改动落点：

- **P0-1**：`WordInteractionCoordinator` 两次识别均传焦点带（光标 ±1.5 行高，重抓用实际行高），
  新增测试 `WordCapturePreviewTests.Recognize_PassesFocusBand_CenteredOnCursor`
- **P0-2a**（实施中发现的真正大头）：`PreprocessDet` 旧实现会把小帧放大到长边 960
  （与上游 PaddleOCR 行为相违），Word 起捕 186×80 被放大 ~5 倍面积。改为 ratio ≤ 1
  （只缩不放），小帧 det 直接降一个数量级
- **P0-2b**：引擎新增 `ComputeDetCrop`（焦点带 ± 20% 帧高边距）+ 包含式 det 缓存
  （新裁剪区落在已缓存裁剪区内即命中，盒子存帧局部坐标）；裁剪 ≥95% 帧高时退化为全帧。
  真实 OCR 回归：`RealOcrRecognitionTests.RecognizeAsync_FocusBand_OnlyBandedLine_AndDetCacheHitsOnSameCrop`
- **P1-4**：`MaxLinesToRecognize` 12 → 8

实测对比（同一探针方法，预热后稳态）：

| 场景 | 前 | 后 | 说明 |
|---|---:|---:|---|
| Word 起捕 186×80 | ~455ms（det 374） | **~145ms（det 42）** | P0-2a 主导，−68% |
| Word 扩抓 900×300 | ~365ms（rec 5 行） | **~288ms（rec 1 行）** | P0-1；剩余为 det 179ms |
| Block 首识 1200×720（探针密集 12 行） | ~1450ms（rec 12 行） | ~1130ms（rec 8 行，det 不变） | P1-4 上限生效；真实块通常 3~5 行，收益更小 |
| Block 同帧扩展 | 194ms | 517ms 中 det=0ms | 缓存继续命中，机制未退化 |
| 带内行数上限 | 12 | 8 | P1-4，探针实测生效 |

与原方案的偏差（诚实记录）：

- **P0-2 原预期「Block 首识 640→400ms」未达成**：默认块流程焦点带高 560px 占 720px 帧的 78%，
  加任何安全边距后裁剪区都 ≥95% 帧高，退化为全帧（det 不变）。P0-2 的实际收益来自意外发现的
  P0-2a（小帧不放大），主要惠及 Word 管线。Block 首识若要再降 det，只剩 P2 的
  `DetMaxSideLen` 降低路线（需召回率 A/B）。
- Word 扩抓剩余 288ms 中 det 179ms 占大头（900×300 缩放到 960×320），同样只有 P2 路线可解。

全量测试：418 通过 / 0 失败（含 2 个新增回归）。

## 9. 落地记录（2026-08-21 晚，P1-3 + P1-5 + P2 预处理融合已完成）

改动落点：

- **P1-3 rec 行级缓存**：引擎内同帧行缓存（键 = 位图引用 + Region，行匹配用
  IoU ≥ 0.6），命中行整体复用（文本+词框+置信度），跳过裁剪/cls/rec/词框切分；
  空结果同样入缓存；帧换代/TTL 8s 整体重建。实施中发现的坑：det 在裁剪输入
  vs 全帧输入下的盒子坐标抖动实测可达 5px（unclip 扩展随盒高变化），精确 box
  相等匹配会漏命中，改 IoU 后稳定命中。
  回归：`RealOcrRecognitionTests.RecognizeAsync_SameFrameWiderBand_RecLineCacheReusesRecognizedLines`
  实测：两行帧首识带内 1 行 → 无带重扫 recHits=1（只跑新行），再次重识
  recHits=2 且 rec=0ms
- **P2 预处理融合**：`PreprocessDet` HWC→CHW 改为单遍线性循环 + 256 项归一化 LUT，
  内循环无乘法索引重算/无浮点运算；数值与旧实现逐字节一致（同公式预烘焙）
- **P1-5 基准刷新**：新基线 `docs/benchmark-results/9a6fde2-1787294446398.md`（fast 模式），
  对比旧基线 cc0c3cb：SqliteCacheAdd 6404µs→141µs、SqliteCacheGet 6310µs→113µs（WAL 生效）。
  口径变化：HotkeyToOverlay 从 110ms 降为 0.04ms，非性能突变——选区覆盖层（扫描动画）
  已改为 OCR 之前即时展示，指标现度量热键→即时视觉反馈路径，不含 OCR/翻译耗时。
- 顺手清理：删除模板残留 `src/QuickTranslate.Platform/Class1.cs` 与根目录调试残留
  `qt_pid.tmp`、`t_enum.ps1`、`t_find.ps1`；弹窗/设置窗/关于窗 emoji 图标统一为
  Segoe Fluent Icons 单色字形

全量测试：443 通过 / 0 失败（含 1 个新增回归）。
