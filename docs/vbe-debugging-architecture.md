# VBE debugging architecture

## Status and audience

This is the developer-facing implementation and maintenance contract for the
VS Code-to-VBE debug workflow. README documents only the user-visible workflow,
requirements, limitations, and data-loss behavior. Decision rationale remains
in ADR 0062 for source-workbook Debug, with historical decisions in ADRs 0019
through 0021, 0024, 0025, 0027, 0040 and 0041. ADR 0022 is superseded.

## Ownership boundary

VscodeExtension contributes the `vba` type, captures editor state and starts
the separate stdio `VbaDebugAdapter`. The adapter owns DAP, native VBE commands,
exact source binding, observation and connection cleanup, not the persistent
workbook or Excel lifetime. [ADR 0062](adr/0062-debug-the-retained-source-workbook.md)
supersedes older disposable/copied Debug decisions only for this route.

VbaDev resolves the manifest through read-only `prepare-debug --describe`.
Managed `prepare-debug` admits raw snapshot bytes, obtains dirty-code consent,
captures modules/forms/references and imports without Save/Close/Quit. It does
not host DAP or generate another execution workbook. Ordinary Build, Test,
Publish and public paired snapshot-output Build retain their independent
source-analysis and ownership/commit policies.

A create-new `DebugWorkspaceLease` issues generation capability for source
scratch only. Exact inventory, physical identity and SHA-256 seal source files
created relative to pinned parents without following reparse points; verify
them after companion completion. Never adopt the source workbook as generated
output or cleanup-owned state. Native binding verifies its exact physical file,
PID and UTC start time. Reuse an existing exact workbook or open the closed
source visibly; neither grants workbook/process lifetime ownership.

Arm durable session retention before child start. Clear it only with proved
terminal Process and Handle release. Lease disposal/reaping must not delete
pending recovery material. The source companion has concurrent stream drains,
cooperative cancellation and independent active/completion bounds, not
kill-on-close ownership. No VbaDev implementation assembly is loaded by the
adapter; CLI and adapter compatibility stay independently validated.

ADR 0039 also applies this one-way provider boundary to project and test
dependencies. `VbaDev` never references or launches the extension, language
server, debug adapter, or their tests and harnesses, including build-only
references and linked compile source. Both the adapter and VbaDev consume
product-neutral `VbaTools.Syntax`; parser reuse creates no dependency between
those products. Other consumers may use an explicitly public VbaDev-owned
non-command library, but command orchestration always uses the public process
contract.

The language server and debug adapter also consume the product-neutral
`VbaTools.ProjectMetadata` foundation described in ADR 0040. One reader accepts
caller-fixed package bytes and supplies immutable project and compilation
facts under the same strict workbook topology, LCID, LCIDINVOKE, and LIBFLAGS
rules. Its private implementation owns CFB and MS-OVBA parsing. The debug
adapter still owns .xlsm file I/O and sharing, settings and setup-failure
projection, and the exact VBA-part identity comparison around workbook open.
The language server's whole-package identity and content fence remain separate;
the metadata foundation performs neither file capture nor lifecycle checks.

UserForm Event discovery is a separate extension-owned lifecycle and never runs
through the debug adapter. Trusted activation asynchronously invokes the
environment-scoped `vba-dev host-event list --format json` at most once, using
one generated blank workbook and temporary UserForm in a private-desktop
`AutomationExcelProcess`, and sends only the complete current catalog to the
language server. Debug start, Restart, break state, and adapter Doctor neither
trigger nor wait for discovery, while synchronous editor requests consume
committed catalog state without starting Excel.

Non-debug Excel automation has a stricter visibility boundary. Every
`AutomationExcelProcess` used by copied generation, Test, Publish or probes
uses the private-desktop contract and current evidence recorded in
[Private-desktop Excel feasibility](private-desktop-excel-feasibility.md).
The shared production path creates Excel suspended on a unique
invocation-scoped desktop, begins exact-PID observation before primary-thread
resume, binds through explicit private-desktop enumeration, and retains the
desktop through complete Job-tree exit. It never calls `SwitchDesktop`, never
falls back to the caller's interactive desktop, and fails closed with available
PID, HWND, desktop, class, title, and lifecycle-phase evidence. Desktop release
means zero active Job processes, no remaining private-desktop window, and
successful closure and invalidation of the owned `HDESK`. Windows has no
delete-desktop operation, so the desktop object's name may remain until all
references close or logoff ends the window-station session.

Within `VbaDev`, `AutomationExcelProcessRuntime` is the sole lifecycle and
extension authority for these automation scenarios. Its adapters establish the
owned host, open or create the workbook, and perform cooperative COM cleanup
with the runtime's cleanup grace. The runtime withholds a successful result
until exact process-tree release, private-desktop release and STA dispatcher
retirement are proved. Bounded workbook sessions provide operations without
`IDisposable` or another independent disposal path, and become unusable before
cleanup begins. The unreachable generic Open/Create/build-owned Open and
workbook-count-based best-effort termination implementation has been deleted.
Current debug inspection keeps read-only process snapshot capture in its debug
process adapter; it cannot use that snapshot to adopt a generic workbook owner.
Startup cleanup and intrinsic UserForm Event inspection retain their exact
strong process owner.

VbaDev command adapters consume the immutable terminal disposition described in
[ADR 0055](adr/0055-centralize-workbook-terminal-disposition.md). Analysis fixes
failure priority, trusted cancellation and the separate process/dispatcher
proofs without owning cleanup or command commitment. Recognized primary nested COM receives
the command's friendly guidance with its original stage context. This does not
change the visible debug process's independent failure-completion policy.

The subsequent `DebugExcelProcess` deliberately does not use this path. Excel,
the VBE, selected code pane, modal prompts, and breakpoint interaction remain
visible on the caller's desktop through an exact borrowed source-workbook
binding. The adapter owns its COM/STA connection resources, not Excel or the
workbook lifetime.

The snapshot directory is authoritative rather than an overlay. It contains the
complete recursive `.bas`, `.cls`, and `.frm` inventory plus same-directory
`.frx` sidecars as actual bytes and preserves the original
`DocumentSourceSet`-relative layout as provenance. Build identity nevertheless
remains flat by exported file name. `VbaDev` fixes those bytes in
invocation-internal scratch, applies normal build inventory validation, and does
not compare them with persistent source. The source template, references, and
other manifest-owned inputs still come from the selected project document.

