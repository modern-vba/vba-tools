# VBE debugging architecture

## Status and audience

This is the developer-facing design for the planned VS Code-to-VBE debug
workflow. The feature is not implemented yet. README documents only the
user-visible workflow, requirements, limitations, and data-loss behavior.
Decision rationale remains in ADRs 0019 through 0022 and 0024.

## Ownership boundary

`VscodeExtension` contributes the `vba` debug type, supplies zero-configuration
F5, resolves editor state, and starts the bundled CLI. The bundled
`vba-dev.exe` hosts the internal stdio `VbaDebugAdapter`. The adapter owns
workbook build orchestration, Excel COM and VBIDE automation, breakpoint
transfer, process monitoring, cancellation, and session output.

The VBE owns interactive debugging. The adapter does not mirror break mode,
stepping, stacks, variables, watches, evaluation, Immediate Window content, or
`Debug.Print` into VS Code.

An explicit `vbaTools.devtool.path` override must advertise a compatible debug
adapter contract through `vba-dev capabilities --format json`, following ADR
0007. Capability inspection remains side-effect free; Excel readiness belongs
to Doctor.

## Launch resolution

A launch uses the `vba` debug type and `launch` request. `launch.json` may
specify:

- `project`;
- `document`; and
- `module` and `procedure` together.

`args`, `noBuild`, `stopOnEntry`, and `attach` are unsupported. When no saved
configuration exists, F5 synthesizes a transient configuration from the active
VBA editor without writing `launch.json`.

Before resolving the final target or breakpoints, the extension saves dirty
files in the selected `WorkbookBackedProject`, waits for save participants to
finish, re-reads editor positions, and captures one `DebugSourceSnapshot`.
Ambiguous project, document, module, procedure, or source membership is a
`DebugSetupError`; launch does not show a target picker.

One VS Code window owns at most one active `VbeDebugSession`. A second launch
fails without replacing the current session. Compound and attach sessions are
unsupported.

## Launch lifecycle

Every launch follows these phases:

1. Save selected project sources and capture `DebugSourceSnapshot`.
2. Build the selected document in a dedicated hidden Excel process.
3. Close the build process and open the manifest-defined bin workbook in a new
   dedicated visible `DebugExcelProcess`.
4. Verify and transfer participating breakpoints.
5. Select and run the `DebugTargetProcedure` in the VBE.
6. Keep the session active until its Excel process exits or the session is
   stopped.

The build and debug Excel processes are never reused or attached to an existing
user Excel session. Reusing the build process after programmatic VBIDE edits can
prevent entry into break mode.

The bin workbook is opened directly rather than through a temporary debug copy.
Its workbook name, path, `ThisWorkbook` identity, relative paths, and links
therefore match the generated artifact. Saved changes to that workbook are
disposable and are replaced by the next build.

Excel events are disabled while the debug workbook opens and re-enabled after
breakpoint setup, immediately before procedure execution. Open-time events do
not run. Automation security is lowered only in the dedicated debug process for
the programmatic open and is then restored. Trusted VBIDE access remains
required.

The Excel application is visible before opening the workbook. Open-time modal
prompts remain interactive and have no timeout. The adapter reports that Excel
input is required. Cancelling a prompt that prevents open is a
`DebugSetupError`; a read-only open may continue.

## Breakpoint transfer

Participating breakpoints are user-enabled ordinary VS Code line breakpoints in
the selected `DocumentSourceSet`. User-disabled breakpoints and breakpoints
outside that source set are ignored. Conditional, hit-count, log, and function
breakpoints are unsupported; an in-scope unsupported breakpoint invalidates
launch.

`.bas`, `.cls`, and `.frm` source lines may participate. `.frx` files do not.
`BreakpointSourceMap` uses the reusable `VbaLanguageServer.Syntax` parser core
to exclude export-only attributes and form designer records, then verifies the
remaining source against the generated workbook's `CodeModule`. A fixed line
offset or a second debug-specific parser is forbidden.

Mapping preserves exact physical-line identity. A comment, declaration, blank
line, rejected continuation line, or other non-breakable location invalidates
launch; the adapter does not move to a neighboring line. Colon-separated
statements retain the VBE rule that execution stops at the first stoppable
statement on the physical line.

The generated workbook's actual `DebugCompilationContext` determines active
conditional-compilation branches. An inactive target or participating
breakpoint invalidates setup. Launch configuration cannot override compiler
constants or select a sibling branch.

DAP breakpoints remain unverified while build and VBE setup are pending. An
exact source map and successful native VBE `Toggle Breakpoint` command form the
verification boundary because VBIDE has no breakpoint readback API. After
success, the adapter emits breakpoint-change events with `verified: true`.
A missing, disabled, or failing command aborts the whole launch. There is no
`Stop`-statement, relocation, or instrumentation fallback. Zero participating
breakpoints is valid and does not imply stop-on-entry.

Breakpoint transfer is frozen before procedure execution. Later editor or
breakpoint changes apply only to a restarted or new session.

