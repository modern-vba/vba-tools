---
status: accepted
---

# Separate VBE debug orchestration from vba-dev

`VbaDev` is a standalone .NET-style project command. Its public responsibilities
are project and manifest resolution plus operations such as build, test,
publish, import, export, reference management, CommonModules management, and
environment diagnostics that are natural prerequisites for those operations.
It does not host a Debug Adapter Protocol endpoint or own a VBE debug session.

`vba-dev build` may accept an immutable source snapshot and direct the generated
workbook to a snapshot-specific destination instead of the manifest-defined bin
path. For that invocation, `VbaDev` owns source-inventory validation, workbook
generation, the dedicated hidden Excel process, cancellation, and internal
scratch cleanup. After it successfully produces the requested output, the
caller owns that output's lifetime; `VbaDev` neither starts a debug session nor
deletes the output later.

Snapshot build uses paired explicit inputs: `vba-dev build --source-snapshot
<snapshot-directory> --output <workbook-path>`. Supplying only one option is a
usage error. Before starting Excel, `VbaDev` resolves case-insensitive,
filesystem-canonical path identities, including reparse-point aliases. The
output must be outside the snapshot directory and every manifest document's
`DocumentSourceSet`, and distinct from the resolved `vba-project.json` and every
document's manifest-defined source template, bin workbook, and publish workbook.
If it cannot establish that separation, validation fails without writing
output. Any other caller-owned target, including an existing file, is eligible
for atomic replacement. `VbaDev` preserves a previously completed target on
build failure or cancellation and leaves a successful target in place after
returning. An ordinary `vba-dev build` without either option retains the
manifest-defined source and bin behavior.

`<snapshot-directory>` is a caller-owned complete source set for the selected
document, not an overlay on the persistent `DocumentSourceSet`. Its `.bas`,
`.cls`, and `.frm` files and same-directory `.frx` sidecars are authoritative as
bytes. Their paths preserve the original `DocumentSourceSet`-relative layout as
source provenance, although build identity remains flat by exported file name.
`VbaDev` applies the normal recursive source discovery, duplicate detection, and
form-sidecar rules to this directory. It does not compare its inventory or
content with persistent source. At invocation start, `VbaDev` fixes the input in
invocation-internal scratch so later caller changes cannot alter the running
build. Project and document selection, the source template, references, and
other manifest-owned configuration still come from the project.

The snapshot producer copies clean source and `.frx` sidecars as their exact
disk bytes. For dirty editor source, it encodes the current editor text using
that document's current editor encoding, including the corresponding BOM
policy, without saving the persistent file. The initial supported forms are
UTF-8 with or without BOM, BOM-marked UTF-16 LE or BE, and the
operation-fixed active Windows ANSI code page without BOM. The extension reads
that code page directly from `GetACP` once at capture start instead of inferring
language or culture; ACP 65001 is canonical UTF-8. A dirty legacy editor
encoding is accepted only when its code page equals that ACP. Every clean and
dirty text source must strict-decode and re-encode to its original bytes before
Excel starts. Detection checks a recognized BOM first, then strict UTF-8, then
the strict fixed ACP. An unsupported encoding, unrepresentable character, or
round-trip difference fails snapshot capture; there is no implicit encoding
change, replacement-character, or lossy fallback. `.frx` remains binary-only,
and the accepted snapshot bytes remain authoritative and unchanged.

The producer starts with a complete disk inventory and overlays all dirty
file-backed source editors canonically inside the selected source set. An
in-scope editor path may be added even when absent on disk; a pathless document
cannot participate and fails selection when it is the target or owns a
participating breakpoint. At capture start, the producer fixes that inventory
and the then-open editor set, text, URI, and encoding. It reads each selected
clean file and sidecar once without a final stability comparison or automatic
retry. Later workspace changes belong to the next invocation; a selected disk
path that cannot be read fails capture. These rules apply to
extension-produced debug and test snapshots only; ordinary `vba-dev` commands
remain disk-based.