The extension produces that complete inventory from disk plus every dirty
file-backed source editor whose canonical URI is inside the selected source
set. A dirty editor replaces disk bytes or adds an in-scope path that does not
yet exist on disk. Pathless documents cannot participate; if one is the target
or owns a participating breakpoint, selection asks the user to save it under
the source set. At capture start, the extension fixes the disk inventory and
then-open editor set, text, URI, and encoding. It reads each selected clean
source and sidecar once without a final inventory or editor-version check and
without automatic retry. Later changes apply only to a later invocation; an
inventoried disk path that cannot be read fails capture. This producer behavior
never becomes `VbaDev` editor integration.

For clean source and `.frx` sidecars, snapshot capture copies exact disk bytes.
For dirty source, the editor-facing snapshot producer encodes the captured text
with `TextDocument.encoding`, including its BOM policy. Snapshot v2 support is
limited to BOM-marked UTF-8, BOM-marked UTF-16 LE or BE, and the active
Windows ANSI code page without BOM. The producer calls `GetACP` once at capture
start rather than inferring UI language or current culture; ACP 65001 is
canonical `utf8`, never `windows-65001`. A dirty editor encoding without a BOM
is accepted only when its code
page equals that fixed ACP. Every clean and dirty text source must strict-decode
and re-encode to its original bytes before Excel starts. Detection checks a
recognized BOM first, then only the strict fixed ACP for bytes without a BOM.
BOM-less UTF-8 requires ACP 65001. Any
unsupported or lossy conversion is a `VbaDebugSelectionError`; capture does not
save the file, substitute characters, or guess. `.frx` remains binary-only, and
the accepted snapshot bytes remain authoritative and unchanged.

That raw-byte assumption was subjected to a real-Excel compatibility gate
because `VBComponents.Import` accepts a file name but exposes no encoding
parameter. The supported Windows Excel host imported equivalent non-ASCII
modules encoded as the active ACP, BOM-less UTF-8, BOM-marked UTF-8, and
BOM-marked UTF-16 LE and BE. The gate covered `.bas`, `.cls`, and `.frm` plus an
exported `.frx` sidecar, while excluding document modules that
`VBComponents.Import` does not replace. It compared `CodeModule` text with
`VbaCodeModuleProjection.CodeModuleLines` both immediately after import and
after save, close, and reopen, verified UserForm and sidecar-backed control
state, and recorded the VBE export encoding. Its result selected either direct
raw import or the explicit shared import representation below; no implicit
fallback was permitted.

The initial gate used Excel 16.0 with ACP 932. Raw CP932 passed for `.bas`,
`.cls`, and `.frm` plus `.frx` both immediately and after save/reopen.
BOM-less UTF-8 imported but corrupted non-ASCII code, UTF-8 BOM corrupted the
component header and caused class and form inputs to become standard modules,
and UTF-16 LE and BE were rejected. VBE export produced CP932 text and the
expected `.frx` sidecar. Therefore the current raw-byte statement above is not
implementable for non-ACP snapshot text. Debug source preparation and public
snapshot builds instead use the shared `VbaDev` import representation below.

Every `VbaDev` command that reaches `VBComponents.Import` creates an
invocation-internal `VbeImportSourceSet`, regardless of whether its input is a
persistent `DocumentSourceSet` or a `BuildSourceSnapshot`. `VbaSourceAdmission`
captures `GetACP` once. Ordinary project source, explicit-import source, and
materialized snapshot source use a recognized BOM or, without a BOM, only the
strict fixed ACP. The ACP interpretation wins a dual-valid byte sequence;
there is no UTF-8 probe. DAP text tokens are separately revalidated before
materialization, not passed into VbaDev as a proof. Each
source must re-encode to its original bytes, then strict-round-trip the decoded
Unicode text through the fixed ACP before Excel starts. `.frx` remains
byte-exact beside the staged `.frm` with the same base name. Unsupported
encoding, unrepresentable characters, and best-fit-only conversions fail
without starting Excel. The staged mirror is removed with `VbaDev` invocation
scratch and never rewrites the persistent source, snapshot, or DAP payload.
Build preflight, import projection, and built-test source locations consume the
same immutable admission. Ordinary and snapshot test materialization returns
the exact admission paired with the committed workbook, and `VbaDev` copies its
module, callable-range, and persistent-URI facts into an immutable
`ExecutedSourceIndex` before execution. Result resolution does not reread
scratch, caller, or persistent source, obtain ACP, decode, or parse again. A
no-build run has no proved admission, inspects no project source for navigation,
always omits locations, and emits one fixed non-failing warning after a
completed run.

After each import and before workbook save, `VbaDev` builds
`VbaCodeModuleProjection` from the strict-decoded Unicode source and requires
the component name, kind, line count, and every projected `CodeModule` line to
match exactly. Export-only class `VERSION` and `BEGIN`/`END` headers,
`Attribute` records, UserForm designer records, and the synthetic terminal
newline are excluded from the projected code. The known UserForm leading empty
line is included. The contract assumes no automatic VBE insertion or
normalization beyond this projection; every unmodeled difference fails before
save.

Runtime verification stops at component identity, kind, and projected code.
`VbaDev` does not re-export every imported component or present a partial set of
COM-visible metadata properties as exhaustive verification. Export-only
metadata, UserForm designer state, and `.frx` content remain authoritative to
`VBComponents.Import` and are covered by representative real-Excel import,
save, close, and reopen integration fixtures. This coverage detects regressions
in the supported import path rather than proving arbitrary form and metadata
state during each command. The fixtures compare expected attributes, control
structure, selected properties, and readable sidecar-backed binary values
semantically both immediately after import and after reopen. They do not require
whole re-exported files or `.frx` bytes to remain identical when VBE has only
reordered records, materialized defaults, or changed equivalent serialization.

Commands do not close and reopen a generated workbook solely to repeat the
component and projected-code checks after save. Save failure prevents output
commit or explicit-import persistence. The release-blocking real-Excel fixture
owns save/close/reopen regression coverage, avoiding a second workbook-open
lifecycle, event and prompt surface, and open deadline in every command.

