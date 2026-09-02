# Private-desktop Excel feasibility

## Status

This document records the bounded feasibility proof in Issue #329. The proof is
test-only: it does not yet change the production launch path for
`AutomationExcelProcess`, and it does not change the visible
`DebugExcelProcess` used by the debug adapter.

The overall verdict is **supported** on the recorded Windows and Excel
environment. The proof passed native object-model binding, representative
workbook and VBE automation, execution-only macro enablement, attempted UI,
pre-existing Excel preservation, and every required terminal path without
caller-desktop exposure. Issue #330 may adopt the lifecycle contract below.
This verdict does not itself change the current production launch path.

## Hidden invariant

For this proof, `hidden` means that no user-facing top-level window owned by the
exact automation-process PID appears on the caller's interactive desktop from
before the owned process's primary thread resumes through confirmed process
exit. A visible snapshot or a transient show event is exposure; making the
window invisible again does not erase it. Windows that exist only on the
invocation-scoped private desktop do not violate this invariant.

`Application.Visible = false`, `SW_HIDE`, delayed hiding, and visual inspection
are not proof of this invariant. The synchronized observer filters by exact PID
and records HWND, desktop, window class, title, visibility, lifecycle phase,
and observation cause. It observes caller-desktop window events and enumerates
both the caller and private desktops at lifecycle boundaries.

## Supported lifecycle contract

A production implementation may adopt this proven contract:

1. Capture the caller's interactive desktop and the pre-existing Excel state.
2. Create a uniquely named, invocation-scoped desktop in the current window
   station. Do not display it with `SwitchDesktop`.
3. Create the kill-on-close Job Object, then create the Excel process suspended
   with both atomic Job assignment and `STARTUPINFO.lpDesktop` set to the
   private desktop's qualified name.
4. Start the exact-PID observer and complete its initial snapshots before
   resuming the primary thread exactly once.
5. Enumerate the private desktop explicitly, bind through the owned Excel
   window's native object model, and verify that `Application.Hwnd` belongs to
   the exact owned PID. Never fall back to caller-desktop enumeration, process
   attachment, or moving a window after launch.
6. Keep macro automation disabled during workbook creation, project mutation,
   initial save, and close. Begin the bounded execution-security phase by
   lowering automation security immediately before opening the disposable
   execution workbook, retain that setting only through `Application.Run`, and
   restore forced-disable immediately after the call returns, before reading
   workbook-owned evidence or closing the workbook. Restore it in `finally` if
   open or execution fails.
7. Capture observer evidence at workbook automation, test execution, VBE
   automation, shutdown, and confirmed process-exit boundaries.
8. On success, command failure, cancellation, timeout, or process loss, release
   COM references, close or terminate only the owned process tree, confirm its
   exit, stop the observer, remove every owned artifact, and close the private
   desktop. Job termination remains authoritative if cooperative Excel cleanup
   does not complete.
9. Verify that every pre-existing Excel process and its window, focus, workbook,
   and lifetime state are unchanged.

An interactive prompt on the private desktop must become bounded, actionable
failure evidence; it must not cause an indefinite wait or justify revealing the
desktop. Failure to satisfy any stage in a production adoption must fail the
command. It does not permit best-effort flicker suppression.

Windows provides `CloseDesktop`, but no operation that deletes a desktop object
by name. Closing the proof's owned `HDESK` releases that handle; the named
desktop object may remain until all references close or logoff ends the
window-station session. The proof therefore
defines complete desktop release as all three of: the owned Job reports zero
active processes, explicit enumeration reports no remaining window on the
private desktop, and `CloseDesktop` succeeds and invalidates the proof's owned
handle. Failure of a later `OpenDesktop` call is not part of the contract.

Job active-process accounting and the bounded process-tree drain entry point are
internal, proof-only instrumentation. They do not replace or change the existing
`DebugExcelProcessOwner.TerminateAsync` behavior, production automation path, or
debug path.

## Recorded environment and result

The following representative-success result was produced on 2026-09-03 by the
dedicated integration proof:

