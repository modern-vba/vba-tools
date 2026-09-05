# VBE debugging architecture

## Status and audience

This is the developer-facing implementation and maintenance contract for the
VS Code-to-VBE debug workflow. README documents only the user-visible workflow,
requirements, limitations, and data-loss behavior. Decision rationale remains
in ADRs 0019 through 0021, 0024, 0025, and 0027. ADR 0022 is superseded.

## Ownership boundary

`VscodeExtension` contributes the `vba` debug type, supplies zero-configuration
F5, resolves editor state, and starts a debug component separate from `VbaDev`.
That component hosts the stdio `VbaDebugAdapter` and owns DAP transport, visible
Excel and VBIDE automation, breakpoint transfer, process monitoring,
cancellation, session output, and debug-artifact cleanup. It invokes
snapshot-aware `vba-dev build` as a subprocess for workbook generation.

`VbaDev` owns manifest and project resolution, snapshot source-inventory
validation, the hidden build Excel process on an invocation-scoped private
desktop, generation atomicity, and internal scratch cleanup for the duration of
each build invocation. It returns a successful snapshot-specific workbook to
the caller and does not own that workbook's later debug-session lifecycle.

The extension supplies a typed `DebugSessionId`, which remains an opaque 32
lowercase hexadecimal character value throughout the adapter. It is neither a
generation identity nor a restart-preparation identity. The debug component
atomically claims a `DebugWorkspaceLease` for that session. Only that live
lease can issue a create-new `DebugGenerationWorkspace` for a typed
`DebugGenerationId`; an existing generation is never reopened or reused. The
generation capability fixes the exact selected-document source snapshot and
workbook paths, materializes the complete source directory, and supplies those
paths to `vba-dev build --source-snapshot <snapshot-directory> --output
<workbook-path>`. The two options are inseparable. Each staged source is opened
relative to a pinned physical parent without following reparse points. Before
the child starts, the capability reopens each source without write sharing and
seals the exact directory inventory, physical file identity, and SHA-256.
After the child exits successfully, it rejects any inventory, identity, or
content change.
The generated workbook is likewise opened relative to the pinned output
directory without following reparse points, must have one physical link, and is
pinned without write sharing by file identity and SHA-256. VBE opens it
explicitly read-only, and the capability verifies it immediately before and
after that open. Denied rename or delete access is defense in depth rather than
the integrity proof: any observable mismatch fails closed. These controls bind
the adapter-owned lifecycle; they are not a kernel isolation boundary against a
hostile process running under the same Windows access token that already holds
an independently authorized handle. Before Excel starts, the CLI verifies through
case-insensitive, filesystem-canonical path identities that the output is
outside the snapshot directory and every manifest document's
`DocumentSourceSet`, and differs from the resolved `vba-project.json` and every
document's source template, bin workbook, and publish workbook. Reparse-point
aliases are included, and inability to establish safety is a validation
failure. Any other caller-owned target may be atomically replaced. The
`DebugGenerationWorkspace`, rather than a path string, ancestry check, or
ownership flag, is the authority that opens and later removes those artifacts;
`VbaDev` does not allocate or publish an implicit temporary path.

The adapter consumes only the public CLI process contract: arguments, stdout,
stderr, exit status, and cancellation. It does not load `VbaDev.App` or
`VbaDev.Infrastructure` into the adapter process. A cancelled or failed child
build must release its hidden Excel process before the adapter cleans
caller-owned session artifacts or starts visible Excel. CLI and adapter
compatibility are validated and versioned independently.

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
`AutomationExcelProcess`, including the preparatory snapshot build used by a
debug launch, uses the private-desktop contract and current evidence recorded in
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

The subsequent `DebugExcelProcess` deliberately does not use this path. Excel,
the VBE, selected code pane, modal prompts, and breakpoint interaction remain
visible on the caller's desktop and under the debug session's separate exact
process ownership.

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
with `TextDocument.encoding`, including its BOM policy. Initial support is
limited to UTF-8 with or without BOM, BOM-marked UTF-16 LE or BE, and the active
Windows ANSI code page without BOM. The producer calls `GetACP` once at capture
start rather than inferring UI language or current culture; ACP 65001 is
canonical UTF-8. A dirty legacy editor encoding is accepted only when its code
page equals that fixed ACP. Every clean and dirty text source must strict-decode
and re-encode to its original bytes before Excel starts. Detection checks a
recognized BOM first, then strict UTF-8, then the strict fixed ACP. Any
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
implementable for non-ACP snapshot text. Debug snapshot builds instead use the
shared `VbaDev` import representation below.