The minimum real-Excel fixture uses valid non-default class and member
attributes, host-ACP-representable non-ASCII text, and only Office-provided
intrinsic UserForm controls. It includes a nested `Frame` with child `Label` and
`TextBox` controls plus an `Image` or equivalent `.frx`-backed value. Semantic
assertions cover names, kinds, parent-child structure, selected stable
properties, and readability of that binary value immediately after import and
after reopen. Third-party ActiveX controls are not baseline dependencies.

This fixture uses the existing `WindowsExcelIntegration` category and is
included by `test:windows-excel-integration` and
`verify:release:windows-excel`. It remains outside ordinary unit and
pull-request suites, which do not assume an installed, licensed Excel/VBE host.

Runtime import has no ACP allowlist. The operation accepts the `GetACP` value
when .NET supplies a strict encoding and the source passes the required byte and
Unicode round trips, followed by component and projected-code verification. The
initial release-blocking real-Excel baseline is Excel 16.0 / ACP 932.
Deterministic non-Excel coverage fixes selection and conversion behavior for
932, 1252, and 65001, including canonical UTF-8 treatment for ACP 65001.
Additional real-Excel hosts extend the tested baseline by running the same
semantic fixture; an untested ACP is not rejected solely for being absent from
that matrix. Integration output records the Excel version and active ACP.

The extension does not create a shared temporary directory. It carries every
text source as `{ relativePath, sourceUri, encoding, contentBase64 }` and every
binary `.frx` sidecar as `{ relativePath, contentBase64 }` in the immutable DAP
snapshot. Relative paths must be safe descendants, unique case-insensitively,
and preserve source-set provenance. Text encoding is one canonical `utf8`,
`utf8bom`, `utf16le`, `utf16be`, or `windows-<decimal-code-page>` token. The
adapter fixes its own ACP once and validates base64, paths, source membership,
token and BOM policy, strict decoding, exact byte round trip, matching Windows
code page, source identity, sidecar pairing, and the complete flat inventory
before materializing its own session source directory. The `utf8` token requires
ACP 65001, while `windows-65001` is rejected as noncanonical. A mismatch fails before
Excel starts. Active source positions and breakpoints refer to the persistent
source URI rather than an internal file. The adapter owns the materialized
source directory, never the persistent debug workbook; the extension never
grants it an arbitrary directory path to delete.

The VBE owns interactive debugging. The adapter does not mirror break mode,
stepping, stacks, variables, watches, evaluation, Immediate Window content, or
`Debug.Print` into VS Code.

`VscodeExtension` resolves `vba-dev` from an explicit
`vbaTools.devtool.path` or its bundled absolute path and resolves the adapter
from an explicit `vbaTools.debugAdapter.path` or its bundled absolute path,
following ADR 0007. It never searches PATH, the registry, adjacent files, or a
download source. A configured `vba-dev` override that is missing or incompatible
produces an actionable warning and falls back to the compatible bundled CLI,
which is pinned as the effective path for every extension consumer in that
session. A missing or incompatible debug-adapter override fails without bundled
fallback. Capability inspection remains side-effect free; Excel readiness
belongs to the component that performs the corresponding operation.

## Capability and packaged-extension contract

`vba-dev-contract.json` is the extension-owned compatibility requirement for the
CLI command surface. Its `contractVersion` versions that surface independently
from extension and CLI releases, and `commandSchemaVersions` pins each command
output consumed by the extension. `vba-dev capabilities --format json` reports
only its own provider command contract; it never reads this extension-owned
requirement manifest and does not advertise or start a debug adapter. The
command contract remains `1.0`, Build/Test source-snapshot features are `2.0`,
and `sourceSnapshot.activeWindowsCodePage` remains `1.0`.

The same CLI requirement independently pins
`featureVersions["hostEvent.list"] == "1.0"` and
`commandSchemaVersions["host-event list"] == "1.0"` for the extension-owned
UserForm Event lifecycle. These values do not become debug-adapter requirements:
the extension invokes and validates the environment result, then supplies the
language server's separate catalog notification schema `1.0`.

The debug component advertises adapter contract 1.0, DAP protocol 2.0, stdio,
lowercase-hex-32 session IDs, cleanup/Doctor, Doctor schema 1.0,
`doctor.stdinCancellation: 1.0` and `debug.sourceWorkbook: 1.0`.
The exact required CLI feature map is:

- `build.sourceSnapshot: 2.0`;
- `debug.sourceWorkbookPreparation: 1.0`;
- `invocation.stdinCancellation: 1.0`;
- `invocation.stdinWorkbookConfirmation: 1.0`; and
- `sourceSnapshot.activeWindowsCodePage: 1.0`.

Both providers are checked before capture/preparation without starting Excel.
The extension generates the session ID and pins both compatible executable
paths. Restart rechecks that pair without switching configured paths. The
adapter checks consumed CLI features, not the whole tool version or CLI contract.
Snapshot Test keeps its Test/source-analysis requirements and the exact adapter
map; ordinary non-snapshot commands remain CLI-only.

Debug no longer invokes public snapshot Build or requires the old adapter
`snapshotBuild.diagnostics` feature. Normal editor diagnostics and all
Build/Test/Publish/public snapshot-output quality gates remain. Their diagnostic
origin contracts and ADR0058 history remain valid for those consumers. Package
tools and contracts together; do not silently accept mixed old/new providers.

The VSIX must contain the self-contained Windows x64 executables
`bin/vba-dev/win-x64/vba-dev.exe` and
`bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe` as distinct artifacts,
plus their independent extension-owned compatibility requirements.
`package.json` must point `main` at the compiled extension entry point, activate
dynamic `vba` configuration resolution, contribute the launch selector schema
and user commands, and omit an attach schema. Packaging verification inspects
those contributions, executes both side-effect-free capability commands, and
starts the bundled adapter with `--stdio`, `--vba-dev`, and a valid test
`--session` without requiring a machine-wide .NET runtime or PATH installation.

## Launch resolution

A launch uses the `vba` debug type and `launch` request. `launch.json` may
specify:

- `project`;
- `document`; and
- `module` and `procedure` together.