The raw-byte import assumption was tested as an implementation gate rather than
adopted as an undocumented compatibility claim. A supported Windows Excel
environment imported equivalent non-ASCII VBA source encoded as its active
Windows ANSI code page, BOM-less UTF-8, BOM-marked UTF-8, and BOM-marked UTF-16
LE and BE through `VBComponents.Import`. The probe covered standard modules
(`.bas`), class modules (`.cls`), and UserForms
(`.frm`) with an exported `.frx` sidecar. Document modules are excluded because
they are not replaced through `VBComponents.Import`. The probe compares
`CodeModule` text with `VbaCodeModuleProjection.CodeModuleLines` immediately
after import and again after save, close, and reopen, and also verifies the
UserForm and sidecar-backed control state. It
records the active code page and the encoding emitted by VBE export; a Japanese
environment that exports CP932 is one required baseline. The result decided
whether direct raw import was valid or the explicit `VbeImportSourceSet`
contract below was required; it never authorized an implicit fallback or
mutation of caller-owned source.

The first probe ran against Excel 16.0 with active ACP 932. VBE-exported `.bas`,
`.cls`, and `.frm` text strict-round-tripped as CP932 and not as UTF-8, and the
UserForm export produced an `.frx` sidecar. Raw ACP input preserved standard
module, class module, and UserForm code and form state both immediately and
after save/reopen. Raw BOM-less UTF-8 imported all three component kinds but
decoded the non-ASCII code as CP932 mojibake, which survived save/reopen.
UTF-8 BOM bytes were likewise interpreted as CP932 text: the standard-module
header became code, while class and form files were imported as standard
modules instead of their declared component kinds. Both BOM-marked UTF-16 LE
and BE were rejected for all three kinds. This result invalidates the
no-transcoding assumption for non-ACP source. Snapshot encoding implementation
therefore uses a strict internal VBE-facing import representation rather than
narrowing the accepted source encodings.

Every `VbaDev` path that reaches `VBComponents.Import`, including ordinary and
snapshot build, publish, build-before-test, snapshot test, and explicit import,
creates an invocation-internal `VbeImportSourceSet`. `VbaDev` fixes `GetACP`
once for the operation. For ordinary `DocumentSourceSet`, explicit-import, and
materialized snapshot files, it recognizes a supported BOM first, then tries
strict BOM-less UTF-8, then the strict fixed ACP; a byte sequence valid as both
uses UTF-8 rather than failing as ambiguous. DAP text entries additionally have
their declared encoding token revalidated before materialization. Every path
must decode and re-encode to its original bytes. `VbaDev` then strict-encodes
the resulting Unicode text into the fixed ACP and decodes it again to require
exact text equality. An unsupported encoding, unrepresentable character,
best-fit substitution, or any other difference fails before Excel starts.
Source already encoded in that ACP produces the same text bytes.
`.frx` sidecars are copied byte-for-byte beside the staged `.frm` with the same
base name. The original `DocumentSourceSet`, caller-owned
`BuildSourceSnapshot`, and DAP snapshot bytes are never rewritten. The
invocation owns and removes only the staged import copy with its other internal
scratch.

After importing each component and before saving the workbook, `VbaDev` derives
the reusable `VbaCodeModuleProjection` from the strict-decoded Unicode source
and verifies the imported component name, component kind, line count, and every
projected `CodeModule` line exactly. The projection excludes export-only
serialization records: a class module's `VERSION` and `BEGIN`/`END` header,
`Attribute` records, a UserForm's designer block, and the synthetic physical
line that only represents a terminal newline. It also models the one known
leading empty `CodeModule` line produced by UserForm import. The contract
assumes no automatic VBE insertion or normalization beyond this projection.
Any unmodeled difference fails before save, so a generated output is not
committed and an explicit-import target is not saved.

That per-invocation proof deliberately stops at component identity, component
kind, and projected code. `VbaDev` does not re-export each imported component,
enumerate an incomplete subset of COM-visible properties, or claim exhaustive
runtime verification of export-only metadata, UserForm designer state, or
`.frx` content. Within a supported Excel environment, `VBComponents.Import`
remains authoritative for that state. Representative real-Excel integration
fixtures cover class and member attributes plus UserForm controls, properties,
and sidecar-backed state through import, save, close, and reopen. Those tests
detect compatibility regressions in the supported path; they do not prove every
arbitrary component during each command. Their oracle is semantic rather than
whole-file byte equality: it checks the expected attributes, control structure,
selected properties, and readability of binary sidecar-backed values both
immediately after import and after reopen. VBE-reordered records, materialized
defaults, or a semantically equivalent `.frx` serialization do not fail the
fixture by themselves.

