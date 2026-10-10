# vba-debug-adapter

`vba-debug-adapter` is the separately versioned, self-contained Windows x64
debug companion bundled with the VBA Tools extension. It owns native VBE debug
connections and delegates manifest resolution and source preparation to the
exact compatible `vba-dev` selected for the session. Debug uses the selected
source workbook, not a bin or temporary execution copy: reuse its exact open
Excel process, or open it visibly. The workbook/process lifetime is borrowed,
including a workbook newly opened by Debug. It is never automatically saved,
closed or killed. Source editors are never automatically saved either.

F5 and Restart replace the selected workbook's live VBE code with captured
source without asking about unsaved workbook changes. Starting debugging is
the replacement authority; unsaved VBE-direct edits can be overwritten, while
cell edits remain and no tool-initiated Save occurs. An already-open workbook
keeps its exact file, Excel process and existing window state. Build and Test
retain their own dirty-workbook confirmation policies. The adapter requires
`debug.sourceWorkbookPreparation: 2.0` so an older prompting CLI cannot satisfy
the current Debug contract. DAP and command-result schemas are unchanged.

Debug omits the independent syntax/type/argument-error gate, but still requires
readable/representable bytes, safe target/breakpoint mapping, workbook access and
native VBE compile/runtime behavior. Build/Test/Publish and public paired
snapshot-output Build retain their source-analysis requirements.

The extension manages this executable for normal use. Run the following command
only when inspecting its machine-readable compatibility contract:

```text
vba-debug-adapter capabilities --format json
```

To diagnose native VBE debugging readiness independently of any VBA project,
run:

```text
vba-debug-adapter doctor --format json
```

Doctor creates one dedicated temporary Excel/VBE session and proves trusted
VBIDE access, the native breakpoint and Run/Continue command contexts, an
actual breakpoint stop, harmless procedure completion, exact process ownership,
and terminal cleanup. It accepts no project, document, or timeout input, does
not call `vba-dev doctor`, and does not change persistent project state.

Once command handling begins, stdout contains exactly one schema `1.0` JSON
object with ordered stable checks; diagnostic logs use stderr. A complete
overall `pass` or `warning` exits zero. A `fail`, `unverified`, or incomplete
result exits nonzero while preserving the valid JSON report. Each operation has
its own bounded deadline, and terminal cleanup still runs after a failed,
timed-out, or cancelled stage.

Each stdio session uses a random 32-character lowercase hexadecimal ID and a
create-new lease beneath the adapter-owned temporary root. Restart keeps that
session ID, validates a fresh snapshot for the originally bound target, and
captures modules/forms/FRX and references while current execution remains active.
A one-shot, generation-bound
ready record permits commit only after live binding revalidation. Restart then
Reset/import/Run uses the same workbook/PID with fresh source, preserving cells
without saving or asking for dirty-workbook confirmation. Readiness rejection
or cancellation before commit preserves current execution.
Failure or cancellation during companion replacement/verification attempts
captured code/reference recovery and reports retained manual material if
incomplete; it is not whole-workbook rollback. After verified preparation, native
breakpoint/Run errors do not restore old code: they preserve the imported source
for inspection and request bounded Reset/detach without saving.

Stop/disconnect requests bounded native Reset and leaves workbook/code/cells
open and unsaved. Unconfirmed Reset reports manual VBE guidance, never process
termination. Actual workbook close or Excel exit ends the session without
reopening; cancelled close stays active. An actual workbook close is distinct
from observed process exit and cannot invent an exit code. Other workbooks and
processes are never cleanup targets.

After an unexpected adapter exit, the extension invokes the session-ID-only
cleanup surface:

```text
vba-debug-adapter cleanup --session <lowercase-hex-32>
```

The command never accepts a filesystem path. It removes only a workspace whose
PID and process-start-time lease proves stale and whose pending-source-companion
retention gate is absent and inspectable. It retries deletion for five seconds
and reports retained paths. A durable marker armed before source-companion start
blocks outer disposal/reaping until terminal Process+Handle release is proved;
partial tree deletion is never a substitute for this gate. A later startup uses
the same conservative checks. No source workbook is cleanup-owned.

See the root [Debug in the VBE](../../README.md#debug-in-the-vbe) guidance for
the supported user workflow.
