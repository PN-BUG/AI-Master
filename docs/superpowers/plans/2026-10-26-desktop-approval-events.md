# Desktop approval events implementation plan

> **For agentic workers:** Use the current delegated transport/reducer tasks and inline service integration. Preserve all pre-existing workspace changes; do not commit or publish.

**Goal:** Display actual pending approval requests from the Codex desktop instance that owns each conversation.

**Architecture:** Passively follow the existing local `codex-ipc` coordination stream. Project version 11 snapshots and revisioned patches into bounded request/status metadata, then overlay those observations onto the existing task feed. Keep rollout-derived status available when desktop evidence is invalidated.

**Tech stack:** .NET 8 WPF, Windows named pipes, System.Text.Json, existing smoke runner. No new dependencies.

**Spec:** The user explicitly selected accurate status from real approval events and continued source integration. Recognize command execution, file change, and permission approval requests; do not infer approvals from shell arguments, prose, auto-review items, or input option labels.

## Constraints and review focus

- Never claim ownership, start/resume a turn, answer a server request, or modify Codex configuration.
- Internal protocol is version-specific; mismatched source/host/version and revision gaps must not produce a status.
- Preserve request-array positions while discarding command text and conversation contents.
- Multiple pending requests must clear independently; approval ends when the actual pending request disappears, before command output.
- Clear observations on owner/transport loss and restore rollout status without retaining the previous approval overlay.
- Use one application-level observer, bounded tracked tasks, and no retained conversation JSON.
- Native/fake-pipe tests cannot prove a real human approval interaction; record this validation boundary.

## Tasks

- [x] Add offline pipe fixtures, observe RED, implement allowlisted follower transport, run GREEN.
  Files: `Services/CodexDesktopIpcClient.cs`, `tests/AIMaster.Smoke/DesktopIpcChecks.cs`.
  Interface: `Start(Func<IReadOnlyList<string>>, Action<JsonElement>, Action<bool>)`, `RequestSnapshot(string)`, `DisposeAsync()`.
- [x] Add snapshot/patch fixtures, observe RED, implement minimal state reducer, run GREEN.
  Files: `Services/CodexDesktopTaskState.cs`, `tests/AIMaster.Smoke/DesktopApprovalStateChecks.cs`.
  Interface: tracked thread IDs, scoped `GetStatus`, `Observe`, `Clear`, `SnapshotRequired`.
- [x] Add feed integration regression, observe RED, integrate runtime ownership and local refresh wakeup.
  Files: runtime, service, task summary, task feed comparer, file monitor, app startup, smoke runner.
  Assertions: actual request produces approval; removal restores running before tool output; stale refresh cannot overwrite; disconnect restores local state; observer shutdown releases resources.
- [x] Run the focused tests and existing smoke suite. Inspect bilingual/theme approval renderings. Read-only probe the real local owner without creating an approval or changing any chat.
- [x] Document the internal-protocol compatibility boundary and the connector prompts which lack an explicit approval discriminator.

## Verification result

The final Release smoke run passed 578 assertions. The visual run passed and produced 36 anonymous PNGs. A read-only live probe connected to the existing desktop owner, received a version 11 snapshot, and confirmed conversation/session identity for this chat; no human approval was created during validation. Independent review findings about candidate capacity, root replacement, and restarted revisions were fixed with regression coverage. The existing transactional updater published locally, verified, replaced, and restarted the running standalone application at its original workspace path. No Codex configuration, installed desktop files, Git commits, or GitHub releases were changed.