`args`, `noBuild`, `stopOnEntry`, and `attach` are unsupported. When no saved
configuration exists, F5 synthesizes a transient configuration from the active
VBA editor without writing `launch.json`.

Before resolving the final target or breakpoints, the extension captures one
complete `DebugSourceSnapshot` for the selected project document without saving
editor buffers. Every in-scope dirty file-backed editor contributes its
in-memory text, even when its path is not yet present on disk; other source
contributes its once-read disk bytes. A pathless target or breakpoint source,
or an inventoried path that cannot be read, is a `VbaDebugSelectionError`.
Ambiguous project, document, module, procedure, or source membership is a
`VbaDebugSelectionError`; launch does not show a target picker or start the
adapter.

One VS Code window owns at most one active `VbeDebugSession`. A second launch
fails without replacing the current session. Compound and attach sessions are
unsupported.

## Launch lifecycle

1. Capture the complete immutable selected-document snapshot, including dirty
   file-backed editor bytes without saving.
2. Admit inventory, encoding, module identities, eligible target and exact
   participating breakpoint projection once. This is not the independent
   syntax/type/argument-error gate omitted by Debug.
3. Resolve manifest `templatePath` with read-only CLI description, check selected
   document metadata, then materialize/seal lease-owned source scratch.
4. Bind the exact caller-desktop source workbook/physical file/PID/start time,
   reusing an open workbook or opening it visibly. Fail uncertainty/ambiguity;
   neither basename nor active workbook is identity.
5. Start managed `prepare-debug`. Obtain dirty-workbook consent, then capture
   modules, UserForms/FRX and references. Failed capture prevents replacement.
6. Read the bound `debugPreparationReady` record while DAP stays responsive.
   Only a current accepted launch/Restart claims commit: recheck binding, confirm
   native Reset and answer its exact readiness nonce.
7. Verify receipt and sealed source, transfer native breakpoints and Run in the
   VBE. Keep the actual workbook available after procedure completion and Stop.

VbaDev independently admits transported bytes through its process contract;
adapter syntax-tree/DTO authority does not cross that boundary. Unsafe target
or breakpoint identification fails with an explanation. Unknown live custom
conditional constants are neither inferred from an older saved package nor
assumed zero.

Known initial source rejection permits a corrected launch. Rejected/declined
Restart before commit preserves its usable current session; closed/stopped
sessions cannot be revived. Unexpected parser/process/COM failures keep primary
and cleanup evidence. Failed output is never retried. Code/reference recovery
does not roll back arbitrary cells, external state or concurrent user effects.

The actual source opens read/write, not as an execution copy. Closed source
workbooks open in a new visible application; open-time events are suppressed only
around Open and its original settings are restored. The adapter does not borrow
a shared application for this setting-sensitive open. Exact already-open source
reuse leaves its application settings unchanged. Unrelated workbooks are not
normalized or cleanup-owned. Access permissions, Trust Center and native VBE
compile/runtime behavior remain required.

## Breakpoint transfer

Participating breakpoints are user-enabled ordinary VS Code line breakpoints in
the selected `DocumentSourceSet`. User-disabled breakpoints and breakpoints
outside that source set are ignored. Conditional, hit-count, log, and function
breakpoints are unsupported; an in-scope unsupported breakpoint invalidates
launch.

`.bas`, `.cls`, and `.frm` source lines may participate. `.frx` files do not.
`BreakpointSourceMap` uses the product-neutral `VbaTools.Syntax` parser core
through the generation's already parsed source to exclude export-only class
headers, attributes, and form designer records,
then verifies the projected source against the imported source workbook's
`CodeModule`. The projection includes the known UserForm leading blank and
assumes no other automatic VBE insertion or normalization. A fixed line offset
or a second debug-specific parser is forbidden.

Mapping preserves exact physical-line identity. A comment, declaration, blank
line, rejected continuation line, or other non-breakable location invalidates
launch; the adapter does not move to a neighboring line. Colon-separated
statements retain the VBE rule that execution stops at the first stoppable
statement on the physical line.

The live source workbook's proved `DebugCompilationContext` determines active
conditional-compilation branches. An inactive target or participating
breakpoint invalidates setup. Launch configuration cannot override compiler
constants or select a sibling branch. The adapter verifies the deferred
generation-bound evidence only when the live authority establishes that context and
before it issues native breakpoint commands or executes the target.

DAP breakpoints remain unverified while preparation and VBE setup are pending. An
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
wait. Dismissing the dialog may produce `DebugSetupError`; source cleanup does
not terminate Excel. Stop requests bounded Reset and reports manual guidance
when it cannot confirm the selected project stopped.
The reusable parser may support source mapping and diagnostics, but it does not
replace the VBE as compiler authority or provide a fallback execution path.

Both Excel and the VBE are visible, the target code pane remains displayed, and
focus may move away from VS Code. Once execution belongs to the VBE, VBA runtime
errors and break interaction remain VBE concerns. The adapter does not change
error trapping, compile-on-demand behavior, watches, or explicit `Stop`
statements.

VS Code continues to show the session as running even when the VBE is in break
mode. Normal procedure completion does not end the session; the adapter reports
completion and waits for actual source close, Excel exit or explicit disconnect.

## Source binding, cancellation and retention

`SourceVbeDebugAutomation` borrows an exact COM binding on its STA dispatcher.
Existing and newly opened source Excel are not put under disposable-session or
kill-on-close ownership. Other workbooks/processes are not Reset or cleanup
targets. Release proves COM/STA/handle completion, not Excel death; Dispose or a
caller-composed path is never release proof.

Stop/disconnect requests native Reset in the selected project and confirms
design mode within ten seconds. If unconfirmed, report manual Reset guidance.
Never Save, restore arbitrary edits, Close, Quit or kill Excel as a fallback.
Imported VBA, cell changes and other edits remain unsaved/open after Stop,
including a workbook opened by this launch.

Actual source close ends the connection without reopening. Cancelled Excel
save/close stays active. Completion distinguishes WorkbookClosed, observed
ProcessExited with nullable actual code, and Detached. Procedure completion is
not terminal; workbook close never fabricates an Excel exit.

