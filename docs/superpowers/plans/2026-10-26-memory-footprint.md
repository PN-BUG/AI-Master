# AI-Master lightweight monitoring implementation plan

> Use independent implementers for the ledger, streaming projection, and shared backend; the primary agent integrates and verifies the application.

**Goal:** Keep only statistics, task metadata and readable user input, with one production backend and bounded caches.

**Architecture:** Application-owned runtime shares the Codex connection and local rollout repository. Per-window services retain their own settings and snapshots. Log readers project the required JSON fields while streaming, and the shared-usage ledger saves only on actual state changes.

**Tech stack:** Existing .NET 8 WPF, System.Text.Json, existing Codex app-server protocol.

**Spec:** The user approved all three previously identified optimizations and clarified that the application should consume statistics and limited conversation input. Preserve the existing quota, task/input status, history, clear/restore, migration, and local update behavior.

## Constraints and review focus

- Preserve all pre-existing uncommitted changes and user data; work in the current checkout.
- This is a WPF application, not a Unity project. Validate through its .NET build and smoke program.
- No full-history fetch for passive monitoring; explicit pause actions may fetch active turn identifiers.
- Large images/tool output must be skipped during parsing, including unfinished records.
- Window disposal must not dispose the application-owned client or caches.
- Inactive cache eviction may replay source logs but must never discard persisted historical counts.
- No-change ledger updates must still recompute time-dependent summaries and allow network sync.
- Failed saves must preserve all live state and independent clear/undo baselines.
- No hard memory limit, forced periodic collection, or working-set trimming.

## Tasks

1. [x] Establish current process/replay baselines and add regressions for each changed boundary.
2. [x] Share one application-owned client and rollout caches across the background, dashboard and floating monitor; retain isolated services for tests/custom settings.
3. [x] Add safe bounded/inactive cache eviction and reuse unchanged statistics without losing active writer detection.
4. [x] Stream-project only required rollout fields, preserving input requests/replies, UTF-8, malformed-line recovery, truncation and large mixed text/image records.
5. [x] Skip no-change ledger copies/saves and load persisted state once from UTF-8 while retaining legacy detection.
6. [x] Avoid passive full-turn requests and cap retained display text to a documented useful size.
7. [x] Run complete smoke, shared usage, live task, connection and new memory regressions; review concurrency and lifecycle changes.
8. [x] Update the verified running copy in place, then measure the main process and its child processes under the same workload.
9. [x] Compact duplicated loaded-ledger strings and disable single-file assembly compression; verify the installed application's retained memory again.

## Execution notes

The existing source already includes the first-round buffer release, pooled reads, streamed ledger saving and process-scoped GC conservation. Those changes remain in place. The accepted scope authorizes implementing the complete set without another design approval round.

Validation discovered detailed-parser LRU thrashing across more than 128 eligible files. The added recent aggregate cache reduces an unchanged 180-file fixture from 2,612,712 to 170,984 allocated bytes while retaining the detailed cursor cap. Metadata-only fallback now supplements the latest user message from newly discovered local rollout paths.

Final validation passed the .NET Release build with zero warnings/errors, complete smoke checks, memory regressions, shared usage, immediate task/input monitoring, connection client and refresh checks. Independent source review found no concrete P1/P2 issue. The final 4 MiB discarded partial-record fixture allocates 88 bytes and retains 352 bytes. A real local-log replay (32 tasks / 26 usage sessions) allocates 2,896,640 bytes across five unchanged refreshes in 243 ms; retained projected line state is 2,680,485 bytes. Replay-process heap/working-set metrics are diagnostics, not a substitute for measuring the installed running application.

The first installed v2 process confirms one Codex child instead of two. Its initial working set/private bytes were 463,396,864/290,930,688 before any heap diagnostic. A diagnostic GC heap contains about 46 MB, including a single 49,117-sample persisted ledger with only 1,576 unique sessions, 8 model names and 35,804 baseline keys. A temporary per-load string canonicalization pass can reduce duplicate references without deleting data. Separately, the standalone package enables assembly compression; .NET's documented decompression into memory accounts for additional private runtime images. The default is now uncompressed to favor resident memory over EXE size, while ZIP distribution remains compressed.

Final installed process: PID 23920, started 2026-10-26 13:21:33 UTC+08:00, at the verified original standalone EXE path. Six natural-running samples show working set 293.35–297.51 MiB and private committed bytes 191.29–195.85 MiB, with exactly one Codex child in every sample. The current-turn baseline was 513.77 MiB working set / 334.76 MiB private bytes with two Codex children. A separate OS sample reports 144.46 MiB private physical working set; total working set also includes file-backed shared runtime images. No forced GC or working-set trimming was used on this final installed process. The earlier compressed-process heap diagnostic is not used as final measurement evidence.

The final Release build reports zero warnings/errors. All complete smoke, memory-v2, shared-usage, live-task/input, connection-client and connection-refresh suites passed after ledger compaction and server metadata bounding. The latter preserves short metadata and caps long server titles/previews at 32,768 UTF-16 characters while keeping surrogate pairs and the final request. Independent final source review found no concrete P1/P2 issues. String sharing preserves all serialized ledger values and file content in tests (427 retained string references become 46); temporary startup compaction costs about 720 KiB per 5,000-record fixture. EXE size grows from 78,167,991 to 186,782,713 bytes; the standalone runtime remains included. Original process children 75832 / 66484 / 72200 are no longer present. Logs and OS/runtime measurements are under artifacts/memory-optimization-v2/.