Ordinary commands do not close and reopen the generated workbook merely to
repeat component or code verification after persistence. A save failure still
fails the command and prevents atomic output replacement or persistence of an
explicit-import target. The release-blocking real-Excel fixture supplies the
save/close/reopen regression proof without imposing a second workbook-open
lifecycle, its events and prompts, or another open deadline on every build,
publish, test build, and import.

The minimum fixture set contains valid non-default class and member attributes,
non-ASCII text selected to strict-round-trip through the host's active ACP, and
a UserForm composed only of Office-provided intrinsic controls. The form
includes a nested container such as a `Frame` with child `Label` and `TextBox`
controls plus an `Image` or equivalent property whose payload is stored in
`.frx`. Assertions cover component and control names and kinds, parent-child
structure, selected stable properties, and successful reading of the
sidecar-backed value at both verification points. Third-party ActiveX controls
are outside this baseline.

The fixture belongs to the existing `WindowsExcelIntegration` category. It runs
through `npm run test:windows-excel-integration` and therefore participates in
`npm run verify:release:windows-excel`, while remaining outside ordinary unit
and pull-request test runs that cannot assume an installed, licensed Excel/VBE
host.

Runtime import does not whitelist ACP 932 or another closed code-page set. It
accepts the operation's `GetACP` value when .NET can construct its strict
encoding and every source can satisfy the byte and Unicode round trips already
required by `VbeImportSourceSet`; later component and projected-code
verification remains authoritative for that invocation. The initial
release-blocking real-Excel baseline is Excel 16.0 with ACP 932. Deterministic
non-Excel tests cover code-page selection, strict conversion, and ACP 65001
canonicalization for 932, 1252, and 65001. A real-Excel host using another ACP
may add a tested baseline by running the same semantic fixture, but absence from
that empirical matrix does not make the ACP invalid at runtime. Integration
results record the Excel version and active ACP so documentation does not imply
broader empirical coverage.

The extension sends those encoded bytes to `vba-debug-adapter` inside the DAP
snapshot rather than creating a cross-process temporary directory. Each text
source entry carries its safe source-set-relative path, persistent source URI,
canonical `utf8`, `utf8bom`, `utf16le`, `utf16be`, or
`windows-<decimal-code-page>` encoding token, and base64 content. Binary `.frx`
entries carry a relative path and base64 content without an encoding. The
adapter fixes its own ACP once, revalidates the token, BOM policy, strict
decoding, byte round trip, and matching `windows-` code page before
materialization, and rejects a mismatch without starting Excel. Active
positions and breakpoints continue to refer to persistent source identity. The
adapter materializes the complete directory in its own session workspace, then
owns that directory and the caller-selected debug workbook until cleanup.
Restart uses a newly captured immutable byte snapshot through the same protocol.
The accepted base64 expansion avoids shared-directory ownership and
arbitrary-path deletion across the process boundary.

A separate debug component owns DAP transport, snapshot validation and
materialization, debug-target and breakpoint resolution, visible Excel and VBE
automation, Restart, termination, debug-session output, and cleanup of the
snapshot inputs and successful build outputs it uses. The extension owns editor
snapshot capture and sends the immutable bytes through DAP. The adapter composes
the snapshot-aware
`vba-dev build` command rather than adding debug-session behavior to `VbaDev`.

Restart is a `DebugRestartPreparation` transaction rather than a new target
selection. An opaque preparation ID is bound to the adapter session, canonical
project root, manifest document, and original target module and procedure; each
pending restart also has its DAP request sequence and a monotonically increasing
session-local generation. The extension captures the bound document regardless
of the active editor. Before terminating the old session, the adapter requires
every identity to match and proves that the same target still exists in the
fresh snapshot. A stale or mismatched preparation, capture failure, or removed
target fails only the restart and retains the current session. A matching
preparation commits a complete new temporary build and launch under the same
session ID.