Managed source preparation drains both streams and uses exact one-shot stdin
control frames. Schema-1.0 dirty consent and captured readiness have closed
fields/nonces; readiness also binds generation, workbook, PID and UTC start.
Malformed, duplicate, stale or out-of-order records confer no authority.
Pending callbacks do not block stderr/DAP reads. Cancellation sends one
`cancel` frame then awaits recovery/terminal exit. Independent twelve-minute
active/completion budgets allow the provider's ten-minute recovery bound and
per-operation limits to finish; no child kill substitutes for completion.

Session scratch stays under
`Path.GetTempPath()/vba-debug-adapter/workspaces/<session-id>`. Arm a durable
retention marker before child start; clear it only with proved Process+Handle
release. Unproved completion keeps the exact child/generation and reports manual
paths. Lease disposal and public/stale cleanup refuse whole-tree deletion on
any marker or uncertain inspection, not incidental file-sharing failure.
Existing create-new, pinned-parent and bounded-deletion authority remains.

Restart captures latest source for the original document/target, reconfirms a
dirty workbook and captures recovery while current execution stays active.
After readiness and binding claim, Reset/import/Run use the same workbook/PID,
preserving cells without saving. Decline/capture failure/cancellation/stale
identity before commit preserves current execution where proved cleanup permits.
Failure/cancellation during the companion's replacement/verification attempts
captured code/reference recovery, not resurrection of prior execution. After
verified preparation releases that capsule, native breakpoint/Run failure leaves
the imported code in place and requests bounded Reset/detach; it does not promise
an old-code restore or undo execution effects.

`DebugFailureCompletion` keeps original stack, later cleanup faults and
stage/resource/PID/path evidence. Unproved release revokes commit authority but
never grants Excel lifetime authority. Public cleanup accepts only session ID,
proves stale lease/retention state before deletion, and reports retained paths.
Unrelated retained sessions do not block a fresh random session.

## DAP surface and output

The initial adapter supports launch, ordinary line breakpoints, configuration
completion, restart, termination, and output. It does not support pause,
continue, stepping, stack traces, scopes, variables, evaluation, exception
breakpoints, function breakpoints, or attach.

`DebugLifecycleOutput` reports preparation progress, Excel-input waits, breakpoint
verification, target start and completion, cancellation, setup failure, and
Excel-process exit. It never scrapes VBE runtime state or VBA output.

### Transport and request ordering

The separate `vba-debug-adapter.exe` exposes its DAP entry point through
`--stdio`; `vba-dev` has no `debug-adapter` subcommand. DAP messages use the
standard `Content-Length` framing. The DAP adapter and the C# LSP adapter share
`VbaTools.ContentLengthFraming` for header and body bytes, EOF classification,
limits, and serialized writes only. DAP and LSP JSON parsing and envelope
validation remain protocol-local. Headers are limited to 1 KiB; LSP bodies are
limited to 64 MiB and DAP bodies to 256 MiB. EOF is clean only before the first
byte of the next frame. Malformed or truncated framing after that point is a
typed transport failure. A frame write may be cancelled before output ownership
or final write admission and then writes zero bytes. After admission, its header
and body are written as one serialized buffer and flushed without cancellation
so a restart or disconnect cannot leave a partial frame on stdout.

The extension resolves the selected project from persistent manifest state and
captures the selected document without saving it. The launch request carries one
immutable encoded-byte `sourceSnapshot` with schema version 2. The adapter
neither reads editor buffers nor models dirty state; it decodes text according
to the supplied encoding only for target and source-map work and writes the
supplied bytes unchanged for `vba-dev prepare-debug`. DAP breakpoint responses
remain unverified until import, exact source mapping and native command complete.
Setup and monitor work run in supervised background tasks. A response, event, or
monitor transport failure ends the adapter without waiting for stdin to close.
Cleanup attempts bounded Reset and detaches its source binding, with explicit
manual-stop guidance if execution cannot be confirmed stopped. It never treats
that failure as proof that the workbook closed or as authority to terminate Excel.

### Request argument admission

For a valid DAP frame and request envelope, one internal `DebugRequestAdmission`
Module reads all consumed arguments into immutable accepted results before the
runner changes request or breakpoint state. Required fields, scalar types and
ranges, every array element, and known optional breakpoint fields are validated
completely. An unsupported condition or nonempty exception filter cannot
short-circuit validation of later fields such as `filterOptions`.

Malformed arguments receive a failure response for that request. They do not
replace breakpoint entries, consume breakpoint IDs, affect other sources, cancel
an in-progress launch, or stop the active session. Well-formed unsupported
breakpoint settings are a different result: the adapter remembers them and
rejects subsequent launch and Restart while applicable unsupported settings
remain. Source breakpoint participation retains its existing in-scope rule.
Only a valid update can remove the remembered settings; malformed updates
cannot add or clear them.

Existing Busy checks retain precedence over payload admission. Restart receipt
correlation is the deliberate exception to validation-before-state-change:
unrelated, stale, future, mistyped, or duplicate correlation is acknowledged
without payload validation, while an exact match consumes the pending request
once before payload admission. A malformed matching payload fails the original
Restart and leaves no pending request to revive; a usable current session stays
active. The protocol below retains ownership of those decisions.

This boundary does not change framing or envelope rejection and does not catch
arbitrary infrastructure exceptions as input errors. If writing a rejection
fails, the existing output latch ends the adapter, performs terminal cleanup,
and retains owner-release evidence without retrying the failed stream.
Source inventory, target, and semantic breakpoint admission remain owned by
`DebugSourceAdmission` and its single immutable source generation.

### Restart preparation protocol

Protocol 2.0 retains the two-party native VS Code Restart transaction introduced
in protocol 1.1. Its preparation marker remains version 1; only the complete
source-snapshot payload moves to schema 2:

1. The resolved launch configuration contains
   `__vbaRestartPreparation: { protocolVersion: 1, id }`. The identifier is bound
   in extension-owned state to the adapter session ID, canonical selected
   project root, manifest document name, and originally resolved target module
   and procedure. It is opaque on the wire and is represented internally as a
   typed `DebugRestartPreparationId`; it is not interchangeable with
   `DebugSessionId` or `DebugGenerationId`. The adapter retains the same launch
   identities independently.