| Field | Recorded value |
| --- | --- |
| Windows | `Microsoft Windows NT 10.0.26200.0` |
| Excel `Application.Version` | `16.0` |
| Excel executable file version | `16.0.20326.20112` |
| Excel executable product version | `16.0.20326.20112` |
| Excel process architecture | `X64` |
| Exact owned PID | `27652` |
| Caller desktop | `WinSta0\\Default` |
| Private desktop | Generated invocation-scoped `WinSta0\\vba-dev-automation-*` desktop |
| Recorded observations | `218` |
| UserForm Host Events | `22`, including `Initialize` and `QueryClose` |
| Reference evidence | Microsoft Scripting Runtime GUID `420b2830-e718-11cf-893d-00a0c9054228` |
| Workbook evidence | Added standard module persisted after save and reopen; `UnitTestMain` wrote `private-desktop-executed` into the workbook |
| Automation security | `3 -> 1 -> 3`: forced-disable, execution only, forced-disable restored |
| Result | Representative success test passed |

The PID and generated desktop name are per-run evidence, not stable
identifiers. The test started the observer before primary-thread resume,
launched Excel suspended on the private desktop with atomic Job ownership,
bound the native object model through explicit private-desktop enumeration,
and verified the returned application HWND against the exact PID. It created,
saved, reopened, mutated, and closed a disposable macro-enabled workbook;
exercised `VBProject`, UserForm Host Event, native code-pane, and reference
automation (the current reference path requires no project-window interaction).
It lowered automation security immediately before reopening the disposable
execution workbook, retained it through `UnitTestMain`, and restored
forced-disable before reading the workbook-owned evidence or closing the
workbook. It observed no caller-desktop exposure and restored the initial
Excel-process, bootstrap-artifact, and proof-artifact sets.

The attempted-UI test detected a modal owned by exact PID `34948` at HWND
`0x1451030`, class `#32770`, with its unique private title on the private desktop
during `TestExecution`. It converted that condition to actionable failure,
completed end-to-end cleanup in 48 milliseconds, retained 246 observations, and
recorded no caller-desktop exposure.

The terminal-mode test exercised timeout with a genuinely blocking
`Application.Run` invocation. A run-unique file marker proved that VBA entered
the macro before the timeout, cancellation, or injected process loss was
accepted; an incomplete or merely queued COM call is not sufficient. The test
does not reuse the attempted-UI case as a timeout proxy. It also passed injected
command failure, cooperative cancellation of another blocking
`Application.Run`, and unexpected root-process loss. The per-mode evidence was:

| Terminal mode | Exact owned PID | Recorded observations |
| --- | ---: | ---: |
| Timeout | `33512` | `150` |
| Command failure | `36908` | `186` |
| Cooperative cancellation | `37104` | `150` |
| Unexpected process loss | `45408` | `150` |

Success, command failure, cancellation, timeout, and process loss all drained
the exact Job process tree to zero, left no private-desktop windows, closed and
invalidated the owned desktop handle, stopped the observer, and removed the
bootstrap and temporary proof artifacts without caller-desktop exposure.

The explicit baseline test reproduced both the bootstrap and target-workbook
leaks for exact PID `25268` on the caller's interactive desktop. Both exact-PID
observations referred to HWND `0x7A0934`. The target observation was accepted
only after the visible `XLMAIN` title contained the run-unique
`vba-dev initial target <GUID>` identity and while the target `SaveAs` task was
still incomplete (`targetSaveWasBlocked=True`). A completed or faulted save and
an already-visible bootstrap window cannot satisfy that condition. The proof
then automatically terminated the owned Job. The baseline retained 187
observations and cleaned its staging directory and artifacts. This is
production-leak evidence, not the pre-existing Excel control test.