Composition occurs only through the public CLI process boundary. The debug
component starts `vba-dev build` as a subprocess, supplies ordinary project,
document, snapshot-directory, and output-path arguments, and consumes stdout,
stderr, exit status, and cancellation behavior. It does not reference or invoke
`VbaDev.App`, `VbaDev.Infrastructure`, or another internal application-service
API. The debug component owns the `vba-dev` child-process handle and cancellation
request; `VbaDev` owns its hidden Excel process and invocation scratch until the
child exits. The debug component then owns the successful output and subsequent
visible debug session. Their compatibility contracts and release versions are
independent.
Low-level code that later needs reuse may move to a neutral library, but that
library must not bypass the public `VbaDev` command contract.

The debug component is distributed as a separate self-contained Windows x64
.NET executable named `vba-debug-adapter.exe`. The VSIX bundles it at
`bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe` alongside, but not inside,
the bundled `vba-dev.exe`. It exposes `capabilities --format json` for its own
contract and `--stdio` for DAP transport. `--stdio` requires a `--session`
argument. The extension-owned `vba-debug-adapter-contract.json` requires a
capability response containing `toolVersion`, adapter `contractVersion: "1.0"`,
`protocolVersion: "1.1"`, `transports: ["stdio"]`,
`sessionIdFormat: "lowercase-hex-32"`, `commands: ["cleanup", "doctor"]`,
`commandSchemaVersions: { "doctor": "1.0" }`,
`featureVersions: { "doctor.stdinCancellation": "1.0" }`, and
`requiredVbaDevFeatureVersions: { "build.sourceSnapshot": "1.0" }`. The
extension validates that response independently from `vba-dev` before launch.
The adapter is an internal extension companion rather than a user-facing
project command, requires neither a machine-wide .NET runtime nor PATH
installation, and is Windows-only while Excel/VBIDE automation is the supported
host.

`vba-dev-contract.json` and the corresponding capabilities response remove
`debugAdapterProtocolVersion` and instead advertise
`featureVersions: { "build.sourceSnapshot": "1.0" }`. That feature version
covers the paired snapshot input/output options, byte and inventory semantics,
pre-Excel output safety, atomic replacement, cancellation, and owned-process
release consumed by the adapter. The adapter validates only that feature
version, not the CLI tool version or its complete command-contract version.
Both capability commands remain side-effect free.

Each adapter session strongly owns its visible Excel process and any active
`vba-dev` child through a kill-on-close Windows Job Object. `VbaDev` continues to
own its hidden Excel process internally, so terminating the child closes that
nested ownership as well. Session files live only under the adapter-owned
`Path.GetTempPath()/vba-debug-adapter/workspaces/<session-id>` root. Before
starting the adapter, the extension generates `<session-id>` as 32 lowercase
hexadecimal characters from 128 bits of cryptographically secure randomness and
retains it for cleanup. The adapter accepts only that canonical form. It claims
the directory and lease with create-new semantics; an existing ID fails launch
without reuse or deletion. The lease records the adapter PID, process start
time, and a separate random lease ID. Restart retains the same session ID and
replaces only that session's artifacts.

Normal termination ends owned processes before deleting the session directory.
After an unexpected adapter exit, the extension invokes the internal
`vba-debug-adapter cleanup --session <session-id>` operation. It accepts no
arbitrary path and validates the lowercase-hex-32 ID before any filesystem
access. An invalid ID is a nonzero usage error. A missing workspace or an ID
that was never claimed is a silent successful no-op, so the extension can
attempt cleanup even when adapter initialization failed. A stale lease that is
removed successfully also exits zero.

Cleanup canonicalizes only beneath the adapter-owned root and refuses deletion
with a nonzero result while the recorded PID and process start time identify a
live owner, or when malformed lease state prevents it from proving staleness.
After staleness is proved, deletion receives bounded retries for five seconds.
A still-locked or otherwise unremovable workspace is retained; stderr reports
the reason and absolute path, and the command exits nonzero without widening
its deletion scope. Cleanup has no initial JSON output contract: the extension
uses the exit code and reports stderr as a housekeeping warning without changing
an already determined debug-session outcome.

If both extension and adapter terminate, the next adapter startup applies the
same lease and bounded-deletion rules to each stale session. A retained
unrelated workspace is reported but does not block launch under a new random
session ID. Cleanup never expands to project files or another temporary root.