2. On a DAP `restart` request containing that marker, the adapter keeps serving
   requests, advances a typed session-local `DebugRestartGeneration`, parks the
   restart, and retains the old session. Additional Restart requests during
   preparation, capture, or commit receive `DebugLaunchBusy`; they do not change
   counters, cancel or recapture preparation, or enter a queue.
3. The extension resolves the marker only against that original binding and
   captures a fresh immutable source snapshot for the bound document without
   saving project files. The active editor cannot select another document or
   target.
4. The extension sends `vba/restartPrepared` with the original
   `restartRequestSequence`, matching `preparationId`, adapter-issued
   `generation`, the fresh snapshot, `success`, and an optional failure
   message. The numeric generation on the wire is parsed back into
   `DebugRestartGeneration`, which launch preparation explicitly maps to a
   `DebugGenerationId`; neither identity is used directly as cleanup authority.
5. The adapter first correlates session ID, preparation ID, request sequence,
   and generation. Only a complete exact match consumes the pending request.
   It then validates all bound identities, snapshot structure and encoding,
   and the continued existence of the same target module and procedure in the
   fresh source, then fixes that evidence for one-shot launch preparation.
6. Preparation supplies fresh inventory to managed `vba-dev prepare-debug`.
   Dirty consent and pre-replacement recovery capture finish while the current
   execution remains active. Bound readiness produces a one-shot launch plan.
7. After captured readiness, one-shot commit rechecks the bound session identity,
   restart request sequence, restart generation, canonical project, document,
   module, and procedure. A stale or superseded binding cleans the new
   generation and starts no replacement.
8. Only a matching current binding can commit Reset/import/Run in the same source
   workbook/PID. No replacement process starts and no cell effects are undone.

A stale or future request sequence or generation, wrong session or preparation
ID, malformed correlation, or duplicate notification cannot consume pending
preparation or advance its counters. Missing, mistyped, out-of-range, and
duplicate correlation keys establish no ownership. Such notifications receive
a successful receipt acknowledgement without payload validation; the adapter
neither follows future values nor adds a notification timeout. With no awaiting
request, notifications are acknowledged without repeating preparation or response.
JSON syntax and DAP framing failures remain transport failures.

Receipt acknowledgement is distinct from the original Restart response. An
exactly correlated notification consumes its request once; a reported failure,
invalid payload, missing or malformed launch marker, wrong launch binding,
wrong document or target, target removal, snapshot/consent/capture failure,
or restart-only cancellation before commit fails that restart,
cleans any new generation, and retains the still-current old session only under
the owner-evidence rules above. If the old session exits
during preparation, its completion cleans releasable scratch and starts no
replacement. The unreleased protocol has no marker-less compatibility path
because it could not capture a fresh editor snapshot. Replacement/verification
failure while the preparation companion owns its capture attempts code/reference
recovery, not prior-execution resurrection. Later native execution/setup failures
retain the imported source and request bounded Reset/detach without saving.

The adapter's `DebugRestartPreparation` module owns pending consumption,
monotonic counters, the binding captured when Restart begins, and swap authority.
Launch preparation and commit reuse its binding policy while retaining their
separate pre-capture and immediately-before-commit checks. The runner owns DAP
transport and the launch service owns preparation and one-shot execution.

Disconnect/terminate/transport loss cancels pending preparation and requests
source-project Reset without killing Excel. If the binding ends before commit,
no replacement workbook is opened.

Normal procedure completion is output, not terminal. Source completion keeps
actual workbook close, observed process exit and detach distinct; emit DAP
`exited` only with observed process-exit evidence. Cancelled close stays active.
Native dialogs/runtime interaction remain VBE-owned. Doctor retains its separate
private-probe limits; source Stop uses bounded Reset/manual guidance.

Latch failed DAP output and never retry it. Cleanup preserves primary/owner
evidence, borrowed source lifetime and durable pending-companion retention. A
late result cannot revive invalidated Restart or reopen a closed source.

## Failure categories

- Compatibility: missing exact provider contract; no capture/Excel preparation.
- Selection/source rejection: unsafe document, target, breakpoint or bytes;
  fail the request and preserve a usable current session before commit.
- Busy/malformed DAP: existing ordering and full argument validation remain.
- Setup/native failure: binding, capture/import, mapping or VBE start fails;
  report primary cause, attempt allowed code/reference recovery, release only
  owned scratch/connection resources.
- Cancellation: cooperative child completion and bounded source Reset, no
  automatic Save/Close/Kill; retain original cause and cleanup evidence.
- Input wait/runtime error: user-facing VBE dialogs/break interaction, not an
  infrastructure failure merely because a prompt is present.
- Transport/lifecycle loss: end connection with evidence, preserve borrowed
  Excel and pending recovery material.
- Actual workbook/process close: end without reopening; only observed process
  exit supplies a code. Procedure completion/cancelled close are not these.

None permits breakpoint relocation, injected Stop, instrumentation,
Application.Run, wrappers, caption matching or SendKeys. Exact native source
reuse is supported; arbitrary attach/retarget configuration remains unsupported.

## Test seams

The TypeScript client isolates editor and workspace state behind
`VbaDebugConfigurationHost`, process capability inspection behind
`ProcessRunner`, and VS Code lifecycle integration behind small session and
notification interfaces. Client tests pin selection, scoped snapshot capture, restart
identity, missing or mismatched restart-marker rejection, lifecycle
cancellation, configuration contributions, and CLI compatibility. Extension
Host tests prove production F5 resolution, dirty-text snapshot capture, and the
absence of project-file saves.

Source Debug isolates read-only manifest resolution behind
`VbaDevSourceWorkbookResolver`, cooperative preparation behind
`IManagedDebugPreparationProcess`, native binding behind
`ISourceVbeDebugSessionFactory` / `ISourceVbeDebugSession`, and lifecycle and
workbook-consent sinks. Tests exercise immutable source capture, exact binding,
capture/ready/Reset/import/Run ordering, refusal before replacement, same-process
Restart, and typed source-workbook versus process completion. Workspace tests
prove create-new leases/generations and durable companion retention before child
start; unproved child release prevents snapshot deletion and stale reaping.
Malicious paths and symlink/reparse substitutions never become ownership
evidence. `VbaDev` preparation tests pin strict encoding, live preflight,
consent, recovery, no independent source-quality input, and no Save/Close/Quit.
Native source tests substitute exact desktop/process/file inventory, acquired
COM references, modal-window observation, foreground activation and the STA
dispatcher without granting process lifetime ownership. DAP tests use byte
streams and held-open input to verify framing, ordering, confirmation during
pending preparation, cancellation and background failures.