## Target execution

A `DebugTargetProcedure` is a parameterless public `Sub` in a standard module.
Implicit Public is accepted. Private procedures, Functions, Properties,
class/form/document methods, event handlers, and parameterized procedures are
ineligible. An otherwise eligible procedure remains eligible in an
`Option Private Module`.

The adapter selects the target inside its VBE code pane and invokes the native
`Run Sub/UserForm` command. It does not call external `Application.Run` or
inject a debug-only wrapper module. A missing, disabled, or failing run command
is a `DebugSetupError` with no fallback.

Before resolving or executing a native command, the adapter establishes
`VbeCommandContext`: the project is in design mode, the intended code pane is
assigned as `ActiveCodePane`, the exact line is selected, the code window has
focus, and the VBE is foreground. Localized captions are not command
identities. The currently verified built-in IDs are 51 for Toggle Breakpoint
and 186 for Run Sub/UserForm; Doctor must fail if either control cannot be
resolved and enabled in the established context.

If the VBE reports a compile error before the target begins, the modal error
remains visible and has no timeout. `DebugLifecycleOutput` reports a VBE-input
wait. Dismissing the dialog produces `DebugSetupError` and terminates the
dedicated Excel process; Stop may force-terminate it while the dialog is open.
The reusable parser may support source mapping and diagnostics, but it does not
replace the VBE as compiler authority or provide a fallback execution path.

Both Excel and the VBE are visible, the target code pane remains displayed, and
focus may move away from VS Code. Once execution belongs to the VBE, VBA runtime
errors and break interaction remain VBE concerns. The adapter does not change
error trapping, compile-on-demand behavior, watches, or explicit `Stop`
statements.

VS Code continues to show the session as running even when the VBE is in break
mode. Normal procedure completion does not end the session; the adapter reports
completion and waits for the owned Excel process to exit.

## Process ownership and cancellation

The visible `DebugExcelProcess` is strongly bound to the debug session with an
ownership mechanism such as a Windows Job Object. Explicit Stop, VS Code
shutdown, Extension Host restart, adapter failure, and Restart Debugging
force-terminate it without a save prompt. Every workbook opened in that process
is session-owned and loses unsaved changes on termination.

If the debug workbook actually closes, the adapter force-terminates the
dedicated Excel process and ends the session. Cancelling workbook close leaves
the session active.

Stop is valid in every launch phase:

- completed source saves are not rolled back;
- during build, the hidden build Excel process is terminated and incomplete
  temporary output is removed;
- cancellation before atomic output replacement preserves the previous
  completed bin workbook;
- cancellation after replacement retains the new bin workbook but does not
  start the visible debug process; and
- after visible Excel starts, that process is force-terminated.

Cancellation is reported as cancelled rather than as a setup failure. Restart
Debugging force-terminates the current process and performs the complete save,
snapshot, build, open, transfer, and run sequence again.

## DAP surface and output

The initial adapter supports launch, ordinary line breakpoints, configuration
completion, restart, termination, and output. It does not support pause,
continue, stepping, stack traces, scopes, variables, evaluation, exception
breakpoints, function breakpoints, or attach.

`DebugLifecycleOutput` reports build progress, Excel-input waits, breakpoint
verification, target start and completion, cancellation, setup failure, and
Excel-process exit. It never scrapes VBE runtime state or VBA output.

## Doctor

Normal `VBA Tools: Doctor` includes an active `DebugEnvironmentDiagnostic`.
Using a temporary dedicated Excel/VBE session and temporary standard module, it:

1. verifies trusted VBIDE access;
2. finds the native Toggle Breakpoint and Run Sub/UserForm controls;
3. sets a breakpoint on an executable line in a harmless temporary procedure;
4. runs the procedure and observes `VBProject.Mode` enter break mode;
5. continues execution and verifies a completion side effect;
6. clears the native breakpoint;
7. proves Excel PID capture and strong process ownership; and
8. closes all temporary state.

The probe does not modify persistent project files. Excel or the VBE may appear
briefly. A missing, disabled, or failing required command fails the diagnostic;
there is no fallback.

## Feasibility evidence

On 2026-07-20, a non-persistent probe against the local Windows Excel/VBE
environment established an active standard-module code pane and selected an
executable line. Without explicit code-window activation and foreground focus,
Toggle Breakpoint ID 51 was present but disabled. After establishing
`VbeCommandContext`, IDs 51 and 186 were both enabled.

The probe set a native breakpoint, invoked Run Sub/UserForm, observed
`VBProject.Mode` enter break mode, continued execution, and verified completion
of a public parameterless `Sub` in an `Option Private Module`. It also resolved
a dedicated Excel PID and assigned it to a kill-on-close Job Object. No
persistent workbook or repository file was changed.

A later clean COM quit left the probe Excel process alive until explicit
termination, which confirms that graceful COM cleanup is not a sufficient
session-lifetime guarantee. Strong process ownership remains mandatory.