Every `VbaDev` command that reaches `VBComponents.Import` creates an
invocation-internal `VbeImportSourceSet`, regardless of whether its input is a
persistent `DocumentSourceSet` or a `BuildSourceSnapshot`. It captures `GetACP`
once. Ordinary project source, explicit-import source, and materialized snapshot
source all use the same detector: recognized BOM, strict BOM-less UTF-8, then
the strict fixed ACP. UTF-8 wins a dual-valid byte sequence without an ambiguity
error. DAP text tokens are separately revalidated before materialization. Each
source must re-encode to its original bytes, then strict-round-trip the decoded
Unicode text through the fixed ACP before Excel starts. `.frx` remains
byte-exact beside the staged `.frm` with the same base name. Unsupported
encoding, unrepresentable characters, and best-fit-only conversions fail
without starting Excel. The staged mirror is removed with `VbaDev` invocation
scratch and never rewrites the persistent source, snapshot, or DAP payload.

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
before materializing its own session source directory. A mismatch fails before
Excel starts. Active source positions and breakpoints refer to the persistent
source URI rather than an internal file. The adapter owns the materialized
directory and debug workbook; the extension never grants it an arbitrary
directory path to delete.

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
only this project-command contract; it no longer advertises or starts a debug
adapter.

The same CLI requirement independently pins
`featureVersions["hostEvent.list"] == "1.0"` and
`commandSchemaVersions["host-event list"] == "1.0"` for the extension-owned
UserForm Event lifecycle. These values do not become debug-adapter requirements:
the extension invokes and validates the environment result, then supplies the
language server's separate catalog notification schema `1.0`.

The debug component separately versions its DAP extensions and advertises its
stdio entry point through `vba-debug-adapter capabilities --format json`. The
extension-owned `vba-debug-adapter-contract.json` requires `toolVersion`,
adapter `contractVersion: "1.0"`, `protocolVersion: "1.1"`,
`transports: ["stdio"]`, `sessionIdFormat: "lowercase-hex-32"`,
`commands: ["cleanup", "doctor"]`,
`commandSchemaVersions: { "doctor": "1.0" }`, and
`requiredVbaDevFeatureVersions: { "build.sourceSnapshot": "1.0" }`. The
extension validates those reported values before starting
`vba-debug-adapter --stdio --vba-dev <absolute-path> --session <session-id>`.
The extension generates the session ID before process launch as 32 lowercase
hexadecimal characters from 128 bits of cryptographically secure randomness.
The adapter reads no VS Code setting and performs no CLI discovery. It validates
the supplied
`vba-dev capabilities --format json` once at startup and requires
`featureVersions["build.sourceSnapshot"] == "1.0"`. This feature version covers
the paired snapshot input/output options, byte and inventory semantics,
pre-Excel output safety, atomic replacement, cancellation, and owned-process
release. The adapter does not require a particular CLI tool version or its
complete command-contract version. `vba-dev-contract.json` no longer carries
`debugAdapterProtocolVersion`; debug compatibility belongs only to the adapter
contract. Neither capability inspection starts Excel or probes VBE readiness. A
session pins both executable paths until termination.

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

Every launch follows these phases:

1. Capture the selected document's immutable `DebugSourceSnapshot`.
2. Ask the live `DebugWorkspaceLease` to issue a create-new
   `DebugGenerationWorkspace`, materialize the snapshot at its exact source
   path, and supply that inventory to `vba-dev build`, which generates the
   workbook at the capability's exact workbook path in a dedicated hidden Excel
   process on an invocation-scoped private desktop and exits.
3. Close the build process, transfer the same generation capability to the new
   `VbeDebugSession`, and open its workbook in a new dedicated visible
   `DebugExcelProcess`.
4. Verify and transfer participating breakpoints.
5. Select and run the `DebugTargetProcedure` in the VBE.
6. Keep the session active until its Excel process exits or the session is
   stopped.

The build and debug Excel processes are never reused or attached to an existing
user Excel session. Reusing the build process after programmatic VBIDE edits can
prevent entry into break mode.

