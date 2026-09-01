# QuickTranslate

[![ci](https://github.com/xf2214/QuickTranslate/actions/workflows/ci.yml/badge.svg)](https://github.com/xf2214/QuickTranslate/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> **The fast screen-reading translator for Windows — hover over any text and press a hotkey.**

No selecting. No copying. No tab-switching. Just park your cursor on the target text and press a global hotkey. QuickTranslate recognizes and translates it right where you are.

[中文文档](./README.md)

| Hotkey | Action |
|:---:|---|
| **Alt + 1** | Word translation (compact popup: IPA / POS / definition) |
| **Alt + 2** (tap) | Block translation (streaming popup) |
| **Alt + 2** (hold ≥ 400 ms, then drag down) | Drag-select: selection grows line by line; release to translate |
| **Esc** | Cancel operation + close overlays |

> 🎯 **Typical flow**: hover over the word *"significantly"*, press **Alt + 1** → local OCR runs → a teal rectangle locks the word → a compact card shows IPA + part-of-speech + Chinese definition. Your foreground app never loses focus.

---

## Core capabilities

| | Feature |
|:-:|---|
| 🎯 | **Point-and-translate**: cursor → OCR → box → popup, never steals foreground focus |
| ⚡ | **Instant**: Hotkey → Overlay P50 < 180 ms; cached hits return without network |
| 🖱️ | **Drag-select**: hold Alt+2 and drag down — the selection grows live, line by line; you decide the paragraph extent |
| 🧠 | **Local OCR**: full PP-OCRv6 pipeline (det → cls → rec → CTC decode) runs on-device |
| 🔒 | **Privacy-first**: screenshots live only in memory, never saved or uploaded; keys DPAPI-encrypted |
| 🧩 | **Any model**: default custom OpenAI-compatible endpoint (SSE streaming), fallback to Qwen-MT |
| 🖥️ | **High-DPI**: PerMonitorV2 aware, accurate popup placement across multi-scaling multi-monitor setups |
| 🎙️ | **TTS**: Windows SAPI text-to-speech, one-click playback |
| 📦 | **Tray resident**: no main window, ~< 0.5% CPU / ~25 MB working set while idle |

---

## Interaction modes

### 1 · Word mode Alt+1

Hover over any English word and press **Alt + 1**. The app takes a tiny cursor-centered screenshot → PP-OCRv6 recognition runs locally → a teal selection box locks the target → a **compact definition card** pops up (IPA / POS / Chinese meaning / copy button) automatically positioned to avoid occluding the original text and clamped to the monitor's work-area.

Routing priority: in-memory LRU → SQLite L2 → ECDICT-lite local dictionary → online LLM. Frequent words hit zero-network.

### 2 · Block mode Alt+2 (tap)

Place the cursor inside a paragraph and **tap** **Alt + 2**. The OCR line under the cursor becomes the anchor; a heuristic (line-height / inter-line gap / left-alignment / horizontal overlap) grows the selection **upward and downward** into an intended paragraph. An amber semi-transparent overlay highlights the chosen lines and, below it, a **medium-size translation card** appears with:

- A 3-line OCR preview at the top (helps verify the recognition scope)
- **Streaming translated text** via SSE (TTFT target < 700 ms with online providers)
- Auto-sized content area + scrollbar
- Copy / Read-aloud / Close actions

### 3 · Drag-select mode — hold Alt+2

Heuristic picked the wrong range? **Hold Alt + 2 (≥ 400 ms) to enter drag mode.** After the initial recognition, every OCR line box your cursor crosses downward extends the dashed preview selection live (no repeated OCR); releasing switches to the solid scan box and translates. Dragging past the initial capture's bottom edge triggers a one-shot larger re-capture (1600×1200) + re-recognition, so growth can continue.

### 4 · Settings & About

- **Settings window** (tray context menu → Settings): four cards with live status badges.
  - **Translation Engine**: drop-down for "Custom OpenAI compatible / Qwen-MT". Fill in Base URL, Model, API Key — changes apply **immediately without restart**. Works out-of-the-box with DeepSeek, Moonshot, local Ollama, one-api aggregates, etc.
  - **Global hotkeys**: rebindable with auto conflict-detection (system + self) on save. Green badge ✔ "No conflicts".
  - **Miscellaneous**: Autostart (HKCU Run, user-level, no admin required); "Click outside to dismiss"; Debug log toggle.
  - **System status**: live badges for OCR engine (PP-OCRv6 ready / Mock fallback) and API Key configuration.
- **About window** (tray context menu → About): version info + hotkey cheat sheet.

---

## How it works

### Interaction pipeline

```
Hotkey (Alt+1 / Alt+2 tap / Alt+2 hold-and-drag)
   → Coordinators:  WordInteractionCoordinator / BlockInteractionCoordinator
   → Cursor-centered screenshot (physical pixels, GDI BitBlt, only in-memory)
   → IOcrEngine.RecognizeAsync  —  PP-OCRv6 ONNX: det → cls → rec → CTC decode
   → Word / Block selection (screen-coord hit / geometric grow / drag line filtering)
   → SelectionOverlay (WS_EX_NOACTIVATE, unfocused; dashed preview while dragging)
   → ITranslationRouter → ITranslationProvider (see routing below)
   → WordPopup / BlockPopup (auto-positioned + size-adaptive + optional TTS)
```

### Translation routing

```
Word mode                                  Block mode
  │                                            │
  ├→ L1 in-memory LRU hit? ──yes──→ done       ├→ L1 / L2 translation cache hit? ──yes──→ done
  │ miss                                       │ miss
  ├→ L2 SQLite (persisted, cross-launch)       └→ online provider
  │ miss                                         ├→ Custom OpenAI (SSE streaming, default)
  ├→ ECDICT local dictionary (Word only)        └→ Qwen-MT (fallback, non-streaming)
  │ miss / disambiguate-context needed
  └→ online provider (with OCR neighbor lines for WSD)
```

## Tech stack

| Layer | Component |
|---|---|
| Language / runtime | C# / .NET 10 (`net10.0-windows`) |
| UI | WPF + Win32 P/Invoke (`WS_EX_NOACTIVATE` unfocused popups) |
| OCR | PP-OCRv6 + ONNX Runtime (CPU; session kept warm post-startup) |
| Screenshot | GDI BitBlt, in-memory Bitmap buffer — by default **never persisted** |
| Cache | LRU memory (1000 entries) + SQLite L2 (SHA-256 indexed, with hit tracking) |
| Dictionary | ECDICT-lite (user custom overlay → packed high-frequency binary) |
| Translation | `ITranslationProvider` abstraction with dispatch: default custom OpenAI SSE streamer / optional Qwen-MT |
| Key storage | Windows DPAPI `CurrentUser` + entropy file |
| Logging | Serilog with leveled filtering (source text excluded by default; opt-in Debug switch) |
| TTS | Windows SAPI (`QuickTranslate.TextToSpeech`) |
| Tests | xUnit (~580+ cases) covering geometry / selection / cancellation races / DPI / OCR / cache / providers |
| CI | GitHub Actions (Windows build + full test run; real-model regressions skipped conditionally) |
| Packaging | `win-x64` publish profile; models & dictionaries copied conditionally when present |

## Solution layout

| Project | Responsibility |
|---|---|
| `src/QuickTranslate.Core` | Domain abstractions (OCR / translation / selection / cache interfaces), physical-vs-DIP strong types, `AppSettings` |
| `src/QuickTranslate.Platform` | Win32 interop: GDI screenshots, global hotkeys (incl. hold detection), PerMonitor DPI, window styles |
| `src/QuickTranslate.Infrastructure` | PP-OCRv6 engine, translation providers, dictionary/cache, `SettingsManager`, `ModelDownloader`, DPAPI store |
| `src/QuickTranslate.App` | WPF UI (popups / overlays / loading indicators / Settings / About windows), Word & Block coordinators, popup positioning & size estimation |
| `src/QuickTranslate.TextToSpeech` | Windows SAPI TTS wrapper |
| `benchmarks/QuickTranslate.Benchmarks` | Performance console harness (samples in `docs/benchmark-results/`) |
| `tests/QuickTranslate.Tests` | xUnit unit + integration tests |

## Privacy & security

| Item | Policy |
|---|---|
| Screen image | Exists only as an in-memory `Bitmap`. `Dispose()` is called as soon as OCR finishes. **No files written by default.** |
| Upload | Only the **recognized text** (+ optional disambiguation context lines) is sent; **no screen image is ever uploaded.** |
| API Key | Encrypted with Windows **DPAPI `CurrentUser`** + a machine-unique entropy.dat. It is **never** serialized as plaintext into JSON / logs / source. |
| Logs | Captures timing, length, error codes and other non-content metrics by default. Only the Debug switch allows richer technical context. |
| Single-instance | Startup `Mutex` prevents multi-instance hotkey race conditions. |

## Performance baseline

From [docs/benchmark-results/9a6fde2-1787294446398.md](docs/benchmark-results/9a6fde2-1787294446398.md) (WAL + synchronous=NORMAL, see [docs/perf-analysis-20260820.md](docs/perf-analysis-20260820.md), commit 9a6fde2; manually collected with Mock OCR + offline translation; real-device end-to-end will differ):

| Metric | P50 | P95 |
|---|---:|---:|
| Idle Working Set | ~24.8 MB | ~25.2 MB |
| Idle CPU | 0.00% | 0.10% |
| Hotkey → Overlay | ~110 ms | ~110 ms |
| WordSelector | ~32 µs | ~3048 µs |
| Cancel Latency | ~33 µs | ~328 µs |
| SqliteCache Add/Get | ~141 µs / ~113 µs | ~277 µs / ~138 µs |

---

## Getting started

### Requirements

- Windows 10 / 11 (x64)
- .NET 10 SDK
- OCR model assets (det / cls / rec ONNX + `ppocr_keys.txt`) — expected under `assets/models/`. Missing models trigger a Mock OCR fallback for development convenience; use the download script below for production use.

### Build & run

```powershell
dotnet build QuickTranslate.sln
dotnet run --project src/QuickTranslate.App
```

The app resides in the system tray: **Alt+1** for words, **Alt+2** for blocks (hold to drag-select), **Esc** to cancel.

### Model assets (fresh checkout)

`assets/models/version.json` contains SHA-256 manifests. If det/rec ONNX are missing, the engine falls back to Mock OCR. To download the real models:

```powershell
# Downloads PP-OCRv6 det/rec zips from HoVDuc/ppocrv5-onnx v1.1.0,
# then regenerates ppocr_keys.txt from the released inference.yml.
.\scripts\download-v6-final.ps1
```

> The dictionary size **must exactly match** the rec output categories (18710 = blank + 18708 chars + space). `ModelVersionVerifier` checks SHA-256 at startup; mismatches log a Warning and revert to Mock.

### Tests

```powershell
# Full suite (~580+ tests)
dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj

# Real-model recognition regressions (requires assets/models; skipped automatically if models aren't present)
dotnet test tests/QuickTranslate.Tests/QuickTranslate.Tests.csproj --filter "FullyQualifiedName~RealOcrRecognitionTests"
```

### Publish (win-x64)

```powershell
dotnet publish src/QuickTranslate.App -c Release -r win-x64 --self-contained true -o ./artifacts/publish
```

---

## Further reading

- [QuickTranslate_Agent_Project_Spec_v0.1.md](QuickTranslate_Agent_Project_Spec_v0.1.md) — PRD + system design + interface contracts + acceptance criteria (milestones M0–M7)
- [docs/handoff-20260817.md](docs/handoff-20260817.md) — latest engineering loop (OCR garble root cause fix / custom LLM wiring / loading animations / size-adaptive popups)
- [docs/ocr-models-and-custom-llm.md](docs/ocr-models-and-custom-llm.md) — OCR model provenance & custom LLM integration guide
- [docs/superpowers/specs/2026-08-27-block-drag-design.md](docs/superpowers/specs/2026-08-27-block-drag-design.md) — drag-select feature design doc
- [docs/benchmark-results/](docs/benchmark-results/) — performance reports
- [docs/per-monitor-dpi-test-guide.md](docs/per-monitor-dpi-test-guide.md) — multi-monitor / high-DPI testing checklist

## License & credits

- Code in this repository is released under the [MIT License](LICENSE).
- Third-party component notices are listed in [LICENSES.txt](LICENSES.txt), including: PP-OCRv6 model weights (Apache-2.0), ONNX Runtime (MIT), ECDICT dictionary, .NET Runtime, xUnit, and others.