The separate interactive-control test deliberately made its caller-desktop
fixture visible only after hidden setup. Exact PID `25372` remained foreground
at HWND `0x7403AC` while a concurrent private-desktop probe ran. Across 147
continuous samples and 122 observer records, its visibility, foreground focus,
complete visible exact-PID top-level window set, workbook state, selection, and
lifetime did not change, and no visible exact-PID lifecycle or foreground event
occurred during the private probe. Hidden native/OLE housekeeping windows are
not user-facing perturbations; a later show or foreground event would still
fail the proof. The control and all artifacts were then cleaned.

## Evidence matrix

| Required evidence | Status | Recorded proof |
| --- | --- | --- |
| Exact-PID private-desktop launch, atomic Job assignment, pre-resume observer, and native object-model binding | Supported | Representative success test; PID `27652` and application HWND agreed, with 218 observations and no caller exposure |
| Current interactive-desktop bootstrap and target-workbook leak | Supported | Production baseline PID `25268` reproduced both leaks on HWND `0x7A0934`; the exact-PID target title carried a run-unique identity while `SaveAs` was contemporaneously blocked, then exact cleanup completed automatically |
| Workbook create/open, `VBProject` access, project mutation, save, reopen, close, and release | Supported | A standard module added to the disposable `.xlsm` persisted after save and reopen |
| Execution-only macro enablement and `UnitTestMain` through `Application.Run` | Supported | Security transitioned exactly `3 -> 1 -> 3`: lower immediately before execution-workbook open, run the macro, then restore before evidence read and close; the macro wrote `private-desktop-executed` |
| VBE-dependent UserForm Host Event catalog and reference probes, including required native code-pane or project-window interaction | Supported | 22 UserForm events exercised native code-pane automation; the Scripting reference GUID was observed and the current reference path requires no project-window interaction |
| Success and command-failure cleanup | Supported | Exact Job process tree reached zero; desktop enumeration was empty; the owned `HDESK`, observer, and artifacts were released |
| Cooperative cancellation and timeout cleanup | Supported | Both paths interrupted a blocking `Application.Run` and released exact ownership within their bounds |
| Unexpected process-loss cleanup | Supported | Injected root-process loss still drained the complete owned Job tree and completed exact cleanup |
| Attempted workbook, add-in, or component UI | Supported | Private modal PID `34948`, HWND `0x1451030`, class, title, desktop, and phase were recorded; end-to-end cleanup completed in 48 milliseconds |
| Pre-existing Excel preservation | Supported | Separate control PID `25372` retained visibility, foreground HWND `0x7403AC`, its complete visible exact-PID top-level window set, workbook state, and lifetime through 147 continuous samples |
| Repeatability and complete environment record | Supported | Dedicated opt-in command and exact environment/output records require no manually timed visual observation |

Issue #330 is unblocked. Production behavior remains unchanged until that issue
adopts the supported lifecycle contract. A future deterministic failure on a
supported environment must fail closed and record its first failing stage; it
must not weaken `hidden` or fall back to the interactive desktop.

## Running the proof

Run only the isolated opt-in category from a configured Windows host with Excel
and trusted VBIDE access:

```powershell
npm run test:private-desktop-excel-feasibility
```

The command does not run the general Windows/Excel integration suite,
`InitialWorkbookCreation`, the debug-adapter suite, or the release gate. The
production-baseline case deliberately reproduces current caller-desktop
exposure, so Excel and a Save As dialog may be visible for up to five seconds
during that one case. No user action is required: the proof automatically
terminates the exact owned Job and removes its staging artifacts at the bound.
This is required evidence, not an allowed fallback for any private-desktop case.
The separate interactive-control case deliberately displays its control Excel
instance while verifying that the private probe does not disturb it. Do not
include this opt-in proof in ordinary unattended test runs until these visible
cases are removed or separately gated.

## Debug asymmetry

Automation and debugging have different visibility requirements. A production
`AutomationExcelProcess` should be private-desktop isolated when no user
interaction is required. A `DebugExcelProcess` must remain on the caller's
interactive desktop because Excel, the VBE, the selected code pane, modal
prompts, and break interaction are intentionally user-facing. This proof does
not route debug-adapter launch, Doctor, or VBE command handling through the
private desktop.