The debug workbook is a disposable generation artifact rather than the
manifest-defined bin workbook. Snapshot staging and workbook generation do not
rewrite the `DocumentSourceSet`, `vba-project.json`, or completed bin output.
It is created at the exact destination owned by the
`DebugGenerationWorkspace`, with the configured bin workbook's file name. This
preserves `ThisWorkbook.Name`, while `ThisWorkbook.Path` identifies the
temporary location for diagnostics. The capability retains handle-backed
cleanup authority and the sealed source/workbook identity evidence across build
failure, cancellation, and successful build. On success the builder transfers
that exact capability, without reconstructing it from path naming or ancestry,
to the `VbeDebugSession`, which verifies the workbook around open and discards
the capability at session end. `VbaDev` owns only scratch needed during its
invocation.

Excel events are disabled while the debug workbook opens and re-enabled after
breakpoint setup, immediately before procedure execution. Open-time events do
not run. Automation security is lowered only in the dedicated debug process for
the programmatic open and is then restored. Trusted VBIDE access remains
required.

The Excel application is visible before opening the workbook. Open-time modal
prompts remain interactive and have no timeout. The adapter reports that Excel
input is required. The generated workbook is deliberately opened read-only;
cancelling a prompt that prevents open is a `DebugSetupError`.

## Breakpoint transfer

Participating breakpoints are user-enabled ordinary VS Code line breakpoints in
the selected `DocumentSourceSet`. User-disabled breakpoints and breakpoints
outside that source set are ignored. Conditional, hit-count, log, and function
breakpoints are unsupported; an in-scope unsupported breakpoint invalidates
launch.

`.bas`, `.cls`, and `.frm` source lines may participate. `.frx` files do not.
`BreakpointSourceMap` uses the product-neutral `VbaTools.Syntax` parser core
to exclude export-only class headers, attributes, and form designer records,
then verifies the projected source against the generated workbook's
`CodeModule`. The projection includes the known UserForm leading blank and
assumes no other automatic VBE insertion or normalization. A fixed line offset
or a second debug-specific parser is forbidden.

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

The same kill-on-close Job owns any active `vba-dev` child process. `VbaDev`
retains its own strong ownership of hidden build Excel, so adapter Job closure
terminates the CLI and causes the CLI's Excel ownership to close. The adapter
establishes Job membership before it accepts process-dependent session state.

Session files exist only under
`Path.GetTempPath()/vba-debug-adapter/workspaces/<session-id>`. The extension
generates and retains the path-safe `DebugSessionId` before launch. The adapter
accepts only 32 lowercase hexadecimal characters and atomically claims the
directory and `DebugWorkspaceLease` with create-new semantics before
materializing source. An existing ID fails launch without reuse or deletion.
The lease contains the adapter PID, process start time, and a separate random
lease ID. While live, it is the sole factory for create-new generation
capabilities; a caller cannot compose a generation path from the session ID or
recover ownership by proving that a path is beneath the session directory.
Normal cleanup ends owned processes, consumes the session's generation
capabilities, then removes the session directory. It never treats project
source, manifest output, or another temporary root as session-owned.

If the debug workbook actually closes, the adapter force-terminates the
dedicated Excel process and ends the session. Cancelling workbook close leaves
the session active.

Stop is valid in every launch phase:

- during build, cancellation is sent to `vba-dev build`; `VbaDev` terminates its
  hidden build Excel process and removes only invocation-internal scratch;
- after the build invocation exits, the active `DebugGenerationWorkspace`
  removes its exact source snapshot and successful or incomplete workbook;
- persistent project source, manifest state, and completed bin output remain
  unchanged; and
- after visible Excel starts, that process is force-terminated.

Cancellation is reported as cancelled rather than as a setup failure. Restart
Debugging completes fresh-snapshot preparation, downstream snapshot
revalidation, and the complete temporary build while retaining the current
session. The isolated hidden build Excel process may coexist with the current
visible debug process, but two visible debug processes never overlap. After the
build succeeds, the adapter rechecks the bound session, restart request,
project, document, module, and procedure. Only a still-current binding enters
the swap: the old process is force-terminated immediately before the replacement
visible Excel process starts under the same session ID, using a new
`DebugGenerationId` and a new lease-issued generation capability.

