# VBE debugging architecture

## Status and audience

This is the developer-facing implementation and maintenance contract for the
VS Code-to-VBE debug workflow. README documents only the user-visible workflow,
requirements, limitations, and data-loss behavior. Decision rationale remains
in ADRs 0019 through 0022 and 0024.

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

## Capability and packaged-extension contract

`vba-dev-contract.json` is the extension-owned compatibility requirement. Its
`contractVersion` versions the CLI command contract independently from the
extension and CLI release versions. `commandSchemaVersions` pins each command
output consumed by the extension. `debugAdapterProtocolVersion` pins the
extension-to-adapter additions beyond the base DAP contract; the current value
is `1.1`.

`vba-dev capabilities --format json` must report the matching contract version,
all required command schema versions, and this debug adapter capability:

```json
{
  "debugAdapter": {
    "protocolVersion": "1.1",
    "transport": "stdio",
    "command": "debug-adapter"
  }
}
```

The extension validates this response before using either the bundled CLI or an
explicit path override. There is no PATH discovery or compatibility fallback.
Capability inspection does not start Excel or probe VBE readiness.

The VSIX must contain `package.json`, `client/out/extension.js`,
`vba-dev-contract.json`, and the self-contained
`bin/vba-dev/win-x64/vba-dev.exe`. `package.json` must point `main` at the
compiled extension entry point, activate dynamic `vba` configuration
resolution, contribute the launch selector schema and user commands, and omit
an attach schema. Packaging verification inspects those contributions, checks
the VSIX file list, executes the bundled capabilities command, and starts the
advertised adapter entry point with `--stdio`.

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

### Transport and request ordering

The CLI entry point is `vba-dev debug-adapter --stdio`. DAP messages use the
standard `Content-Length` framing. A frame write may be cancelled while waiting
for the connection write gate. After the gate is acquired, its header and body
are written as one serialized buffer and flushed without cancellation so a
restart or disconnect cannot leave a partial frame on stdout.

The extension resolves and saves the selected project before the adapter starts.
The launch request carries one immutable `sourceSnapshot` with schema version 1;
the adapter does not reread editor buffers. DAP breakpoint responses remain
unverified until the build, exact source mapping, and native command complete.
Setup and monitor work run in supervised background tasks. A response, event, or
monitor transport failure terminates the adapter and releases process ownership
without waiting for stdin to close.

### Restart preparation protocol

Protocol 1.1 makes native VS Code Restart a two-party transaction:

1. The resolved launch configuration contains
   `__vbaRestartPreparation: { protocolVersion: 1, id }`. The identifier is bound
   to the canonical selected project root.
2. On a DAP `restart` request containing that marker, the adapter keeps serving
   requests but parks the restart and retains the old session.
3. The extension saves only dirty exported sources in the newly selected
   project, retaining every save that completed before cancellation or failure.
4. The extension sends `vba/restartPrepared` with the original
   `restartRequestSequence`, matching `preparationId`, `success`, and an optional
   failure message.
5. Only a matching successful result can commit the restart. The old session is
   then terminated and a complete fresh snapshot, build, open, breakpoint
   transfer, and run begins.

A stale request sequence cannot consume the pending preparation. A malformed or
wrong preparation identity fails that restart. Disconnect, terminate, session
release, or notification-transport failure cancels pending preparation and ends
the owned session. If the old Excel process exits before restart commit, process
exit is authoritative and no replacement process starts. Adapter clients that
do not supply the private preparation marker retain immediate-restart behavior.

Normal target completion is output, not a terminal DAP event. The owned Excel
process exit, explicit termination, or an unrecoverable adapter failure claims
the single terminal transition.

## Failure categories

Failures are classified by the boundary that can act on them:

- `VbaDevCompatibilityError`: the configured or bundled CLI cannot satisfy the
  extension-owned command or debug-adapter contract. No adapter or Excel process
  starts.
- `VbaDebugSelectionError`: the extension cannot select one project, document,
  eligible procedure, or valid participating breakpoint from saved source.
- `DebugLaunchBusyException`: the VS Code window or selected project already
  owns an incompatible launch. The active session is retained.
- `DebugSetupException`: build, workbook open, source verification, command
  context, native command, compiler, or VBE setup could not establish the
  requested session. Any owned process and incomplete temporary output are
  cleaned.
