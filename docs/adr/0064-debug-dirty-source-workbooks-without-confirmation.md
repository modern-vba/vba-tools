---
status: accepted
---

# Debug dirty source workbooks without confirmation

- Date: 2026-10-10
- Issue: #451
- Partially supersedes: the Debug dirty-workbook consent requirement and
  preparation feature version in
  [ADR 0062](0062-debug-the-retained-source-workbook.md).

## Context

Source-workbook Debug already reuses the exact selected open workbook and its
Excel process, retains the session after Stop, and performs no tool-initiated
Save. The earlier decision required an additional confirmation before replacing
live VBE code when the workbook had unsaved changes. The maintainer now wants
F5 and Restart to proceed directly against that open workbook, including its
unsaved state, without this extra interaction.

This changes Debug replacement authority, not workbook selection, source
admission, recovery, execution safety or lifetime ownership. Build can persist
other workbook edits and Test runs arbitrary test procedures; their separately
accepted dirty-workbook consent policies remain unchanged.

## Decision

### The Debug request authorizes live code replacement

An accepted launch or Restart request authorizes importing the complete captured
source into the exact selected source workbook. A dirty workbook does not
produce a terminal or GUI confirmation, a DAP workbook-confirmation event or a
non-interactive refusal. This applies equally to F5 and Restart. Never select a
workbook by its active-window status or basename: the manifest-selected physical
file, Excel PID and process UTC start time remain the binding authority.

An already-open workbook uses its existing file, Excel process and window state.
Captured source can overwrite unsaved VBE-direct code. Existing cell edits remain;
Debug is not whole-workbook rollback and does not undo arbitrary macro effects.
The tool never saves the workbook or source editors before preparation, after
import, after execution, on Restart or on Stop. A target procedure may itself
save or cause other effects; no-Save is a tool policy, not a sandbox guarantee.

Closed-source Debug remains unchanged: open the selected source visibly with
temporary open-time event suppression, restore the original application settings
and retain the workbook/process without taking lifetime ownership. Unrelated
workbooks and Excel processes are never Reset, Save, Close, Quit or termination
targets.

### Capture, readiness and recovery remain mandatory

Removing the dirty-workbook prompt does not bypass complete immutable snapshot
admission, strict encoding, safe target/breakpoint identification, live
project/reference/component preflight or pre-replacement recovery capture.
Failed capture still prevents replacement. Debug still omits only the
independent source-error analysis gate; native VBE compile/runtime behavior
remains authoritative.

During Restart, capture completes while current execution remains active.
Only the current generation-bound readiness continuation, revalidated against
the exact workbook/process binding, permits bounded native Reset, replacement,
verification, breakpoint transfer and Run. Readiness rejection or cancellation
before commit preserves current execution. A stale, closed or replaced binding
cannot be revived or silently retargeted.

Replacement or verification failure/cancellation still attempts bounded
code/reference restoration from the captured modules, UserForms and exact
`.frx` bytes. Incomplete recovery retains its material and reports manual
guidance. Once verified preparation releases that capsule, later native Run or
breakpoint failure preserves the imported code and requests bounded Reset/detach
rather than restoring old code. Existing cancellation, Reset, cooperative
companion release, retention and scratch-cleanup contracts remain unchanged.

### Explicit compatibility admission

`debug.sourceWorkbookPreparation` becomes `2.0`. The CLI provider, adapter's
exact required CLI feature map and extension-owned capability requirements
change together. A provider advertising `1.0` can still prompt and must be
rejected before snapshot capture or workbook preparation. Tool release versions,
CLI command/result schemas, adapter feature `debug.sourceWorkbook: 1.0` and
the schema-1.0 DAP extensions do not change merely because this feature changes.

`prepare-debug --interactive` remains accepted for command-line compatibility
only. Both true and false perform the same no-prompt Debug preparation; neither
grants or refuses dirty-workbook consent. Cooperative cancellation and the
one-shot readiness continuation remain active.

The existing `invocation.stdinWorkbookConfirmation: 1.0` transport and DAP
confirmation handlers remain compatibility surfaces. Current Debug preparation
does not emit `workbookConfirmation`; their retained implementation does not
reintroduce a dirty-workbook prompt. Build and Test keep their current
confirmation requests and `--interactive` behavior.

## Consequences and verification

User documentation must explicitly warn that Debug replaces unsaved VBE-direct
edits while retaining cell changes and never initiating Save. It must not claim
that a dirty workbook requires an extra confirmation for launch or Restart.
ADRs 0061 and 0026 remain the Build and Test policy authorities; ADR 0062's
other source-binding, recovery, execution and lifetime decisions remain in force.

Verification must cover dirty initial launch and same-process Restart without
confirmation or Save, both `--interactive` values, preserved unsaved cells and
saved workbook bytes, exact binding and retained window/process lifetime. It
must separately retain capture/recovery/readiness/cancellation/Reset safety
regressions and Build/Test consent regressions. Capability tests must reject an
older `1.0` preparation provider. No release or integration is authorized by
this ADR.
