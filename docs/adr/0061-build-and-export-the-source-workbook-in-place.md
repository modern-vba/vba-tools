---
status: accepted
---

# Build and export the source workbook in place

- Date: 2026-10-09
- Issue: #447
- Partially supersedes: [ADR 0019](0019-separate-build-and-debug-excel-processes.md)
  and the ordinary Build/project Export portions of the
  [workbook-backed project command model](https://github.com/modern-vba/vba-tools/blob/main/tools/vba-dev/docs/adr/0001-workbook-backed-project-command-model.md).

## Context

Ordinary Build previously copied a saved template, imported saved source, and
committed a separate bin workbook. Project Export then extracted that generated
copy. This split the authoring round trip across two workbooks and prevented the
developer from rebuilding an already-open source workbook without replacing its
Excel session. The maintainer approved an in-place source-workbook contract;
Publish, public paired snapshot-output Build, standalone Import and explicit
Export are explicitly unchanged. Debug, Test and final legacy-bin configuration
removal belong to dependent issues rather than this implementation slice.

## Decision

### Source and session authority

The selected document's `templatePath` is its `SourceWorkbook`, conventionally
`src/<document_name>/<document_name>.xlsm`; `sourcePath` identifies exported VBA.
Ordinary `SourceWorkbookBuildCommand` imports admitted saved disk source into
that exact workbook and saves it in place, without producing bin output. CLI
and Command Palette Build neither save nor overlay dirty source-editor buffers.

The existing source capture, syntax/semantic analysis completion gate, encoding
rules, reference selection, module-name preflight and VBE import verification
remain mandatory. Semantic evidence can inspect the invocation-captured saved
package read-only. The actual selected workbook's live project, retained
document-component and reference identities are checked before destructive
replacement, after normalization and after import; saved package evidence does
not authorize mutation of an arbitrarily selected live workbook.

`ISourceWorkbookAutomation` supplies a bounded `ISourceWorkbookSession` rather
than process-lifecycle authority. An exact already-open workbook and Excel
process are borrowed: their displayed windows remain unchanged, Build saves,
and the workbook remains open. A same basename or arbitrary active workbook is
not identity. A closed source file is opened hidden from the start and closed
after processing by its command-owned lifetime. Unrelated workbooks/processes
are never cleanup targets. Borrowed resources grant no hide, close, quit or
force-termination authority, including on timeout, cancellation and failure.

This boundary is separate from `AutomationExcelProcessRuntime`, which retains
its strong private-desktop ownership for copied generation, probes and other
existing consumers. Borrowing is not a fallback mode of that runtime.

Source-workbook Build alone uses a flat VBE import mirror beneath a stable shared
temporary parent. Its invocation-owned source and sidecar files carry a GUID
prefix; only parser-proven designer resource filenames are rewritten to the
paired prefixed `.frx` basename. Component identity, code, unrelated literals,
authoring bytes and binary resource data remain unchanged. Cleanup proves
automation-handle release and STA retirement before deleting exact owned files;
it never deletes the shared parent or another invocation's files, nor requires
the borrowed Excel process to exit. Unproved release/ownership retains affected
files and reports manual guidance. Publish, explicit Import and legacy
Test/snapshot routes retain their existing directory-mirror/process-release
policy.

### Consent and recovery

A clean already-open workbook needs no dirty-workbook confirmation. A dirty
one requires explicit consent before replacement, explaining replacement of
VBE-direct code and saving of other unsaved workbook edits. Declining leaves
it unchanged. Direct CLI Build prompts with terminal `[y/N]`, defaulting to
refusal. `--interactive true` is the default; `--interactive false` refuses
without waiting or changing the workbook when consent is needed. Terminal,
redirection and CI state do not infer that option, and EOF/nonaffirmative input
does not grant consent. VS Code retains its own explicit confirmation UX.

Capability features `build.sourceWorkbook: 1.0` and
`export.sourceWorkbook: 1.0` distinguish this behavior from old bin-semantic
providers without reinterpreting the unchanged output schemas. Managed Build
also requires `invocation.stdinWorkbookConfirmation: 1.0`: the existing hidden
stdin cancellation transport uses one byte-frame reader for the exact
`cancel\n`, `confirm:<requestId>:yes\n`, and `confirm:<requestId>:no\n` frames.
The provider emits a
schema-`1.0` `workbookConfirmation` JSON stderr record carrying a
32-lowercase-hex request ID and warning message. VS Code validates and answers
that exact request using `Import and Save` or cancellation, without replaying
the command. Source-workbook Build disables caller force-kill cancellation;
bounded workbook operations and cooperative cleanup remain authoritative.
Publish and snapshot-output invocation contracts are unchanged.

Before destructive replacement in a borrowed workbook, capture all replaceable
standard/class modules, UserForms with paired binary sidecars, and reference
state needed for restoration. Failed capture starts no replacement.
Workbook-owned document modules remain outside the replacement scope.

On replacement failure or cancellation before Save starts, attempt restoration
of captured code/reference state and leave the user's workbook open without
saving. An independent ten-minute total recovery budget complements existing
per-COM-operation limits; no further recovery mutation is requested after expiry.
Recovery re-exports restored modules and forms and verifies exact source/`.frx`
bytes, module inventory and reference priority against the pre-Build capture.
Mismatch means incomplete restoration. Newly opened hidden Build workbooks close
without saving on this path. Incomplete restoration retains its recovery
materials and reports partial state and actionable manual guidance. This is
neither a whole-workbook transaction nor rollback of arbitrary VBA, cell,
external or concurrent user side effects.

`SourceWorkbookSaveState` explicitly distinguishes `NotStarted`, `Unknown` and
`Saved`. Save is ordinary Build's persistence boundary, not a staged output
replacement. A late cancellation after normal Save and required verification
does not replace success. Required VBE import/live-authority verification occurs
before native Save; it is not a saved-package reopen/hash gate. A later step or
cleanup failure must say that saving completed and identify the failed step.
A Save that may have executed but cannot be confirmed is unknown, never claimed
unchanged or successful.
Completed Save is not automatically undone.

### Project Export and editor conflicts

Project Export reads live VBA from the exact open `SourceWorkbook`, including
unsaved VBE code, or saved contents from the closed source file. It never saves
that workbook or closes its borrowed session. `SourceWorkbookModuleExporter`
uses the existing staging producer boundary; destination validation,
placement, overwrite/stale-source-deletion consent, recovery and completed
destination commitment remain Export policy. Command-owned resources keep
bounded cleanup, while borrowed producer release requires handle release, not
termination of the user's Excel process.

Export neither adds a mandatory dirty-destination-editor stop nor automatically
saves, discards or reloads editor buffers. A later editor save can conflict with
or overwrite exported disk files; the documentation makes that accepted risk
explicit. The CLI's explicit invocation remains overwrite/cleanup consent,
and VS Code retains its pre-launch confirmation.

## Consequences and staged compatibility

- Ordinary Build/project Export now share one source-workbook authoring round
  trip, with explicit session borrowing and truthful save outcomes.
- Publish retains all existing features, exclusions, private copied generation
  and atomic publish-output commitment.
- Public `build --source-snapshot ... --output ...` retains its caller-owned
  output, safety restrictions, source admission and staged generation semantics.
- Standalone Import and explicit Export retain their current contracts.
- Legacy ordinary/no-build Test and snapshot Test/debug consumers retain their
  existing generation routes until #448/#449 are integrated. Retaining a legacy
  `binPath` during this transition must not break them; #450 owns final cutover.
- Historical dedicated-process and staged-output evidence remains truthful for
  the routes it measured. This ADR supersedes only the ordinary Build/project
  Export assumptions, not those separate contracts or historical results.
- Regression coverage must exercise public behavior across open/closed/dirty
  targets, consent/refusal/EOF, capture/import/restore failures, preserved forms,
  references and document modules, Save outcome boundaries, live/saved Export,
  editor input policy, and untouched unrelated Excel sessions.
