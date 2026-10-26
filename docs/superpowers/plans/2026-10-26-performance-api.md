# QuickTranslate Performance and API Implementation Plan

> **For agentic workers:** Use the relevant implementation, testing and review
> skills. The owner has requested autonomous execution; proceed without additional
> design approvals. Independent tasks have disjoint file ownership.

**Goal:** Reduce avoidable OCR/dictionary work and make translation endpoints
configurable, reliable and testable.

**Architecture:** Preserve public module boundaries. Extract pure OCR decoding and
small HTTP protocol helpers. Keep provider-specific payloads separate from shared
transport/response handling. Keep dictionary snapshots immutable.

**Tech Stack:** C#/.NET 10, WPF/Win32, ONNX Runtime 1.20, SQLite, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-26-performance-api-design.md`

## Global constraints

- Base commit `4859c4527bda0d582b47a3d0503ded40feb7c9e1`; no master writes or merges.
- Preserve local-only OCR, in-memory screenshots, DPAPI secrets and physical coordinates.
- Preserve cancellation and settings/cache isolation; no raw text/key error logging.
- Keep existing protocols and packed formats compatible. No new heavyweight runtime.
- Add behavior regressions before implementation; verify meaningful failure then pass.
- Run Windows CI as the authoritative full-solution test gate.

## Review focus

- Anonymous endpoints must receive no inherited Authorization header.
- Body stalls, early EOF and malformed SSE must not become successful translations.
- Corrupt/oversized dictionary data must not allocate from unvalidated lengths or leak partial state.
- L2 errors must not block local lookup/successful translation; caller cancellation must propagate.
- CTC tensor lifetime/dimensions and blocks exceeding eight lines must remain correct.

### Task 1: OCR output and region selection

Owner: OCR implementer. Files: `Infrastructure/Ocr/*`, related OCR tests.

Interfaces: retain `IOcrEngine` and `PaddleOcrV6Engine.CtcGreedyDecode` compatibility;
new decoding helper accepts `ReadOnlySpan<float>` with explicit dimensions.

- [x] Add regressions for CTC blanks/repetition/confidence, invalid dimensions,
  span slices and recognition-region lists longer than eight.
- [x] Observe failures for new behavior; record command/result.
- [x] Remove recognition-output copy and extract the pure decoder; remove implicit
  Block truncation while preserving focus-band selection.
- [x] Run targeted tests and compile Infrastructure; record allocation evidence.

- [x] Serialize session users and rebuilds with a cancellable lease, defer disposal,
  retire pending initialization, and dispose partial native-session construction.

### Task 2: Dictionary loading and cache resilience

Owner: dictionary implementer. Files: `ILocalDictionary`, `EcdictLiteDictionary`,
`DefaultTranslationRouter`, `SqliteTranslationCache`, corresponding tests, and only
the OCR membership delegate in `ServiceCollectionExtensions`.

Interfaces: add `ContainsWord(string)` with a compatibility default implementation;
ECDICT overrides it. Keep cache interfaces and cache-key implementation unchanged.

- [x] Add regressions for truncated/oversized/versioned packed data, fallback,
  exact overlay membership and non-Chinese target handling.
- [x] Observe failures, then use bounded reusable buffers and atomic snapshots.
- [x] Test dictionary success bypasses L2; test L2 exceptions do not lose successful
  translations, and cancellation does not become fallback success.
- [x] Implement dictionary-before-L2 and safe diagnostic cache degradation.
- [x] Test and enforce bounded L2 capacity with documented trimming behavior.
- [x] Run dictionary/router/SQLite regressions and measure membership allocations.

### Task 3: Configurable and reliable API integration

Owner: API implementer. Files: `Infrastructure/Translation/*`, `AppSettings`,
`TranslationCacheKey`, `SettingsValidator`, `SettingsWindow.*`, corresponding tests.

Interfaces: the three true-by-default flags named in the spec; preserve existing
provider constructors/methods and existing settings files.

- [x] Add regressions for full/base URLs, queries, malformed endpoints and loopback.
- [x] Add regressions for authentication omission, JSON/stream modes, optional
  temperature, settings roundtrip/revision and cache isolation.
- [x] Add controllable-stream tests for header success followed by body stall,
  explicit cancellation, DONE without EOF, finish reasons, malformed/empty/truncated
  streams and service echoes containing secrets.
- [x] Verify failures, then extract URI/response/HTTP helpers and implement flags
  consistently in configuration, requests, UI and connection tests.
- [x] Run provider, settings and cache regressions; compile the WPF solution.

### Task 4: Integration, evidence and delivery

Owner: controller. Files: test/build tooling, benchmark evidence, documentation.

- [x] Establish baseline compilation and portable actual-source test execution.
- [x] Integrate and review each task for both behavior and maintainability.
- [x] Record OCR primary-source comparison, actual model manifest and model-swap
  constraints; correct misleading performance/asset documentation.
- [x] Run reproducible before/after microbenchmarks; label the measurement scope.
- [ ] Run full Windows build/tests in GitHub Actions and inspect the exact head.
- [ ] Commit reviewable changes, create a dependent PR, report changes, verification,
  limitations and practical configuration instructions. Do not merge.

### Confirmed integration corrections

- [x] ModelDownloader must compare actual model/dictionary hashes to the manifest;
  absent/invalid manifests cannot pass. Keep the classifier optional.
- [x] Remove machine-specific download paths and clarify proxy restart behavior.
- [x] Preserve hidden settings when switching the settings UI to atomic ApplyFrom.
- [x] Bare loopback URLs must use default HTTP port 80, preserving explicit ports.
- [x] Check cancellation after synchronous first dictionary loading.

## Integration verification before remote CI

- Release solution rebuild: 0 errors (235 analyzer warnings).
- Full linked Engine suite: 439 passed, 0 failed, 0 skipped.
- Review regressions: malformed SSE and nested official YAML first 6 failed / 69 passed,
  then 89 passed after fixes (including integrity cases).
- Actual medium ZIP extraction via production ModelDownloader: 18,708 labels;
  detector, recognizer and generated dictionary SHA-256 all match the manifest.
- Fresh independent review and focused follow-up: no remaining code blocker.
- PowerShell script tests are wired to Windows CI; no local pwsh runtime was available.
