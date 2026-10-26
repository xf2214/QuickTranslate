# QuickTranslate OCR, dictionary and API optimization

Date: 2026-10-26. Implementation decision under the owner's explicit request to
research and optimize independently. Baseline: `4859c4527bda0d582b47a3d0503ded40feb7c9e1`
(PR #1). Work belongs on `perf/ocr-dictionary-api-20261026`; do not merge or change
`master`. The new PR depends on PR #1.

## Intent and constraints

Improve the existing mouse/hotkey translation pipeline in five requested areas:
OCR, the internal dictionary, API compatibility, maintainability and performance.
Keep C#/.NET 10/WPF, the existing Core/Platform/Infrastructure/App boundaries,
single-process operation, local OCR and the CPU baseline. Screenshots must never
be uploaded or persisted. API keys remain in DPAPI, absent from ordinary settings,
logs and cache identities. Cancellation must prevent stale operation results.

## Evidence and approach

The existing implementation already has persistent ONNX sessions, pooled input
buffers, focus-band recognition, a frozen dictionary, startup dictionary loading,
L1/L2 caches and HTTP connection pooling. Replacing those without evidence would
not address the remaining hot paths.

Three approaches were considered: replace the OCR stack, rewrite the application
into more projects, or improve the current pipeline behind its existing contracts.
The implementation chooses the third. A model replacement needs paired weights,
character dictionary, preprocessing verification and representative accuracy data.
More projects would not by itself make recognition faster. Small, independently
testable decoding/transport/endpoint components address current coupling.

Findings to address:

- Recognizer output is copied with `ToArray()` before CTC decoding. With the
  current 18,710-class model, this produces multi-megabyte allocations per line.
- Focus-band recognition truncates to eight regions even for a Block request.
- The packed dictionary contains 500,000 entries, not the documented 30–50k.
  Its loader allocates byte arrays for every field, validates some lengths after
  reading, and can publish partially loaded corrupt data.
- OCR vocabulary checks call full dictionary translation, allocating results
  that are immediately discarded. Local dictionary lookup also follows L2 I/O.
- L2 failures can discard a successful translation; configured capacity has no
  production enforcement.
- Provider timeout CTS lifetime ends at response headers, before streamed bodies.
  SSE handling and errors can accept incomplete responses or expose server echoes.
- Complete chat-completions URLs are appended a second time. Local keyless servers
  cannot be used explicitly without a dummy key. Streaming and temperature are
  unconditionally sent.

## Changes

### OCR

Decode contiguous model output through a read-only span while the output owner
is alive. Extract the CTC algorithm from the large engine class; preserve blank,
duplicate, whitespace and probability/logit confidence semantics. Validate shapes.
Remove implicit truncation of focus-band regions so a block can contain more than
eight lines. Keep physical coordinates and the existing recognition/selection
contracts. Do not silently substitute a new model or change GPU defaults.

### Dictionary and cache

Keep exact lookup and overlay precedence. Add a backwards-compatible membership
query to `ILocalDictionary`; ECDICT implements it without producing translations.
Use bounded reusable decoding storage and validate the complete packed candidate
before publishing. Corrupt user overrides may fall back to a valid bundled
dictionary; overlay loading remains independent. Retain both existing packed
formats and version 1. No heuristic stemming or unvalidated dictionary pruning.

After L1 misses, check the local dictionary before persistent cache I/O. Treat
recoverable L2 read/write/maintenance errors as cache misses or skipped writes,
with content-free diagnostic logging; caller cancellation still propagates.
Enforce configured capacity without an unbounded cache or an unconditional
full-table sort on every write. Document the precise enforcement behavior.

### API and structure

Separate endpoint normalization, response parsing and request-lifetime handling
from provider-specific prompts/request bodies. Both existing providers retain
their protocol-specific requests. Base URLs and complete endpoints, including
query parameters, must work consistently in validation and requests. Accept only
HTTP(S), reject embedded credentials and accurately identify loopback hosts.

Persist three backwards-compatible flags, all defaulting to true:
`CustomLlmRequireApiKey`, `CustomLlmUseStreaming`, `CustomLlmSendTemperature`.
Disabling authentication must omit Authorization even if the shared key exists.
Connection tests use the same options. Each flag participates in settings
revisions, settings copies and translation-cache identity.

One cancellation/timeout scope covers headers and body reads. Valid SSE deltas
are shown progressively; completion requires a terminal signal. Malformed,
empty and prematurely terminated responses are errors and cannot be cached as
successful translations. Support non-streaming JSON responses. Error messages
contain classifications/status, never raw service bodies, credentials or input.

## Verification and practical limits

Run regression tests against actual source, compile the Windows solution and run
Windows GitHub Actions on the new branch/PR. Add reproducible measurements for
the changed CPU/allocation paths, clearly separating microbenchmarks and mock
interaction timings from real OCR. Research model options using primary sources.
Headless CI cannot establish interactive multi-monitor/DPI or representative OCR
accuracy/latency; leave those explicitly identified, without invented speedups.

## Review focus

Settings changing during a request; missing keys with explicit anonymous mode;
SSE completion before connection closure and mid-body stalls; oversized/truncated
packed records with a usable fallback; blocks with more than eight lines; L2
failure/cancellation after an online result; CTC output ownership and dimensions.