`VscodeExtension` resolves both companion executables explicitly. It uses
`vbaTools.devtool.path` for `vba-dev` and
`vbaTools.debugAdapter.path` for `vba-debug-adapter` when configured; otherwise
it uses the corresponding bundled absolute path. It never searches PATH, the
registry, adjacent files, or a download source. An invalid or incompatible
`vba-dev` override emits an actionable warning and falls back to the compatible
bundled CLI, which becomes the effective path for every extension consumer in
that session. An invalid debug-adapter override fails without bundled fallback.

After side-effect-free capability validation, the extension starts
`vba-debug-adapter --stdio --vba-dev <absolute-path> --session <session-id>`.
The adapter does not read VS Code settings or discover the CLI. It validates the
supplied CLI's capabilities once before accepting Excel-dependent launch work
and requires `featureVersions["build.sourceSnapshot"] == "1.0"`. One debug
session pins both resolved paths and its session ID; configuration changes apply
only to a later session.

`vba-dev check`, the two `vba-dev doctor` scopes, and
`vba-debug-adapter doctor --format json` are independent diagnostic functions
and never invoke one another. `vba-dev check` owns deterministic manifest,
source-set, CommonModules, and command-default facts without starting Excel; it
does not claim compilation, COM/VBIDE, live reference materialization, import,
save, or debugger readiness. Project Doctor adds selected-reference,
disposable-template materialization, and applicable active environment evidence
and reports the normalized absolute project root. The adapter diagnoses native
VBE command context, breakpoint and break-mode operation, visible debug-process
ownership, session workspace leases, reaping, and debug cleanup. A native-debug
failure does not make `vba-dev` project automation unready.

The extension invokes adapter Doctor as `doctor --format json
--cancellation-transport stdin-v1`. That hidden caller-neutral transport accepts
only the BOM-less byte frame `cancel\n`; malformed, incomplete, CRLF, BOM-marked,
or oversized frames and EOF alone are neutral. Ordinary `doctor --format json`
does not read stdin, and DAP `--stdio` retains exclusive ownership of its input.
After requesting adapter-Doctor cancellation, the extension does not force-kill
that adapter process: it waits for process close, terminal cleanup, and one
schema-valid JSON result.
Missing or invalid terminal output remains infrastructure failure rather than
being hidden by the local cancellation request.

`vba-dev doctor --scope environment --format json` explicitly skips project
discovery and project-specific checks and reports only the Windows, Excel COM,
actual VBIDE project-access, and hidden automation-process ownership and cleanup
prerequisites for project operations. Guided `new excel` runs that scope before
collecting project input and never substitutes the adapter Doctor, because
native breakpoint and break-mode readiness is not a creation prerequisite. A
failed or unverified required environment check blocks the guided flow without
offering Run Anyway, while direct `new excel` still performs its authoritative
checks in the Excel process used for creation. Neither Doctor nor the extension
changes Trust Center or registry settings.

Environment scope emits exactly these required check IDs in fixed order:
`platform.windows`, `excel.comStartup`, `excel.processOwnership`,
`excel.vbideProjectAccess`, and `excel.processCleanup`. Each appears exactly once.
A conclusively blocked downstream check remains present as `skipped`; after an
Excel process starts, cleanup is attempted and reported even if VBIDE access
fails. Consumers bind behavior to stable IDs rather than display text, allow no
replacement or additional environment rows, and require all five checks plus
the overall result to be `pass` with `complete: true` for guided creation.
Their stable detail keys, in the same order, are `isWindows`,
`dedicatedInstanceStarted`, `ownedByInvocation`, `projectAccessSucceeded`, and
`ownedProcessReleased`; pass maps to `true`, fail to `false`, and every other
status to `null`.