This build-before-swap ordering intentionally replaces the former
validation-before-swap behavior. Validation alone never authorizes teardown of
a usable current session. Preparation, snapshot revalidation, build, target
removal, restart cancellation, or a stale binding before the swap cleans any
new generation and leaves an active current session unchanged. If the current
session exits during the build, its completion cleans the new generation and
starts no replacement. If replacement startup fails after the swap, restart
fails and the new generation is cleaned without reviving or reusing the
terminated process.

If the adapter exits unexpectedly, the extension runs
`vba-debug-adapter cleanup --session <session-id>` after observing process exit.
The public cleanup and stale-reaping boundaries accept only `DebugSessionId`,
never a generation ID or directory path, and validate the lowercase-hex-32
value before any filesystem access. An invalid ID is a nonzero usage error. A
missing workspace, an ID that was never claimed, or a stale workspace removed
successfully exits zero without structured output.

Cleanup resolves only beneath the adapter-owned workspace root. It refuses
deletion and exits nonzero when the lease still identifies a live owner or its
state cannot prove staleness. Once staleness is proved, deletion receives
bounded retries for five seconds. A remaining workspace is retained and
reported by reason and absolute path on stderr rather than broadening deletion
scope. That retained absolute path is diagnostic information only and never
becomes deletion authority. The extension treats such failure as a housekeeping
warning and does not rewrite the debug outcome that preceded cleanup.

The next adapter startup applies the same checks independently to stale sessions
when the extension could not run cleanup. A retained unrelated workspace does
not block a new random session ID. The initial cleanup command has no JSON
schema; its machine contract is its arguments, zero/nonzero status, and stderr.

## DAP surface and output

The initial adapter supports launch, ordinary line breakpoints, configuration
completion, restart, termination, and output. It does not support pause,
continue, stepping, stack traces, scopes, variables, evaluation, exception
breakpoints, function breakpoints, or attach.

`DebugLifecycleOutput` reports build progress, Excel-input waits, breakpoint
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
immutable encoded-byte `sourceSnapshot` with schema version 1. The adapter
neither reads editor buffers nor models dirty state; it decodes text according
to the supplied encoding only for target and source-map work and writes the
supplied bytes unchanged for `vba-dev build`. DAP breakpoint responses remain
unverified until the build, exact source mapping, and native command complete.
Setup and monitor work run in supervised background tasks. A response, event, or
monitor transport failure terminates the adapter and releases process ownership
without waiting for stdin to close.

### Restart preparation protocol

Protocol 1.1 makes native VS Code Restart a two-party transaction:

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
   restart, and retains the old session.
3. The extension resolves the marker only against that original binding and
   captures a fresh immutable source snapshot for the bound document without
   saving project files. The active editor cannot select another document or
   target.
4. The extension sends `vba/restartPrepared` with the original
   `restartRequestSequence`, matching `preparationId`, adapter-issued
   `restartGeneration`, the fresh snapshot, `success`, and an optional failure
   message. The numeric generation on the wire is parsed back into
   `DebugRestartGeneration`, which launch preparation explicitly maps to a
   `DebugGenerationId`; neither identity is used directly as cleanup authority.
5. The adapter validates all bound identities, snapshot structure and encoding,
   and the continued existence of the same target module and procedure in the
   fresh source, then fixes that evidence for one-shot launch preparation.
6. Preparation supplies the fresh inventory to snapshot-aware `vba-dev build`.
   Downstream snapshot revalidation and the complete new generation build finish
   while the old visible session remains active. Only build success produces an
   immutable one-shot launch plan that owns the built generation.
7. After build success, one-shot commit rechecks the bound session identity,
   restart request sequence, restart generation, canonical project, document,
   module, and procedure. A stale or superseded binding cleans the new
   generation and starts no replacement.
8. Only a fully matching current binding can enter the swap. The old session is
   terminated immediately before a replacement visible Excel process opens the
   built workbook, transfers breakpoints, and runs the target.

A stale request sequence or restart generation cannot consume the pending
preparation. A missing or malformed marker, wrong bound identity, wrong
document or target, target removal, downstream snapshot revalidation failure,
build failure, or restart-only cancellation before the swap fails that restart,
cleans any new generation, and retains the old session. If the old session exits
during the build, its completion cleans the new generation and starts no
replacement. The unreleased protocol has no marker-less compatibility path
because it could not capture a fresh editor snapshot. If replacement startup
fails after the swap, the old session remains terminated and the new generation
is cleaned; neither process nor generation is revived or reused.

