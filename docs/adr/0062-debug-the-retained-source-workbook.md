---
status: accepted
---

# Debug the retained source workbook

- Date: 2026-10-10
- Issue: #448
- Partially supersedes: the disposable Debug workbook, process-termination and
  build-before-replacement decisions in ADRs 0019, 0020, 0021, 0024, 0025, 0027,
  0041, 0044, 0048 and 0058. Their independent Build, Test, Publish, Doctor,
  snapshot-output and historical verification contracts remain unchanged.
- Depends on: [ADR 0061](0061-build-and-export-the-source-workbook-in-place.md).

[ADR 0063](0063-retire-project-workbook-bin-configuration.md) subsequently makes
legacy `binPath` optional and deprecated without changing this source Debug
binding, preparation, retained lifetime, or no-Save contract.

[ADR 0064](0064-debug-dirty-source-workbooks-without-confirmation.md) subsequently
supersedes this ADR's dirty-workbook consent requirement and preparation feature
version for Debug only. The original accepted decision below remains historical;
capture, readiness, recovery, exact binding and retained/no-Save lifetime remain.

## Context

The maintainer selected one source workbook as the authoring and Debug state.
Copying it for each Debug launch, killing Excel on Stop and replacing its process
on Restart would discard the state that the developer now expects to inspect.
Debug also needs native VBE compile/runtime behavior without a separate product
source-error gate. This does not relax byte admission, safe target selection or
the quality gates of other commands.

## Decision

### Separate input, execution and lifetime authority

`DebugSourceSnapshot` remains immutable, complete and generation-bound: selected
document dirty file-backed editors supply their unsaved text, other sources and
form sidecars supply saved bytes. No editor is automatically saved. Parsing for
module identity, target eligibility and exact breakpoint projection is not the
independent syntax/type/argument diagnostics gate, which Debug no longer runs.