The legacy snapshot-Build and disposable `IVbeDebugSession` fixtures remain
separate compatibility/Doctor evidence. Their Job Objects, output atomicity and
owned-process destruction are not source Debug test seams or cleanup authority.
Public snapshot-output Build retains its protected-path, snapshot-subtree,
encoding, process ownership and caller-owned-output regressions.

Source-admission tests prove that `N` text sources are parsed exactly `N` times
for any breakpoint count, that validation and build bytes come from one frozen
generation, and that a rejected admission never reaches workbook acquisition or
source preparation.
Product-neutral package tests prove strict OPC topology, bounded CFB and
MS-OVBA handling, LCID and LIBFLAGS rules, and all shared metadata facts once;
debug tests retain only file capture, projection, identity fencing, and
product-specific failure behavior.

Opt-in `WindowsExcelIntegration` tests use real Excel, VBIDE, native command IDs,
modal prompts, DAP Stop/Reset and same-process Restart, adapter death, canceled
and actual workbook close, and Excel-initiated exit. Doctor's separate tests
retain their disposable Job ownership. They are serialized and require
`VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1`. Packaging tests separately inspect the
VSIX surface and execute the independent CLI compatibility and debug-component
entry points.

The rename/build/export round-trip that crosses the language server and CLI is
owned by `tools/vba-integration-tests/tests/VbaTools.Integration.Tests`, not by
the VbaDev test project. Its own process client invokes already-built apphosts;
it neither links a product's test harness nor creates a build-order dependency
on another executable project. Run its ordinary, Excel-skipping surface with
`npm run test:cross-product-integration`. The Windows integration gate builds
the executable prerequisites explicitly before opting into the real Excel
case. Absolute executable overrides are
`VBA_TOOLS_INTEGRATION_LANGUAGE_SERVER_PATH` and
`VBA_TOOLS_INTEGRATION_VBA_DEV_PATH`. Shared conformance fixtures remain data
only; each product owns its assertions and lifecycle.

`npm run verify:architecture` checks production and test project references,
assembly references, linked compile source, and product-contract imports. It
rejects reverse VbaDev dependencies and neutral-foundation-to-consumer
dependencies without prohibiting consumer-to-provider reuse.

## Maintenance guidance

- When a consumed CLI command changes, update `vba-dev-contract.json`, the CLI
  capabilities response, client validation, packaging fixtures, compatibility
  tests, and this document together. When the adapter protocol changes, update
  the separate debug-component contract and its compatibility tests without
  adding that capability back to `vba-dev`.
- Keep restart preparation project-bound and sequence-bound. New restart fields
  require deterministic tests for stale, malformed, cancelled, process-exit,
  and transport-failure ordering before Windows coverage. Preserve typed,
  non-interchangeable `DebugRestartPreparationId`, `DebugRestartGeneration`,
  and `DebugGenerationId` internally even where the wire representation is
  string or numeric.
- Resolve VBE commands by stable built-in ID only after establishing
  `VbeCommandContext`. A command-ID or context change requires Doctor,
  deterministic automation, and real Excel integration updates; never add a
  localized-caption or `SendKeys` fallback.
- Keep source mapping in the reusable syntax core and verify generated
  `CodeModule` content. Do not introduce fixed offsets, neighboring-line repair,
  or a debug-only parser.
- Extend `AdmittedDebugSourceSnapshot` when new source-derived launch facts are
  required. Do not add another transport validator, per-breakpoint source walk,
  conditional parse, or builder-side source interpretation beside that
  authority.
- Keep OPC, CFB, MS-OVBA decompression, and directory-record meaning in
  `VbaTools.ProjectMetadata`. Debug-specific file I/O and errors remain in its
  Adapter; do not add a permissive topology mode or return raw format layers.
- Establish exact workbook/file/PID/start-time binding before source Debug
  preparation or native commands. Every new terminal path needs a test proving
  owned COM/STA/handle release while retaining the borrowed workbook/Excel.
  Kill-on-close Job ownership and Job-disposal proofs apply only to the
  dedicated disposable Doctor/probe path, never to source Debug.
- Restrict public workspace cleanup and reaping to a canonical `DebugSessionId`.
  Within a live session, cleanup authority belongs to the lease-issued
  `DebugGenerationWorkspace`, never to an absolute path, ancestry proof, or
  ownership boolean. Test live-lease refusal, PID-reuse protection through
  process start time, duplicate generation claims, adapter-exit cleanup,
  next-start reaping, locked-file retention, path-traversal rejection, and
  symlink/reparse substitution.
- Keep README limited to user actions, prerequisites, supported behavior,
  interactive waits, and data-loss warnings. Put protocol, command identity,
  seam, and maintainer details here or in an ADR.
- Run `npm run verify:release` for the non-Excel release surface. On a configured
  Windows/Excel host, run `npm run verify:release:windows-excel` and the packaged
  VSIX smoke in `docs/release.md`.

## Doctor

Three diagnostic authorities are exposed through four invocations and never
call one another. `vba-dev check` owns Excel-free static project facts.
`vba-dev doctor` defaults to active project readiness, while
`vba-dev doctor --scope environment` owns exactly the five ordinary Excel
environment checks without discovering a project. The independent
`vba-debug-adapter doctor --format json` owns native VBE debugging readiness.

| Readiness property | `vba-dev check` | project Doctor | environment Doctor | adapter Doctor |
| --- | --- | --- | --- | --- |
| Manifest, paths, source identity, CommonModules, command defaults | Static authority | Includes static facts | No project access | No project access |
| Selected-reference availability and resolution | No live proof | Active authority | No | No |
| Applying references to a generated workbook | No | No | No | No |
| Disposable project-template open and `VBProject` access | No | Active authority | No project template | No project template |
| Ordinary Windows, COM, process ownership, VBIDE, and cleanup | No | Includes environment evidence | Exact authority | Debug-fixture evidence only |
| VBA compilation, import, or save | No | No | No | No |
| Native command context, breakpoint, break mode, and Continue | No | No | No | Active authority |
| Requires a project | Yes | Yes | No | No |
| Starts Excel | Never | May start private-desktop owned instances | One private-desktop owned instance | One visible adapter-owned fixture |
| CI-safe without Excel | Yes | No | No | No |