Disconnect, terminate, session release, or notification-transport failure
cancels pending preparation and ends the owned session. If the old Excel process
exits before restart commit, process exit is authoritative and no replacement
process starts.

Normal target completion is output, not a terminal DAP event. The owned Excel
process exit, explicit termination, or an unrecoverable adapter failure claims
the single terminal transition.

## Failure categories

Failures are classified by the boundary that can act on them:

- `VbaDevCompatibilityError`: the configured or bundled CLI cannot satisfy the
  extension-owned project-command and snapshot-build contract. No build process
  starts.
- debug-component compatibility error: the component cannot satisfy the
  extension-owned DAP contract. No adapter or Excel process starts.
- `VbaDebugSelectionError`: the extension cannot select one project, document,
  eligible procedure, or valid participating breakpoint from the captured
  source snapshot.
- `DebugLaunchBusyException`: the VS Code window or selected project already
  owns an incompatible launch. The active session is retained.
- `DebugSetupException`: build, workbook open, source verification, command
  context, native command, compiler, or VBE setup could not establish the
  requested session. Any owned process and incomplete temporary output are
  cleaned.
- cancellation: F5 cancellation, Stop, Restart, disconnect, or shutdown is
  reported as cancellation rather than setup failure. Temporary debug source
  and workbook output are removed; persistent project files are unchanged.
- input wait: an interactive Excel or VBE modal is a lifecycle state, not a
  failure or timeout. User dismissal may subsequently produce setup failure;
  Stop may force-terminate the process immediately.
- transport or lifecycle failure: malformed protocol, failed DAP output,
  adapter death, Extension Host death, or unexpected parent exit closes strong
  ownership and terminates the session-owned Excel and child-process tree. The
  extension or next adapter start reaps a stale session lease.
- owned-process exit: normal Excel exit is the final session outcome. Procedure
  completion alone is not process exit.

None of these categories permits breakpoint relocation, `Stop` insertion,
instrumentation, `Application.Run`, generated wrappers, caption matching,
`SendKeys`, or attachment to an existing Excel process.

## Test seams

The TypeScript client isolates editor and workspace state behind
`VbaDebugConfigurationHost`, process capability inspection behind
`ProcessRunner`, and VS Code lifecycle integration behind small session and
notification interfaces. Client tests pin selection, scoped snapshot capture, restart
identity, missing or mismatched restart-marker rejection, lifecycle
cancellation, configuration contributions, and CLI compatibility. Extension
Host tests prove production F5 resolution, dirty-text snapshot capture, and the
absence of project-file saves.

The debug component isolates its `vba-dev build` client,
`IVbeDebugSessionFactory`, `IVbeDebugSession`, and debug lifecycle sinks.
Deterministic fakes control every snapshot/build/open/modal, breakpoint, run,
process-exit, restart, and generation-capability transfer boundary. Workspace
tests prove lease loss refusal, duplicate create-new generation claims,
handle-backed cleanup after build failure and cancellation, transfer to and
cleanup by the `VbeDebugSession`, adapter-crash cleanup, stale reaping, and the
diagnostic-only treatment of retained paths. Malicious paths and
symlink/reparse substitutions never become ownership evidence. `VbaDev`
tests separately pin snapshot validation, protected-path and snapshot-subtree
rejection, reparse-point alias handling, build-process ownership, output
atomicity, and the rule that successful caller-owned output is not deleted.
Debug infrastructure tests substitute the Excel process API, Job Object,
modal-window API, foreground window activation, and COM dispatcher without
weakening production ownership. DAP tests use in-memory byte streams and
held-open input to verify framing, ordering, cancellation, and background-task
failure.

Opt-in `WindowsExcelIntegration` tests use real Excel, VBIDE, native command IDs,
Job Objects, modal prompts, DAP Stop/Restart, adapter death, and Excel-initiated
exit. They are serialized and require
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
- Establish PID and kill-on-close Job ownership before workbook open, prompts,
  breakpoint transfer, or target execution. Every new terminal path needs a
  test proving Job disposal and launch-guard release.
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