The CLI resolves the manifest-selected `SourceWorkbook` through read-only
`prepare-debug --describe`. The adapter independently checks that absolute path
against the selected document metadata. Native attachment binds its physical
file, Excel PID and process UTC start time, not its basename or active workbook.
An exact already-open workbook reuses its process without changing its application
settings. A closed source opens in a new visible application on the caller's
interactive desktop. Its open operation temporarily suppresses open-time events
and restores the application's original settings; a shared existing application
is not borrowed merely to perform this setting-sensitive open. Neither path
grants workbook or process lifetime ownership, including a newly opened Debug
workbook.
The newly created application must prove visible, user-controlled state before
its binding is released: Excel can quit when the last automation reference is
released if `UserControl` is false. See Microsoft's
[Application.UserControl contract](https://learn.microsoft.com/en-us/office/vba/api/excel.application.usercontrol).
Real-Excel verification must release all product references and then independently
prove that this workbook/application still exists; a retained test reference is
not sufficient evidence.

An unidentified target or participating breakpoint fails closed. Conditional
compilation must be proved against the actual live context; an unavailable
custom project-constant authority cannot be replaced by stale saved-package
evidence or an assumption that unknown constants are zero.
Verified live host built-ins and snapshot-local `#Const` definitions can prove a
branch without asserting that the live custom-project constant inventory is
complete. Any consulted unresolved global constant fails that proof.

### Consent, capture and commit

The separate `prepare-debug` companion requires capability
`debug.sourceWorkbookPreparation: 1.0`, closed generation/process binding and
managed `invocation.stdinCancellation: 1.0` transport. It admits raw snapshot
bytes without invoking ordinary/public snapshot Build's diagnostics gate, then
uses the same live module/reference preflight and replacement/recovery capsule
as source-workbook Build. It never saves, closes or quits the source session.

A dirty already-open workbook requires explicit consent to replace live VBE
code, with no implication of saving other edits. The CLI stderr confirmation
nonce is answered through the adapter's separate schema-1.0 DAP
`vba/workbookConfirmation` event and `vba/workbookConfirmationResult` request.
Adapter session ID, generation and one-shot request ID bind that decision.
Unknown, duplicate, stale or malformed responses confer no authority.

After consent, capture replaceable modules, UserForms with exact `.frx` bytes
and reference state before replacement. A failed capture prevents replacement.
The companion's schema-1.0 `debugPreparationReady` control record carries its
generation, workbook and process binding. Its exact nonce waits for
`prepare:<nonce>:ready` or `prepare:<nonce>:declined`. The adapter continues to
read DAP and CLI streams during this wait.

Only a still-current accepted launch/Restart claims commit authority. It
rechecks the exact binding, confirms native Reset, authorizes replacement,
verifies the imported snapshot, sets native breakpoints and runs the target.
Restart uses the same workbook/process with fresh captured sources, preserving
cell changes and never saving. Declining or cancelling before commit leaves the
current execution unchanged; a closed binding is not reopened.

Replacement failure/cancellation attempts code/reference restoration with an
independent ten-minute recovery budget and verification. Report incomplete
recovery and retain its material. This is not whole-workbook rollback and does
not undo arbitrary execution, cell, external or concurrent-user side effects.
Once the companion has completed verified preparation and released the capture,
later native breakpoint/Run errors leave the imported code available for
inspection and request bounded Reset/detach, not an old-code restoration.

### Stop, close and evidence

Stop/disconnect requests native Reset in the exact project and confirms design
mode within a ten-second operation. If confirmation fails, explain the state
and guide manual Reset in the selected VBE; never substitute Excel termination.
The VBE Reset control is application-wide: a non-design project must already
be the exact active project. Changing the active project is not proof of which
project is executing. A different or unproved active project forbids Reset.
The workbook, imported code, cells and other edits remain, unsaved and open.
Native compile/runtime dialogs and break interaction remain VBE concerns.

Actual selected-workbook close ends the session without reopening it or
touching other workbooks. A cancelled close is not proof of closure. Report
`WorkbookClosed`, `ProcessExited` or `Detached` separately; emit a process-exit
code only when it was actually observed. Ordinary procedure completion is not
session termination.
An unexpected lifetime-observation failure is an error, not a successful close
or detached-session receipt. It triggers bounded exact-binding cleanup and
manual guidance when execution cannot be proved stopped.

The source companion has no kill-on-close Job. Cancellation sends one exact
`cancel` frame and awaits terminal exit, both drained streams and owned-handle
release. A twelve-minute active bound is separate from the independent
twelve-minute cooperative completion bound, allowing the recovery budget to
finish. Unproved release retains the child and generation material rather than
claiming that disposing a handle proves process exit. A durable retention gate
is armed before child launch, so outer lease disposal or stale-session reaping
cannot delete pending recovery material after adapter loss.

Native release evidence proves COM/STA/handle release; it does not claim that
the borrowed Excel process ended. `DebugFailureCompletion` continues to preserve
the primary failure, later cleanup faults and retained paths.

## Compatibility and consequences

The adapter advertises `debug.sourceWorkbook: 1.0` and its exact required CLI
map: `build.sourceSnapshot: 2.0`, `debug.sourceWorkbookPreparation: 1.0`,
`invocation.stdinCancellation: 1.0`, `invocation.stdinWorkbookConfirmation: 1.0`
and `sourceSnapshot.activeWindowsCodePage: 1.0`. DAP protocol remains 2.0.
The old adapter `snapshotBuild.diagnostics` feature is no longer a Debug
dependency. Build, Test, Publish and public paired snapshot-output Build keep
their independent source-analysis features and quality gates.

Legacy temporary/owned Debug implementations and their fixtures are not the
source-session lifetime authority. Update user guides, help, domain definitions
and architecture together, and verify source launch/reuse, dirty capture,
consent, native error handling, recovery, Reset, same-process Restart and actual
close behavior before closing this issue. No release is authorized by this ADR.