`vba-dev doctor` JSON schema `1.0` is one object with `schemaVersion`,
`toolVersion`, `scope`, `project`, overall `status`, `complete`, and ordered
`checks`. Environment scope requires `scope: "environment"` and `project: null`;
ordinary project Doctor requires `scope: "project"` and the normalized canonical
absolute project root in `project`. It has no selected-document field because the
project report may cover every document. Each check requires a stable `id`,
`status`, human-readable `message`, and nonnegative
`durationMilliseconds`, plus required machine-readable `details`; the closed
`vba-dev` check shape has no `remediation` field. Overall and check statuses are
`pass`, `warning`, `fail`, or `unverified`; a dependency-
blocked check may additionally be `skipped`. Cancellation or command
infrastructure that prevents the planned diagnostic from completing sets
`complete: false`. A check skipped because an earlier check conclusively failed
does not by itself make the diagnostic incomplete.

The independent `vba-debug-adapter doctor` schema remains adapter-owned and does
not gain the project CLI's `scope` or `project` fields. Guided creation accepts
only request-matching `vba-dev` output with `scope: "environment"` and
`project: null`; an otherwise valid Doctor object from another scope is an
untrusted result. Because these contracts are unreleased, this replacement
retains schema version `1.0`.

After command handling begins, stdout contains exactly one schema-valid JSON
object even when checks fail; logs use stderr. A complete overall `pass` or
`warning` exits zero. Overall `fail` or `unverified`, or any incomplete result,
exits nonzero. The extension still parses and displays valid JSON from a nonzero
exit. Invalid or missing JSON on nonzero exit is a Doctor-command
infrastructure failure. Aggregate priority is `fail`, then `unverified` or
`skipped`, then `warning`, then `pass`. Project JSON ends with the exact five
environment checks in stable order. Exit `130` is reserved for an incomplete
canceled `vba-dev` result with `excel.processCleanup: pass` and cannot hide an
observed failed check. The adapter Doctor accepts no project or document and
uses only adapter-owned temporary fixture state.

Adapter Doctor uses independent stage deadlines rather than one wall-clock
timeout. Workspace and lease creation has 5 seconds; Excel process startup has
30 seconds; fixture workbook creation, workbook open, and VBIDE access each
have 60 seconds; VBE command-context establishment, breakpoint setup, entry
into break mode, continue, and harmless-procedure completion each have 60
seconds. Cooperative process close has 5 seconds before Job Object termination,
and workspace deletion has the existing 5-second bounded retry.

The initial command exposes no timeout override, but cancellation remains
available throughout. A stage timeout is `unverified`, not a conclusive
capability `fail`, and dependent checks become `skipped`. When the timeout is
classified and cleanup reaches a terminal result, the diagnostic remains
`complete: true`; cancellation or infrastructure failure that prevents terminal
classification sets `complete: false`. The adapter-owned fixture expects no
interactive prompt, so an unexpected modal dialog consumes the applicable
finite stage deadline rather than creating an unbounded wait.

The Command Palette action `VBA Tools: Doctor` is the product-level aggregator.
It runs both commands independently, continues with the second when the first
fails, and displays separately labelled `Project automation` and `VBE debugging`
results. Neither underlying executable provides the aggregate operation, and no
new `vba-tools.exe` command is introduced. Capability inspection remains
side-effect free; only explicit Doctor execution may start temporary Excel/VBE
sessions, and neither diagnostic mutates persistent project files. Cancelling
during the project stage stops the ordinary `vba-dev` child and ends the
aggregate before adapter Doctor; that palette path does not claim cooperative
project cleanup, terminal JSON, or exit `130`. Once adapter Doctor starts, its
cooperative `stdin-v1` cancellation remains authoritative.

The Marketplace README describes this implemented contract and states the
user-visible promises: F5 and Restart capture current editor content without
saving it, debugging uses a session-temporary workbook without changing project
source, template, or bin output, debug workbook changes are discarded with the
session, the Command Palette Doctor aggregates the two independent diagnostics,
and the adapter executable has its own override setting. The README does not
expose DAP payload encoding, base64 transport, lease structure, subprocess
composition, or other internal mechanisms that are not part of the user
contract.

This supersedes ADR 0022 and the adapter-hosting and command-owned
session-artifact portions of ADR 0025. It retains the separate build/debug Excel
processes and strong debug-process lifetime decisions in ADRs 0019 and 0021.
ADR 0026 separately accepts snapshot input for `vba-dev test`, where the test
command creates, consumes, and removes its own internal workbook rather than
returning a caller-owned build output.