Project Doctor reports the absolute resolved root and exhaustively combines
manifest, source, CommonModules, selected-reference, disposable-template
materialization, and active environment evidence. Environment Doctor returns
only `platform.windows`, `excel.comStartup`, `excel.processOwnership`,
`excel.vbideProjectAccess`, and `excel.processCleanup`, in that order, with no
project or selected-document context. Native debug commands and break mode
cannot fail either `vba-dev` diagnostic. Their stable detail keys, in the same
order, are `isWindows`, `dedicatedInstanceStarted`, `ownedByInvocation`,
`projectAccessSucceeded`, and `ownedProcessReleased`; pass maps to `true`, fail
to `false`, and every other status to `null`.

`vba-debug-adapter doctor --format json` owns the active
`DebugEnvironmentDiagnostic`. Using a temporary dedicated Excel/VBE session and
temporary standard module, it:

1. verifies trusted VBIDE access;
2. finds the native Toggle Breakpoint and Run Sub/UserForm controls;
3. sets a breakpoint on an executable line in a harmless temporary procedure;
4. runs the procedure and observes `VBProject.Mode` enter break mode;
5. continues execution and verifies a completion side effect;
6. clears the native breakpoint;
7. proves Excel PID capture and strong process ownership; and
8. closes all temporary state.

The probe does not modify persistent project files. Its adapter-owned Excel and
VBE may appear briefly because native debug interaction is the capability under
test. A missing, disabled, or failing required command fails the diagnostic;
there is no fallback. This visibility exception does not apply to project or
environment `vba-dev doctor`, whose automation processes use private desktops.

Both Doctor executables use independently owned schema `1.0` results. The
`vba-dev` schema adds `scope` and nullable-or-absolute `project` request context;
the adapter schema has neither field. Each emits one JSON object with
`schemaVersion`, `toolVersion`, overall `status`, `complete`, and ordered
`checks`. Each check contains a stable `id`, `status`, human-readable `message`,
and nonnegative `durationMilliseconds`. The closed `vba-dev` check shape also
requires machine-readable `details` and permits no adapter-only `remediation`;
adapter checks may add `remediation` and `details`. Overall and check status
values are
`pass`, `warning`, `fail`, and `unverified`; a check blocked by a prerequisite
may additionally be `skipped`. A conclusive prerequisite failure followed by
dependency skips is still a complete diagnostic. Cancellation or command
infrastructure that prevents the planned diagnostic from reaching a terminal
classification sets `complete: false`.

Once command handling begins, stdout contains exactly one schema-valid object
on both successful and failed diagnostics; logs use stderr. A complete overall
`pass` or `warning` exits zero. Overall `fail` or `unverified`, and every
incomplete result, exits nonzero. The extension parses a valid payload even on
nonzero exit. Missing or invalid JSON on nonzero exit is a Doctor-command
infrastructure failure rather than a collection of check results. The command
`vba-debug-adapter doctor` accepts no project or document input and uses only
adapter-owned fixture state. Aggregate priority is `fail`, then `unverified` or
`skipped`, then `warning`, then `pass`. Project-scope `vba-dev` JSON ends with
the exact five environment checks in stable order. Exit `130` is reserved for
an incomplete canceled `vba-dev` result with `excel.processCleanup: pass` and
cannot hide an observed failed check.

Doctor has no single wall-clock timeout. Workspace and lease creation has a
5-second deadline; Excel process startup has 30 seconds; fixture workbook
creation, workbook open, and VBIDE access each have 60 seconds; command-context
establishment, breakpoint setup, break-mode entry, continue, and harmless
procedure completion each have 60 seconds. Cooperative process close has 5
seconds before Job Object termination, and workspace deletion uses the same
5-second bounded retry as the cleanup command.

The initial Doctor command has no timeout override. Explicit cancellation is
always accepted. A stage timeout reports that check as `unverified` and its
dependants as `skipped`, rather than concluding that the capability is absent.
If timeout classification and cleanup finish, the result is still
`complete: true`; cancellation or infrastructure failure that prevents a
terminal classification makes it incomplete. The adapter-owned fixture expects
no user interaction, so an unexpected modal dialog is governed by the current
finite stage deadline rather than the unbounded prompt policy of an interactive
debug launch.

The Command Palette action `VBA Tools: Doctor` invokes both diagnostics even
when one fails and presents separately labelled `Project automation` and
`VBE debugging` results. It is the only aggregate surface; there is no
`vba-tools doctor` executable. Capability commands remain side-effect free and
do not substitute for either Doctor. Cancelling during the project stage uses
the hidden managed `vba-dev` `stdin-v1` transport and waits for child close.
Exit `130` ends the aggregate silently before adapter Doctor, while exit `0` or
failure remains authoritative after the local request. Cancellation after
adapter Doctor starts uses its separate cooperative `stdin-v1` transport and
awaits terminal cleanup evidence.

Probe startup failures carry explicit cleanup evidence. A categorized failure
may report cleanup as passing only when `CleanupVerified` is true and no
`CleanupException` was recorded. A missing session is not cleanup evidence:
uncategorized startup failures therefore fail the cleanup diagnostic. Hidden
workbook creation removes its temporary directory on cancellation, and a
cleanup failure during cancellation is preserved separately from the timeout.
Startup adapters preserve `DebugSetupException` and `OperationCanceledException`
classification while attaching this evidence. If COM activation resolves to an
Excel PID that existed before startup, ownership is rejected and only the COM
reference is released; adapter Doctor and debug launch never call `Excel.Quit` or kill
that user-owned process. Source Debug now uses the separate retained-workbook
binding described above rather than this disposable probe owner. When no exact
process owner was established for Doctor, cleanup
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
disposable-probe lifetime guarantee. Strong process ownership remains mandatory
for Doctor and other disposable probes, not for source Debug's borrowed lifetime.