- cancellation: F5 cancellation, Stop, Restart, disconnect, or shutdown is
  reported as cancellation rather than setup failure. Completed source saves
  and atomically replaced bin output are not rolled back.
- input wait: an interactive Excel or VBE modal is a lifecycle state, not a
  failure or timeout. User dismissal may subsequently produce setup failure;
  Stop may force-terminate the process immediately.
- transport or lifecycle failure: malformed protocol, failed DAP output,
  adapter death, Extension Host death, or unexpected parent exit closes strong
  ownership and terminates the session-owned Excel process.
- owned-process exit: normal Excel exit is the final session outcome. Procedure
  completion alone is not process exit.

None of these categories permits breakpoint relocation, `Stop` insertion,
instrumentation, `Application.Run`, generated wrappers, caption matching,
`SendKeys`, or attachment to an existing Excel process.

## Test seams

The TypeScript client isolates editor and workspace state behind
`VbaDebugConfigurationHost`, process capability inspection behind
`ProcessRunner`, and VS Code lifecycle integration behind small session and
notification interfaces. Client tests pin selection, scoped save, restart
identity, lifecycle cancellation, configuration contributions, and CLI
compatibility. Extension Host tests prove production F5 resolution and that only
the selected project is saved.

The application layer isolates build and VBE work behind
`IDebugWorkbookBuilder`, `IVbeDebugSessionFactory`, `IVbeDebugSession`, and the
debug lifecycle sinks. Deterministic fakes control every save/build/open/modal,
breakpoint, run, process-exit, restart, and cleanup boundary. Infrastructure
tests substitute the Excel process API, Job Object, modal-window API, foreground
window activation, and COM dispatcher without weakening production ownership.
DAP tests use in-memory byte streams and held-open input to verify framing,
ordering, cancellation, and background-task failure.

Opt-in `WindowsExcelIntegration` tests use real Excel, VBIDE, native command IDs,
Job Objects, modal prompts, DAP Stop/Restart, adapter death, and Excel-initiated
exit. They are serialized and require
`VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1`. Packaging tests separately inspect the
VSIX surface and execute the bundled compatibility and adapter entry points.

## Maintenance guidance

- When a consumed CLI command or adapter extension changes, update
  `vba-dev-contract.json`, the CLI capabilities response, client validation,
  packaging fixtures, compatibility tests, and this document together. Bump
  `debugAdapterProtocolVersion` for an incompatible adapter contract change.
- Keep restart preparation project-bound and sequence-bound. New restart fields
  require deterministic tests for stale, malformed, cancelled, process-exit,
  and transport-failure ordering before Windows coverage.
- Resolve VBE commands by stable built-in ID only after establishing
  `VbeCommandContext`. A command-ID or context change requires Doctor,
  deterministic automation, and real Excel integration updates; never add a
  localized-caption or `SendKeys` fallback.
- Keep source mapping in the reusable syntax core and verify generated
  `CodeModule` content. Do not introduce fixed offsets, neighboring-line repair,
  or a debug-only parser.
- Establish PID and kill-on-close Job ownership before workbook open, prompts,
  breakpoint transfer, or target execution. Every new terminal path needs a
  test proving Job disposal and launch-guard release.
- Keep README limited to user actions, prerequisites, supported behavior,
  interactive waits, and data-loss warnings. Put protocol, command identity,
  seam, and maintainer details here or in an ADR.
- Run `npm run verify:release` for the non-Excel release surface. On a configured
  Windows/Excel host, run `npm run verify:release:windows-excel` and the packaged
  VSIX smoke in `docs/release.md`.

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

Probe startup failures carry explicit cleanup evidence. A categorized failure
may report cleanup as passing only when `CleanupVerified` is true and no
`CleanupException` was recorded. A missing session is not cleanup evidence:
uncategorized startup failures therefore fail the cleanup diagnostic. Hidden
workbook creation removes its temporary directory on cancellation, and a
cleanup failure during cancellation is preserved separately from the timeout.
Startup adapters preserve `DebugSetupException` and `OperationCanceledException`
classification while attaching this evidence. If COM activation resolves to an
Excel PID that existed before startup, ownership is rejected and only the COM
reference is released; Doctor and debug launch never call `Excel.Quit` or kill
that user-owned process. When no exact process owner was established, cleanup
is unverified unless the failure proves that no temporary process was created.

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
