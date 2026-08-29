# VBA Tools

VBA Tools provides Visual Studio Code tooling for exported VBA source files and
workbook-backed VBA projects. This glossary defines the domain terms used when
discussing language-server behavior, VS Code integration, and companion command
tooling for VBA.

For the shared CommonModules collection and distribution wrappers, the current implementation scope is intentionally plain Windows PowerShell. Small, readable improvements to the working scripts take precedence over unimplemented managed-helper state machines, native lease protocols, transactional recovery frameworks, and exhaustive failure taxonomies described in older exploratory dialogue.

## Product

**VbaTools**:
The repository-level product area for the VS Code extension, VBA language
server, Test Explorer integration, and companion CLI used for modern VBA source
workflows.
_Avoid_: VBA-LanguageServer, vba-devtools, VbaDev, xls-common-devtools

**VbaLanguageServer**:
The language-server component that provides editor intelligence for exported VBA
source files in VS Code.
_Avoid_: extension, CLI, test adapter

**VbaDev**:
The C#/.NET companion CLI that performs workbook-backed project operations such
as project creation, CommonModules management, reference management, build,
test, publish, export, read-only host-class inspection, and environment
diagnostics.
_Avoid_: language server, VS Code command, CommonModules package

**VscodeExtension**:
The VS Code extension package that activates VBA language support, launches the
language server, invokes `VbaDev` for project-level workflows and
`HostClassProjectionLifecycle`, and launches the separate `VbaDebugAdapter` for
VBE debugging.
_Avoid_: language server, command-line tool

**VbaDebugAdapter**:
The Debug Adapter Protocol entry point in a debug component separate from
`VbaDev`, distributed in the VSIX as the self-contained Windows x64
`vba-debug-adapter.exe`. It owns `VbeDebugSession` lifecycle, visible Excel and
VBIDE automation, and starts snapshot-aware `vba-dev build` subprocesses through
the public CLI contract without referencing `VbaDev` application assemblies. It
supports side-effect-free capability inspection, stdio launch, ordinary source
breakpoints, restart, termination, and
`DebugLifecycleOutput`, but does not project VBE interactive state or VBA
program output into VS Code.
_Avoid_: vba-dev subcommand, language server, project command

**VbaLaunchConfiguration**:
A VS Code `launch` configuration that selects a `WorkbookBackedProject`,
`DocumentSourceSet`, and `DebugTargetProcedure` explicitly or through the active
source position. `VscodeExtension` can synthesize it transiently for
zero-configuration F5 without creating or changing `launch.json`.
_Avoid_: attach configuration, test selector, command default

**ToolingCommand**:
A user-facing or automation-facing `VbaDev` command. It should have explicit
inputs, outputs, side effects, and verification behavior.
_Avoid_: script, helper, task

**VbaDevTerminal**:
A VS Code integrated terminal session opened by `VscodeExtension` for direct
`VbaDev` use. It scopes command availability to that terminal environment
rather than treating `VbaDev` as a machine-level PATH installation.
_Avoid_: global CLI install, project command, automatic project creation

**CompanionExecutableResolution**:
The extension-owned selection and side-effect-free compatibility validation of
the bundled or explicitly overridden `vba-dev.exe` and
`vba-debug-adapter.exe`. Overrides use `vbaTools.devtool.path` and
`vbaTools.debugAdapter.path`. A missing or incompatible `vba-dev` override
produces an actionable warning and falls back to a compatible bundled CLI; the
resulting effective CLI path is pinned for every extension consumer in that
session, and Doctor reports any difference from the configured path. A missing
or incompatible debug-adapter override remains a failed explicit selection and
does not fall back. Neither executable is discovered from `PATH`. A debug
session pins both effective absolute paths and passes the CLI path to the adapter
through `--vba-dev`.
_Avoid_: PATH lookup, sibling inference, automatic download, silent fallback

**VbaDebugAdapterContract**:
The extension-owned compatibility requirement stored as
`vba-debug-adapter-contract.json`, independent from `vba-dev-contract.json`.
Its initial capability contract requires adapter contract `1.0`, DAP extension
protocol `1.1`, stdio transport, lowercase-hex-32 session IDs, cleanup and
Doctor commands, Doctor schema `1.0`, and required VbaDev feature
`build.sourceSnapshot` version `1.0`. `VbaDev` advertises that build primitive
under `featureVersions` and does not advertise a debug-adapter protocol. The
adapter validates only the feature version it consumes rather than the CLI tool
version or complete project-command contract. Both capability inspections are
side-effect free.
_Avoid_: vba-dev contract, package version, Doctor readiness

**VbeDebugLaunch**:
A workbook-backed launch initiated from VS Code. The debug component captures
an immutable `DebugSourceSnapshot`, supplies its build-neutral source inventory
to `vba-dev build`, owns the resulting temporary `DebugWorkbook`, starts a
selected VBA procedure from that build in visible Excel, displays the target
code pane in the VBE, and hands interactive debugging to the VBE.
_Avoid_: VS Code-owned VBA debugger, headless macro run

**VbeDebugSession**:
A VS Code Debug Adapter session that owns `VbeDebugLaunch` configuration,
breakpoint transfer, and launch lifecycle while the VBE owns interactive
debugging. It remains running until its launched Excel process exits, and any
session termination force-terminates that process. One VS Code window owns at
most one active session. Closing its `DebugWorkbook` also terminates the owned
process and session. Restart captures a fresh `DebugSourceSnapshot` and replaces
the process through a complete new temporary build and launch.
_Avoid_: language-server session, VBE debugging session, headless macro run

**DebugExcelProcess**:
The dedicated visible Excel process owned by one `VbeDebugSession`. It is
separate from build automation and from the user's existing Excel sessions.
It enables macros only while programmatically opening its `DebugWorkbook`;
every workbook opened in the process belongs to the same session lifetime.
_Avoid_: build process, active Excel session, shared Excel instance

**AutomationExcelProcess**:
A dedicated hidden Excel process exclusively owned by one Excel-automating
`ToolingCommand` invocation, never a user's existing or shared Excel session.
One invocation may own zero, one, or multiple such processes sequentially and
remains responsible for every live instance through normal completion,
cooperative cancellation, and forced cleanup.
_Avoid_: DebugExcelProcess, active Excel session, shared Excel instance

**DebugWorkbook**:
A session-temporary workbook built from one `DebugSourceSnapshot` and opened by
a `DebugExcelProcess`. It uses the manifest-defined bin workbook's file name in
a snapshot-specific temporary directory, preserving `ThisWorkbook.Name` without
replacing completed bin output; `ThisWorkbook.Path` therefore identifies the
temporary directory. The debug component owns and removes the successful build
output after its Excel process ends; `VbaDev` owns only build-invocation scratch.
It is not a source of persistent workbook changes. Excel events are suppressed
while it opens and re-enabled before its `DebugTargetProcedure` runs; open-time
modal prompts remain user-facing.
_Avoid_: bin workbook, source template, publish workbook

**VbeBreakpoint**:
A native, session-local VBE breakpoint associated with one executable physical
line in an `ExportedVbaSource`. It is verified only after its source position
maps exactly and the native VBE breakpoint command succeeds. Multiple
colon-separated statements on that line retain the VBE's line-level stop
semantics.
_Avoid_: VS Code breakpoint, `Stop` statement, instrumented breakpoint

**BreakpointTransfer**:
The mapping of user-enabled ordinary VS Code line breakpoints from the selected
`DocumentSourceSet` into native `VbeBreakpoint`s at the same executable
physical line. User-disabled breakpoints and breakpoints outside that source set do
not participate; an in-scope breakpoint that cannot map exactly makes the
launch invalid rather than moving to another statement. Transfer is fixed
before procedure execution; later breakpoint changes apply to the next
`VbeDebugSession`. Participating VS Code breakpoints remain unverified while
setup is pending; any mapping or native command failure invalidates the entire
launch.
_Avoid_: breakpoint instrumentation, workspace-wide breakpoint copy, breakpoint fallback

**BreakpointSourceMap**:
The content-verified mapping from `.bas`, `.cls`, and `.frm`
`ExportedVbaSource` positions to the corresponding VBE code-module positions.
Export-only class headers such as `VERSION` and `BEGIN`/`END`, `Attribute`
records, and form designer records do not represent VBE code lines; the known
UserForm leading blank belongs to the projection.
_Avoid_: fixed line offset, raw file line number, form sidecar mapping

**DebugCompilationContext**:
The actual conditional-compilation environment of the snapshot-generated
temporary `DebugWorkbook` in its `DebugExcelProcess`. It determines which `#If` branches
can contain a `DebugTargetProcedure` or `VbeBreakpoint`; the launch
configuration does not override its constants.
_Avoid_: parser-inferred branch, launch-specific compiler constants, active-branch fallback

**DebugTargetProcedure**:
The parameterless public `Sub` in a standard module selected for a
`VbeDebugLaunch`. It is inferred from the active source position unless the
launch configuration explicitly identifies its document, module, and procedure.
It must be active in the `DebugCompilationContext`. A public procedure remains
eligible when its standard module contains `Option Private Module`.
_Avoid_: active file, test selector, startup macro

**VbeProcedureRun**:
The execution of a `DebugTargetProcedure` by selecting it in its VBE code pane
and invoking the native `Run Sub/UserForm` command. It supports an eligible
procedure in an `Option Private Module`; command unavailability or failure
invalidates launch.
_Avoid_: Application.Run, generated wrapper procedure, macro-dialog invocation

**VbeCommandContext**:
The design-mode VBE state in which the intended code pane is active, its window
has focus, and its exact physical line is selected before a native debug command
is resolved and executed. A command that remains disabled in this context
invalidates launch.
_Avoid_: caption matching, SendKeys, background code-pane selection

**VbeDebugEnvironment**:
The user-owned VBE debugging preferences and facilities that govern interactive
execution, including error trapping, compile-on-demand behavior, watches, and
explicit `Stop` statements. `VbeDebugLaunch` does not normalize or repair them,
so they may stop execution independently of transferred breakpoints.
_Avoid_: adapter debug state, session defaults, managed VBE profile

**DebugSourceSnapshot**:
The immutable selected-document source state from which one `VbeDebugLaunch`
resolves its target, breakpoints, source map, and `BuildSourceSnapshot`. The
extension captures its `SnapshotSourceInventory` without saving or rewriting
the `DocumentSourceSet`; text entries carry persistent source identity and
`SnapshotSourceEncoding`, while the adapter alone materializes and owns the
transported bytes.
_Avoid_: mutable editor state, stale build input, workspace-wide snapshot

**SnapshotSourceInventory**:
The complete selected-document source set fixed at capture start from one disk
inventory and the then-open dirty file-backed editors whose canonical paths
belong to that set, including an in-scope path not yet present on disk. Each
selected value is captured once without an end-of-capture stability check;
pathless documents cannot participate, and an inventoried disk path that cannot
be read fails capture.
_Avoid_: dirty-file overlay, active-editor-only snapshot, untitled source identity

**SnapshotSourceEncoding**:
The strict text-byte contract shared by debug and test source snapshots. Its
initial supported forms are UTF-8 with or without BOM, BOM-marked UTF-16 LE or
BE, and the operation-fixed active Windows ANSI code page without BOM. The
Windows snapshot workflow reads that code page directly from `GetACP` once at
operation start rather than inferring UI language, current culture, console
encoding, or `.NET Encoding.Default`; ACP 65001 is canonical UTF-8. Byte
detection checks a recognized BOM first, then strict UTF-8, then the strict
fixed ACP. Dirty legacy text is supported only when its editor code page equals
that ACP. Every text source must pass strict decode and exact re-encoding to its
original bytes before snapshot build or test starts Excel; clean source retains
those exact bytes, and `.frx` remains unvalidated binary content. Snapshot
capture and transport do not rewrite those bytes. `VbaDev` separately derives a
`VbeImportSourceSet` in the operation-fixed ACP without guessing, replacement
characters, or a lossy conversion. DAP text entries identify the captured form
as `utf8`, `utf8bom`, `utf16le`, `utf16be`, or
`windows-<decimal-code-page>`.
_Avoid_: arbitrary editor encoding, lossy fallback, treating VBE import bytes as snapshot bytes

**BuildSourceSnapshot**:
A caller-owned complete source directory explicitly supplied to `vba-dev build`
in place of reading the manifest-selected `DocumentSourceSet`. Its recursive
`.bas`, `.cls`, and `.frm` files and same-directory `.frx` sidecars are
authoritative as bytes, not an overlay to compare with persistent source. Their
paths preserve the original source-set-relative layout as provenance even though
build identity remains flat by exported file name.
`VbaDev` fixes the input in invocation-internal scratch and consumes it for one
build but does not own the caller's directory or successful output afterward.
Snapshot mode pairs `--source-snapshot` with a caller-selected `--output` path
outside the snapshot subtree and every manifest document's
`DocumentSourceSet`, and distinct from the resolved `vba-project.json` and every
document's source template, bin workbook, and publish workbook; neither option
is valid alone.
Before Excel starts, `VbaDev` compares case-insensitive, filesystem-canonical
path identities, including reparse-point aliases, and fails when it cannot
establish that the output is safe. Any other caller-owned target, including an
existing file, remains eligible for atomic replacement.
It does not contain debug targets, breakpoints, editor dirty-state concepts, or
artifact-lifecycle policy. Its producer preserves exact disk bytes for clean
source and sidecars; dirty editor text follows `SnapshotSourceEncoding`.
`VbaDev` never rewrites those accepted bytes; it derives a separate
`VbeImportSourceSet` for the VBE boundary.
_Avoid_: debug session, source overlay, implicit editor integration

**VbeImportSourceSet**:
The invocation-owned VBE-facing mirror derived from a `DocumentSourceSet` or
`BuildSourceSnapshot` before `VBComponents.Import`; text components strictly
round-trip through the operation-fixed active Windows ANSI code page while
`.frx` sidecars retain their exact bytes and relative pairing. An
unrepresentable or best-fit-only character fails before Excel starts, and the
mirror never changes caller-owned bytes and is removed with command scratch.
_Avoid_: source snapshot, persistent source conversion, lossy staging file

**SnapshotTestExecutionWorkspace**:
The command-owned temporary directory created only by
`vba-dev test --source-snapshot`. It contains the invocation-fixed source and a
test workbook whose file name matches the manifest-defined bin workbook. The
test command consumes and removes it after releasing owned Excel processes on
success, failed assertions, command failure, and cancellation; it never owns the
caller's snapshot directory or mutates persistent bin output. Failure to prove
owned-process release is a command-level infrastructure error. After release is
proved, workspace deletion receives bounded retries; a remaining deletion
failure retains and reports the absolute path as a warning without changing
individual test outcomes or the test-result exit status.
_Avoid_: BuildSourceSnapshot, caller-selected build output, persistent bin directory

**TestExecutionTimeout**:
The positive, finite, whole-second deadline applied only to the test macro
execution stage of ordinary, snapshot, and no-build `vba-dev test`. Resolution
is `--timeout-seconds`, then
`commandDefaults.test.executionTimeoutSeconds`, then 600 seconds. It does not
shorten build, workbook open/save, reference normalization, or cleanup stages.
`VscodeExtension` adds no independent timeout or shorter watchdog and relies on
the CLI outcome and cancellation contract.
_Avoid_: command timeout, Excel timeout, infinite timeout, VS Code watchdog

**VbaDebugSelectionError**:
An extension-owned failure to select exactly one project, document, eligible
procedure, or valid participating breakpoint from a captured
`DebugSourceSnapshot`, including unsupported or lossy dirty-source encoding.
It occurs before adapter or Excel launch and does not become a
`DebugSetupError`.
_Avoid_: adapter setup failure, VBA runtime error, target picker

**DebugSetupError**:
A failure after debug selection succeeds but before the `DebugTargetProcedure`
begins, including build, workbook open, source mapping, breakpoint setup, VBE
compile failure, or `VbeProcedureRun` command failure. A modal VBE compile error
may remain interactive without a timeout until the user dismisses it or cancels
launch. A setup error prevents `VbeDebugLaunch` from becoming an interactive
run.
_Avoid_: VBA runtime error, break mode, failed assertion

**VbeRuntimeError**:
A VBA runtime error raised after the `DebugTargetProcedure` begins and presented
through Excel or the VBE without ending the `VbeDebugSession`.
_Avoid_: debug setup error, launch failure, test failure

**DebugLifecycleOutput**:
The build, prompt-wait, breakpoint-verification, procedure-run, setup-error, and
Excel-process lifecycle messages emitted by the separate `VbaDebugAdapter` to
the VS Code Debug Console. It does not copy `Debug.Print`, Immediate Window
content, or VBE runtime state.
_Avoid_: VBA output stream, Immediate Window mirror, debug-state projection

**DebugLaunchCancellation**:
An explicit stop before or during `VbeDebugLaunch` completion. It terminates any
owned build or debug Excel process. `VbaDev` cleans only its active invocation's
internal scratch; after that invocation exits, the debug component removes its
caller-owned snapshot and `DebugWorkbook`. Persistent project source and
completed bin output remain unchanged, and cancellation is reported rather than
a `DebugSetupError`.
_Avoid_: launch failure, graceful workbook close, persistent source rollback

**DebugSessionId**:
The lowercase 32-character hexadecimal identifier generated by
`VscodeExtension` from 128 bits of cryptographically secure randomness before
it starts one `VbaDebugAdapter` session. The extension supplies it through the
required `--session` argument, retains it for crash cleanup, and reuses it for
Restart within that debug session. It is an opaque path-safe identifier, not a
`DebugWorkspaceLease` ownership token.
_Avoid_: lease ID, project ID, adapter-generated cleanup handle

**DebugRestartPreparation**:
The transaction that captures and validates a fresh `DebugSourceSnapshot`
before replacing an active `VbeDebugSession`. Its identity is bound to the
session, canonical project, selected document, original target module and
procedure, restart generation, and DAP request sequence; any mismatch or
missing target retains the current session.
_Avoid_: new launch target, active-editor retargeting, project-only restart token

**DebugWorkspaceLease**:
The adapter-owned record atomically created under the canonical directory named
by one `DebugSessionId`, containing the owner PID, process start time, and a
separate random lease ID. An existing directory is never reused or deleted
during claim. The lease prevents a cleanup process or later adapter start from
confusing a live or PID-reused session with stale temporary state.
_Avoid_: project lock, manifest lock, arbitrary cleanup path

**DebugSessionReaper**:
The internal `vba-debug-adapter cleanup --session <session-id>` operation and
equivalent next-start scan that remove only stale leased directories beneath the
adapter-owned workspace root. `VscodeExtension` can invoke it even when adapter
initialization failed because it generated and retained the `DebugSessionId`
before process launch. An invalid ID fails before filesystem access. A missing
or never-claimed workspace is a silent successful no-op; a proved-stale
workspace receives five seconds of bounded deletion retries. Reapers refuse a
live or unverifiable lease and report a retained absolute path on stderr without
widening deletion scope. Their nonzero result is a housekeeping warning that
does not alter an already determined debug outcome. A next-start scan continues
past an unrelated retained workspace, and the initial cleanup command has no
JSON output contract.
_Avoid_: recursive temp cleanup, project cleanup, user-facing project command

**ConsoleEntryPoint**:
The C# entry point that parses command-line arguments, invokes a
`ToolingCommand`, and returns a meaningful process exit code.
_Avoid_: UI, macro, language-server endpoint

**DotNetProject**:
A .NET project that builds `VbaDev`, its tests, or shared implementation
code in this repository.
_Avoid_: workbook project, npm package

**CommonModulesPackage**:
A versioned release artifact produced by `xls-common-devtools`, normally as
`common_modules_repo.zip`, that provides shared VBA source files and a
machine-readable CommonModules manifest consumed by `VbaDev`.
_Avoid_: vendored source, submodule, built-in library

## CommonModules

**CommonModulesRepository**:
A generated closed flat package directory named `common_modules_repo`. It contains the canonical manifest, exactly one root-level source unit for every manifest row, and only each listed form's optional matching `.frx`; every other entry is outside the package. COLLECT writes this output, while mandatory baseline and fallback candidates come only from `CommonModules Authoring Source Set`.
_Avoid_: authoring source set, transaction workspace, package cache

**Collection Search Root**:
The workspace directory explicitly supplied to COLLECT. COLLECT recursively discovers `vba-project.json` files without descending into reparse child directories; the explicit root itself may be a reparse path, while neither the script location nor a discovered package repository substitutes for this input.
_Avoid_: output root, script repository, distribution search root

**CommonModules Authoring Source Set**:
The unique discovered project document source set whose root directly contains `common-modules-manifest.tsv`. It establishes the canonical manifest and mandatory fallback sources, but its project manifest does not locate the wrapper's repository; zero or multiple matches establish no collection authority.
_Avoid_: generated common_modules_repo, folder-name convention, arbitrary manifest match

**Wrapper Repository Parent**:
The working directory captured when COLLECT or DIST starts. Its direct `common_modules_repo` child is the COLLECT output or DIST source, independently of either Search Root and any project manifest's `commonModulesRepository` value.
_Avoid_: collection search root, distribution search root, project manifest directory

**Distribution Search Root**:
The workspace directory explicitly supplied to DIST, whose immediate child project directories may contain opted-in `CommonModulesRepository` targets. It is independent of the source repository under the `Wrapper Repository Parent` and is never inferred from that repository's path.
_Avoid_: source repository, source parent, distribution target

**Distribution Target**:
An existing exact `common_modules_repo` beneath an immediate child project directory of the `Distribution Search Root`, excluding the central source repository. Project manifests, repository names, nested directories, and missing repositories do not create distribution authority.
_Avoid_: inferred target, nested project, newly created repository

**Distribution Candidate Set**:
The path-ordered target set fixed by one initial scan of the `Distribution Search Root`. A target appearing later waits for another invocation; a selected target that disappears before its turn becomes a `Distribution Warning Skip` without widening or rebuilding the set.
_Avoid_: live target query, retry set, recursive discovery

**Distribution Package Match**:
Two closed flat `CommonModulesRepository` inventories with the same ordinal-exact root-level names and the same per-file `LastWriteTimeUtc` and length values. DIST does not read file bytes or hashes to establish this match.
_Avoid_: content equality, directory timestamp, hash comparison

**Distribution Candidate Failure**:
A conclusive defect or operation failure isolated to one distribution candidate. The candidate is not retried or rolled back, later candidates remain eligible, and the DIST invocation ultimately fails.
_Avoid_: warning skip, global failure, automatic recovery

**Distribution Warning Skip**:
A candidate whose opt-in state cannot be established before mutation, or which disappears before its turn begins. DIST leaves it untouched, warns, and continues; this classification alone does not fail the invocation.
_Avoid_: candidate failure, silent exclusion, retry

**Distribution Global Failure**:
An invalid or unreadable central source, `Distribution Search Root`, or complete discovery operation. It prevents a trustworthy candidate set and stops the invocation with failure.
_Avoid_: candidate failure, warning skip

**CommonModulesRuntimeBaseline**:
The shared VBA source files required for ordinary runtime use of CommonModules
inside a `DocumentSourceSet`.
_Avoid_: all common modules, test modules

**CommonModulesTestFoundation**:
The shared VBA source files required to author and run VBA unit tests inside a
`DocumentSourceSet`.
_Avoid_: runtime baseline, project-specific tests

**CommonModulePrimaryRole**:
The exactly one repository classification that places a CommonModules entry in
`runtime-baseline`, `test-foundation`, `optional`, or `test-double`. It determines
initial-root selection and the installed entry's test-only classification.
_Avoid_: arbitrary category bag, category precedence

**CommonModulePublicUdfModifier**:
The optional `public-udf` classification attached only to a runtime
`CommonModulePrimaryRole`; it describes worksheet exposure without changing root
selection or test-only classification.
_Avoid_: primary role, publish exclusion

**CommonModuleDependency**:
A shared VBA source file that must accompany another CommonModules file for that
file to work inside a `DocumentSourceSet`. A runtime entry may depend only on
runtime entries, while a test-only entry may depend on either classification.
_Avoid_: optional module, copied file list

**CommonModuleDependencyComponent**:
A maximal set of CommonModules entries that are mutually reachable through
`CommonModuleDependency` relationships. Its members remain distinct entries but
form one indivisible unit when dependency closure and canonical order are derived.
_Avoid_: invalid cycle, merged CommonModule

**CommonModuleRequiredReference**:
A human-visible `VbaProjectReference` name declared by one CommonModules
repository-manifest entry as a direct selectable external dependency of that
source entry. It excludes the always-active `VbaStandardLibraryReference`; an
installed dependency closure takes the ordered union of included declarations.
_Avoid_: inferred reference, source-scanned reference, sidecar reference

**CommonModulesReferenceResolutionEvidence**:
An invocation-fixed conclusive VBE-equivalent result for one missing
`CommonModuleRequiredReference`, scoped to a `ProjectDocument` and its selected
canonical source template before mutation-lease acquisition. It may authorize
only the same still-missing requirement after latest-state replanning and is
neither cached catalog data nor manifest selection authority.
_Avoid_: reusable reference cache, live resolution, selected reference

**CommonModuleName**:
The stable case-insensitive extensionless `ModuleIdentity` used to identify one
CommonModules source entry across manifests and tooling. Its canonical spelling
exactly matches repository source metadata and the flat exported-source basename;
repository reconciliation may refresh an installed spelling without creating a
new identity.
_Avoid_: file path, file name with extension, display label

**InstalledCommonModule**:
A CommonModules source entry that has been added to one `DocumentSourceSet` and
is tracked as part of that document's desired shared-source set. It retains its
extension-including source file identity and test-only classification independently
of current `CommonModulesRepository` availability or source-content drift.
_Avoid_: inferred module file, loose copy, reference

**OrphanedInstalledCommonModule**:
An `InstalledCommonModule` whose retained identity is absent from the latest
successfully validated `CommonModulesRepository`. Its source remains active in
the desired shared-source set, and the state is advisory evidence for a future
fully revalidated purge rather than proof of retirement, rename, or a successor.
_Avoid_: retired module, renamed CommonModule, disabled module

**CommonModulesMutationIntent**:
The request to add named CommonModules to one document or update every currently
installed entry, evaluated against the latest valid **ProjectManifest** and
repository/source layout. It is not an invocation-start manifest replacement or
a reusable pre-lease copy plan.
_Avoid_: manifest snapshot, stale copy plan

**CommonModulesMutationSnapshot**:
The invocation-fixed repository bytes and per-target existence and raw-byte
preconditions that authorize one **CommonModulesMutationIntent** after
latest-state replanning. It prevents mixed repository generations and lost
target edits without making `--force` an unconditional overwrite grant.
_Avoid_: live repository copy, package version, target backup

**CommonModulesMutationCommitmentBoundary**:
The first planned CommonModules source-file deletion or copy, after which an
observed cancellation is deferred until manifest commit or recovery-result
determination preserves an intelligible project state. It is not itself a
success boundary, and later transaction failure remains failure.
_Avoid_: manifest commit, cancellation success, automatic rollback

**ManagedModuleIdentity**:
The `ModuleIdentity` of a manifest-listed `InstalledCommonModule`, whose stable `CommonModuleName` and extension-including source identity remain owned by the CommonModules contract even when its local content drifts. It becomes project-local identity only through an explicit detach from that contract, not through ordinary semantic Rename.
_Avoid_: local CommonModule name, read-only module, repository filename

**CommonModulesDirectory**:
The `common-modules` organization directory inside a `DocumentSourceSet`. It is
the default placement for CommonModules source files when `VbaDev` needs to copy
an `InstalledCommonModule` source file but no existing same-name source file
already chooses a location. It does not create a separate source set, and
CommonModules installation is still determined by the `ProjectManifest`.
_Avoid_: CommonModulesRepository, source set, installed-module marker

## Workbook Projects

**WorkbookBackedProject**:
A VBA development project that keeps exported source files and one or more
Office macro documents under a project manifest. The initial supported document
kind for workbook-backed automation is an Excel `.xlsm` workbook.
_Avoid_: workspace folder, repository, source folder

**ExplicitWorkbookExport**:
A `VbaDev` export operation scoped by a caller-provided workbook path rather
than by a `ProjectManifest` document definition.
_Avoid_: path-only export, ad hoc export, project export

**ExplicitWorkbookImport**:
A `VbaDev` import operation scoped by a caller-provided source directory and
workbook path rather than by a `ProjectManifest` document definition. Its
compatibility authority is the staged source identity set together with the
live target's current project, reference, and retained-component names.
_Avoid_: path-only import, ad hoc import, project import

**WorkbookMaterializationNamePreflight**:
The compatibility decision required before a generated workbook accepts its
selected source set, using authoritative source identities and the workbook's
actual final project, active-reference, and retained-component names. A failed
decision preserves the complete deterministic conflict set rather than only its
first member; it is not compile verification.
_Avoid_: reference resolution, compile check, CommonModules consistency check

**ProjectManifest**:
The project-local manifest, stored as `vba-project.json`, that identifies a
`WorkbookBackedProject` and carries default values for VS Code commands and
`VbaDev` operations. It is also the language server's source of truth for the
`VbaProjectReferenceSelection` of each document definition; VS Code settings do
not define project references for workbook-backed projects. It identifies the
source template inspected for `HostClassProjection`, but does not store a
generated host Event list. A project-local `project.json` is not a
`ProjectManifest` for language-server project-boundary or reference-selection
behavior.
_Avoid_: package file, extension settings, workspace settings

**ProjectManifestSchema**:
The closed, case-sensitive schema-1 structural vocabulary of a
**ProjectManifest**. It supports editor feedback but does not replace
`VbaDev` authority over manifest bytes, cross-field relationships, or domain
invariants, and it does not describe a **ProjectManifestRecoveryArtifact**.
_Avoid_: command-result schema, recovery schema, complete manifest validator

**ProjectManifestRecoveryArtifact**:
An immutable create-new sibling copy of a validated planned **ProjectManifest**
emitted only when prior project-file mutation cannot be paired with a trusted
canonical manifest commit. Its atomically committed final path is manual merge
input, never an automatically applied replacement.
_Avoid_: backup manifest, automatic restore, temporary manifest file

**InitialProjectCreation**:
The transition from an absent **WorkbookBackedProject** to one established by
its initial atomic **ProjectManifest** commit. Before that boundary, only
provably invocation-owned unchanged artifacts are rollback state; pre-existing,
unknown, externally changed, or process-surviving unproven content never is.
_Avoid_: directory scaffolding, partial project

**RequestedProjectRoot**:
The lexically normalized absolute project-root spelling selected by a creation
caller and retained in user-visible results and navigation. It is not the
filesystem identity used to prove ownership.
_Avoid_: physical path, lease identity, resolved symlink target

**ProjectRootIdentity**:
The case-insensitive filesystem identity that unifies aliases of one project
root for lease, collision, ownership, and safety decisions. It is not persisted
or exposed as a replacement for the **RequestedProjectRoot**.
_Avoid_: displayed project path, manifest field, URI spelling

**InitialProjectTarget**:
The **ProjectRootIdentity** selected for **InitialProjectCreation**, reached
through one **RequestedProjectRoot** and eligible only while its complete
inventory contains the owned lease marker and unchanged invocation-owned
artifacts. Any foreign, missing, replaced, changed, or identity-divergent state
before commit ends eligibility rather than being adopted into the project.
_Avoid_: merge destination, existing project root, force target

**ExcelProjectTemplateBaseline**:
The semantically empty macro-enabled source template created for `new excel`
with exactly one worksheet, independent of per-user new-workbook sheet-count or
default-template choices. It is not a bundled binary fixture and is not promised
to serialize byte-for-byte identically across Excel versions.
_Avoid_: user workbook template, default workbook, reproducible binary

**InitialWorkbookIdentityBaseline**:
The locale-independent identities established for an
**ExcelProjectTemplateBaseline**: `Sheet1` for both the visible worksheet and
its document module, `ThisWorkbook` for the workbook document module, and
`VBAProject` for its **VbaProjectName**. These identities are independent of the
manifest project name, document name, and workbook file name.
_Avoid_: Excel-selected default identities, localized identities, project-name-derived VBA identities

**InitialVbaProjectReferenceSelection**:
The initial ordered non-baseline reference intent for a generated document,
formed from the actual external references of its source-template baseline and
the declared external-reference requirements of its installed initial
CommonModules closure. It excludes the always-active
**VbaStandardLibraryReference**, records baseline references as directly
requested and CommonModules-only additions as not directly requested, and never
infers requirements from VBA source.
_Avoid_: standard initial references, hard-coded Scripting and RegExp references, source-inferred references

**ProjectManifestMutationLease**:
The **ProjectRootIdentity**-scoped cross-process exclusive ownership held by `VbaDev`
across a trusted mutation window. Existing projects hold it from the final
trusted manifest reload before the first project mutation through atomic
manifest commit or recovery-result determination; initial project creation
holds it from before the first project artifact through initial atomic manifest
commit or through pre-commit rollback of in-target artifacts. Terminal creation
outcome is determined only after lease release and marker and directory cleanup;
the lease serializes `VbaDev` writers without covering long read-only discovery
or controlling independent editors.
_Avoid_: whole-command project lock, manifest version, editor lock

**ProjectManifestMutationPreflight**:
The VS Code extension gate immediately before it invokes a `VbaDev` command
that mutates the selected `ProjectManifest`. When the matching file-backed
editor buffer is dirty, the user chooses `Save and Continue` or `Cancel`; only
that manifest is saved. After a successful save, the extension rereads and
validates its selection-critical projection and re-resolves the exact selected
project and document before process launch. Cancellation, save failure, an
unusable disk projection, or a missing selected target starts no CLI process.
The extension does not overlay or structurally merge an unsaved manifest into
CLI output, and `VbaDev` retains authority over complete manifest validation and
writing during mutation. A clean open buffer that differs from disk offers
immutable comparison, explicit reload, or cancellation and restarts preflight
only after buffer-to-disk equality is proved.
_Avoid_: save all, automatic dirty-buffer merge, CLI editor state, pre-save target reuse

**ProjectManifestPostMutationCoherence**:
The VS Code extension reconciliation applied whenever a launched
manifest-mutating `VbaDev` invocation leaves a changed post-invocation manifest
with a structurally usable selection projection on disk. The extension records
the pre-launch buffer text, dirty-state
observations, and every distinct content revision. The only passive-safe content
transition is a clean direct transition from the pre-launch text to the exact
post-invocation disk snapshot, with no other distinct content or dirty
observation. Auto Save or a later clean state therefore does not erase evidence
of an intermediate edit. When competing content was observed, the extension
preserves its snapshot, performs no reload, save, or structural merge, and
warns that the CLI outcome and editor state may have diverged. Recovery offers
`Compare Changes` against the immutable post-invocation disk snapshot,
confirmation-gated `Reload from Disk` after proving the disk state and editor
revision are still the confirmed pair, and non-mutating `Keep Editing`; it
offers no automatic merge. For a passive-safe buffer, the extension allows two
seconds for VS Code's native external-file synchronization and proves that the
buffer is clean and equals both current disk text and the immutable snapshot.
If it does not converge, the extension neither moves editor focus nor rewrites
the buffer; it reports incomplete coherence and offers explicit recovery,
retaining the same-manifest mutation guard.
_Avoid_: unconditional reload, last-writer-wins save, automatic JSON merge, silent divergence

**ProjectManifestEditorDivergence**:
The unresolved state after a launched CLI mutation in which an open
`ProjectManifest` buffer has competing editor evidence or cannot be proved clean
and equal to both current disk state and the immutable post-invocation disk
snapshot. While this state persists, the extension blocks another
manifest-mutating command for the same canonical manifest identity and reoffers
the explicit coherence recovery actions. Passive synchronization clears it only
without competing evidence; after competing evidence, resolution requires an
explicit recovery action and a fresh equality proof. Read-only commands may
continue against disk state when that basis is made visible to the user; this
exception covers Reference List, CommonModules List, and project automation
Doctor rather than workbook- or source-mutating workflows.
_Avoid_: mutation retry, implicit overwrite, global command lock

**ProjectManifestMutationBusyGuard**:
The VS Code window-local single-flight guard for one canonical manifest
identity. While one manifest-mutating command owns the guard, a later mutation
for that manifest is rejected as busy rather than queued, and identifies the
running command and selected target. The guard remains held through
post-mutation coherence or divergence determination. Other manifests may run
concurrently; cross-window and direct-CLI writers remain coordinated by
`ProjectManifestMutationLease`.
_Avoid_: global single flight, stale queued mutation, replacement command

**ProjectManifestMutationOutcome**:
The extension-side manifest-coherence classification that combines a launched
mutation process's terminal result with the manifest bytes captured immediately
before launch and read after child-process exit. Structurally usable
byte-identical state proves only that the manifest is unchanged and needs no
editor reconciliation;
it does not prove that the whole operation was a no-op or that changes to other
files were rolled back. Operation success and no-op status remain owned by the
command's schema-valid result. Structurally usable changed bytes enter
`ProjectManifestPostMutationCoherence`; after failure or cancellation they also
produce an abnormal-change warning rather than a rollback claim. A missing,
unreadable, or structurally unusable post-invocation manifest is untrusted and
blocks another mutation for that identity until explicit recovery. Process
status alone never proves that a launched writer did not commit.
_Avoid_: manifest-bytes-as-operation-result, exit-code-only commit inference, cancelled-means-unchanged

**LanguageServerManifestResolution**:
The lightweight language-server process that reads `vba-project.json` directly and
resolves `ProjectManifest`, `DocumentSourceSet`, and
`VbaProjectReferenceSelection` for editor features. Completion, hover, and
signature help do not synchronously invoke `VbaDev` to resolve project or
reference state. Background `VbaProjectReferenceCatalogRefresh` may invoke the
machine-readable `vba-dev reference list --format json` contract with only
project and document inputs; the selected `ProjectManifest` supplies the
reference names. `HostClassProjectionLifecycle` separately supplies committed
immutable host-class projections; synchronous editor requests neither invoke
nor wait for their producer. `VbaDev` does not consume VS Code editor state,
and the language server does not own Excel/VBIDE automation. `VscodeExtension` resolves
the bundled or explicitly configured absolute `vba-dev.exe` path and passes it
to the language server through `--vba-dev`. At startup, the server validates
that supplied executable once through side-effect-free capability inspection
and requires `reference list` JSON schema `1.0`; it does not repeat validation
for each catalog refresh. The server does not search `PATH`, inspect VS Code
settings, infer a sibling executable, or replace the supplied selection. A
missing argument or failed startup validation records a warning and keeps
registry-only, fail-closed catalog discovery without stopping the language
server. A schema-valid, complete `reference list` response with project scope
and matching project, document, and mode is consumed per reference even when
the command exits nonzero: resolved entries may update their catalogs, while
conclusively ambiguous or unavailable entries preserve their own
`LastKnownGoodReferenceCatalog`. An unverified entry makes the response
incomplete. Malformed JSON, schema or request-context mismatch,
`complete: false`, or nonzero exit without a valid response rejects the entire
invocation and preserves every affected last-known-good catalog.
_Avoid_: CLI-backed completion, command-line manifest resolution, synchronous tooling call

**ExportedVbaSource**:
A `.bas`, `.cls`, or `.frm` text file exported from a VBA project and edited or
analyzed outside the VBE.
_Avoid_: workbook, code blob

**CommandPaletteManifestSelectionProjection**:
The narrow on-disk `ProjectManifest` view used only to choose a Command Palette
project and applicable document. It proves the required project,
primary-document, document-name, source-path, canonical source-root, and
`DocumentSourceSetIsolation` evidence without consuming an unsaved manifest
overlay or replacing the existing structural schema and complete `VbaDev`
domain validation.
_Avoid_: ProjectManifestSchema, complete manifest validator, dirty-buffer overlay

**CommandPaletteProjectTarget**:
The one `WorkbookBackedProject` selected from an on-disk, selection-capable
`CommandPaletteManifestSelectionProjection` for a project-aware VS Code Command
Palette invocation. The nearest manifest containing the active file identifies
the candidate and must supply the required project, primary-document,
document-name, source-path, and `DocumentSourceSetIsolation` evidence. An
unusable nearest manifest fails closed rather than falling back to an ancestor
or workspace candidate. Without a containing manifest, the sole
selection-capable workspace project is automatic and multiple projects require
explicit user choice. This projection does not replace `VbaDev` as the complete
manifest validator. The selection is invocation-local and is never recovered
from remembered state. Cancellation starts no CLI process.
_Avoid_: arbitrary workspace project, last selected project

**CommandPaletteDocumentTarget**:
The one `DocumentSourceSet` selected from the same on-disk
`CommandPaletteManifestSelectionProjection` within a
`CommandPaletteProjectTarget` for a document-scoped invocation. A file-backed
active `ExportedVbaSource` selects its uniquely owning source set; otherwise a
project with exactly one manifest document selects that document automatically,
while multiple documents require explicit user choice. `PrimaryOfficeDocument`
does not break a multiple-document tie. The extension always passes the
selected document name explicitly to `VbaDev`; this caller rule neither removes
the primary document nor makes `--document` globally mandatory for direct CLI
use. An unusable selection projection cannot supply a target. Before process
launch, the extension shows the target in cancellable progress and command
output without adding a generic target-only modal confirmation; command-specific
consent remains in force.
_Avoid_: active file's document, last selected document, primary-document fallback, omitted CLI document

**CommandPaletteDocumentFocus**:
The initially active QuickPick item when a `CommandPaletteDocumentTarget`
requires explicit choice. The extension captures `activeTextEditor` before the
chooser opens and prefers its owning document; if that cannot identify a
document, a non-empty set of eligible visible sources that all identify one
document, then a non-empty set of eligible open sources that all identify one
document, receives focus. Mixed or absent evidence focuses the
manifest's `PrimaryOfficeDocument`. Focus never accepts an item automatically,
and inactive editors' retained cursor selections do not prove input focus.
_Avoid_: automatic choice, remembered focus, any editor with a cursor

**VbaFormSidecar**:
An `.frx` binary sidecar that stores non-text designer data for a `.frm` form.
It belongs to the same form source unit as the matching same-directory `.frm`
file and does not define separate source identity or placement.
_Avoid_: exported source, separate module, generated cache

**PrimaryOfficeDocument**:
The single Office macro document that a `WorkbookBackedProject` treats as the
subject of project lifecycle commands.
_Avoid_: arbitrary workbook, generated output, secondary document

**DocumentSourceSet**:
The exported VBA source files and source template document that belong to one
Office macro document within a `WorkbookBackedProject`. Nested organization
directories under the document source path do not create separate source sets;
exported VBA source identity remains flat, and extension-including source file
names must be unique within the source set.
_Avoid_: source folder, document, test suite

**DocumentSourceSetIsolation**:
The `ProjectManifest` invariant that every document's source-path root is physically disjoint from every other document's root. Equal roots, ancestor/descendant roots, and filesystem aliases that overlap the same subtree are invalid; shared VBA source is represented through explicit distribution ownership rather than one file belonging to multiple document projects.
_Avoid_: first matching document, shared sourcePath, nested document source set

**VbaProjectReference**:
A library reference that one Office macro document's VBA project requires to
compile or run. A `DocumentSourceSet` may require zero or more
`VbaProjectReference`s, named by the human-visible library name shown to VBA
developers. A manifest entry records whether that dependency is directly
requested independently of CommonModules; both directly requested and
CommonModules-introduced entries remain effective, while the distinction is a
conservative eligibility marker for future auto-removal rather than current
deletion authority. It does not prove that current module and project names are
compatible with the library.
_Avoid_: Reference, .NET ProjectReference, CommonModuleDependency

**MainVbaProjectReference**:
The `VbaProjectReference` that corresponds to the `PrimaryOfficeDocument` kind
and acts as the precedence winner for unqualified external definition names when
multiple referenced libraries provide the same name. For an Excel document, this
is the Excel object library; for a Word document, this is the Word object
library; and equivalent Office document kinds follow the same rule. It is the
expected main reference for the document kind, but it contributes definitions
only when that reference is present in the `VbaProjectReferenceSelection`. Other
equal rank external matches remain ambiguous. Host-generic root names such as
`Application` and `ActiveWindow` come from whichever catalog is the active main
reference and are not synthesized for projects without that reference.
_Avoid_: active reference, preferred library, MainHostApplication

**ProtectedVbaProjectReference**:
A `VbaProjectReference` that Office or VBIDE keeps as part of the workbook's VBA
project and that tooling should not remove during generated workbook
normalization.
_Avoid_: built-in reference, default reference, undeletable reference

**PublishableVbaSource**:
An exported VBA source file from a `DocumentSourceSet` that should be imported
into the distributed Office macro document.
_Avoid_: test-only source, build-only source

**TestOnlyVbaSource**:
An exported VBA source file used for authoring or running VBA unit tests and
excluded from published Office macro documents by default.
_Avoid_: runtime source, publishable source

**PublishExclusionMarker**:
The `'#ExcludePublish` source comment marker that declares a project-local
exported VBA source file as `TestOnlyVbaSource` or otherwise not publishable.
_Avoid_: filename-only test detection, implicit publish exclusion

## Testing

**TestExplorerNode**:
A VS Code Testing API item representing a runnable or discoverable testing scope
for workbook-backed VBA tests. Project, document, and module nodes are runnable
scopes; a discovered `TestProcedure` node may additionally carry its
`TestProcedureSourceLocation`.
_Avoid_: test result row, source symbol, command

**TestProcedure**:
A VBA procedure that the workbook-backed test runner can execute and report as
an individual test after a `DocumentSourceSet` or project test run.
_Avoid_: macro, module, assertion

**TestProcedureSourceLocation**:
The exported source URI and declaration-name range that identify one
`TestProcedure` within its `DocumentSourceSet`. An unavailable or ambiguous
location does not change the test outcome. For snapshot test mode, its range
comes from the invocation-fixed snapshot bytes while its URI identifies the
corresponding persistent source path; it never exposes an internal workspace
URI.
_Avoid_: failure location, result location, test file location

**TestDiscoverySnapshot**:
The output-derived set of module and `TestProcedure` `TestExplorerNode`s for one
`DocumentSourceSet`. It remains valid only while that document's exported VBA
source and project definition remain unchanged. A normal snapshot test run may
carry ranges derived from captured unsaved editor state while retaining stable
persistent source URIs; ordinary and no-build runs use saved-source locations.
If the document-level source/project revision changes during a snapshot run,
outcomes remain visible but the resulting discovery snapshot and locations are
not committed. Initial invalidation is document-wide rather than per-file.
_Avoid_: test index, cached tests, static source discovery

**TestRunError**:
A project-level or document-level failure that prevents a workbook-backed test
run from completing as a normal set of `TestProcedure` outcomes.
_Avoid_: failed assertion, failed test, diagnostic

**TestResultOutput**:
The command-line report emitted by a `ToolingCommand` after running VBA unit
tests for a `PrimaryOfficeDocument`.
_Avoid_: worksheet result sheet, internal test state

**EnvironmentDiagnostic**:
A read/check-oriented `vba-dev doctor` function that reports whether the local
Windows, Excel, COM, VBIDE, project prerequisites, profile-specific workbook
materialization names, CommonModules state, and reference catalog availability
can support workbook-backed project automation and editor intelligence. It does
not diagnose native VBE debugging.
_Avoid_: build, test run, repair command

**GuidedProjectCreation**:
The `VscodeExtension` workflow exposed as `VBA Tools: Create Excel VBA Project`
that delegates authoritative project creation to `VbaDev` while owning the
guided preflight, input, result-validation, and navigation experience. It is
available without an existing workspace or `ProjectManifest` and creates only
under a `file:` Windows filesystem parent reachable by the local CLI.
_Avoid_: `new excel` command, VbaDevTerminal, automatic workspace creation

**GuidedProjectCreationPreflight**:
The project-independent `EnvironmentDiagnostic` scope run before guided project
input to prove Windows, Excel COM, actual VBIDE project access, and owned-process
cleanup readiness without creating project state. It does not prove native VBE
debugging or replace the creation command's own host validation.
_Avoid_: post-create Doctor, registry setting inference, debug readiness probe

**ProjectCreationPathValidation**:
The versioned pre-artifact composition shared by `GuidedProjectCreation` and
`VbaDev`, combining one `ProjectNameLexicalContract` with the selected host's
document-path contract without rewriting user input. Its feature version is
independent from the creation command's successful-result schema.
_Avoid_: filename sanitization, UI-only validation, generic path validation

**ProjectNameLexicalContract**:
The host-neutral part of `ProjectCreationPathValidation` that preserves an exact
well-formed Unicode spelling while establishing one valid Windows basename.
_Avoid_: VBA identifier rule, Excel filename rule, trimmed project name

**ExcelWorkbookPathContract**:
The Excel-specific part of `ProjectCreationPathValidation` that establishes
bracket compatibility and full-path viability for every Excel-facing workbook.
_Avoid_: Windows basename contract, generic `MAX_PATH`, host-neutral path rule

**DebugEnvironmentDiagnostic**:
The independent `vba-debug-adapter doctor --format json` active probe that uses
a temporary Excel/VBE session to verify native command context, an actual
breakpoint stop, `VbeProcedureRun` completion, `DebugExcelProcess` ownership,
workspace leases, reaping, and cleanup without changing persistent project
files. Its schema `1.0` reports `schemaVersion`, `toolVersion`, overall
`status`, `complete`, and ordered checks with stable ID, status, message, and
duration plus optional remediation and details. Status is `pass`, `warning`,
`fail`, or `unverified`; a dependency-blocked check may be `skipped`. A complete
pass or warning exits zero, while failure, unverified state, or incomplete
execution exits nonzero. Once handling starts, even a failed diagnostic emits
one valid JSON object on stdout; logs use stderr. It accepts no project or
document and uses only adapter-owned fixture state. Its independent stage
deadlines are 5 seconds for workspace/lease creation, 30 seconds for Excel
startup, 60 seconds each for fixture creation/open, VBIDE access, and every
native VBE transition, then 5 seconds each for cooperative process close and
workspace deletion. It has no initial timeout override or whole-command
deadline. A stage timeout is `unverified`; a classified timeout with completed
cleanup remains complete, while cancellation or infrastructure that prevents
terminal classification is incomplete. Unexpected modal UI is bounded by the
current Doctor stage.
_Avoid_: vba-dev doctor, debug launch, static capability declaration

**VbaToolsDoctor**:
The Command Palette action labelled `VBA Tools: Doctor` that independently runs
`vba-dev doctor` and `vba-debug-adapter doctor --format json`, continues when
either fails, and presents separate `Project automation` and `VBE debugging`
results. It is not a third executable or an underlying aggregate CLI command.
_Avoid_: vba-tools.exe, vba-dev doctor, debug-adapter doctor

## Language

**VbaProject**:
A set of exported VBA source files that belong to the same logical VBA project. When a source file belongs to a `ProjectManifest` document definition, the project boundary is that document's `DocumentSourceSet`; otherwise the ad-hoc project boundary is the folder containing the active `.bas`, `.cls`, or `.frm` file.
_Avoid_: workspace, repository, package

**VbaProjectName**:
The actual VBA project name represented by `VBProject.Name`, which participates in VBA's module, project, and object-library name uniqueness rule. A manifest-backed `VbaProject` binds its authority to the exact request-start source-template content and may reuse an observation only for the same content fingerprint; `ProjectManifest.projectName`, a document name, a workbook filename, and a value from older template content are not substitutes, while an `AdHocVbaProject` has no containing-project-name authority by design.
_Avoid_: ProjectManifest.projectName, document name, workbook basename

**VbaProjectDiskInventory**:
The syntax-free disk capture for one resolved `VbaProject`. It owns `.bas`,
`.cls`, and `.frm` enumeration, recursive versus top-directory scope,
descendant `ProjectManifest` ownership, path/URI identity, stable byte reads,
source decoding, decoded-text reuse, and manifest probes. Cold snapshot capture
may reuse decoded text only while file metadata and the explicit invalidation
generation remain unchanged. Watched-source capture performs ownership
validation, invalidation, and one stable source read without enumerating the
project. `ProjectReconciliation` always rereads source bytes even when metadata
is unchanged. Its one-method reconciliation observation Seam accepts an
immutable disk-only request containing the resolved project disk scope,
ordered manifest probes, barrier overrides, and observed barrier URIs. The
shared filesystem inventory instance and decoded-source cache remain the same
ones used by cold and watched capture. The request does not contain an
authority key, authority generation, workspace, source, or manifest revisions,
known sources, or open-document state; those remain in
`VbaProjectReconciler` and the workspace reconciliation Seam. The inventory
does not parse syntax, apply open-buffer priority, or decide whether a captured
authority may commit.
_Avoid_: raw filesystem reader, source document cache, semantic inventory

**DiskSourceDecoding**:
The strict byte-to-Unicode policy used when `VbaProjectDiskInventory` reads a
closed exported VBA source file. It recognizes UTF-8, UTF-16 LE, or UTF-16 BE
from a BOM, then tries BOM-less strict UTF-8, then on Windows tries the active
ANSI code page fixed once at language-server process start; ACP 65001 is
canonical UTF-8. A non-Windows process has no implicit legacy fallback, and an
unsupported or invalid byte sequence produces a source diagnostic instead of
guessed text. Open editor text already arrives as Unicode and does not use this
policy. Decoding determines source text but does not restrict which
`VbaIdentifierForm` that text may contain.
_Avoid_: CP932 fallback, locale inference, identifier-form selection, VBE import encoding

**DiskContentIdentity**:
An opaque equality identity derived from decoded exported-source text by
`VbaProjectDiskInventory`. Equal decoded text has equal identity even across
invalidation and reread; changed decoded text has a different identity.
Callers compare it without depending on timestamps, lengths, bytes, hashes, or
parser state.
_Avoid_: file metadata, source revision, syntax identity

**ProjectReconciliation**:
The watcher-miss convergence operation owned by the deep `VbaProjectReconciler`
Module. It scans immutable disk facts in parallel, converts each authority into
one ordered `VbaProjectReconciliationScopePlan`, and commits each plan through
one required scheduler mutation. A stale authority fence rejects only that
scope; fresh peer scopes still commit. Manifest transitions request a fresh
follow-up plan instead of applying source mutations captured under the former
authority. Each accepted scope dispatches diagnostics and manifest notification
effects synchronously inside its required scheduler mutation, after the
workspace commit and before the ordered lane is released.
_Avoid_: flat reconciliation batch, out-of-lane effect dispatch, workspace helper sequence

**AdHocVbaProject**:
A `VbaProject` inferred from exported source files when no containing
`ProjectManifest` can be found. It provides source definitions,
`LanguageVocabulary`, and the always-active `VbaStandardLibraryReference`, but
it has no manifest-controlled `VbaProjectReferenceSelection` and therefore does
not contribute definitions from other external references. It also has no
source-template-backed `HostClassProjection`, so intrinsic host Event evidence
remains `indeterminate`. Its source boundary is the active source file's
directory, not nested organization directories or the VS Code workspace root.
_Avoid_: workbook-backed project, default Excel project, settings-backed project

**VbaDefinition**:
An identifiable declaration in a `VbaProject` that editor features can refer to. It includes modules, classes, forms, procedures, properties, constants, variables, parameters, enums, user-defined types, and events.
_Avoid_: symbol, item, thing

**VbaProjectReferenceDefinition**:
A definition supplied by the always-active `VbaStandardLibraryReference` or by
an active manifest-controlled `VbaProjectReference`, rather than by exported
source files in a `VbaProject`. VBA standard-library constants, Office object
model members, Scripting Runtime types, RegExp types, DAO/ADO types, and other
referenced-library members are all `VbaProjectReferenceDefinition`s.
For a callable TypeLib Property, `INVOKE_PROPERTYGET`,
`INVOKE_PROPERTYPUT`, and `INVOKE_PROPERTYPUTREF` remain distinct physical Get,
Let, and Set accessor definitions with their own signatures. Ordinary property
use may coalesce them into one logical readable or writable member, but
interface implementation semantics retain the accessor set.
_Avoid_: HostDefinition, ReferenceLibrary, built-in, standard library, external symbol

**VbaStandardLibraryReference**:
The always-active reference-catalog representation of the Visual Basic For
Applications standard library. It is present in every `VbaProject`, including an
`AdHocVbaProject`, independently of `ProjectManifest` reference selection, and
supplies all supported VBA standard-library constants as structured external
definitions. Those definitions retain their declaring owner, definition kind,
declared type, documentation, completion presentation, and semantic-token facts;
their reference origin keeps them outside `RenameTarget`. Its catalog-owned
`VBA` qualifier is available in every project and exposes the standard library's
public root surface, while still following ordinary `NameResolution` so a
higher-rank source definition named `VBA` can shadow it. Its metadata is bundled
with the language server and does not depend on TypeLib discovery, COM registry
state, Office installation state, or `VbaProjectReferenceCatalogLifecycle`.
_Avoid_: language vocabulary, implicit string list, host library

**HostGlobalReferenceDefinition**:
A root-level, read-only property `VbaProjectReferenceDefinition` supplied by an
active `MainVbaProjectReference` and usable without an explicit host object such
as `Application`; its owning main reference determines the host-specific type.
Neither a document kind alone nor an `AdHocVbaProject` activates it when the
owning `VbaProjectReference` is absent. Host-generic names such as
`Application` and `ActiveWindow` are still catalog-derived host globals: Excel,
Word, PowerPoint, and other hosts supply them only when their active main catalog
exposes them. Excel-specific names such as `ActiveCell`, `ActiveSheet`,
`ActiveWorkbook`, and `ThisWorkbook` are available only from the Excel main
reference. Excel `ThisWorkbook` is modeled through this catalog-derived host
global; source files are not promoted to document module instances by matching
the reserved `ThisWorkbook` name, and document module/base-object member merging
is outside this concept. Its hover declaration uses value-reference form such as
`ActiveCell As Range`, not accessor-declaration form such as
`Property ActiveCell As Range`. Host globals whose catalog type is deliberately
unavailable, such as Excel `ActiveSheet`, do not gain guessed member completion
from possible runtime object kinds. Member completion for a typed host global is
derived from the declared catalog type, such as `Workbook` or `Range`; it does
not inspect live host application state, open workbook contents, worksheet names,
or active cell state.
_Avoid_: built-in global, implicit host variable, language intrinsic

**LibraryGlobalReferenceDefinition**:
A root-level `VbaProjectReferenceDefinition` supplied whenever its owning
`VbaProjectReference` is active, independently of which reference is the main
host. VBA standard-library constants such as `vbCrLf` and public
referenced-library enum members such as Excel `xlCenter` are
`LibraryGlobalReferenceDefinition`s; their availability follows the activation
rule of their respective owning references. Enum-member declaration labels use
the catalog-provided declared type and do not infer a contextual enum type from
the use site; for example, Excel `xlCenter` is shown as `xlCenter As Long` when
the Excel catalog records the member type as `Long`.
_Avoid_: language constant, host global, project constant

**ReferenceDefinitionGlobalExposure**:
The catalog-owned classification that determines whether a
`VbaProjectReferenceDefinition` participates in unqualified `NameResolution`.
A library global is exposed whenever its reference is active, a main-host
global only when its reference is the `MainVbaProjectReference`, and an ordinary
member never participates as a root definition. The classification reflects
the referenced library's application-object, library-module, and enum binding
metadata rather than identifier-name or owner-name rules. Generated and bundled
catalogs preserve the same classification. Catalog members that are hidden or
restricted are not ordinary completion roots; they contribute root candidates
only when this classification has explicitly selected them as a library global
or main-host global. A catalog that lacks this classification does not infer
root exposure from owner names, hidden type names, or member names; its ordinary
type and member metadata may remain usable, but root globals are absent until a
refreshed catalog supplies explicit exposure metadata.
_Avoid_: root member, static member, implicit completion

**VbaProjectReferenceSelection**:
The manifest-controlled ordered set of non-baseline `VbaProjectReference`s
whose definitions are active for a `VbaProject`, with nonempty, already-trimmed,
case-insensitively unique names, in addition to the always-active
`VbaStandardLibraryReference`. It excludes the reserved canonical human-visible
name `Visual Basic For Applications` under `OrdinalIgnoreCase`; that library is
never a selected manifest reference. The selection comes from the
`ProjectManifest` document definition; source templates and VS Code host settings
do not select references.
_Avoid_: HostApplicationSelection, mode, profile, target language

**VbaProjectReferenceCatalog**:
A discoverable, cached, or bundled metadata source that maps active
`VbaProjectReference`s to `VbaProjectReferenceDefinition`s,
`CallableSignature`s, catalog-owned qualifier aliases, and the raw TypeLib facts
needed to derive a `TypeLibEventSurface`. If no catalog is available for an
active reference, the reference remains active but contributes no external
definitions. A legacy catalog may be partially usable when ordinary type,
member, and signature metadata are present while newer root-exposure or TypeLib
Event-surface metadata is absent. `ReferenceDefinitionGlobalExposure` and
`TypeLibEventSurface` then fail closed independently while the proven ordinary
metadata remains usable. A generated callable Property likewise preserves its
physical TypeLib Get, value-put, and reference-put accessors rather than
collapsing them to `Writable`; a catalog without that distinction remains usable
for ordinary property access but cannot prove Let or Set implementation
contracts. The bundled `VbaStandardLibraryReference` catalog is baseline
metadata rather than refreshable Office TypeLib metadata.
_Avoid_: host catalog, object model snapshot, reference cache

**TypeLibRegistryCatalog**:
The neutral, read-only .NET discovery contract shared by `VbaDev` and
`VbaLanguageServer` before any VBE-equivalent selection is required. On
supported Windows versions it scans the merged, shared
`HKEY_CLASSES_ROOT\TypeLib` view once, parses version and LCID keys as
hexadecimal, and groups registered versions of one GUID as a descending library
lineage. It does not union `Registry32` and `Registry64`, use consumer process
bitness as Office bitness, or prefer a `win64` identity; `win32` and `win64`
paths remain metadata. It performs no Excel/VBIDE automation. An incomplete
catalog fails closed, while individual malformed registrations do not create
invented identities.
_Avoid_: VBE resolver, Office-bitness catalog, registry-view union

**TypeLibEventSurface**:
The authoritative aggregate from which VBA-facing Event projections are
derived for one declared external type. The declared type must be a
`TKIND_COCLASS`; a directly declared `TKIND_INTERFACE` or `TKIND_DISPATCH` is
not a specific class and contributes no Event projection of its own. Exactly
one implemented interface whose flags contain both `IMPLTYPEFLAG_FDEFAULT` and
`IMPLTYPEFLAG_FSOURCE` defines the coclass's Automation Event source. A
non-default `FSOURCE` interface is ignored even when it is the only source
interface, and `FDEFAULTVTABLE` alone is not a fallback. When the same
implemented interface also carries `FDEFAULTVTABLE`, it is still read once
through its `FDEFAULT | FSOURCE` identity.

The aggregate retains raw coclass and interface `TYPEFLAGS`, callable-member
`FUNCFLAGS`, member identity and signatures, and completeness so it can expose
separate `TypeLibStructuralEventSurface`,
`TypeLibEventAuthoringSurface`, and
`TypeLibExistingHandlerRecognitionSurface` projections. A complete TypeLib
with no default source interface, or with a structurally empty default source,
has an authoritative empty structural surface and therefore supports
`invalidNoEvents`. More than one default source interface violates the TypeLib
contract and is `indeterminate`; unreadable, missing, stale, or incomplete
type, implemented-interface, flag, identity, member, or completeness metadata
is likewise `indeterminate` rather than empty. Creatability is independent.
_Avoid_: all-FSOURCE union, sole-source fallback, flattened Class inference, browser-visible Event list

**TypeLibStructuralEventSurface**:
Every callable member of the unique default source interface, including a
member marked `FUNCFLAG_FHIDDEN` or `FUNCFLAG_FRESTRICTED`. This projection
establishes whether a complete external coclass structurally exposes any Event;
member presentation flags do not turn a hidden-only or restricted-only source
into `invalidNoEvents`.
_Avoid_: completion Event list, public Event count, browsable Event surface

**TypeLibEventAuthoringSurface**:
The `TypeLibStructuralEventSurface` members offered for ordinary Event
completion and retained as eligibility evidence for future
`MemberStubGeneration`. A member marked `FUNCFLAG_FHIDDEN` or
`FUNCFLAG_FRESTRICTED` is excluded, matching the VBE authoring surface.
_Avoid_: structural Event surface, existing-handler lookup, all default-source members

**TypeLibExistingHandlerRecognitionSurface**:
The member-name projection used when an already-written
`WithEventsHandlerCandidate` resolves its Event suffix. It includes structurally
known hidden and restricted members so `variable_Event` can retain the same
object/Event association shown by the VBE code-window dropdown, without implying
that the Event is offered for authoring or that VBE compile validates the
external handler signature.
_Avoid_: Event completion surface, generated handler list, compile-validated handler

**HostClassProjection**:
The authoritative host description of one intrinsic form or document class,
identified by its `HostClassIdentity` and containing its
`IntrinsicEventSourceName` plus the authoritative `HostEventSignature`s
available to that class. Its Event surface is self-contained rather than
reconstructed from a reference catalog; source file extensions, reserved
document-module names, and ordinary module names do not establish it.
_Avoid_: filename-inferred form class, catalog-rehydrated host Event surface, TypeLib Event surface

**HostClassIdentity**:
The identity of one intrinsic host class within a `ProjectDocument`, composed of
the projection-supplied `VBComponent.Name` and component kind (`form` or
`document`) with case-insensitive name equality and projection casing retained.
One enumeration may contain it at most once; a duplicate is a class-enumeration
failure rather than a coalescible class.
Source association requires a compatible source kind and explicit
`Attribute VB_Name`; file-name fallback, display sheet name, component ordinal,
COM identity, and temporary path do not participate.
_Avoid_: ModuleIdentity fallback, worksheet display name, VBComponent ordinal

**HostManagedModuleIdentity**:
The `ModuleIdentity` of an intrinsic document source or of a form source currently associated with a `HostClassIdentity`, whose component identity is controlled by the source template rather than by source text alone. Last-known-good association evidence is insufficient to authorize identity mutation, while a form conclusively outside host association remains project-local.
_Avoid_: ordinary form name, filename-owned host class, projected Event name

**IntrinsicEventSourceName**:
The projection-supplied VBE Object-box name that qualifies intrinsic handlers
in one `HostClassProjection`, such as `Worksheet`, `Workbook`, or `UserForm`;
combining it with `_` and a `HostEventIdentity` forms the handler name.
Comparison is case-insensitive with projection casing retained, and the value is
never inferred from `HostClassIdentity`, component kind, source file name, or
`HostClassBaseTypeProvenance`.
_Avoid_: VBComponent.Name, inferred handler prefix, base-type name

**HostClassBaseTypeProvenance**:
The optional catalog-resolvable identity of the built-in host type behind a
`HostClassProjection`. It supports provenance and navigation only; absence or
failed catalog resolution neither supplies nor invalidates the projection's
authoritative Event signatures.
_Avoid_: host Event source of truth, required base type, catalog-owned host projection

**HostEventSignature**:
The structured shape of one inspected Event in a `HostClassProjection`,
containing its name and ordered parameters with names,
`HostEventTypeReference`s, passing mechanisms, array shape, available
`Optional` or `ParamArray` metadata, optional documentation, and
`HostEventAvailability`. Display labels are derived, and parameter names are
presentation rather than Event-handler compatibility identity.
_Avoid_: display-only Event signature, serialized CallableSignature, parameter-name identity

**HostEventIdentity**:
The case-insensitive Event name within one `HostClassIdentity`. It denotes one
structural Event rather than an overload or conditional family; duplicate
observations coalesce only when their callable contracts and
`HostEventAvailability` agree, while a conflict makes the class unverified.
_Avoid_: Event overload, signature identity, TypeLib member ordinal

**HostEventAvailability**:
The projected behavior of one structurally present `HostEventSignature`:
`authoringAvailable` controls ordinary completion and retains eligibility
evidence for future `MemberStubGeneration`, while
`existingHandlerRecognizable` controls association of an already-written
handler. These values describe inspected host behavior rather than exposing raw
TypeLib flags, may differ for the same Event, and must both be known for a
resolved host class.
_Avoid_: raw TypeLib flags, one Event visibility flag, structural Event existence

**HostEventTypeReference**:
The portable parameter type evidence in a `HostEventSignature`: an intrinsic
form has a canonical VBA type name, a TypeLib form has its type name plus
library GUID, major/minor version, and LCID, and an unresolved form retains only
its display name as valid opaque evidence without establishing canonical
equality. Display casing, human-visible reference names, VBA qualifiers, and
registry paths do not participate in identity.
_Avoid_: qualifier-based type identity, registry-path type identity, name-only external type

**HostClassEventSurface**:
The effective Event surface of one intrinsic form or document class, formed for
structural eligibility, ordinary authoring, and existing-handler recognition by
combining its valid source Event declarations with the structurally present
members and applicable `HostEventAvailability` projections of a complete,
current `HostClassProjection` under `HostEventShadowing`. A missing, stale, or
incomplete projection makes the surface `indeterminate`, not authoritatively
empty, and an intrinsic module handler remains separate from `WithEvents`
binding.
_Avoid_: host-name inference, intrinsic handler binding, source-only host Event list

**HostEventShadowing**:
The relationship by which an unguarded current valid source Event in an
intrinsic form or document class replaces a same-name projected host Event in
the source and external `WithEvents` Event surfaces, while the projected Event
remains separate evidence for intrinsic host-handler recognition. A guarded
valid source Event instead retains its `ConditionalCallableFamily` and the
same-name host Event as distinct configuration-dependent alternatives without
proving branch coverage; a `RecoveredEventDeclaration` establishes neither
form.
_Avoid_: source-host coalescing, duplicate Event collision, intrinsic handler Rename

**HostClassProjectionLifecycle**:
The consumer-owned `VscodeExtension` project lifecycle that obtains one
immutable `HostClassProjection` for each active `ProjectDocument` from
`HostClassList` and commits each `resolved` class independently to the language
server. An `unverified` class preserves its
`LastKnownGoodHostClassProjection`, or remains `indeterminate` when none exists;
`VbaDev` owns only the inspection invocation and its Excel process.
_Avoid_: CLI-owned projection cache, completion-time inspection, manifest Event list

**HostClassProjectionRefreshGeneration**:
The consumer-local, monotonically increasing commit fence for one
`ProjectDocument`'s `HostClassProjectionLifecycle`. A result can commit only
while its generation remains current and its project, document, and source
template request context still matches; the value is neither a `VbaDev` input
nor projection data.
_Avoid_: CLI request ID, inspection timestamp, source-template hash

**HostClassProjectionRefreshTrigger**:
A lifecycle event that can change the selected source-template host classes:
project-document activation, effective document or source-template identity
change, same-template file change, or explicit consumer refresh. Exported
source edits, reference-catalog changes, editor selection, and generated-output
changes are not triggers.
_Avoid_: source-edit refresh, catalog refresh, build-completion refresh

**HostClassSourceAssociationReevaluation**:
The source-only reassociation of present form and document sources against a context-compatible current `HostClassProjectionSnapshot` after source or manifest changes. It starts no Excel inspection and advances no projection generation; it updates `HostClassSourceAssociationFailure`s and clears their attention state as soon as all associations succeed.
_Avoid_: HostClassProjectionRefreshTrigger, automatic Excel refresh, projection regeneration

**HostClassProjectionRefreshScheduler**:
The consumer-owned, extension-wide single-flight coordinator for
`HostClassProjectionRefreshTrigger`s. It keeps only the latest pending
generation per `ProjectDocument`, preserves FIFO fairness between documents,
and never starts replacement inspection before superseded inspection has
finished cooperative cleanup.
_Avoid_: parallel Excel inspection, unbounded refresh queue, queue-wait timeout

**HostClassProjectionRefreshRecovery**:
The explicit recovery boundary after host-class inspection fails or returns an
unverified result: a later `HostClassProjectionRefreshTrigger` starts a new
generation, while the lifecycle performs no timer-based retry or hidden
backoff.
_Avoid_: automatic Excel retry, retryable CLI result, background retry loop

**HostClassProjectionStatus**:
The consumer-visible operational state of one `ProjectDocument`'s host-class
projection, including queued or running refresh, current data, last-known-good
use, unavailable template, partial result, invocation failure, or
`HostClassSourceAssociationFailure`. It belongs to
extension status and output rather than source diagnostics or `VbaDev`
environment diagnostics.
_Avoid_: source warning, Doctor result, automatic retry state

**HostClassSourceAssociationFailure**:
The attention-required state in which current host-class projection data exists but a present form or document source cannot establish its `HostClassIdentity` because `Attribute VB_Name` is missing or mismatched or its component kind is incompatible. Only that source's host Event evidence becomes indeterminate; the current projection and correctly associated sources remain usable, and recovery stays in `HostClassProjectionStatus` rather than source diagnostics, Doctor, or automatic retry.
_Avoid_: host-class compile error, failed projection refresh, file-name binding

**HostClassProjectionSnapshot**:
The immutable, document-wide effective host-class state that `VscodeExtension`
atomically supplies to the language server after combining current projections,
last-known-good projections, indeterminate identities, and authoritative
deletion. It is a full replacement rather than a CLI result, class delta, or
class tombstone.
_Avoid_: HostClassProjectionResult, incremental projection update, operational log

**HostClassProjectionSnapshotRevision**:
The consumer-owned, monotonically increasing transport fence for successive
`HostClassProjectionSnapshot`s of one `ProjectDocument`. The language server
accepts only the latest matching project context and replays the latest desired
snapshot after a connection restart.
_Avoid_: HostClassProjectionRefreshGeneration, CLI schema version, source revision

**HostClassProjectionAuthority**:
The semantic trust of one snapshot entry: `current` is authoritative,
`lastKnownGood` supplies advisory presentation while leaving
`HostClassEventSurface` indeterminate, and `indeterminate` supplies no projected
Event evidence. Only current evidence may establish compile-style diagnostics
or authorize meaning-preserving mutation.
_Avoid_: stale semantic authority, cached validation evidence, all-or-nothing projection use

**HostClassList**:
The read-only, document-scoped `vba-dev host-class list` operation that inspects
the selected source template and returns its `HostClassProjection` as
human-readable text or schema-versioned JSON. It defaults to the
`PrimaryOfficeDocument` when no document is specified, owns a dedicated
`AutomationExcelProcess`, and changes neither the workbook, source, manifest,
nor projection storage.
_Avoid_: host Event export, projection refresh command, manifest update

**HostClassProjectionResult**:
The schema-versioned JSON result of `HostClassList`, containing one `resolved`
or `unverified` entry per enumerated intrinsic host class,
`classEnumerationComplete` for authority over the identity set, and `complete`
only when that set and every class projection are complete. A structurally
complete class remains `resolved` when a successfully inspected parameter has
an unresolved `HostEventTypeReference`; incomplete or untrusted inspection,
including an unavailable `HostEventAvailability` value, makes it `unverified`.
_Avoid_: all-or-nothing host projection, omitted empty class, partial class inference

**UnverifiedHostClassEntry**:
The non-authoritative `HostClassProjectionResult` entry for an enumerated class
whose inspection is incomplete or untrusted. It carries only
`HostClassIdentity`, status, a stable reason code, and a human-readable message;
it contains no partial `HostEventSignature` or `HostClassProjection` payload.
_Avoid_: partial host projection, diagnostic Event list, best-effort class update

**HostClassInspectionFailureReason**:
The stable reason category of an `UnverifiedHostClassEntry`: Event enumeration,
signature read, availability read, inspection timeout, inspection abort,
cancellation, or an otherwise unclassified class-local inspection failure.
Whole-class enumeration failure and invocation-invalidating failures are
reported outside this entry taxonomy; cooperative cancellation uses
`cancelled` and top-level `operationCancelled`, never `inspectionAborted` or
`classEnumerationFailure`.
_Avoid_: exception type, parsed message, class-enumeration reason

**HostClassInspectionState**:
The shared invocation-local Excel/VBIDE state used to inspect successive
intrinsic host classes. If it becomes untrusted, a conclusively finalized class
may remain resolved only when its isolation from the failure is established;
the causal class reports its specific reason and known later classes report
`inspectionAborted`.
_Avoid_: retryable class state, replacement Excel process, all-results cache

**LastKnownGoodHostClassProjection**:
The latest `resolved` `HostClassProjection` already committed for one intrinsic
host class under its `HostClassIdentity`. An `unverified` entry, incomplete
enumeration, failed invocation, or in-flight refresh does not replace or remove
it; absence from a class-complete identity set does remove it.
_Avoid_: current inspection attempt, partial projection, manifest cache

**HostClassInspectionWorkspace**:
The invocation-owned temporary directory in which `HostClassList` opens a fresh
copy of the selected source template with macros and Excel Events disabled. It
imports no source, changes no references, never saves the copy, and removes the
workspace only after releasing its `AutomationExcelProcess`; inability to
prepare the copy or prove process release yields no projection. After release
is proved, deletion receives bounded retries; a remaining deletion failure
retains and reports the absolute path as a housekeeping warning without changing
the projection or successful exit status.
_Avoid_: source template lock, generated workbook, projection cache

**VbaProjectReferenceCatalogIdentity**:
The machine-readable identity used by a `VbaProjectReferenceCatalog` after a
human-visible `VbaProjectReference` name has been resolved, such as a TypeLib
GUID, major/minor version, LCID, and path. `ProjectManifest` references are not
required to store catalog identities. Multiple registry entries with the same
description are not sufficient by themselves to make a reference ambiguous.
Registered versions that share one TypeLib GUID form one library lineage rather
than separate ambiguity candidates. The lineage begins with its highest
registered version and falls back through older versions only when the current
VBE environment conclusively rejects a higher version. The concrete identity
returned by VBE remains authoritative even when it is newer than the requested
registry version.
The resolver first applies the current VBE environment's reference-selection
behavior: it adopts the concrete identity that enabling the matching entry in
the VBE References dialog would add. A VBE-equivalent selection probe runs only
after registry matching leaves more than one candidate. Zero registry matches
are unavailable, and one registry identity is adopted without opening Excel or
the source template. In a dedicated hidden
Office/VBE process, it tests each candidate from the same temporary project
baseline through `References.AddFromGuid`. That baseline is a fresh temporary
copy of the source template for the explicitly selected `ProjectDocument`, or
for the `PrimaryOfficeDocument` when no document is specified. The probe
observes the returned `Reference` identity and coalesces candidates that VBE
resolves to the same identity. Probe-result identity is the case-insensitive
returned GUID plus major and minor version. `FullPath`, `Description`, registry
LCID, and registry path are diagnostic metadata rather than probe-result
identity, and an empty returned `FullPath` is valid. A candidate-specific
rejection makes that candidate unusable, while an Excel/VBIDE lifecycle, access,
timeout, identity inspection, or cleanup failure leaves it unverified. Any
unverified candidate prevents selection for its name. A candidate-local failure
may leave later probes trustworthy, but loss of trust in the owned probe process
stops further VBE work without an automatic process replacement and leaves
every remaining probe-dependent name unverified. After every candidate is
evaluated conclusively, one distinct usable result is selected, no usable
result is unavailable, and more than one distinct usable result remains
ambiguous. The probe removes all temporary state and never saves a project
workbook.
If a same-name reference already exists in a fresh ambiguity-probe baseline,
its concrete GUID, major, and minor identity is adopted instead of adding a
duplicate. Build, publish, and test-build materialization may likewise adopt a
same-name reference from the workbook they already opened. `reference add`,
either `reference list` mode, and `doctor` never open a source template only to
search for that shortcut.
_Avoid_: manifest name, display name, reference description

**VbaProjectReferenceResolutionInventory**:
The configured or available collection of human-visible reference names and
their environment-resolution states. It establishes whether a reference
identity can be selected, not whether the current project source is compatible
with that selection.
_Avoid_: addability check, project-health report, module-name compatibility list

**ReferenceAddQuickPick**:
The project-and-document-scoped VS Code selection surface populated from the
`resolved` entries of one complete available
`VbaProjectReferenceResolutionInventory`. It submits one inventory-ordered,
atomic multi-selection and is not a free-text reference-name editor or a view of
partial and unusable candidates.
_Avoid_: Reference.Description input box, partial registry results, disabled issue list

**ReferenceRemoveQuickPick**:
The project-and-document-scoped VS Code selection surface populated from the
complete `VbaProjectReferenceSelection`, preserving stored spelling and order.
It submits one atomic multi-selection without requiring any selected reference
to resolve in the current environment.
_Avoid_: Reference.Description input box, registry-backed removal list, resolved-only removal

**VbaProjectReferenceQualifier**:
A catalog-owned qualifier alias that lets a `QualifiedReference` address one
active `VbaProjectReference` explicitly, such as `Excel`, `Word`, or
`Scripting`, and includes always-active standard-library qualifiers such as
`VBA`. It is not stored in `vba-project.json` and is not mechanically derived
from `Reference.Description` alone. It participates in `NameResolution` at
referenced-library rank, so a higher-rank source definition with the same name
shadows the qualifier rather than the qualifier acting as an absolute reference
escape hatch. When used with a trailing dot in completion, it exposes that
reference catalog's public root surface: root-level types, exposed constants,
and explicit `ReferenceDefinitionGlobalExposure` definitions, not hidden owners
or restricted internal members. The exposed surface is still filtered by the
active `CompletionExpectation`, so type, value, and creatable-type contexts see
different role-compatible candidates.
_Avoid_: manifest alias, display name, host name

**ReferencedVbaProjectName**:
The authoritative project or object-library name exposed by one selected active `VbaProjectReference`, such as the actual TypeLib library name. For mutation it comes from explicit bundled authority or an identity-backed current catalog committed for the active `ReferenceSelectionFingerprint`; a stale-persisted catalog, manifest reference name, and generated or supplemental `VbaProjectReferenceQualifier` do not become collision authority merely because they can supply editor metadata or address the same catalog.
_Avoid_: reference display name, sanitized qualifier, every qualifier alias

**VbaProjectReferenceCatalogAvailability**:
The operational state describing whether an active `VbaProjectReference` has a
usable `VbaProjectReferenceCatalog`. Missing catalog availability can be
reported through language-server output, status, or trace and through an
`EnvironmentDiagnostic`, but it does not create source diagnostics by itself.
A catalog with usable ordinary metadata but missing root-exposure or TypeLib
Event-surface metadata is available for the metadata it can prove, while each
missing semantic projection is stale, fails closed independently, and remains
eligible for refresh.
_Avoid_: source diagnostic, unresolved reference, compile error

**ManifestReferenceConsistency**:
The condition that a `ProjectManifest` document definition contains the
references expected for its document kind, including the expected
`MainVbaProjectReference`. Missing expected references are reported through
language-server output, status, or trace and through `EnvironmentDiagnostic`;
they do not cause the language server to implicitly activate references that are
absent from the manifest.
_Avoid_: source diagnostic, auto-added reference, implicit default

**VbaProjectReferenceCatalogRefresh**:
The lifecycle-owned background process that preloads persisted metadata and
discovers TypeLib metadata after project activation or an effective
`VbaProjectReferenceSelection` change. Ordinary VBA source edits do not restart
it. Editor requests use the best committed catalog without waiting for preload
or discovery. When registry identity is not unique, refresh may asynchronously
obtain the VBE-equivalent identity through
`vba-dev reference list --format json` before discovering catalog metadata.
Per-reference ownership spans preload, identity resolution, and discovery so
stale work cannot overtake a newer commit, while an explicit refresh may bypass
lifecycle negative caching for references that are currently free.
_Avoid_: completion-time discovery, source-edit refresh, blocking metadata load

**VbaProjectReferenceCatalogLifecycle**:
The project-scoped C# responsibility that reacts to project activation,
effective `ProjectManifest` reference-selection changes, and deactivation. It
schedules persisted-catalog preload and TypeLib discovery independently from
ordinary VBA source edits. Completion, hover, signature help, and other editor
queries only read committed catalog state and do not wait for in-flight preload
or discovery work to finish.
_Avoid_: source-edit refresh, completion-time preload, per-document catalog reload

**ReferenceSelectionFingerprint**:
The case-insensitive deterministic identity of one effective
`VbaProjectReferenceSelection`, including the document kind, main-reference
state, and normalized reference names. Repeated activation with the same
project scope and fingerprint shares one automatic catalog lifecycle revision.
_Avoid_: document version, manifest version, catalog identity, TypeLib identity

**ReferenceCatalogLifecycleRevision**:
One generation of automatic persisted preload and discovery for a project scope
and `ReferenceSelectionFingerprint`. Missing or unreadable persisted results
are negative-cached only for this revision; an explicit retry or changed
selection may start new work.
_Avoid_: source revision, cache format version, LSP document version

**LastKnownGoodReferenceCatalog**:
The latest usable bundled, persisted, stale-persisted, or generated catalog
revision already committed for a reference. A cancelled or failed refresh does
not replace or remove it; only a later successful atomic commit changes the
editor-facing catalog. While new catalog work is in flight, editor requests use
this committed snapshot when it exists; if no committed snapshot exists for a
reference, that reference contributes no editor candidates until a later request
sees a successful commit.
_Avoid_: in-flight catalog, failed discovery result, source diagnostic

**SyntaxHighlighting**:
Editor coloring for VBA source text. It combines lexical classification for VBA syntax with meaning-aware classification from parsed project information when that information is available.
_Avoid_: color theme, formatting

**SyntaxDiagnostic**:
An editor diagnostic that reports malformed VBA source syntax in a `VbaProject`. A `SyntaxDiagnostic` is about grammar and source structure, not semantic checks such as unresolved `VbaDefinition`s, missing `VbaProjectReferenceDefinition`s, type mismatch, or ambiguous `NameResolution`. A source Event outside a class module's module-level code, a `Private` or `Friend` Event, an underscore in an Event name, an Event parameter declared with `Optional` or `ParamArray`, a `WithEvents` declarator outside a class module's module-level code or using an array, `As New`, a type-declaration character, or no explicit `As` type, a `RaiseEvent` outside a procedure in a class-module code section, a `RaiseEvent` argument list with a named argument, empty parentheses, or an omitted argument, a procedural-module duplicate `ModuleIdentityMetadata` record, and malformed `ModuleIdentityMetadata` are malformed syntax rather than validation failures. None of these invalid forms is admitted as a valid Event signature, eligible Event source, call shape, or authoritative module identity for later semantic analysis.
_Avoid_: compile error, semantic diagnostic, runtime error

**EventDeclarationPlacementSyntaxDiagnostic**:
The error-severity `SyntaxDiagnostic` emitted when a source Event declaration is
not at module level in a class-module code section, including an Event in a
standard module or inside a procedure. Its code is
`syntax.eventDeclarationNotAllowedInModule`, its range is exactly the `Event`
keyword, and its stable message is
`Event declarations are allowed only at module level in a class module.`
_Avoid_: project validation, handler placement diagnostic, module-kind warning

**WithEventsDeclarationPlacementSyntaxDiagnostic**:
The error-severity `SyntaxDiagnostic` emitted once for each individual
declarator whose written `WithEvents` modifier is not at module level in a
class-module code section. Its code is
`syntax.withEventsDeclarationNotAllowedHere`, its range is exactly that
declarator's `WithEvents` keyword, and its stable message is
`WithEvents variables are allowed only at module level in a class module.`
The diagnostic is declarator-local in a comma-separated declaration and does
not transfer the modifier or error to sibling declarators. The recovered
variable retains its definition and written modifier for ordinary editor
features, but is not eligible for `WithEvents` Event binding.
_Avoid_: line-wide WithEvents diagnostic, invalid handler placement, type validation

**WithEventsDeclarationShapeSyntaxDiagnostic**:
One of four independent error-severity `SyntaxDiagnostic`s emitted for an
individual declarator that writes `WithEvents` but does not have the MS-VBAL
shape `WithEvents IDENTIFIER As class-type-name`.
`syntax.withEventsArrayNotAllowed` selects the complete array designator,
including its parentheses and any bounds, and has the stable message
`WithEvents variables cannot be arrays.`
`syntax.withEventsNewNotAllowed` selects exactly the `New` keyword and has the
stable message `New cannot be used with WithEvents.`
`syntax.withEventsTypeDeclarationCharacterNotAllowed` selects exactly the
offending `%`, `&`, `^`, `!`, `#`, `@`, or `$` suffix and has the stable message
`Type-declaration characters cannot be used with WithEvents.`
`syntax.withEventsTypeRequired` selects the variable identifier when the `As`
clause is absent, or exactly the `As` keyword when its type is absent, and has
the stable message
`WithEvents variables require an explicit class type in an As clause.`
Every independently present violation is retained on the same declarator,
including both the type-declaration-character and type-required diagnostics.
The placement diagnostic is independent and remains alongside these shape
diagnostics. Neither diagnostics nor recovery propagate to comma-separated
siblings, and a neighboring declarator's type is never inherited.
_Avoid_: generic invalid WithEvents declaration, class-type validation, line-wide recovery

**EventVisibilitySyntaxDiagnostic**:
The error-severity `SyntaxDiagnostic` emitted for an explicit `Private` or
`Friend` modifier on a source Event declaration. Its code is
`syntax.eventVisibilityNotAllowed`, its range is exactly the offending
visibility keyword, and its stable message is
`Event declarations can only be Public.` Explicit `Public` and omitted
visibility are valid and both mean Public. Placement and visibility diagnostics
are independent and are both retained when both restrictions are violated.
_Avoid_: Event accessibility validation, implicit Private Event, combined declaration diagnostic

**EventNameSyntaxDiagnostic**:
The single error-severity `SyntaxDiagnostic` emitted for one source Event
declaration whose identifier contains an ASCII underscore. Its code is
`syntax.eventNameCannotContainUnderscore`, its range is the complete Event-name
identifier rather than one diagnostic per underscore, and its stable message is
`Event name cannot contain an underscore.` The project-independent restriction
is evaluated before callable projection and produces an invalid-name
`RecoveredEventDeclaration`.
_Avoid_: handler-name diagnostic, per-underscore diagnostic, validation diagnostic

**EventParameterModifierSyntaxDiagnostic**:
An error-severity `SyntaxDiagnostic` emitted once for each forbidden parameter
modifier in a source Event declaration. `Optional` produces
`syntax.eventOptionalParameterNotAllowed` over exactly its keyword token, and
`ParamArray` produces `syntax.eventParamArrayParameterNotAllowed` over exactly
its keyword token. When both occur, each independently offending token receives
its own diagnostic.
_Avoid_: invalid handler signature, Event signature validation

**RaiseEventPlacementSyntaxDiagnostic**:
The single error-severity `SyntaxDiagnostic` emitted for a `RaiseEvent`
statement that is not inside a procedure in a class-module code section. Its
code is `syntax.raiseEventStatementNotAllowedHere`, its range is exactly the
`RaiseEvent` keyword, and its stable message is
`RaiseEvent statements are allowed only inside a procedure in a class module.`
The diagnostic covers both procedure-external placement and a procedure in a
procedural module. Argument-shape `SyntaxDiagnostic`s remain independent, but
the invalid statement does not enter target resolution or
`CallArgumentMapping`.
_Avoid_: unresolved Event diagnostic, standard-module warning, two placement diagnostics

**RaiseEventNamedArgumentSyntaxDiagnostic**:
The error-severity `SyntaxDiagnostic` with code
`syntax.raiseEventNamedArgumentNotAllowed`, emitted once for each named-argument
form in a `RaiseEvent` statement. Its range begins at the argument-name
identifier and ends after `:=`, excluding the value expression. Because the
named form is not admitted as a `CallArgument`, the same `RaiseEvent` does not
also produce `validation.duplicateNamedCallArgument`,
`validation.positionalCallArgumentAfterNamed`, or
`validation.incompatibleCallArgumentList`.
_Avoid_: invalid event argument name, positional event argument

**RaiseEventEmptyArgumentListSyntaxDiagnostic**:
The error-severity `SyntaxDiagnostic` with code
`syntax.raiseEventEmptyArgumentListNotAllowed`, emitted when a zero-argument
`RaiseEvent` uses an empty parenthesized argument list. Its range is the
complete `()` source range and its stable message is
`RaiseEvent must omit parentheses when no arguments are supplied.` The empty
list is not also treated as an omitted argument list and is not admitted to
`CallArgumentMapping`.
_Avoid_: missing Event argument, zero-argument call validation

**RaiseEventOmittedArgumentSyntaxDiagnostic**:
The error-severity `SyntaxDiagnostic` with code
`syntax.raiseEventOmittedArgumentNotAllowed`, emitted once for a parenthesized
`RaiseEvent` argument list containing one or more omitted argument slots. Its
range is the complete argument-list source range, including both parentheses,
and its stable message is `RaiseEvent arguments cannot be omitted.` It is not
emitted once per omitted slot. The complete malformed list is not admitted to
`CallArgumentMapping`; independently malformed named arguments retain their
own `RaiseEventNamedArgumentSyntaxDiagnostic`.
_Avoid_: optional Event argument, per-slot omitted-argument diagnostic

**VbaValidationDiagnostic**:
An editor diagnostic produced after a source file has been parsed into
`VbaSyntaxTree`, when VBA validity rules can be checked without treating the
source as parser recovery. Duplicate callable parameter names, duplicate
call-site named arguments, and positional arguments after named arguments are
`VbaValidationDiagnostic`s, even when they are published as LSP errors. Some
`VbaValidationDiagnostic`s are document-local, while others require project
state such as `NameResolution`, `TypeResolution`, `VbaProjectReferenceSelection`,
or available `VbaProjectReferenceCatalog`s. Reference-catalog availability,
stale exposure metadata, missing host globals, and host-global assignment
validity are not `VbaValidationDiagnostic`s in the current scope. Project-aware
validation evaluates each affected immutable `VbaProjectSnapshot` once and
partitions its results by source URI across every member of
`SourceDocuments`, including open buffers and closed disk sources in both
`WorkbookBackedProject`s and `AdHocVbaProject`s. It does not revalidate
unrelated project scopes. Publication combines each partition with the member's
document-local diagnostics and enqueues only URIs whose complete publishable
diagnostic set changed; a previously published URI receives an empty tombstone
when that complete set becomes empty. Closing an open source ends its
open-buffer lifecycle and invalidates queued buffer-authoritative diagnostics.
If the URI remains a project member with a tracked disk source, validation
switches to a newly captured disk-authoritative source and republishes its
current diagnostics instead of clearing them. An empty tombstone is reserved
for deletion, project departure, or loss of any tracked disk source; a later
reopen starts a new open-buffer lifecycle.
_Avoid_: SyntaxDiagnostic, parser recovery diagnostic, raw compiler error

**WithEventsTypeValidationDiagnostic**:
One mutually exclusive error-severity, project-aware `VbaValidationDiagnostic`
emitted for a conclusive-invalid `WithEventsTypeEligibility`. Every diagnostic
selects the complete declared type reference, including any qualifier.
`invalidEnclosingClass` produces
`validation.withEventsTypeCannotBeEnclosingClass` with
`A WithEvents variable cannot use its enclosing class as its declared type.`
`invalidNotClass` produces `validation.withEventsTypeMustBeClass` with
`WithEvents variables must use a specific class type.`
`invalidInaccessibleType` produces
`validation.withEventsTypeMustBeAccessible` with
`The declared WithEvents class must be accessible to VBA.` `invalidNoEvents`
produces `validation.withEventsTypeMustExposeEvents` with
`The declared WithEvents class must expose at least one Event.` The exclusive
precedence is enclosing class, non-class, inaccessible class, then no Event. An
`indeterminate` result publishes none of these diagnostics and is retained as
indeterminate Event-binding evidence rather than guessed invalidity.
_Avoid_: unresolved-type cascade, restricted-type syntax error, creatability diagnostic, multiple WithEvents type diagnostics

**RaiseEventTargetDiagnostic**:
The error-severity project-aware `VbaValidationDiagnostic` emitted after a
syntactically admitted `RaiseEvent` identifier fails to resolve to a source
Event declared in its enclosing class module. Its code is
`validation.raiseEventTargetNotDeclaredInEnclosingModule`, its range is the
complete Event-name identifier, and its stable message is
`RaiseEvent target must be an Event declared in the enclosing class module.`
Target resolution never falls back to a same-named non-Event declaration, an
Event in another class, a `VbaProjectReferenceDefinition`, or an intrinsic host
Event. This diagnostic takes precedence over a generic unresolved-name or
aggregate call diagnostic for the same occurrence. A local
`RecoveredEventDeclaration` remains a navigation and Rename binding and
supplies indeterminate call evidence instead of this diagnostic.
_Avoid_: unresolved identifier diagnostic, incompatible call diagnostic, external Event binding

**ProjectDiagnosticRevision**:
The snapshot-scoped freshness identity for one project-aware validation run. It
captures the resolved project authority, source membership, every member's
open- or disk-authoritative source revision, effective manifest and reference
selection, and semantic reference-catalog revisions used by validation. Every
per-URI fan-out partition carries this revision plus that target document's
authority, version, lifecycle epoch, and reservation fence. Publication
rechecks both: a stale document fence rejects that URI, while a stale
`ProjectDiagnosticRevision` rejects every partition from the superseded project
result even when a target URI itself did not change. A newer project snapshot
must be validated; diagnostic equality is not used to salvage an old partition.
While that newer validation is pending, a directly changed URI immediately
publishes its new document-local diagnostics without mixing in a project
partition fenced to its former document revision. Unchanged project members
retain their last accepted complete diagnostic set until the fresh project
result replaces only changed sets. Delete and project departure still clear
immediately. Repeated invalidations coalesce to the latest pending validation
for the resolved project authority rather than clearing every project member on
each edit.
_Avoid_: document version, publish sequence, permanent project identity

**DeclarationCollision**:
A case-insensitive pair or set of simultaneously active source declarations
that the MS-VBAL declaration-kind and namespace rules prohibit from sharing a
name. The shared collision matrix covers procedure-local, module, Enum-member,
UDT-member, and project-level public type and `ModuleIdentity` namespaces.
Property identity and its Get, Let, and Set accessor kinds are established
before conditional-family or collision analysis. Complementary accessor kinds
form one legal Property family even when some are unconditional and others are
conditional. Within one accessor kind, an unconditional declaration collides
with a same-named conditional declaration, while all-conditional declarations
may form one `ConditionalDeclarationFamily`; a repeated accessor that can be
active together collides. Same-named public procedures in different modules do
not collide by declaration alone. Membership in one
`ConditionalDeclarationFamily` neither proves mutual exclusivity nor suppresses
a collision between variants that can be active together.
_Avoid_: same spelling, ambiguous reference, project-wide reserved name

**ConditionalDeclarationFamily**:
One semantic and refactoring identity for one or more case-insensitively
same-named declarations in the same VBA declaration scope and namespace when
every declaration is guarded by conditional compilation. A guarded declaration
with no same-name peer is a one-variant family; the model does not add a
synthetic variant representing configurations in which the declaration is
absent. Distinct conditional branch paths form physical variants even across
separate `#If...#End If` blocks. This grouping models the source author's
alternative-declaration intent without asserting that the conditions are
mutually exclusive or that only one definition is active. Each variant retains
its own declaration kind, available valid signature, type, visibility,
declarator-local `WithEvents` presence and `WithEventsTypeEligibility` where
applicable, branch path, and source location. Name Resolution binds uses to the
family rather than selecting an arbitrary physical variant. A
`RecoveredEventDeclaration` retains its missing-signature recovery state, while
a `RecoveredWithEventsVariableDeclaration` retains its ordinary variable
identity and written modifier without becoming eligible for Event binding.
Repeated declarations within one branch path remain a `DeclarationCollision`,
and an unconditional declaration is not absorbed into the family. For Property
declarations, this grouping is evaluated independently within each accessor
kind after complementary Get, Let, and Set accessors have
been linked by Property identity. Completion projects one conditional symbol
when at least one variant is completion-eligible; a family containing only
placement-, visibility-, or name-invalid recovered Event declarations
contributes none. Hover and Signature Help preserve their available alternatives
without exposing condition expressions, Definition returns every physical
variant, References bind to the family, and Rename changes the family atomically
only when its
meaning-preservation proof succeeds. A one-variant family follows these same
projections and uses the generic `[#If]` marker. Callable variants additionally
form a `ConditionalCallableFamily` when their signatures are valid. A
`RecoveredEventDeclaration` remains a physical declaration-family variant but
does not become a callable variant. `Public`, `Private`, and `Friend` are variant
metadata rather than family-identity components, so conditional visibility
differences neither split the family nor create a collision. Name and Call
Resolution classify visibility independently for each use and physical
variant. A proven-invisible variant does not contribute an applicable callable
signature or named-argument candidate at that use, but it remains a physical
family variant returned by Definition and changed by a family-wide Rename.
Same-named, same-scope, all-conditional `WithEventsHandlerCandidate`s classified
`resolvedHandler` or `nonSubProcedureAssociation` use this same family identity
rather than a separate handler-family kind. Their complete declaration-name
occurrences bind the family, while each physical Sub handler retains its own
`EventHandlerCompatibility` and each Function or Property accessor retains its
non-Sub association and any authority-permitted diagnostic evidence. Property
identity remains an orthogonal
logical relationship. The resulting candidate family is a
`DependentRenameTarget` rather than an independently renameable callable family
only when `ConditionalDependentRenameCoverage` is `completeDependent`.
_Avoid_: ignored duplicate, proven-exclusive definition, selected physical variant, ConditionalHandlerFamily

**ConditionalDependentRenameCoverage**:
The family-wide classification used before an Event or module-level
`WithEvents` variable Rename can derive edits for a
`ConditionalDeclarationFamily` containing one or more
`WithEventsHandlerCandidate`s. It is `completeDependent` only when every
physical family variant is classified `resolvedHandler` or
`nonSubProcedureAssociation`; the complete family is then one
`DependentRenameTarget`. It is `conclusiveMixed` when any physical variant is a
conclusive `ordinaryProcedure` or a noncandidate declaration. Definition and
References still retain the unsplit family, but a non-no-op upstream Rename
fails with `resolutionChanged` rather than renaming the unrelated variant or
splitting the family. It is `indeterminateCoverage` when there is no conclusive
mixed variant but at least one candidate or recovered variant cannot yet be
classified completely; the Rename fails with `analysisIncomplete`.
`conclusiveMixed` takes precedence when both conclusive nondependent and
indeterminate evidence exist, because a meaning change is already proven.
Prepare Rename target selection for a proven variable prefix or convergent Event
suffix remains available; the requested Rename performs this complete coverage
proof before producing any edit.
_Avoid_: partial family Rename, role-split family, rename unrelated conditional variant

**ConditionalFamilyIdentity**:
The snapshot-scoped semantic identity of one
`ConditionalDeclarationFamily`, composed from its `VbaProject`, VBA declaration
scope, declaration namespace, and name under the shared case-insensitive VBA
name-equivalence rule. It is not derived from a representative variant, source
range, conditional-directive offset, or variant visibility. Each physical
variant remains separately identifiable through its source declaration and
accessor kind while retaining its visibility and branch path as semantic
metadata. Raw family and variant identities are not assumed equal across project
snapshots: incremental analysis may reuse them only when unchanged ownership is
proven, otherwise it rebuilds them. A `RenamePlan` establishes its own explicit
pre-edit to post-edit target correspondence rather than relying on raw
cross-snapshot equality.
_Avoid_: declaration-range key, first-variant identity, permanent revision identity

**ConditionalFamilyCanonicalName**:
The presentation spelling of a `ConditionalDeclarationFamily`, taken from its
first physical variant in stable project declaration order. Declarations in one
source use source order; a project-level scope orders canonical source URIs and
then declaration ranges. Completion, a family-level Hover heading, and Prepare
Rename use this spelling. Each variant's Signature Help and Definition detail
retains its source spelling. Visibility, active signature, use site, and
conditional branch do not change the canonical name, and this presentation
choice is not part of `ConditionalFamilyIdentity`. Family formation never edits
source; an explicit case-only Rename rewrites every family declaration and
resolved occurrence to the requested spelling.
_Avoid_: active-variant name, majority spelling, normalized uppercase name

**ConditionalVariantMarker**:
The presentation-only `[#If]` marker that identifies a
`ConditionalDeclarationFamily`, one of its variants, or a host Event retained
as a configuration-dependent `HostEventShadowing` alternative without
rendering, normalizing, or summarizing the source condition. All simple, nested,
and long conditional-compilation expressions use the same marker. Callable
variants and host alternatives use it on their distinct Signature Help and
Hover declarations; a name-deduplicated Event or named argument available in
only some variants may use it in completion detail. The marker is never part of
source insertion text. Contract-facing Completion, Signature Help, Hover, and
diagnostic detail derive it from `ConditionalContractProvenance`, not merely
from the declaration or authoring location. `@` is not included because it is a VBA
type-declaration character.
_Avoid_: `[@#If]`, condition label, branch-path summary

**ConditionalContractProvenance**:
The presentation fact for one Event or interface contract alternative, marked conditional when its applicable `WithEvents` or `Implements` relationship, source Event or interface member, Public variable owning a derived accessor, or configuration-dependent host-shadow alternative is conditional. Contract-facing Completion, Signature Help, Hover, and diagnostic detail project the same fact, while the guardedness of the completion, handler, or implementation location alone never contributes.
_Avoid_: completion-branch marker, active `#If` condition, declaration-location conditionality

**RecoveredEventDeclaration**:
A source Event declaration whose declaration identity is recoverable but whose
Event-specific syntax cannot form a valid `CallableSignature`, including an
Event outside a class module's module-level code, an explicit `Private` or
`Friend` modifier, a name containing an ASCII underscore, or a parameter
containing forbidden `Optional` or `ParamArray`. It remains a `VbaDefinition`
and, when guarded, a physical `ConditionalDeclarationFamily` variant.
Definition, References, and Rename retain that identity, and an existing
syntactically admitted `RaiseEvent` occurrence can bind to it. Name completion
retains a recovered declaration only when its Event name, placement, and
visibility are valid; parameter-only recovery remains completion-eligible,
while invalid placement, visibility, or name is never suggested. Rename may
repair an invalid name to a valid underscore-free Event name, but Event-specific
`RenameName` validation rejects another underscore-bearing name. Invalid
placement and visibility
require source-structure edits outside Rename. A placement-, visibility-, or
name-invalid declaration is excluded from Event suffix resolution after
`WithEventsHandlerNameDecomposition`. Every recovered form contributes no
callable variant, `ResolvedEventSignatureSet` entry, or Signature Help item. Its
presence is retained as indeterminate signature evidence so call-argument and
handler-signature diagnostics do not turn the declaration's syntax error into
downstream cascades.
_Avoid_: valid Event signature, unresolved Event, placeholder callable signature

**ConditionalCallableFamily**:
The callable projection of one `ConditionalDeclarationFamily`, containing the
valid Function, Sub, source Declare, parameterized Property, and Event
signatures from its conditional variants. A recovered declaration without a
valid signature remains in the originating `ConditionalDeclarationFamily` but
not in this callable projection. Name Resolution retains the originating
declaration-family identity while Call Resolution applies the same
`CallArgumentMapping` operation to each valid callable variant and preserves
any recovered variant as indeterminate evidence. A better match never becomes a
selected definition or inferred `#If` branch. Event-specific invocation and
handler rules are context policies and projections over this family rather than
a separate `ConditionalEventFamily`. Signature Help and named-argument
completion consume only valid signatures; Definition, References, and Rename
retain every declaration-family variant. Type Resolution does not take one
variant's return type merely because its arguments match better.
_Avoid_: overload set, active callable definition, inferred compilation environment

**CallContextCompatibility**:
The three-state per-variant fact that records whether a callable kind and access
mode is compatible, incompatible, or indeterminate for the syntactic role of
one call. A statement invocation can admit a Sub or Function and discard a
Function result, while a value-producing expression requires a value-producing
callable; Property read and assignment contexts apply their respective accessor
rules. A syntactically recognized `RaiseEvent` context admits Event variants
and makes non-Event callable kinds incompatible. An Event is incompatible in
ordinary statement, value, and Property contexts. Incomplete syntax that does
not yet establish whether the role is `RaiseEvent` is indeterminate, not
incompatible. A context-incompatible conditional variant is conclusively
inapplicable but remains in its `ConditionalCallableFamily` and
`ConditionalCallCompatibility` rather than being filtered before analysis. Its
independent argument-mapping evidence may still support presentation without
supplying semantic binding or a result type. Signature Help retains every
family variant and ranks compatible, indeterminate, then incompatible contexts.
Named-argument completion uses compatible and indeterminate variants, excluding
only proven-incompatible variants; it is empty for context only when every
variant is proven incompatible.
_Avoid_: callable-kind filter, selected accessor variant, inferred active branch

**CallArgumentMapping**:
The reusable analysis that maps one complete or in-progress call's positional,
named, and omitted `CallArgument`s to one `CallableSignature`. It retains the
argument-to-parameter mapping, optional active parameter, remaining named
parameters, and compatibility evidence, including `CallContextCompatibility`.
The active parameter is present only when the current argument maps uniquely;
extra positional arguments map to the terminal `ParamArray`, while an unknown
named argument, duplicate mapping, or excess positional argument without
`ParamArray` has no active parameter. A known mapping remains available when a
separate type or call-context rule makes the variant inapplicable. A mapping is
inapplicable as soon as a context, structural, or modeled MS-VBAL type rule
proves a violation. It is applicable only for a complete call whose context,
mapping, required parameters, and type compatibility are all proven valid.
In-progress source, missing expression type or classification, an unmodeled
Let-coercion, incomplete parameter-mechanism metadata, or library-specific
behavior makes an otherwise unproven mapping indeterminate. ByVal checking uses
modeled Let-coercion semantics; ByRef checking preserves declared-type and
expression-classification rules, including a parenthesized expression's
value-temporary behavior. Unknown rules are never treated as incompatibility
merely because the implementation lacks them. A signature containing
`ParamArray` rejects every named argument and contributes no named-argument
completion candidates. An omitted positional slot mapped to the `ParamArray` is
a valid placeholder, while one mapped to a required fixed parameter remains
incompatible. After syntax admission, `RaiseEvent` applies this same operation
to Event signatures and maps each valid argument by source position. A named
argument form is a `SyntaxDiagnostic`; it is neither reinterpreted as positional
nor passed to `CallArgumentMapping`. `RaiseEvent` exposes no remaining named
parameters, regardless of the signature's named-argument metadata. Empty
parentheses and a list containing any omitted argument slot are likewise
`SyntaxDiagnostic`s. Neither complete list is passed to this operation. A
`RaiseEvent` reaches this operation only after its placement is syntactically
admitted and its target resolves to an enclosing-class source Event or
conditional Event family. Placement failure, target failure, and a recovered
target without a valid signature do not manufacture an argument mapping.
_Avoid_: overload selection, arity-only match, guessed type incompatibility

**ConditionalCallCompatibility**:
The family-wide result of applying `CallArgumentMapping` independently to every
signature in a `ConditionalCallableFamily`. Its primary information is the
complete variant-keyed set of applicable, inapplicable, and indeterminate
mapping results. It is not collapsed into one selected signature or one
exclusive four-state value.
Consumers derive facts such as every variant being applicable, at least one
variant being applicable, at least one being inapplicable, no variant being
applicable, and any variant remaining indeterminate without discarding the
underlying results. Applicability describes the configurations in which the
call could be valid; it never changes the call's family binding.
_Avoid_: selected overload, exclusive aggregate status, call-site declaration identity

**ConditionalCallResultType**:
The optional Type Resolution fact for a call bound to a
`ConditionalCallableFamily`. It exists only when
the complete `ConditionalCallCompatibility` establishes that every variant is
applicable and every variant has the same known canonical resolved value-result
type. It is absent when any variant is inapplicable or indeterminate, or has no
value, an unknown result type, or a different canonical result type. An absent
result stops downstream member completion. Presentation ranking and an
apparently applicable subset never narrow the family to manufacture this type.
An Event invocation never supplies a result type.
_Avoid_: selected-variant return type, best-signature type, speculative member completion

**WithEventsVariableDeclaration**:
One individual module-variable declarator carrying its own written `WithEvents`
modifier and syntactically admitted for `WithEvents` analysis. Admission requires
module-level placement in a class-module code section and the complete shape
`WithEvents IDENTIFIER As class-type-name`: no array designator, `As New`, or
type-declaration character, and an explicit `As` type. Its separate
`WithEventsTypeEligibility` determines whether it participates in Event binding.
`Public`, `Private`, or `Dim` may introduce the containing module declaration
without changing those rules. In a comma-separated declaration, `WithEvents`
belongs only to the declarator on which it is written and never propagates to a
sibling. For example, in
`Private WithEvents publisher As Publisher, other As Publisher, WithEvents app As Excel.Application`,
only `publisher` and `app` carry `WithEvents`. Class modules include `.cls` and
`.frm` source and document-module source exported as `.cls`; a standard module
and a procedure-local declaration are not class-module module-level placement.
_Avoid_: declaration-line WithEvents flag, procedure-local event source, handler declaration

**RecoveredWithEventsVariableDeclaration**:
A variable declarator that retains its normal `VbaDefinition` and its written
`WithEvents` modifier after a `WithEvents`-specific syntax restriction prevents
it from becoming a syntactically admitted `WithEventsVariableDeclaration`.
Invalid placement,
an array designator, `As New`, a type-declaration character, and a missing `As`
or class type all use this recovery when the identifier remains recoverable.
Definition, References, Hover, ordinary variable Type Resolution, and ordinary
variable Rename continue to use the recovered identity and any surviving
declaration metadata. The declarator is excluded from
`WithEventsEventBindingSet`, handler-prefix binding, handler diagnostics, and
dependent Rename. It is not represented as `notWithEvents` or `indeterminate`
binding evidence and establishes no dependent relationship of its own. When the
recovered declarator belongs to a `ConditionalDeclarationFamily`, ordinary
variable Rename still targets that complete family; a sibling variant whose
type eligibility is `eligible` may therefore establish family-wide dependent
edits independently of the recovered variant. A syntactically admitted
declaration with a statically
impermissible type is not this recovery form; it retains a conclusive-invalid
`WithEventsTypeEligibility` instead.
_Avoid_: discarded variable definition, type-eligible WithEvents variant, indeterminate Event binding

**WithEventsTypeEligibility**:
The project-aware static-semantic classification of one syntactically admitted
`WithEventsVariableDeclaration` after ordinary VBA Type Resolution. `eligible`
means the declared type resolves to a specific class other than the enclosing
class, is accessible to VBA, and has an authoritative, complete Event surface
containing at least one structurally valid Event. For an external TypeLib
coclass, that existence test uses `TypeLibStructuralEventSurface`, so
`FUNCFLAG_FHIDDEN` and `FUNCFLAG_FRESTRICTED` members still count.
`invalidEnclosingClass` means its canonical type identity is the enclosing class
itself. `invalidNotClass` means the type conclusively resolves to something
other than a specific class. `invalidInaccessibleType` means a specific class is
conclusively unavailable to VBA, including an external coclass marked
`TYPEFLAG_FRESTRICTED`. `TYPEFLAG_FHIDDEN` affects discovery but does not make
an explicitly resolved coclass inaccessible. `invalidNoEvents` means a
specific, accessible, non-enclosing class has a complete authoritative
structural Event surface with no valid Event. `indeterminate` means the type is
unresolved or ambiguous, the applicable catalog or `HostClassEventSurface` is
missing, stale, or incomplete, or only `RecoveredEventDeclaration` evidence is
available. Creatability is neither required nor disqualifying. Assignment
compatibility and `Implements` compatibility do not establish eligibility. The
four conclusive-invalid states are mutually exclusive and use the precedence
`invalidEnclosingClass`, `invalidNotClass`, `invalidInaccessibleType`, then
`invalidNoEvents`. They retain ordinary variable
Definition, References, Hover, Type Resolution, and Rename, but contribute no
`WithEventsEventBindingSet` entry, handler diagnostic, or dependent Rename
relationship of their own. An `indeterminate` declaration is not recovered: it
contributes one `indeterminate` binding entry before suffix lookup. That entry
suppresses aggregate handler diagnostics and prevents
`HandlerEventRenameConvergence`; when no entry resolves and the declaration name
therefore remains an `indeterminateCandidate`, an upstream variable Rename fails
with `analysisIncomplete`. Mixed resolved and indeterminate entries retain the
existing resolved navigation and dependent-edit projections without claiming
that the indeterminate Event resolved. A type-eligible conditional sibling may
independently establish family-wide dependent edits that include the
conclusive-invalid declaration in ordinary family Rename. A resolved external
type uses only its `TypeLibEventSurface`; an intrinsic form or document class
uses its `HostClassEventSurface`.
_Avoid_: guessed Event source, creatable-class requirement, static-invalid syntax recovery

**WithEventsHandlerNameDecomposition**:
The syntax-only split of one complete procedure declaration identifier into a
`WithEvents` variable-name prefix and Event-name suffix at its final ASCII `_`.
It applies uniformly to Sub, Function, and each Property Get, Let, or Set
accessor before procedure kind is validated. Both parts must be nonempty and
satisfy their applicable MS-VBAL identifier forms. The prefix may itself contain
ASCII underscores or non-ASCII identifier characters; the Event-name suffix
cannot contain an underscore under VBA's Event-name restriction. Decomposition
does not inspect declared variables, reference catalogs, Event members,
conditional branch paths, parameter signatures, visibility, or `Static`, so
later metadata availability cannot change the split. A name with no final
separator or an empty or invalid part proceeds as an ordinary procedure
declaration.
_Avoid_: first-underscore split, catalog-selected split, candidate split ranking

**WithEventsEventBindingSet**:
The variant-preserving result of resolving the prefix and suffix of a
procedure declaration admitted by `WithEventsHandlerNameDecomposition`, without
choosing a conditional-compilation branch. Tentative prefix resolution
identifies one module-level variable target in the same class or its complete
`ConditionalDeclarationFamily`. After target admission, each physical
module-variable variant contributes one binding entry unless it is a
`RecoveredWithEventsVariableDeclaration` or has a conclusive-invalid
`WithEventsTypeEligibility`. An ordinary variant without the `WithEvents`
modifier is classified `notWithEvents` before type or Event-member lookup. A
`WithEventsVariableDeclaration` whose type eligibility is `eligible` is
`resolved` with its Event target, `notEvent` when the suffix is conclusively not
an Event on that class, or `indeterminate` when Event-member resolution remains
incomplete or ambiguous. An external TypeLib suffix is resolved through
`TypeLibExistingHandlerRecognitionSurface`, not the narrower
`TypeLibEventAuthoringSurface`, so an already-written candidate can retain a
hidden or restricted Event association that ordinary completion would not
offer. An intrinsic form or document suffix follows `HostEventShadowing`: an
unguarded valid source Event supplies only its source target, while a guarded
source Event family and a same-name projected host Event remain separate
configuration-dependent targets without branch-coverage proof. A declaration
whose type eligibility is
`indeterminate` contributes one `indeterminate` entry before suffix lookup.
Every recovered or conclusive-invalid declaration is excluded entirely rather
than becoming `notWithEvents`, `notEvent`, or `indeterminate`.
A nonconditional variable or complete conditional family enters this analysis
only when it contains at least one `WithEventsVariableDeclaration` whose type
eligibility is `eligible` or `indeterminate`; a target containing no such
declaration does not produce a binding set. Different type-eligible
`WithEvents` variable variants may resolve the same suffix to different Event
identities. Consumers
may deduplicate identical presentation locations, but the binding set retains
variable-variant and Event-target provenance. It never infers an active host
branch.
`WithEventsHandlerRecognition` determines which handler projections the
aggregate result can safely expose.
_Avoid_: selected WithEvents type, flattened Event target, conditional host binding

**WithEventsHandlerCandidate**:
A procedure-kind-independent semantic candidate formed for one physical Sub,
Function, or Property Get, Let, or Set declaration in a class-module code
section after `WithEventsHandlerNameDecomposition` succeeds, its prefix resolves
in that same class module to a module-level variable target admitted by at least
one `eligible` or `indeterminate` `WithEventsTypeEligibility`, and a
`WithEventsEventBindingSet` is available. A declaration in a procedural module
or another class, a failed decomposition, or a prefix without such an admitted
same-class target remains an ordinary procedure. The complete declaration
identifier retains its original procedure or Property definition. Its prefix
retains the variable binding, and every resolved suffix association is an
`EventReference`, independently of whether the procedure kind is valid for an
Event handler. Visibility and initial or trailing `Static` remain declaration
metadata and do not change candidate identity, binding, compatibility, or
conditional-family membership. Each Property accessor is a separate physical
candidate even when complementary accessors share one Property identity. Once
recognized as either `resolvedHandler` or `nonSubProcedureAssociation`, its
original procedure or Property logical target is a `DependentRenameTarget`;
Property identity and conditional-family membership expand that target
atomically.
_Avoid_: Sub-only name recognition, visibility-selected handler, Property handler family

**WithEventsHandlerRecognition**:
The aggregate classification applied independently to each physical,
`WithEventsHandlerCandidate` after producing its `WithEventsEventBindingSet`,
without selecting a branch. A Sub with at least one `resolved` entry is
`resolvedHandler`; it becomes a `WithEventsHandlerDeclaration`, and editor
projections use every resolved entry even when other entries are
`notWithEvents`, `notEvent`, or `indeterminate`. A Function or Property accessor
with at least one `resolved` entry is `nonSubProcedureAssociation`; this state
records an Event association and non-Sub procedure kind without itself asserting
that the declaration is invalid. It retains the same prefix and suffix
navigation projections but does not become a `WithEventsHandlerDeclaration` or
enter `EventHandlerCompatibility`. Every resolved target still retains
`EventHandlerValidationAuthority`; an external TypeLib or last-known-good host
association permits this recognition without authorizing a procedure-kind
diagnostic. Every entry being
a conclusive `notWithEvents` or `notEvent`
produces `ordinaryProcedure`;
the complete identifier remains its ordinary definition and receives no
handler-specific prefix binding, `EventReference`, compatibility analysis,
diagnostic, or dependent Rename. No resolved entry and at least one
`indeterminate` entry produces `indeterminateCandidate`, regardless of procedure
kind or other conclusive non-handler entries; the complete identifier retains
its original definition and the prefix retains its variable binding, while
suffix `EventReference`, procedure-kind validation, Event-signature comparison,
handler diagnostic, and dependent Rename are deferred. It is not treated as an
`ordinaryProcedure` merely to permit Rename: a
`WithEvents` variable `RenamePlan` fails with `analysisIncomplete` when any
`WithEventsHandlerCandidate` whose prefix binds that variable target remains an
`indeterminateCandidate`. After later evidence classifies it as
`resolvedHandler` or `nonSubProcedureAssociation`, dependent Rename applies;
after it becomes `ordinaryProcedure`, variable Rename leaves the procedure
unchanged.
Mixed `resolved` and non-resolved entries expose
the resolved editor projections but cannot establish either a procedure-kind or
incompatible-signature diagnostic. A fully resolved set containing any
`externalTypeLibAdvisory` or `lastKnownGoodHostAdvisory` target likewise
preserves all associations while suppressing both compile-style diagnostics.
No classification selects a host branch.
Same-named, same-scope, all-conditional declarations subsequently form the
existing `ConditionalDeclarationFamily`; family formation does not merge their
physical recognition, kind-validation, or compatibility results.
_Avoid_: guessed handler, all-or-nothing conditional binding, non-Event handler diagnostic

**IntrinsicHostHandlerCandidate**:
A procedure-kind-independent semantic candidate formed for one physical Sub,
Function, or Property accessor in a source module associated with a
`HostClassProjection`, when its complete name equals
`IntrinsicEventSourceName`, `_`, and an `existingHandlerRecognizable`
`HostEventIdentity`. Its complete identifier remains the procedure or Property
definition, its Event-name suffix is an `EventReference`, and its source-name
prefix and underscore have no independent semantic target; it has no
`WithEventsEventBindingSet`, and a source `Event` never replaces its host
target. Under current projection authority, the complete procedure or Property
name is a fixed host-contract spelling rather than a `RenameTarget`; no part of
the declaration or an ordinary complete-name reference initiates Rename. A
last-known-good-only association preserves guidance but cannot authorize that
mutation, while the same spelling follows ordinary procedure Rename rules when
neither current nor last-known-good evidence associates it with a host Event.
_Avoid_: WithEventsHandlerCandidate, inferred host handler, source Event handler

**IntrinsicHostHandlerDeclaration**:
A physical Sub declaration admitted by `IntrinsicHostHandlerCandidate`; a
matching Function or Property accessor is instead a
`nonSubProcedureAssociation`. Same-named, same-scope, all-conditional candidates
use the existing `ConditionalDeclarationFamily`, while each physical candidate
retains its own recognition, compatibility, and authority-permitted diagnostic
result without active-branch selection.
_Avoid_: WithEventsHandlerDeclaration, intrinsic Event stub, source Event declaration

**ResolvedEventSignatureSet**:
The nonempty Event-signature projection used by one recognized handler. An
external handler projects every `resolved` entry in its
`WithEventsEventBindingSet`; an `IntrinsicHostHandlerDeclaration` projects its
single associated host Event. A nonconditional source Event or a resolved
TypeLib Event projected through `TypeLibEventSurface` contributes one
signature; a resolved host Event projected through `HostClassEventSurface`
likewise contributes one. A conditional Event `ConditionalCallableFamily`
contributes every physical source Event signature, and
configuration-dependent `HostEventShadowing` retains the distinct host
signature rather than adding it to that family.
For an already-written external handler candidate, a hidden or restricted
member resolved through `TypeLibExistingHandlerRecognitionSurface` contributes
its retained signature even though `TypeLibEventAuthoringSurface` omits it.
Different binding entries may therefore contribute different Event identities.
The set retains their variable-variant and Event-target provenance and does not
turn them into one conditional Event family or overload. Each projected Event
contract alternative combines an applicable external `WithEvents` relationship
and its Event target into one `ConditionalContractProvenance`; an intrinsic
host contract has only target provenance, while a retained host-shadow
alternative is conditional in its own right. Identical Event
locations may be coalesced only for presentation. A binding set with no
`resolved` entry produces no signature set. Missing catalog, type, array, or
parameter-mechanism metadata remains attached to the relevant signature and can
make its comparison indeterminate. A `RecoveredEventDeclaration` is excluded
from the set rather than represented by a placeholder signature, but resolution
retains its presence as separate indeterminate evidence. A resolved target with
only recovered Event declarations contributes no signature; a mixed
conditional Event target contributes its valid signatures while remaining
nonconclusive because of the recovered declaration.
_Avoid_: ConditionalEventFamily, event overload set, guessed handler target

**EventHandlerValidationAuthority**:
The closed, provenance- and freshness-sensitive evidence controlling whether
`EventHandlerProcedureKindDiagnostic` or
`IncompatibleEventHandlerSignatureDiagnostic` may be emitted.
`sourceDeclared` applies to a valid source Event, and
`currentHostProjected` applies to an Event in a current authoritative
`HostClassProjectionSnapshot`; both permit compile-style validation.
`externalTypeLibAdvisory` applies to a TypeLib Event, and
`lastKnownGoodHostAdvisory` applies to retained stale host evidence; both
preserve association and signature guidance without authorizing either
diagnostic. An aggregate external-handler diagnostic requires every binding
entry to be resolved and every resolved target to be `sourceDeclared` or
`currentHostProjected`; an intrinsic-handler diagnostic requires its one target
to be `currentHostProjected`. Any advisory authority or incomplete target
evidence suppresses it.
_Avoid_: Event signature availability, stale host validation, selected target authority

**EventHandlerCompatibility**:
The family-aware, declaration-to-declaration analysis between one syntactically
complete `WithEventsHandlerDeclaration` or
`IntrinsicHostHandlerDeclaration` and every Event signature in its
`ResolvedEventSignatureSet`. Each recognition path resolves the handler target
before parameter comparison. The analysis then retains a compatible,
incompatible, or indeterminate result for every signature without selecting a
conditional variant or compilation branch. The same operation handles a singleton
nonconditional source Event, a singleton resolved TypeLib or projected host
Event, and a multi-variant conditional Event family. For a conditional handler family, it
runs independently for every physical handler variant against the same complete
Event-signature set; one handler variant's match never selects a branch or
changes another variant's result. It shares lower-level parameter-type, array,
parameter-mechanism, and Optional or `ParamArray` shape comparison primitives,
but it is not `CallArgumentMapping`: a handler declaration has no call-site
expressions, named arguments, omitted arguments, or active parameter. Parameter
names are not compatibility elements; ordered parameter position is. Parameter
types match only when normalization and Type Resolution establish the same
canonical type identity. Type-declaration characters and qualified or
unqualified spellings may therefore match when they resolve to that same
identity, but call-site Let coercion and assignment compatibility do not
participate: `Object` and a concrete class, a class and an implemented
interface, `Variant` and a concrete type, and distinct numeric types remain
different. Missing, unresolved, ambiguous, catalog-dependent, or host-dependent
type evidence is indeterminate when it cannot establish a canonical identity;
it is never guessed compatible or incompatible. Array shape, effective
parameter mechanism, and Optional or `ParamArray` shape remain independent
comparison dimensions. Definition, References, Rename, and the handler's
Event-target binding do not change with the compatibility results. A TypeLib or
last-known-good host comparison remains available for Hover, Signature Help,
and other advisory guidance, but `EventHandlerValidationAuthority` prevents it
from causing a compile-style error diagnostic.
_Avoid_: event overload resolution, handler call mapping, selected Event variant

**ConditionalSignatureRanking**:
The presentation-only ordering and active-signature choice among the signatures
of one `ConditionalCallableFamily`. Ranking first prefers
context-compatible variants, then named-argument membership, positional arity
including Optional and `ParamArray` bounds, and exact static type matches or
proven class or interface assignment compatibility. Numeric or string coercion
and conversion through `Variant` do not establish preference; an exact
`Variant`-to-`Variant` match remains exact. Unknown types, incomplete arguments,
and unimplemented coercion rules remain neutral. Compatible call contexts rank
before indeterminate contexts, which rank before incompatible contexts. All
variants stay in Signature Help. On an initial request, equally ranked variants
use stable source order; on a retrigger, a previously selected viable variant
is retained only as a tie-break after the current context, name, arity, and type
ranking. A first-party client supplies `activeSignatureHelp` through LSP
`contextSupport`. The server correlates its selected
`SignaturePresentationIdentity` with the current family without treating it as
semantic binding; an absent, non-unique, or no-longer-tied match falls back to
stable source order. A client without context support also uses stable source
order, and the server keeps no hidden cursor-specific selection state.
_Avoid_: semantic overload resolution, return-type inference, filtered variant set

**SignaturePresentationIdentity**:
The deterministic editor-neutral fingerprint of one displayed signature's
label and ordered parameter presentation metadata, excluding its changing
active-parameter value. It exists only to correlate the client-returned
`activeSignatureHelp` with current signatures during a retrigger. It is not a
definition, variant, family, or semantic binding identity. A non-unique or
missing match supplies no retention hint.
_Avoid_: overload identity, ConditionalFamilyIdentity, server session state

**DuplicateDeclarationDiagnostic**:
The error-severity, project-aware `VbaValidationDiagnostic` with code
`validation.duplicateDeclaration` emitted for each proven
`DeclarationCollision`. Property-family signature or type incompatibility is a
different validation rule, and an ambiguous use of otherwise legal declarations
is diagnosed at the reference rather than as a duplicate declaration. Property
identity and the accessor-kind collision matrix are applied before the
union-of-configurations rule: complementary Get, Let, and Set kinds do not
collide merely because conditional and unconditional accessors are mixed.
Within one accessor kind, and for non-Property declarations, a same-name set
containing any unconditional declaration is evaluated as a collision, including
a set that also contains conditional declarations. Different parameter lists
do not exempt those callables. When every otherwise-colliding declaration is
conditional, an eligible `ConditionalDeclarationFamily` is retained by the
language-server model and requires an authoritative conditional-compilation
environment before a collision diagnostic is emitted.
Each physical declaration having at least one directly proven collision peer
receives one diagnostic on its declaration identifier, with message
`Declaration '<name>' conflicts with another declaration in this scope.` Its
related information points only to those direct peers, in stable project
declaration order. The presentation neither designates a source-order original
nor duplicates the same collision diagnostic at one identifier range.
_Avoid_: syntax diagnostic, property compatibility diagnostic, ambiguous-name diagnostic

**ModuleIdentityNameConflictDiagnostic**:
The single error-severity, project-aware `VbaValidationDiagnostic` with code `validation.moduleIdentityNameConflict` emitted on the authoritative unquoted `ModuleIdentityMetadata` payload when its `ModuleIdentity` conclusively conflicts with one or more containing or referenced VBA project identities. It preserves the complete ordered conflict set without overlapping diagnostics, retains the source definition for navigation and repairing Rename, does not replace source-to-source `DuplicateDeclarationDiagnostic`, and is suppressed rather than guessed when the required project or reference-name authority is incomplete.
_Avoid_: duplicate declaration, invalid module metadata, unresolved reference diagnostic

**IncompatibleCallArgumentListDiagnostic**:
The error-severity `VbaValidationDiagnostic` with code
`validation.incompatibleCallArgumentList` emitted only for a syntactically
complete call whose resolved target has no indeterminate
`CallArgumentMapping` and whose every possible signature is conclusively
inapplicable. A nonconditional callable is the single-signature case of the
same rule. A conditional family with any applicable or indeterminate variant
produces no call-compatibility diagnostic because the language server has not
selected an authoritative compilation environment. The diagnostic is also
suppressed while the same call has
`validation.duplicateNamedCallArgument`,
`validation.positionalCallArgumentAfterNamed`,
`syntax.raiseEventStatementNotAllowedHere`,
`syntax.raiseEventArgumentListRequiresParentheses`,
`syntax.raiseEventNamedArgumentNotAllowed`,
`syntax.raiseEventEmptyArgumentListNotAllowed`, or
`syntax.raiseEventOmittedArgumentNotAllowed`, or while the same occurrence has
`validation.raiseEventTargetNotDeclaredInEnclosingModule`, because those more
specific diagnostics already explain the failed call and the aggregate error
would be a cascade. `CallArgumentMapping` retains every incompatibility reason
for the admitted validation cases during suppression. A placement-invalid,
targetless, malformed empty, or omitted `RaiseEvent` is not admitted to mapping
at all. Once the specific diagnostic is removed, the aggregate rule is
evaluated again against the updated call. When the call supplies one or more
arguments, the primary diagnostic range is the complete argument-list source
range: it includes the parentheses for parenthesized call syntax and covers the
supplied arguments for statement-form syntax. When the call supplies no
arguments, the range is the callee identifier rather than an empty or
delimiter-only range. This diagnostic range is independent of each Signature
Help entry's `activeParameter`. Its stable primary message is
`No available callable signature accepts this argument list.` When the client
supports LSP diagnostic related information, each conclusively inapplicable
physical signature contributes related information at its declaration
identifier with the signature label and concise incompatibility reasons.
Conditional signatures use only the generic `[#If]` marker; source condition
expressions and branch paths are not projected. Through
`ContractDiagnosticDetailProjection`, a client without related-information
support receives the same conclusive signature details in the primary message
instead. Each detail contains every independently conclusive
`CallMismatchReason` rather than stopping after the first. Reasons derived
only as a cascade from an earlier structural failure are omitted. The stable
category order is call context, named or positional mapping, required-argument
or arity failure, `ByRef` compatibility, and proven type compatibility; source
argument order and then declaration parameter order break ties within a
category. Indeterminate type evidence and unmodeled coercion are never presented
as failures. A conclusive context reason uses
`call context: expected <allowed-kind-list>, found <candidate-kind>`. The fixed
expected lists are `Sub or Function` for statement invocation,
`Function or Property Get` for a value-producing read, `Property Let` for value
assignment, `Property Set` for object assignment, and `Event` for `RaiseEvent`.
The found kind preserves the physical declaration label: `Sub`, `Function`,
`Declare Sub`, `Declare Function`, `Property Get`, `Property Let`,
`Property Set`, or `Event`. Mapping and required-input reasons use the exact fragments
`<argument-subject> mapping: named arguments are not accepted`,
`<named-argument-subject> mapping: no parameter named '<written-name>'`,
`<named-argument-subject> mapping: parameter '<declared-name>' is already supplied`,
`<positional-argument-subject> mapping: no parameter accepts this argument`, and
`<parameter-subject>: required argument is missing`. Each supplied argument
receives only its first applicable mapping reason in that order. Unknown
named-argument-support metadata makes the mapping indeterminate. An omitted
`Optional` parameter or unused `ParamArray` portion is not a failure, and a
missing-required reason caused only by an earlier mapping failure is omitted as
a cascade. The dedicated duplicate-named and positional-after-named diagnostics
remain outside these fragments because they suppress this aggregate diagnostic.
A uniquely mapped direct-storage argument rejected by a modeled `ByRef`
exact-storage rule uses
`<argument-subject> for <parameter-subject> ByRef type: expected <parameter-type>, found <argument-type>`
and, independently,
`<argument-subject> for <parameter-subject> ByRef array shape: expected <scalar-or-array>, found <scalar-or-array>`.
The parameter subject falls back from its declared name to its one-based
declaration ordinal. A literal, expression result, callable result, or argument
made into a value temporary by explicit outer parentheses uses ordinary value
compatibility rather than a `ByRef` reason. The presentation never invents
`expected ByRef, found ByVal`; unknown storage-versus-temporary evidence is
indeterminate. When both direct-storage type and array shape are conclusively
incompatible, the type reason precedes the shape reason.
A uniquely mapped ByVal argument or `ByRef` value temporary rejected by modeled
value compatibility uses
`<argument-subject> for <parameter-subject> type: expected <parameter-type>, found <argument-type>`
and, independently,
`<argument-subject> for <parameter-subject> array shape: expected <scalar-or-array>, found <scalar-or-array>`.
The parameter subject has the same name-to-ordinal fallback. A type reason
requires a modeled Let or Set rule to prove conversion failure; unknown static
types, expression classifications, and unmodeled coercions remain indeterminate.
Type labels use resolved canonical presentation rather than expression text or
raw source spelling: type-declaration characters expand to canonical intrinsic
names, intrinsic casing is normalized, and an external type is
reference-qualified whenever otherwise-distinct identities would render alike.
Array shape uses only `scalar` or `array`; rank and bounds are not presented.
When both value type and shape are conclusively incompatible, type precedes
shape.
Each `CallMismatchReason` fragment omits terminal punctuation. After exact
duplicate fragments are removed at their first stable position, every retained
fragment is joined with `; ` in the established category, source-argument, and
declaration-parameter order; the enclosing `Mismatches:` sentence alone adds one
final period. No retained reason is truncated, summarized by count, or reduced
to the first failure. Related information and primary-message fallback reuse the
same ordered reason sequence.
A navigable related item uses
`Candidate signature: <callable-signature> [#If]. Mismatches: <reasons>.`;
an unlocated candidate or a non-supporting client's projected item uses the same
content as two LF-separated `Candidate signature` and `Mismatches` lines. The
marker and its preceding space are omitted for an unconditional signature, and
`Candidate` implies neither active-branch selection nor overload binding.
_Avoid_: conditional-variant warning, incomplete-call error, best-signature error

**CallMismatchReason**:
One conclusive caller-facing explanation for why a `CallArgumentMapping` is inapplicable. It labels the whole call as `call context`, a supplied argument by its one-based source ordinal plus its written name when named, and an absent required parameter by its name or one-based declaration ordinal when metadata has no name.
_Avoid_: parameter-centric supplied-argument error, selected-overload reason, zero-based argument index

**EventHandlerProcedureKindDiagnostic**:
The error-severity `VbaValidationDiagnostic` with code
`validation.eventHandlerMustBeSub` emitted independently for one physical
`WithEventsHandlerCandidate` or `IntrinsicHostHandlerCandidate` classified
`nonSubProcedureAssociation`. A `WithEventsHandlerCandidate` additionally
requires every entry in its `WithEventsEventBindingSet` to be `resolved` and
every resolved target to have `sourceDeclared` or `currentHostProjected`
`EventHandlerValidationAuthority`; an intrinsic candidate requires its one host
target to be `currentHostProjected`. A Function selects exactly its `Function`
keyword. A Property accessor selects the complete source span from `Property`
through `Get`, `Let`, or `Set`. Its stable message is
`Event handlers must be declared as Sub procedures.` A `notWithEvents`,
`notEvent`, or `indeterminate` entry suppresses the diagnostic so the server
does not diagnose from only some possible compilation configurations. Any
`externalTypeLibAdvisory` or `lastKnownGoodHostAdvisory` association also
suppresses it; external TypeLib behavior is advisory, and stale host evidence
cannot establish current compile behavior.
Visibility and initial or trailing `Static` do not participate. Each physical
Property accessor is diagnosed independently. A
`nonSubProcedureAssociation` candidate does not enter
`EventHandlerCompatibility` and never also receives
`validation.incompatibleEventHandlerSignature`; its established Event
association and any applicable external prefix variable binding remain
available for navigation, while only a `WithEvents` association participates in
upstream-initiated dependent Rename under ADR 0029.
_Avoid_: Function handler signature diagnostic, visibility handler error, Property-family kind diagnostic

**IncompatibleEventHandlerSignatureDiagnostic**:
The error-severity `VbaValidationDiagnostic` with code
`validation.incompatibleEventHandlerSignature` emitted independently for one
syntactically complete physical `WithEventsHandlerDeclaration` or
`IntrinsicHostHandlerDeclaration` only when its `ResolvedEventSignatureSet`
contains only conclusively incompatible signatures under
`EventHandlerCompatibility`. An external handler additionally requires only
`resolved` `WithEventsEventBindingSet` entries, no
`RecoveredEventDeclaration`, and wholly `sourceDeclared` or
`currentHostProjected` targets; an intrinsic handler requires its one target to
be `currentHostProjected`. Any `notWithEvents`, `notEvent`, or `indeterminate`
binding entry, compatible or indeterminate signature, recovered declaration,
`externalTypeLibAdvisory`, or `lastKnownGoodHostAdvisory` evidence suppresses
the diagnostic for that physical handler variant. Advisory signatures remain
available for guidance but cannot cause this compile-style error. Another
handler variant's compatibility neither suppresses nor causes it. For a conditional family this avoids selecting a
conditional-compilation environment: a physical handler variant that matches at
least one possible Event signature is not diagnosed, even though the language
server cannot prove that their branch paths correspond. For a singleton source
or current projected host Event, the rule preserves the same fail-closed
metadata behavior. Emission does
not narrow or otherwise change the
handler's Event-target binding. Its primary range is that physical handler's
complete parameter-list source range including parentheses when present, or its
identifier when the parameter list is omitted. Its stable primary message is
`Event handler signature does not match any available Event signature.` When
the client supports LSP diagnostic related information, each conclusively
incompatible Event signature with a navigable location contributes one item at
its source declaration identifier. The item contains its signature label and
every independently conclusive `ContractMismatchReason`, reporting parameter
count first and then each mapped parameter by ordinal with canonical type,
array shape, effective `ByVal` or `ByRef`, and Optional or `ParamArray` role.
Its exact text is
`Required contract: <Event-signature> [#If]. Mismatches: <reasons>.`, with the
marker and its preceding space omitted when the projected Event contract has
unconditional `ConditionalContractProvenance`.
The ordinal labels the slot rather than constituting a mismatch reason, and
parameter names are never mismatch reasons. Navigable Event items use stable
project declaration order and are never ranked by mismatch count, category, or
current edit state. Physical Event variants remain separate even when their
presentations are identical because their related locations are distinct.
Conditional contract provenance uses only `[#If]`, never its source condition. An
authoritative incompatible signature without a navigable location contributes
an `UnlocatedContractDiagnosticDetail` to the primary message; when the client
lacks related-information support, `ContractDiagnosticDetailProjection` also
retains every navigable contract detail there.
_Avoid_: selected-event error, handler syntax diagnostic, call-argument diagnostic

**IncompatibleInterfaceMemberSignatureDiagnostic**:
The error-severity `VbaValidationDiagnostic` with code
`validation.incompatibleInterfaceMemberSignature` emitted independently for a
physical same-kind interface implementation only when every possible contract
variant is conclusively incompatible. Related information identifies each
physical required contract and reports every independently conclusive mismatch
without treating parameter spelling, an implicit-versus-explicit equivalent
parameter mechanism, or a structurally unmappable secondary difference as a
failure. Contract details expose only the generic `[#If]` marker derived from
`ConditionalContractProvenance`.
An authoritative incompatible contract without a navigable declaration uses
`UnlocatedContractDiagnosticDetail` rather than being omitted or assigned a
synthetic related-information location. Navigable contract items use stable
project declaration order rather than a best-match ranking.
_Avoid_: selected interface variant error, first-mismatch-only diagnostic, parameter-name mismatch

**ContractMismatchReason**:
The stable user-facing `expected <contract-value>, found <source-value>`
explanation for one independently conclusive Event-handler or interface-member
signature difference. It identifies ordinary parameters by one-based ordinal,
the final Property Let or Set slot as `value parameter`, and Function or
Property Get output as `return`; multiple reasons are joined by `; ` in one
related-information item and receive one final period.
_Avoid_: parameter-name difference, inferred secondary mismatch, source-spelling comparison

**UnlocatedContractDiagnosticDetail**:
The two-line `Expected signature` and `Mismatches` fallback appended to an Event
or interface primary diagnostic for an authoritative, conclusively incompatible
contract that has no navigable definition. On a client supporting related
information, navigable contracts remain only there; identical unlocated
presentations coalesce, while every
distinct presentation remains visible without truncation. Source-backed
related information uses stable project declaration order; a host projection or
external catalog fallback uses its authoritative contract set's stable
signature order. Neither surface reorders by conditionality, mismatch count,
mismatch category, or edit state, and the two surfaces are not interleaved into
one synthetic sequence.
_Avoid_: synthetic related location, duplicated navigable detail, hidden expected signature

**UnlocatedRequiredContractDiagnosticDetail**:
The one-line `Required contract` fallback appended to an interface missing,
kind-mismatch, or partial-coverage primary diagnostic for an authoritative
required contract without a navigable definition. On a client supporting
related information, navigable contracts remain only there; identical
unlocated presentations coalesce and
every distinct presentation remains visible in the diagnostic's stable contract
order without truncation.
_Avoid_: signature-comparison detail, synthetic related location, omitted external contract

**ContractDiagnosticDetailProjection**:
The capability-aware presentation that keeps conclusively inapplicable callable
candidates and authoritative Event or interface contract evidence visible
without duplication. A client supporting diagnostic related information
receives navigable details there and only unlocated details
in the primary message; a client without that support receives both navigable
and unlocated details in the primary message. The combined fallback uses
canonical kind order where applicable, then source-backed contracts in stable
project declaration order before unlocated host or catalog contracts in their
authoritative set's stable signature order. Within that primary-message
fallback only, exactly identical complete presentations coalesce at their first
position without a multiplicity label; physical contracts remain distinct in
analysis and in location-bearing related information.
_Avoid_: headline-only fallback, always-duplicated contract detail, client-owned reconstruction

**VbaSyntaxTree**:
The parsed VBA source structure needed for `SyntaxHighlighting`,
`SyntaxDiagnostic`s, and completion candidate discovery while preserving the
syntax structure those editor features depend on. It does not include
compile-time type inference or unresolved-name diagnostics in the current
scope.
_Avoid_: regex scan result, semantic model, compiler

**SyntaxChangeSet**:
The reusable parser result represented by `VbaSyntaxTreeChangeSet`, pairing a
complete current `VbaSyntaxTree` with only the semantic reuse proof a consumer
may trust. `Unchanged` proves exact
URI and text equality plus whole-tree reuse, `ModuleMember` identifies the
previous and current `ModuleMember` whose surrounding syntax may be reused, and
`Module` requires module-derived artifacts to be recomputed. It does not expose
the incremental parser route, changed line ranges, source-window dimensions,
fallback reason, segment counters, or other implementation observations.
Only an unmodified parser-produced tree can establish `Unchanged` or
`ModuleMember`; a publicly constructed tree or a tree whose URI, text, token
stream, module, or diagnostics were replaced fails closed to `Module`.
_Avoid_: parse update kind, incremental parser report, source-window result

**VbaTokenStream**:
The source-range-preserving lexical token sequence produced before
`VbaSyntaxTree` parsing. It classifies VBA keywords, identifiers, literals,
operators, punctuation, comments, whitespace, newlines, line continuations, and
preprocessor directives so lexical `SyntaxHighlighting` and parser recovery can
continue even when the full syntax tree is incomplete.
_Avoid_: text split, regex match list, semantic token

**VbaIdentifier**:
A lexical name accepted in its entirety by at least one complete MS-VBAL
`lex-identifier` form: Latin, code-page, Japanese, Korean, simplified Chinese,
or traditional Chinese. Character membership and initial-character rules are
form-specific; combining the forms into one character union must not admit a
name that no individual form accepts. Recognition is independent of the active
Windows ANSI code page and includes characters that generic Unicode or .NET
word and whitespace categories classify differently from MS-VBAL. A typed-name
suffix and `FOREIGN-NAME` syntax are not part of the base `VbaIdentifier`.
_Avoid_: Unicode identifier, current-ACP identifier, union-of-code-pages identifier

**VbaIdentifierForm**:
One complete MS-VBAL alternative that can accept a whole `VbaIdentifier`:
Latin, code-page, Japanese, Korean, simplified Chinese, or traditional Chinese.
It is a lexical compatibility rule rather than a source encoding, current user
language, host locale, or Unicode script; one name may satisfy more than one
form.
_Avoid_: language mode, source code page, locale, script

**ReusableVbaParserCore**:
The parser and syntax model layer that can serve `VbaLanguageServer` editor
features and may later be shared with documentation tooling such as DoxyVB6
without depending on LSP, VS Code, workbook automation, or `VbaDev` command
behavior.
_Avoid_: language-server feature code, DoxyVB6 adapter, workbook parser

**SemanticToken**:
A meaning-aware classification of a source range, derived from parsed `VbaProject` information. `SemanticToken`s refine `SyntaxHighlighting` for declarations and references, using standard editor token categories whenever a VBA meaning can be represented by one.
_Avoid_: syntax token, text token

**SourceFormatting**:
Editor-initiated rewriting of VBA source text to match the language server's
source style. It includes casing normalization and indentation formatting,
while preserving source meaning. Source formatting is fail-closed: incomplete
or malformed source may still receive safe lexical or structural formatting, but
formatting does not guess unresolved names, ambiguous names, or malformed block
relationships.
_Avoid_: syntax highlighting, refactoring

**CasingNormalization**:
A `SourceFormatting` operation that rewrites VBA keywords and identifier
references to their canonical casing. For source-defined names, the declaration
spelling is the canonical casing; formatting normalizes references to that
spelling but does not change the declaration name itself. Identifier reference
casing is normalized only when `NameResolution` resolves the reference to one
definition unambiguously; unresolved or ambiguous names keep their original
casing. Procedure-local `VbaDefinition`s such as local variables and
`CallableParameter`s participate in casing normalization within their visible
procedure scope. `VbaProjectReferenceDefinition`s also participate when their
`VbaProjectReference` is active, a usable `VbaProjectReferenceCatalog` supplies
the definition, and `NameResolution` resolves the reference unambiguously. In a
`QualifiedReference` or `MemberChainResolution` expression, each segment is
normalized only while the corresponding definition can be resolved; once a
segment is unresolved or ambiguous, formatting does not guess casing for that
segment or later segments in the chain. String literals, ordinary comments, and
`DocumentationComment` prose are not casing-normalized even when they contain
text that looks like an identifier.
_Avoid_: rename, spelling correction

**LanguageVocabulary**:
The fixed VBA words whose casing is defined by the language server rather than
by a `VbaDefinition` or `VbaProjectReferenceDefinition`. It includes VBA
keywords, intrinsic types, and literals. VBA standard-library constants are
structured `VbaProjectReferenceDefinition`s supplied by the
`VbaStandardLibraryReference`, not completion-only vocabulary strings.
_Avoid_: host definition, project definition

**CompletionExpectation**:
The syntax-owned description of what may legally follow at an editor position. It is derived from `VbaSyntaxTree`, remains stable across irrelevant trivia, and fails closed when syntax does not establish a valid continuation. A completed grammar marker opens its next slot only after the lexical separator required by VBA, while punctuation operators can open an operand slot immediately. Related position facts may carry canonical contextual statements, syntax words, or module-kind-specific starter words, but contain no `VbaDefinition` or LSP trigger metadata.
_Avoid_: general completion, trigger context

**SyntaxWord**:
A fixed VBA word required by the active grammar transition, such as `Then`, `In`, or a declaration continuation. It is selected by `VbaSyntaxTree` for the exact editor position and is not a general keyword proposal.
_Avoid_: keyword completion, declaration keyword

**CompletionCandidate**:
An editor proposal admitted by a `CompletionExpectation` after semantic resolution. It may originate from a `VbaDefinition`, `VbaProjectReferenceDefinition`, `LanguageVocabulary`, named `CallableParameter`, callable-owned line label, contextual branch statement, `ContractMemberNameCompletion`, or `EndStatementCompletion`. Its insertion and replacement facts are complete before LSP projection, and proposals with the same label remain distinct when their effective insertion text differs.
Candidate discovery reads only the already-admitted `VbaProject` source,
language vocabulary, committed reference-catalog definitions, and committed
host-class projections; it does not perform TypeLib or host discovery or refresh
during an editor completion request, and it does not wait for in-flight catalog
work. Hidden or
restricted catalog definitions are omitted from ordinary completion unless they
are admitted as exposed root definitions. Reference-qualified completion also
filters exposed root definitions by the active role, so type positions, value
positions, and creatable-type positions do not receive one mixed catalog list.
When same-label candidates differ by semantic role or effective insertion text,
they remain separate and rely on completion kind, detail, and icon metadata to
identify the role. Only candidates with the same label, same effective insertion
text, and equal resolution rank are eligible for the existing ambiguity or
coalescing rules. Configuration-dependent same-name source and host Events are
the deliberate exception: external Event authoring exposes one name-only
candidate with `Event [#If]` detail while retaining every signature outside
completion. Editor-facing ordering follows visible-source proximity before
referenced-library candidates: procedure-local, current-module, public project,
then standard or external reference catalog candidates. After an explicit
`VbaProjectReferenceQualifier`, ordering is scoped to that qualifier's admitted
surface and uses existing kind and label metadata without triggering additional
catalog discovery.
_Avoid_: completion definition, raw vocabulary

**QualifierCompletionCandidate**:
A `CompletionCandidate` that helps the user start a `QualifiedReference`, such
as `ModuleIdentity.` or `VbaProjectReferenceQualifier.`. It is not a value,
callable, or type definition by itself; after the qualifier is formed, member
completion owns the next candidate set. Source module qualifiers and active
reference qualifiers use the same qualifier-completion behavior. The displayed
label is the qualifier name without the dot; the inserted text includes the
trailing dot so member completion can continue from the qualified position. It
is admitted only at positions whose `CompletionExpectation` can start a
qualified reference, not after a completed expression or other closed grammar
slot. It remains distinct from a same-name value, callable, type, or constant
candidate because its effective insertion text forms a qualifier.
_Avoid_: module value, namespace object, callable candidate

**PropertyAccess**:
The semantic capability retained when complementary `Property Get`, `Property
Let`, or `Property Set` declarations are coalesced into one logical property.
Property identity is formed before conditional-family and collision analysis.
Conditional alternatives are then grouped independently within each accessor
kind, so mixed conditional and unconditional complementary kinds remain one
legal property while a same-kind mixed set collides. Source accessor identity
distinguishes a legal Get/Let/Set family from duplicate accessors, while
`Readable` and `Writable` capabilities are derived from source accessor kinds or
TypeLib invoke metadata. Parameter-list and declared-type compatibility across
the legal family is a separate Property validation rule. `Unknown` remains
loadable for legacy catalogs but admits no context-specific
`CompletionCandidate` until refreshed.
_Avoid_: getter flag, setter flag, inferred property mode

**IndentationFormatting**:
A `SourceFormatting` operation that rewrites leading whitespace according to
VBA block structure. It depends on source ranges, tokens, and syntax block
structure rather than `NameResolution`; identifier meaning does not affect
indent depth. Each emitted indentation level follows the resolved editor style:
`indentSize` spaces when spaces are requested, or one tab when tabs are
requested. A formatting client that does not provide `indentSize` uses
`tabSize` as a compatibility fallback. When block structure is incomplete or
malformed, indentation uses only recognized structure and does not infer
repairs for missing block boundaries.
_Avoid_: alignment, line wrapping

**EndStatementCompletion**:
An explicit editor completion candidate that inserts the matching VBA block
closer for a block opener, such as `End Sub`, `End Function`, or `End If`.
_Avoid_: `BlockSkeletonInsertion`, automatic typing, on-type edit

**BlockHeader**:
A complete logical VBA statement that opens a body-owning block with a canonical
matching terminator. It may span multiple physical lines through VBA line
continuations, but only its final physical line completes the header.
_Avoid_: definition line, opener line, declaration row

**BlockDeclarationHeader**:
A `BlockHeader` that declares a body-owning module member or module-level type.
Participating forms are non-external `Sub` and `Function`, `Property Get`,
`Property Let`, `Property Set`, `Enum`, and `Type`.
_Avoid_: block header, declaration line

**BlockSkeletonInsertion**:
An editor action that expands a complete `BlockHeader` after an Enter keypress
at the end of its final physical line into an indented empty body and its
matching block terminator. It does not activate on an intermediate line that
continues the logical header. Every participating form receives the same single
empty body line; the action does not add form-specific placeholders such as an
initial `Case` clause. The action inserts that body line and the terminator at
the caret without consuming or reusing pre-existing following blank lines; such
lines remain unchanged after the new terminator.
It is separate from explicit `EndStatementCompletion` and from
`SourceFormatting`. It participates in `BlockDeclarationHeader`s and in
block-form `If...Then`, `For`, `For Each`, `Select Case`, and `With` statement
headers. A single-line `If`, `While...Wend`, and every pre-condition or
post-condition `Do...Loop` form are outside its scope. Unconditional
`Do...Loop` is also outside its scope. An `Event` declaration does not
participate because VBA events have neither a body nor an `End Event`
terminator. External `Declare Sub` and `Declare Function` declarations also do
not participate because their implementation bodies exist outside VBA. A
matching terminator already owned by a participating header
suppresses the action, but post-header block pairing alone does not establish
ownership when nested blocks use the same terminator. Prefix ancestry and
leading indentation distinguish a candidate-owned closer or branch from an
ancestor boundary: candidate indentation suppresses insertion, ancestor
indentation permits further validation, and ambiguous indentation fails closed.
The action does not move or rewrite an existing body. It scaffolds a fresh
empty block rather than repairing an existing unterminated body. Existing code
or comments that could belong to the body make the header ineligible;
intervening blank lines do not.
A following end of file, same-level block declaration, conditional-compilation
boundary, or known ancestor branch or terminator may establish a safe non-body
boundary. The prospective terminator is speculatively inserted and reparsed; it
is eligible only when it closes the candidate, restores the ancestor boundary,
removes only directly caused missing or mismatched diagnostics, and introduces
no new error. Eligibility is otherwise local and fail-closed: the participating
header must parse completely, be permitted by the current `VbaModuleKind`, and
have no overlapping error-severity `SyntaxDiagnostic` or
`VbaValidationDiagnostic` apart from the directly caused missing or mismatched
diagnostics eliminated by the validated insertion. Warnings and informational
diagnostics do not suppress the action, and unrelated diagnostics elsewhere in
the document do not suppress the action. Trailing whitespace and an apostrophe
comment are header trivia and remain unchanged; they do not make the header
ineligible when Enter is pressed at the actual physical line end. Invalid
trivia, such as a comment after a line-continuation marker, remains a syntax
error and suppresses the action.
Branch headers such as `Else`, `ElseIf`, `Case`, and `Case Else` do not own a
new block and never trigger the action, even when the shared enclosing
terminator is missing.
For both `For` and `For Each`, the inserted canonical terminator is the bare
`Next` statement without a repeated counter or element name.
Any top-level colon in the `BlockHeader`, including a trailing colon, makes the
header ineligible because it expresses same-physical-line statement structure.
Colons inside string literals or comments remain trivia and do not affect
eligibility.
Conditional-compilation directives such as `#If...Then` are not `BlockHeader`s
for this action. An ordinary participating header inside a conditional branch
remains eligible only when its block relationship can be established entirely
within that branch; the action never infers a relationship across `#Else`,
`#ElseIf`, or `#End If`.
The terminator copies the exact leading whitespace of the `BlockHeader`'s first
physical line. The empty body line adds one resolved editor indentation unit to
that prefix: `indentSize` spaces when spaces are requested, or one tab when tabs
are requested. A continued header's final line does not become the indentation
base. Existing header whitespace remains unchanged, and inserted text preserves
the document's line-ending convention. The terminator uses canonical
`LanguageVocabulary` casing independently of the header's spelling; the action
does not recase the existing header.
_Avoid_: `EndStatementCompletion`, source formatting, automatic completion

**MemberStubGeneration**:
A deferred explicit source mutation that creates complete VBA procedure
declarations from authoritative Event-handler or `Implements` member contracts.
It covers `WithEvents`, intrinsic host Events, and interface implementation
under one future feature rather than extending ordinary completion or
`BlockSkeletonInsertion`.
_Avoid_: Event-name completion, handler-only generator, block skeleton insertion

**InterfaceVariableAccessorContract**:
The derived Get, Let, and Set implementation requirements contributed by one
valid Public variable in a source interface class named by `Implements`. The
variable remains the sole physical `VbaDefinition` and navigation target:
each declarator's effective declared type comes from its explicit type or
type-declaration character, otherwise the interface module's applicable
`DefType`, otherwise Variant. Variant contributes Get, Let, and Set; Object or a
named class contributes Get and Set; every other valid declared type contributes
Get and Let. When the identity of a named type is unresolved, including one
written with `As New`, Get is the only available contract; Let and Set remain
absent until type resolution establishes the identity. A conditionally guarded
variable gives the derived contracts conditional provenance, while an invalid
Public array or fixed-length String contributes none.
_Avoid_: synthetic Property declaration, TypeLib callable Property, logical writable property

**InterfaceVariableAccessorContractSet**:
The accessor-kind-specific group of every
`InterfaceVariableAccessorContract` derived from one Public-variable
`ConditionalDeclarationFamily` and implemented name. It is an authoring and
fulfillment projection, not a `VbaDefinition`, `ConditionalCallableFamily`, or
Call Resolution target; Signature Help retains each contributing contract while
Definition returns the owning variable family.
_Avoid_: synthetic Property family, selected compilation branch, callable overload set

**InterfaceContractFulfillment**:
The same-kind compatibility relation between every variant in one required
interface callable or accessor contract set and every physical implementation
variant. A contract is covered, or an implementation is compatible, when at
least one counterpart matches; unresolved evidence stays indeterminate, and
conditional expressions, branch order, and nesting never pair variants.
_Avoid_: active-branch selection, conditional-expression equivalence, overload binding

**PartiallyImplementedInterfaceMemberContractDiagnostic**:
The error-severity `VbaValidationDiagnostic` with code
`validation.interfaceMemberContractNotFullyImplemented` representing an
`InterfaceContractFulfillment` in which at least one same-kind contract variant
is covered and at least one other variant is conclusively uncovered.
It is aggregated per `Implements` relationship, implemented member name, and
required kind at the relationship's interface type reference; related
information identifies only the conclusively uncovered physical contracts
without selecting a best implementation match or reporting pairwise mismatch
reasons. An authoritative uncovered contract without a navigable definition is
retained as an `UnlocatedRequiredContractDiagnosticDetail` in the primary
message.
Indeterminate evidence prevents a variant from being called uncovered; a total
absence of same-kind implementations and total conclusive incompatibility
remain distinct diagnostic states.
_Avoid_: missing-member diagnostic, signature-mismatch diagnostic, conditional-branch alignment diagnostic

**PropertyValueParameterContract**:
The final required assignment slot of a Let or Set
`InterfaceVariableAccessorContract`. It has the Public variable's canonical
effective type and effective `ByVal` semantics; `AssignedValue` is its stable
presentation name, while an implementing parameter's spelling and written
`ByVal`, `ByRef`, or omitted mechanism are not contract identity.
_Avoid_: indexed Property parameter, named-argument identity, source variable

**ContractPrefixCompletion**:
A first-stage, name-only `CompletionCandidate` admitted in an empty or
partially typed callable declaration-name slot after `Sub`, `Function`,
`Property Get`, `Property Let`, or `Property Set`. For an associated
`IntrinsicEventSourceName`, a same-class
`WithEvents` variable, or an interface named by an applicable `Implements`
relationship, it inserts only the semantic contract prefix and one trailing
underscore, such as `Worksheet_`, `publisher_`, or `IFoo_`, and leaves the
declaration in the same member-completion context as if that prefix had been
typed manually. A partial name matches candidate prefixes case-insensitively by
leading text, and selection replaces only that partial declaration-name
fragment. Once the fragment exactly equals a complete semantic prefix including
its underscore and that exact prefix has at least one surviving downstream
member, `ContractMemberNameCompletion` takes precedence; a longer prefix that
happens to share that leading text is not mixed into the member list. An exact
textual match with no surviving member remains an ineligible prefix, so viable
longer prefixes that share the text stay in the first-stage list rather than
opening an empty second stage. Before a viable exact prefix exists, downstream
member suffixes are not shown. A prefix appears only when the current
declaration kind has at
least one downstream `ContractMemberNameCompletion` candidate after
authoring-admission and collision filtering. These contracts do not also
contribute complete handler or implemented names at the first stage. This
downstream existence check controls admission only; it does not aggregate
member-level conditional provenance into the prefix presentation. A generic
`[#If]` detail marker on a prefix describes only guarded prefix provenance: the
same-class `WithEvents` declaration or the applicable `Implements`
relationship. An intrinsic host prefix is unmarked, and neither a guarded
completion location nor a guarded downstream Event or interface member adds a
prefix marker. A prefix row presents a relationship origin rather than a
concrete Event or interface contract alternative, so it is not a
`ConditionalContractProvenance` projection. For a coalesced prefix, only
origins that supply at least one surviving downstream member participate in
this presentation, and `[#If]`
appears exactly when every such origin has guarded prefix provenance. One
participating unconditional origin, including an intrinsic host origin, removes
the prefix marker; an origin with no remaining member contributes nothing. The
first-party editor continues immediately to the second-stage member list after
inserting the prefix; an editor without that continuation still reaches the
identical member context through explicit completion.
Case-insensitively identical inserted prefixes form one candidate even when
several contract origins contribute them. Whether inserted or typed manually,
the completed prefix resolves every currently matching contract origin for the
second-stage member list; accepting the prefix does not select one origin. The
candidate retains an actual contributing spelling. When contributors differ
only by casing, they group by `OrdinalIgnoreCase` and the ordinal-minimum exact
spelling supplies both label and insertion text, independently of source or
enumeration order. Its compact detail is `Host Events`, `WithEvents`, or
`Interface` when every contributor belongs to one contract domain, and
`Multiple Contracts` when domains are mixed. Prefix rows contain no signature
or individual-member detail.
_Avoid_: complete member completion, member stub, expression completion

**ContractMemberNameCompletion**:
A second-stage, name-only `CompletionCandidate` admitted after a semantic
contract prefix and one underscore in a callable declaration-name slot. The
prefix may have been typed manually or inserted by `ContractPrefixCompletion`;
both paths use the same candidate discovery, filtering, and presentation. The
contract is an associated `IntrinsicEventSourceName`, a same-class `WithEvents`
variable, or an interface actually named by the class's `Implements`
statements. Candidates show the complete canonical handler or implemented name
but replace only the member suffix. They must match the declared procedure kind:
Events admit only `Sub`, while interface members admit their corresponding
procedure or Property-accessor kind. Completion inserts neither parameters, a
body, nor a terminator; complete member creation remains
`MemberStubGeneration`.
Case-insensitively identical complete names under the same required procedure
or Property-accessor kind form one member candidate even when several contract
origins or signature variants contribute them. Accepting that candidate creates
only the name and never selects an origin or signature. Every contributing
origin and signature remains available to Signature Help, Definition, and
validation after the declaration is completed.
The coalesced row uses `Event` when every contributor is an Event contract,
`Interface Member` when every contributor is an interface contract, and
`Multiple Contracts` when both domains contribute. It appends `[#If]` when any
contributing contract has conditional provenance, even when another contributor
is unconditional; the completion location alone adds no marker. Casing
conflicts use the same contributor-spelling and ordinal-minimum rule as prefix
completion, while the edit replaces only the member suffix and never recases a
manually typed prefix. Its completion detail pane lists each distinct signature
presentation once, where the signature label together with its `[#If]` state
forms presentation identity. It uses the same stable order as Signature Help,
selects no active signature or parameter, and exposes neither contract-origin
names nor conditional expressions. For each displayed signature, empty
documentation is ignored and identical nonempty documentation is shown once.
One distinct document appears directly; several appear in stable contributor
order as numbered `Documentation variants`. The presentation neither chooses,
merges, nor summarizes their content and applies no information-hiding count
limit.
After excluding the declaration being edited and applying the declaration-kind,
namespace, and Property-accessor collision matrix, the candidate remains when
there is no same-scope otherwise-colliding peer with the complete contract name.
When at least one such peer exists, the name is conclusively occupied if the
prospective declaration or any peer is unconditional. A compatible kind and
signature satisfies it while an incompatible kind or signature conflicts with
it; neither state contributes a second completion candidate. When the
prospective declaration and every peer are conditionally guarded, the advisory
candidate remains without comparing conditions, branch ancestry, or nesting,
and later diagnostics own any actual duplicate. Diagnostics and an explicit
repairing action own a conclusive conflict instead of name completion creating
another declaration. Complementary Property Get, Let, and Set kinds are not
collision peers merely because they share a Property name.
A generic `[#If]` detail marker on a member candidate describes conditional
contract provenance, not the completion location: it appears when an applicable `Implements`
relationship, same-class `WithEvents` declaration, interface member or Public
variable owning a derived accessor, source Event declaration, or retained
configuration-dependent host-shadow alternative is conditional. A surrounding
conditional branch alone adds no marker. Conditions are neither displayed nor
proved exhaustive, so equivalent declarations in every branch remain
conditional provenance.
Admission requires a complete contract name, its compatible declaration kind,
and conclusive authoring admission from domain-specific current or committed
last-known-good evidence. For an Event this includes its authoring surface or
explicit `authoringAvailable` behavior; for an interface member it includes a
valid accessible contract and complete accessor or invoke-kind evidence.
Missing signature or documentation metadata may degrade detail and Signature
Help but does not remove that name-only candidate. Missing identity, kind, or
authoring admission contributes no guessed candidate. Once those minimum facts
are known, indeterminate collision evidence does not suppress the advisory
item; a conclusively occupied unconditional name does suppress it even when
incomplete signature evidence cannot distinguish satisfied from conflicting.
For an external TypeLib callable Property, the compatible kind comes directly
from its physical invoke kind: property-get admits `Property Get`, value-put
admits `Property Let`, and reference-put admits `Property Set`. A Property that
exposes both put forms contributes both accessors in their respective
declaration contexts. Declared value types never substitute for missing invoke
kind metadata.

For an intrinsic host contract, only a `Sub` declaration is eligible.
`ContractPrefixCompletion` displays the associated
`IntrinsicEventSourceName` plus underscore only when at least one downstream
`authoringAvailable` Event survives collision filtering. The member candidate
then displays that Event as the complete handler name but replaces only the
typed Event suffix, using current or last-known-good projection evidence. It
omits only a conclusively colliding same-scope name after excluding the
declaration being edited: a set containing any unconditional declaration
collides, while all-conditional peers, other scopes, and indeterminate collision
evidence do not suppress the advisory candidate. Its compact presentation uses
the complete handler name as label, `Event` as kind detail, and the Event suffix
for filtering, sorting, projected signature, and documentation.
Typing `(` after selection enters the existing intrinsic-handler Signature Help
without the completion itself inserting parentheses or parameters.
_Avoid_: MemberStubGeneration, handler snippet, runtime instance completion

**DocumentationComment**:
A structured Doxygen-style VBA comment block attached to a `VbaDefinition` regardless of public or private visibility. Hover shows the complete rendered comment. Signature Help presents only the active `CallableParameter`'s `@param` documentation; its protocol metadata retains documentation per parameter so the client can select the active one, but callable summary, details, and return documentation are not projected. Plain apostrophe comments are not `DocumentationComment`s; when an implementation member has no `DocumentationComment`, it may inherit one from the interface member named by its `Implements` relationship.
_Avoid_: comment, note, description

**CallableSignature**:
The structured call shape for a callable `VbaDefinition` or `VbaProjectReferenceDefinition`. It includes the displayed signature label, ordered parameters, optional parameter metadata, parameter passing metadata, parameter type names, default values, return type names, callable kind, named-argument support, and parameter documentation when that documentation is available from source comments or reference catalog metadata. When shown by Signature Help or as a callable hover declaration, the primary label carries the callable kind (`Sub`, `Function`, `Property`, `Event`, or source `Declare` form), available return type, available parameter type metadata, and effective `ByRef` metadata, including implicit VBA `ByRef`, while `ByVal` is omitted even when explicit. Property accessors are collapsed to `Property`, `ParamArray` is shown when available, array parameters keep their `()` marker, optional parameters are represented with brackets rather than the `Optional` keyword, and visibility modifiers and default values are omitted. Reference catalog signatures follow the same rules but show only metadata supplied by the catalog; missing passing, type, callable-kind, or named-argument support metadata is not inferred. Current TypeLib catalogs establish named-argument support from their parameter metadata, while legacy persisted catalogs remain fail-closed and stale so they can be refreshed. TypeLib discovery maps COM invoke kinds, `[retval]` presence, and return-value semantics to explicit callable kinds. It projects a callable member as `Event` on a coclass only when that member belongs to the coclass's unique `FDEFAULT | FSOURCE` `TypeLibEventSurface`; it does not reclassify direct interface or dispinterface members or union non-default source interfaces. Hidden and restricted members retain that structural Event kind and their flags, while `TypeLibEventAuthoringSurface` independently decides whether ordinary completion may offer them.
_Avoid_: parameter list, call text, method shape

**Hover**:
An editor feature that explains the `VbaDefinition` or `VbaProjectReferenceDefinition` under the cursor. It renders the attached `DocumentationComment`, followed by a horizontal separator and a fenced `vba` declaration block. Callable definitions use their rich `CallableSignature`; other definitions use their `DeclarationLabel`. Hover does not expand per-parameter documentation or track an active `CallableParameter`.
When present, its protocol range identifies the identifier occurrence under the
request in that request document; the resolved declaration range belongs to
definition navigation instead.
_Avoid_: SignatureHelp, tooltip, parameter hover

**SignatureHelp**:
An editor feature that shows the rich `CallableSignature` for a resolved call
site and tracks the active `CallableParameter` independently for each displayed
signature. It omits callable-level documentation and retains per-parameter
documentation. Each LSP parameter label is the complete displayed parameter
segment, including brackets, passing metadata, array markers, and type metadata
when present. A signature omits its active parameter when the current
`CallArgument` has no unique mapping instead of clamping to an unrelated
parameter; a known mapping remains visible when type or call-context
compatibility fails separately. The internal active-parameter value remains
nullable independently of LSP client capability. A first-party client supports
per-signature `activeParameter` and explicit null for no active parameter.
Capability-aware projection sends per-signature indexes when
`activeParameterSupport` is present and explicit null only when
`noActiveParameterSupport` is present; a client without per-signature support
receives only the active signature's top-level index. When an older client
cannot represent null, projection preserves Signature Help and its variants,
omits the unrepresentable value, and accepts the protocol's parameter-zero
display fallback without changing the semantic mapping to zero.
_Avoid_: hover, tooltip, parameter hover

**DeclarationLabel**:
The editor-facing declaration summary for a non-callable `VbaDefinition` or `VbaProjectReferenceDefinition`, or the fallback when no richer `CallableSignature` is available. Constants, enums, and user-defined types include `Const`, `Enum`, or `Type`. Variables, parameters, enum members, root value properties such as `HostGlobalReferenceDefinition`s, and user-defined type members use declaration forms such as `Name As Type`; arrays keep `()` after the name. External enum members use the catalog-provided declared type rather than a contextual enum type inferred from the call or assignment site. Parameter labels include effective `ByRef` metadata while omitting `ByVal`. `Static` and `WithEvents` are included when they apply, while visibility modifiers and unavailable implicit types are omitted.
_Avoid_: signature, display name, hover text, owner-qualified name

**CallableParameter**:
A declared input slot on a callable definition, such as `Arg1` in `Sub Example(ByVal Arg1 As String)`. It is matched by name or position from a `CallArgument`.
_Avoid_: argument, call argument, local variable

**CallArgument**:
A value slot supplied at a call site, such as `"x"` or `Arg1:="x"` in `Example("x")` or `Example Arg1:="x"`. `CallArgument`s are distinct from `CallableParameter`s and may be positional, named, or omitted.
_Avoid_: parameter, callable parameter, argument text

**StatementFormCall**:
A VBA call form that invokes a callable at statement level without the `Call` keyword and without wrapping the argument list in parentheses, such as `ExampleSub Arg1:=1` or `ModuleName.ExampleSub "x"`. It is distinct from a parenthesized call and from expression uses of a callable name.
_Avoid_: bare call, implicit call, call expression

**NamedCallArgument**:
A `CallArgument` that explicitly names the target `CallableParameter`, such as `Arg1:="x"`.
_Avoid_: named parameter, named callable parameter

**PositionalCallArgument**:
A `CallArgument` matched to a `CallableParameter` by ordinal position rather than by name.
_Avoid_: unnamed parameter, indexed parameter

**OmittedCallArgument**:
An empty positional `CallArgument` slot in VBA call syntax, such as the first slot in `Example(, Arg2:="x")`.
It is still positional for named-argument ordering: `Example(Arg2:="x", )` has an omitted positional slot after a named argument.
_Avoid_: missing parameter, blank parameter

**ReferenceSignatureDiscovery**:
The process of collecting `CallableSignature` and type metadata for `VbaProjectReferenceDefinition`s from an available referenced-library catalog source. It enriches reference metadata so editor features can show accurate signature help without guessing signatures from member names alone.
_Avoid_: HostSignatureDiscovery, COM refresh, member scan, metadata scrape

**RenameTarget**:
A source-defined logical target backed by one or more `VbaDefinition`s that can
be renamed inside its `VbaProject`, except when the definition is explicitly a
`DependentRenameTarget`, a `ManagedModuleIdentity`, a
`HostManagedModuleIdentity`, or a current-authority
`IntrinsicHostHandlerCandidate` with a fixed host-contract name. A call occurrence bound to a
`ConditionalCallableFamily` identifies the complete family as its
`RenameTarget`, regardless of signature ranking or per-variant call
compatibility, except when that callable family is itself a
`DependentRenameTarget`. An Event occurrence bound to a
`ConditionalDeclarationFamily` containing a `RecoveredEventDeclaration`
likewise identifies the complete declaration family even when no valid callable
projection exists. Prepare Rename identifies the occurrence under the request
rather than a declaration location and supplies the target's canonical `Name`
as its placeholder. `VbaProjectReferenceDefinition`s, ordinary string literals,
and `DocumentationComment`s are not `RenameTarget`s.
_Avoid_: renameable symbol, edit target

**DependentRenameTarget**:
A source-defined logical target backed by one or more `VbaDefinition`s that can
be changed only as a derived edit inside another target's `RenamePlan`, not as
the initiating target of Prepare Rename or Rename. The original procedure or
Property logical target of a `WithEventsHandlerCandidate` classified
`resolvedHandler` or `nonSubProcedureAssociation` is dependent-only because an
independent name change would alter or remove the Event relationship that VBE
recognition established, whether or not the association's validation authority
permits `validation.eventHandlerMustBeSub`. Complementary Property Get, Let, and Set
accessors, conditional declaration variants, and their ordinary complete-name
references expand the same dependent target atomically only when any containing
conditional family's `ConditionalDependentRenameCoverage` is
`completeDependent`. A `conclusiveMixed` family fails with
`resolutionChanged`, while `indeterminateCoverage` fails with
`analysisIncomplete`; neither classification permits a partial derived edit. On
a candidate declaration-name occurrence, a position within the prefix selects
the `WithEvents` variable `RenameTarget`, a position within the suffix selects the
Event `RenameTarget` only when `HandlerEventRenameConvergence` succeeds, and the
separating underscore selects no target. An ordinary occurrence bound to the
complete procedure, Property, or conditional family also supplies no Rename
target. A direct Rename request for that complete target fails with
`notRenameTarget`; a suffix without convergence fails closed without choosing
among Event targets. The server does not infer an upstream variable or Event
Rename from a requested complete candidate name. Deliberately detaching a
non-Sub-associated Function or Property from the Event relationship requires a
manual edit or a separate repairing Code Action, not meaning-preserving Rename.
The procedure or Property logical target of a conclusively recognized
`Implements` implementation name is likewise dependent-only: a source interface
type Rename owns its prefix edit, while a source interface member Rename owns
its suffix edit; an `InterfaceVariableAccessorContract` uses the owning Public
variable family as that member target. Each upstream plan expands complementary
Property accessors, conditional declaration variants, and ordinary
complete-name references atomically. An independent complete-name Rename would
alter or sever the interface contract, so deliberate detachment requires a
manual edit or a future Code Action. On a conclusively associated implementation
declaration-name occurrence, the interface-prefix segment selects the source
interface type target, the member-suffix segment selects the source member or
owning Public-variable target, and their semantic separator selects no target;
each selected range and placeholder belongs only to that upstream segment. A
non-source-owned, unresolved, or ambiguous upstream identity supplies no Prepare
Rename target.
An ordinary occurrence bound to the complete implementation procedure,
Property, or conditional family carries no interface-prefix or member-suffix
projection and supplies no Prepare Rename target at any character. Definition
and References retain the complete implementation identity, while an upstream
plan still edits the occurrence atomically.
_Avoid_: independently renameable handler, reverse-inferred Rename, read-only definition

**RenameName**:
The exact, untrimmed replacement name requested for a `RenameTarget`. It must
be a complete `VbaIdentifier` accepted by one `VbaIdentifierForm`, contain
between 1 and 255 characters, and not be an MS-VBAL `reserved-identifier`.
A typed-name suffix and `FOREIGN-NAME` syntax are not accepted or stripped.
Lexer, declaration, and Rename validation share the same identifier and
reserved-word authority. A `ModuleIdentity` target additionally permits at most
31 Unicode code points; a longer requested name fails with `invalidName`. An Event `RenameTarget` additionally rejects any name
containing an ASCII underscore with `invalidName`; this target-specific rule is
evaluated even when the requested spelling is ordinally equal to an existing
underscore-invalid recovered Event name. A recovered Event can therefore be
repaired to a valid underscore-free name but cannot preserve or introduce that
invalid form through Rename. For every valid target-specific name, ordinal
equality with the target's canonical `Name` is a successful no-change result. A
case-insensitive match with different spelling is an intentional case-only
Rename that rewrites the declaration and every resolved target occurrence.
_Avoid_: display label, trimmed name, ASCII identifier, foreign name

**RenamePlan**:
A complete meaning-preserving workspace edit proven against one immutable `VbaProject`
snapshot for a `RenameTarget` and `RenameName`. Its atomicity is a planning guarantee: the server returns every required text and resource operation or no plan, but does not promise that an LSP client can roll back a filesystem failure after application begins. Complementary Property Get,
Let, and Set accessors and all variants of a
`ConditionalDeclarationFamily` form their respective logical target
relationships and rename atomically. The plan records explicit correspondence
between the pre-edit target and its hypothetical post-edit target instead of
requiring raw snapshot identities to remain equal. A plan rejects a
case-insensitive collision with a distinct declaration in the same VBA
declaration scope or namespace, and a `ModuleIdentity` plan also rejects a
collision with the containing `VbaProjectName` or an active
`ReferencedVbaProjectName`. After applying the hypothetical edits, every
target occurrence must still resolve to that logical target and every
pre-existing non-target semantic occurrence must retain its prior binding or
unresolved/ambiguous classification. An unrelated pre-existing error does not
invalidate a plan, and same-named public members in different modules are not
rejected unless their presence changes an actual occurrence's resolution. When
an Event Rename changes the complete name of a `WithEventsHandlerCandidate`
classified `resolvedHandler` or `nonSubProcedureAssociation`, the plan first requires
`HandlerEventRenameConvergence` on every affected suffix and requires its single
converged target to be the initiating Event target. Every containing
conditional family must also have `completeDependent`
`ConditionalDependentRenameCoverage`. A `conclusiveMixed` family fails with
`resolutionChanged`; absent such conclusive evidence, an
`indeterminateCoverage` family fails with `analysisIncomplete`. A distinct
resolved Event target or an `indeterminate` binding rejects the complete plan;
`notWithEvents` and `notEvent` entries are neutral. After that proof, the plan
adds a dependent procedure-, Property-, or conditional-family Rename without
making that target part of the Event family. It replaces the event-name suffix
in every physical candidate declaration and changes every ordinary reference
bound to the original procedure, complete Property identity, or conditional
family to the derived complete name. Complementary Property accessors and
conditional variants participate through their existing logical relationships.
The same collision and pre- and post-edit resolution proof covers this dependent
Rename. A Rename of a module-level `WithEvents` variable likewise changes the
prefix of every physical declaration in each `resolvedHandler` or
`nonSubProcedureAssociation` candidate target owned by that variable after applying
the same conditional-family coverage proof, derives each new complete procedure
or Property name while preserving its Event suffix, and renames every ordinary
reference bound to each dependent logical target. The
initiating variable Rename and every dependent candidate-target Rename form one
atomic plan. When `EventHandlerValidationAuthority` is wholly
diagnostic-authoritative—`sourceDeclared` or `currentHostProjected`—
`validation.eventHandlerMustBeSub` remains on each
`nonSubProcedureAssociation` Function or Property accessor after either upstream
Rename; Rename does not repair procedure kind. An external TypeLib or
last-known-good host association remains diagnostic-free before and after the
edit. Any derived collision,
changed binding, or incomplete
candidate ownership or reference analysis fails the complete plan closed. In
particular, after ruling out `conclusiveMixed` coverage, if any
`WithEventsHandlerCandidate` whose prefix binds the initiating variable target
is still an `indeterminateCandidate`, the plan fails with
`analysisIncomplete`; it neither leaves a potentially latent Event relationship
unchanged nor guesses a dependent Rename.
_Avoid_: text replacement, project-wide name reservation, compile-after-edit

**RenameFailure**:
An actionable LSP `RequestFailed` response with code `-32803` for a
well-formed Prepare Rename or Rename request whose recognized semantic occurrence cannot produce a `RenamePlan`. Stable
`error.data.reason` values distinguish `invalidName`, `notRenameTarget`,
`sameScopeCollision`, `resolutionChanged`, `analysisIncomplete`,
`moduleIdentityNotExplicit`, `moduleIdentityInvalid`, `managedModuleIdentity`,
`hostManagedModuleIdentity`, `clientCapabilityMissing`, and
`resourceOperationConflict`. Protocol shape errors remain `InvalidParams`;
Prepare Rename on an occurrence with no semantic target and an ordinally
unchanged `RenameName` use successful `null` results rather than a
`RenameFailure`. A `sameScopeCollision` preserves every semantic conflict in the
always-present ordered `error.data.conflicts` array rather than a singular
collision field. Each entry identifies `sourceDeclaration`,
`containingProject`, or `referencedProject` and carries that kind's authoritative
location or reference identity when available.
_Avoid_: invalid params, no-change result, empty workspace edit

**RenameResourceConflict**:
The pre-plan state in which a required file-following Rename cannot be safely described because its source unit or destination changed or conflicts. Its `resourceOperationConflict` failure distinguishes `sourceMissing`, `sourceChanged`, `destinationExists`, and `sidecarConflict` conditions without emitting any edit.
_Avoid_: DeclarationCollision, WorkspaceEditApplicationFailure, partial file Rename

**WorkspaceEditApplicationFailure**:
The operational failure in which a capable LSP client cannot completely apply an already-valid `RenamePlan`, for example because a destination appears, permissions change, or a filesystem provider fails after planning. It is distinct from an incomplete semantic plan; recovery is client-owned through Undo, retry, or explicit repair rather than server-side rollback.
_Avoid_: RenamePlan rejection, semantic rollback, partial plan

**ExternalDefinitionNavigation**:
The go-to-definition behavior for `VbaProjectReferenceDefinition`s supplied by
reference catalogs. Until the VS Code extension exposes a read-only virtual
catalog document provider with stable definition identities, external
definitions do not return navigable locations. Hover, completion, rename
rejection, find-references behavior, and applicable
`UnlocatedContractDiagnosticDetail`s and
`UnlocatedRequiredContractDiagnosticDetail`s can still use the structured
catalog definition without manufacturing a virtual or local source location.
_Avoid_: vba-reference file, generated source file, decompiled definition

**NameResolution**:
The case-insensitive process of matching an identifier reference to the closest visible `VbaDefinition` or `VbaProjectReferenceDefinition`. Procedure-local definitions outrank current-module definitions, current-module definitions outrank public project definitions, and project definitions outrank referenced-library definitions. `HostGlobalReferenceDefinition`s, `LibraryGlobalReferenceDefinition`s, standard-library constants, and reference qualifier names all use referenced-library rank rather than shadowing source definitions. Among referenced-library definitions, a `MainVbaProjectReference` match outranks matches from other active `VbaProjectReference`s.
_Avoid_: lookup, binding, search

**ModuleIdentity**:
The name of an exported VBA module, class, or form as defined by its authoritative `ModuleIdentityMetadata` record. A source with no such record has only a `FallbackModuleIdentity`, while invalid metadata produces `InvalidModuleIdentityMetadata`; neither state is authoritative for semantic mutation or host-class association.
_Avoid_: file name, module file, path name

**ModuleIdentityMetadata**:
The correctly placed exported record `Attribute VB_Name = "<VbaIdentifier>"` that authoritatively names one source module. A procedural module has exactly one such record; a class or form module may have multiple valid class-header records and uses the last one under MS-VBAL. Its quoted value uses the shared `VbaIdentifier` authority and contains at most 31 Unicode code points.
_Avoid_: first VB_Name attribute, inferred identity, ordinary string attribute

**ShadowedModuleIdentityMetadata**:
A valid class- or form-header `VB_Name` record superseded by a later valid record of the same attribute name. It remains exported source metadata but is neither a `ModuleIdentityOccurrence`, a `RenameTarget`, nor a dependent Rename edit.
_Avoid_: duplicate ModuleIdentity, prior ModuleIdentity, Rename occurrence

**InvalidModuleIdentityMetadata**:
The source state in which a procedural module has duplicate `VB_Name` records, or any module has a misplaced, malformed, over-31-code-point, or invalid-valued `VB_Name`-like record. Repeated valid class- or form-header records are not invalid by themselves. An invalid module body remains locally analyzable, but no record or file name becomes its project-wide identity.
_Avoid_: FallbackModuleIdentity, first-attribute identity, recoverable ModuleIdentity

**FallbackModuleIdentity**:
An analysis-recovery name derived from the source file basename only when no `VB_Name`-like metadata record exists. It may sustain navigation and name analysis, but it cannot authorize semantic `ModuleIdentity` Rename or establish `HostClassIdentity`; invalid metadata does not fall back to this state.
_Avoid_: implicit ModuleIdentity, file-owned module name, renameable fallback

**ModuleIdentityOccurrence**:
A source occurrence that denotes a source-owned `ModuleIdentity`, including the unquoted `Attribute VB_Name` payload, a resolved type occurrence, a standard-module qualifier, a predeclared/default-instance qualifier, or a conclusively associated interface-implementation prefix. The quoted payload is semantic identity metadata rather than an ordinary string literal; an instance variable used to access an interface member does not itself carry the interface `ModuleIdentity`.
_Avoid_: module-name string, file-name occurrence, class-name text

**TypeResolution**:
The process of matching an explicit VBA type annotation to a `VbaDefinition` or `VbaProjectReferenceDefinition` for member completion and member documentation. Source `VbaDefinition`s outrank referenced-library `VbaProjectReferenceDefinition`s unless the annotation is reference-qualified, and assignment-based inference is outside the MVP.
_Avoid_: type inference, runtime type, guessed type

**MemberChainResolution**:
The process of resolving a sequence of member accesses by carrying each resolved member's declared result type to the next member access. It applies to both source `VbaDefinition`s and `VbaProjectReferenceDefinition`s when result type metadata is available; missing or ambiguous result types stop the chain. Host-object member chains use declared type metadata from the active source and reference catalogs only; they do not inspect a live Office application or workbook state.
_Avoid_: host chain resolution, dotted lookup, chained lookup

**ContinuedMemberChain**:
A `MemberChainResolution` expression written across multiple physical VBA lines using code line-continuation markers. It is one logical member chain for resolution, while each segment keeps its original physical source range for editor features; a leading dot on a continued physical line belongs to this explicit chain rather than to a `WithReceiver`, and comment continuations are not part of it.
_Avoid_: logical line, multiline chain, wrapped chain

**ContinuedArgumentList**:
A parenthesized call argument list that spans multiple physical VBA lines using code line-continuation markers. It keeps signature help active and counts the active parameter across those physical lines, but it does not change `MemberChainResolution` or `ContinuedMemberChain`.
_Avoid_: multiline call, wrapped call, logical call

**WithReceiver**:
The nearest active `With ... End With` expression that supplies the implicit receiver for a leading-dot member chain that is not part of a `ContinuedMemberChain`. Its receiver expression may itself be a `ContinuedMemberChain`; nested `With` blocks use the innermost active `WithReceiver`, and missing or ambiguous receiver types do not produce guessed member results.
_Avoid_: with context, current object, implicit type

**QualifiedReference**:
An identifier reference written with a qualifier, such as `ModuleIdentity.MemberName`, `variable.MemberName`, or `Word.Application`. The qualifier itself follows `NameResolution`; a source definition named `Excel` or `Word` may therefore shadow a same-name `VbaProjectReferenceQualifier`. When the qualifier names a module, class, or form, only public members of that definition are visible from outside that module; when it names an active `VbaProjectReferenceQualifier`, only that reference's public root-surface `VbaProjectReferenceDefinition`s are visible.
_Avoid_: dotted lookup, member access, qualified symbol

**EventReference**:
A reference to an Event identity from a `RaiseEvent` statement or the resolved
event-name suffix of a `WithEventsHandlerCandidate` or
`IntrinsicHostHandlerCandidate`. A
syntactically admitted `RaiseEvent`
resolves only a source Event or conditional Event family declared in its
enclosing class module. It never falls back to a same-named Sub or Property,
another class's Event, a TypeLib Event, or an intrinsic form, document, or host
Event. An eligible local `RecoveredEventDeclaration` remains bound for
Definition, References, and repairing Rename, while its absent valid signature
contributes only indeterminate call evidence. A placement-invalid `RaiseEvent`
does not produce an `EventReference`.

A recognized candidate resolves only its event-name suffix through its
`WithEventsEventBindingSet`. The same suffix source range retains every resolved
Event target association rather than selecting one variable variant, including
when a Function or Property accessor is classified `nonSubProcedureAssociation`.
An ordinary occurrence with the same complete spelling refers to the original
procedure or Property identity, not an `EventReference`. When a resolved Event
target belongs to a
`ConditionalDeclarationFamily`, that binding retains the complete declaration
family and its available `ConditionalCallableFamily` projection. Definition
returns the location union of every resolved target and every declaration-family
Event variant, including a `RecoveredEventDeclaration`; References retain each
target association. Signature-dependent projections exclude recovered
declarations. For a candidate classified `resolvedHandler` or
`nonSubProcedureAssociation`, suffix Rename requires
`HandlerEventRenameConvergence` in addition to the complete target proof in ADR
0029. The `WithEvents` variable-name prefix remains a separate reference and is
not changed by an Event Rename. Parameter lists and procedure kind do not select
a conditional branch or narrow the family to one Event variant. When Event
Rename changes the candidate's complete procedure or Property name, its
declarations and every ordinary reference bound to that dependent logical target
participate atomically rather than becoming members of the Event family.

An intrinsic candidate's Event-name suffix refers to its one projected
`HostEventIdentity`; its `IntrinsicEventSourceName` prefix and underscore carry
no separate reference. Hover over the suffix uses the projected Event signature
and documentation. Definition follows `HostClassBaseTypeProvenance` to a
navigable external Event definition when available and otherwise returns no
location without redirecting to the handler procedure. References for that host
identity include intrinsic and external handler suffixes that actually retain
the same projected target; an external handler whose source Event shadows the
host Event is excluded. The complete declaration identifier and ordinary
complete-name occurrences continue to define or reference the procedure or its
`ConditionalDeclarationFamily`. The projected host identity is not a
source-defined `RenameTarget`: under current authority, Prepare Rename returns
no target for the suffix or any other part of the intrinsic candidate, and a
direct non-no-op Rename of the complete target fails with `notRenameTarget`,
including a case-only change. Last-known-good-only association instead makes a
non-no-op direct Rename fail with `analysisIncomplete`. An ordinally unchanged
request retains the general successful-null result.
_Avoid_: callback, event procedure, handler lookup

**HandlerEventRenameConvergence**:
The Rename-only proof that one resolved `WithEventsHandlerCandidate`
declaration suffix identifies exactly one source-owned logical Event
`RenameTarget` despite its `WithEventsEventBindingSet` retaining multiple
entries. At least one entry must
be `resolved`, every resolved Event association must identify the same
`RenameTarget`, and no entry may be `indeterminate`. Physical Event variants in
one `ConditionalDeclarationFamily` converge on that family target. A
`notWithEvents` entry is neutral because that variable variant cannot receive
Events, and a `notEvent` entry is neutral because the type-eligible class lacks
that suffix Event and has no competing Event binding. In either case, the
dependent procedure, Property, or conditional-family Rename still updates its
ordinary references.
Distinct Event identities, any resolved non-renameable external Event, or
incomplete resolution does not converge.
Definition and References continue to expose all resolved associations and are
never narrowed by this proof. Rename initiated from the Event declaration is
subject to the same rule for both `resolvedHandler` and
`nonSubProcedureAssociation`: if one dependent suffix also associates with another
Event target or is indeterminate, the complete `RenamePlan` is rejected rather
than changing only one meaning of the shared token.
_Avoid_: synthetic multi-Event Rename, ranked Event target, Definition narrowing

**WithEventsHandlerDeclaration**:
A physical Sub declaration whose procedure-kind-independent
`WithEventsHandlerCandidate` is classified `resolvedHandler`. The complete
identifier remains the handler procedure's `VbaDefinition`; within its
declaration-name occurrence, the prefix is also a reference to the complete
`WithEvents` variable target and each resolved suffix binding is an
`EventReference`. Public, Private, Friend, or omitted visibility and initial or
trailing `Static` are valid and do not change handler recognition or
compatibility. A Function or Property accessor with the same proven bindings is
instead `nonSubProcedureAssociation`; it receives
`EventHandlerProcedureKindDiagnostic` only when every resolved target has
`sourceDeclared` or `currentHostProjected`
`EventHandlerValidationAuthority`. An external TypeLib or last-known-good host
association retains its navigation, Hover, and signature-guidance projections
without that error. A declaration in a procedural module or
another class, an ordinary same-spelled occurrence, a procedure without a valid
name decomposition, or a candidate classified `ordinaryProcedure` is not a
handler declaration. An `indeterminateCandidate` retains only the projections
permitted by `WithEventsHandlerRecognition` until later snapshot evidence
resolves it. This definition describes only a valid Sub handler; the analogous
dependent-Rename role of a `nonSubProcedureAssociation` Function or Property is
defined by `WithEventsHandlerCandidate` and `DependentRenameTarget`.
Ordinary calls and other references to a resolved handler's complete name bind
its single procedure or, when all same-name peers are conditional, their complete
`ConditionalDeclarationFamily`. Definition of that procedure-family identity
returns every physical handler declaration, and References retain every
complete-name occurrence without selecting a branch. Each physical handler
variant keeps its own signature and `EventHandlerCompatibility`. The single
procedure or complete handler family is a `DependentRenameTarget`: Event Rename
or `WithEvents` variable Rename changes all physical declarations and ordinary
family references as one dependent atomic edit, but the handler target cannot
initiate an independent Rename.
_Avoid_: spelling-based handler, handler call reference, Event family member

**FormDesignerBlock**:
The non-code designer section of an exported `.frm` file, such as form and control property declarations. The MVP keeps it out of AST definitions and references even though the file itself belongs to the `VbaProject`.
_Avoid_: form code, form module, generated code

**ModuleMember**:
A top-level parsed member inside a VBA module, such as a procedure, property, enum, user-defined type, event, constant, variable, or declaration block. Incremental AST updates use `ModuleMember` ranges as their replacement unit.
_Avoid_: function block, top-level node, parse chunk

**VbaInteractiveWorkScheduler**:
The C# language-server module that continuously admits parsed LSP input while
committing workspace mutations and capturing immutable read state through one
ordered FIFO mutation-and-capture lane. Captured reads execute on a bounded
concurrent executor. The scheduler owns request cancellation, internal latency
and fairness policy, and deterministic stop behavior; it does not make the
TypeScript adapter authoritative for VBA semantics.
_Avoid_: unbounded parallel request executor, extension-host scheduler, request thread

**VbaLatestOnlyBackgroundMailbox**:
The non-generic composition Module that retains at most one pending
background delegate per authority, takes the latest delegate when execution
starts, and leverages `VbaInteractiveWorkScheduler` for bounded admission. It
owns active authority, ready FIFO, capacity retry, terminal completion, idle,
discard, and stop behavior. Producer Modules retain revision, freshness,
tombstone, and selection meaning.
_Avoid_: scheduler Adapter, producer-local retry loop, unbounded background queue

**InputSequence**:
The monotonic sequence assigned when `VbaInteractiveWorkScheduler` admits a
request, mutation, barrier, or explicit cancellation control. It records input
causality independently from execution start and response order.
_Avoid_: document version, request id, execution index

**ReadFence**:
The latest relevant workspace-mutation `InputSequence` that precedes an
admitted read. The ordered mutation-and-capture lane commits every earlier
mutation before the read captures one immutable revision. Later mutations may
commit while the captured read executes, but cannot alter that pinned revision;
non-mutating barriers and explicit cancellation controls do not advance the
fence.
_Avoid_: cancellation version, response sequence, source revision

**RequestCancellationOwnership**:
The generation-specific association between one active numeric or string LSP
request id and its request-scoped cancellation token. `$/cancelRequest` signals
that owner outside the ordered mutation-and-capture lane, while the request
executor remains the single owner of its normal or `RequestCancelled` response.
Ownership is released after choosing the terminal response and before writing
it, so a completed id can be reused without an older generation removing the
new owner.
_Avoid_: document-change cancellation, shared server token, response ownership

## Workspace Context

**ModernVbaWorkspace**:
The local multi-repository workspace that may contain `vba-tools`, archived
`vba-devtools`, `DoxyVB6`, and Excel macro repositories for integration work.
_Avoid_: monorepo, single repo

## Example Dialogue

Dev: "Does a `VbeDebugSession` make VS Code the interactive VBA debugger?"
Domain Expert: "No. VS Code owns launch configuration, breakpoint transfer, and launch lifecycle, while the VBE owns break mode, stepping, watches, and continuation."

Dev: "Does F5 keep focus in VS Code while `VbeDebugLaunch` starts?"
Domain Expert: "No. It displays both Excel and the target VBE code pane, and may move focus to the VBE where interactive debugging occurs."

Dev: "Does `VscodeExtension` automate Excel while handling F5?"
Domain Expert: "No. It launches the separate `VbaDebugAdapter`. The adapter invokes `VbaDev` for the hidden snapshot build, then owns visible Excel, VBIDE automation, and the `DebugExcelProcess`."

Dev: "Should `VbaDebugAdapter` reference `VbaDev.App` to avoid starting another process?"
Domain Expert: "No. It invokes the public `vba-dev build` CLI as a child process and uses its arguments, output, exit status, and cancellation contract. Internal application services do not cross the component boundary."

Dev: "Should `VbaDebugAdapter` search beside itself or on PATH for `vba-dev.exe`?"
Domain Expert: "No. `VscodeExtension` resolves and validates both executables and passes the effective absolute CLI path through `--vba-dev`. An invalid `vba-dev` override produces a visible warning and falls back to the compatible bundled CLI for the whole extension session; an invalid debug-adapter override still fails without substitution."

Dev: "What should guided creation do when neither the configured nor bundled `vba-dev` satisfies the required contract?"
Domain Expert: "Stop before environment preflight or project input and report one actionable error with Open Settings and Show Output. Do not search PATH, download another tool, run with an incompatible executable, or retry automatically. A compatible bundled fallback remains visible and session-pinned; a complete resolution failure creates no reusable preflight state."

Dev: "Should the debug adapter require the complete `vba-dev` command contract or a particular CLI release?"
Domain Expert: "No. Its independent contract requires only `featureVersions[\"build.sourceSnapshot\"] == \"1.0\"` from the supplied CLI. `vba-dev-contract.json` does not carry the adapter protocol, and the adapter's protocol, transport, session-ID form, cleanup command, and Doctor schema belong to `vba-debug-adapter-contract.json`."

Dev: "Can `VbaLaunchConfiguration` specify only a module without a procedure?"
Domain Expert: "No. Module and procedure are specified together or both inferred from the active source position captured in `DebugSourceSnapshot`. Project and document may independently narrow the selection."

Dev: "Must a user create `launch.json` before pressing F5 in an eligible VBA procedure?"
Domain Expert: "No. `VscodeExtension` synthesizes a transient `VbaLaunchConfiguration` and immutable source snapshot from the active source. It fails ambiguous resolution without saving source, writing configuration, or showing a target picker."

Dev: "Does `VbaDebugAdapter` provide VS Code stack frames, variables, stepping, or expression evaluation?"
Domain Expert: "No. It provides launch, ordinary source breakpoints, restart, termination, and output. The VBE alone owns interactive debug state and controls."

Dev: "Does `DebugLifecycleOutput` copy `Debug.Print` into the VS Code Debug Console?"
Domain Expert: "No. VBA output and runtime interaction stay in the VBE. VS Code receives only adapter lifecycle and setup messages."

Dev: "Should `VbeDebugLaunch` change VBE error trapping or clear watches so only transferred breakpoints can stop execution?"
Domain Expert: "No. `VbeDebugEnvironment` remains user-owned. Existing settings, watches, and `Stop` statements may stop execution independently."

Dev: "Should the `VbeDebugSession` report that VS Code is stopped when the VBE enters break mode?"
Domain Expert: "No. It remains running in VS Code until its launched Excel process exits; the VBE alone presents its break and run states."

Dev: "Does ending a `VbeDebugSession` ask Excel to save changes?"
Domain Expert: "No. Any session termination force-terminates its `DebugExcelProcess`; unsaved workbook changes and VBE state are lost."

Dev: "Can an adapter crash leave its Excel process, `vba-dev` child, and workspace indefinitely?"
Domain Expert: "The extension generates a random `DebugSessionId` before adapter launch and retains it even if initialization fails. The kill-on-close Job terminates the process tree; the extension then reaps that ID, or the next adapter start removes the stale lease. Neither cleanup path accepts an arbitrary directory."

Dev: "Should failure to remove a stale debug workspace change the result of the debug session that just ended?"
Domain Expert: "No. After five seconds of bounded deletion retries, retain and report the absolute path as a housekeeping warning. Invalid IDs, live or unverifiable leases, and deletion failure return nonzero without widening deletion scope, while a missing or never-claimed workspace is a silent successful no-op."

Dev: "Does `DebugLaunchCancellation` rewrite project source or delete the previous completed bin workbook?"
Domain Expert: "No. Debug launch does not save project source or replace bin output. `VbaDev` removes build-internal scratch, while the debug component removes its caller-owned snapshot and session workbook after the build process exits."

Dev: "Can one VS Code window run two `VbeDebugSession`s at the same time?"
Domain Expert: "No. The initial product permits one active session per window and never ends or reuses that session implicitly for another launch."

Dev: "Does Restart Debugging reuse the existing Excel process?"
Domain Expert: "No. `DebugRestartPreparation` first captures a new `DebugSourceSnapshot` without saving project files while the current session remains active. Only a matching preparation for the same document, module, and procedure force-terminates that process and performs a complete new `VbeDebugLaunch`; changing the active editor does not retarget Restart."

Dev: "Does closing only the `DebugWorkbook` leave an empty debug Excel session running?"
Domain Expert: "No. Once that workbook actually closes, its dedicated Excel process is force-terminated and the `VbeDebugSession` ends. Cancelling workbook close leaves both active."

Dev: "Should a `VbeDebugSession` reuse the hidden Excel process that built the workbook?"
Domain Expert: "No. It owns a fresh, visible `DebugExcelProcess` so breakpoint behavior and session lifetime are isolated from build automation and the user's existing Excel sessions."

Dev: "Does debug launch lower macro security in the user's existing Excel sessions?"
Domain Expert: "No. Macro enablement is scoped to the dedicated `DebugExcelProcess` while it opens the generated `DebugWorkbook`, and the setting is restored after open."

Dev: "Can the target open another workbook in its `DebugExcelProcess`?"
Domain Expert: "Yes, but every workbook in that process is session-owned and is lost if the process is force-terminated. Unrelated workbooks must stay in the user's other Excel sessions."

Dev: "Should a `DebugExcelProcess` open the manifest-defined bin workbook?"
Domain Expert: "No. It opens a session-temporary `DebugWorkbook` built from the launch snapshot in a temporary directory. Reusing the normal bin file name preserves `ThisWorkbook.Name`, while persistent bin output remains unchanged and `ThisWorkbook.Path` identifies the temporary location."

Dev: "Does opening a `DebugWorkbook` run `Workbook_Open` before breakpoints are ready?"
Domain Expert: "No. Excel events are suppressed while the workbook opens and re-enabled before the explicit `DebugTargetProcedure` runs. Startup logic must be exposed through an eligible wrapper to debug it."

Dev: "Does `VbaDebugAdapter` suppress every prompt while opening a `DebugWorkbook`?"
Domain Expert: "No. The `DebugExcelProcess` is visible before open, and open-time modal prompts remain available for the user to answer."

Dev: "Does `VbeDebugLaunch` time out while an Excel open prompt is waiting?"
Domain Expert: "No. It reports that Excel input is required and waits until the user answers or stops the session. Cancelling a prompt that prevents open makes the launch fail."

Dev: "Can a `VbeDebugLaunch` skip the build and use an older workbook?"
Domain Expert: "No. The debug component supplies the current snapshot to `vba-dev build` and opens the fresh caller-owned `DebugWorkbook`, so VS Code source locations and VBE statement locations describe the same source."

Dev: "How does a `VbeDebugLaunch` choose its `DebugTargetProcedure`?"
Domain Expert: "It uses the procedure containing the active source position by default. A launch configuration may explicitly identify the document, module, and procedure; ambiguous partial selection fails instead of guessing."

Dev: "Can a private event handler or a class method be a `DebugTargetProcedure`?"
Domain Expert: "No. A `DebugTargetProcedure` is a parameterless public `Sub` in a standard module. Other procedure forms require an explicit eligible wrapper."

Dev: "Does `Option Private Module` make a public procedure ineligible as a `DebugTargetProcedure`?"
Domain Expert: "No. Module-level project privacy does not exclude an otherwise eligible public procedure from VBE-driven execution."

Dev: "Should `VbeProcedureRun` fall back to `Application.Run` when the native VBE command is unavailable?"
Domain Expert: "No. It selects the target in its VBE code pane and invokes `Run Sub/UserForm`; a missing, disabled, or failing command is a `DebugSetupError`."

Dev: "Can the adapter execute native VBE commands after changing only the code selection in a background window?"
Domain Expert: "No. It must establish `VbeCommandContext` by activating and focusing the code pane before checking command availability. Commands that remain disabled still fail setup."

Dev: "Should a modal VBE compile error time out or be replaced by parser diagnostics?"
Domain Expert: "No. The adapter reports that VBE input is required and waits until the user dismisses it or cancels. Dismissal ends launch with `DebugSetupError`; VBE remains the compiler authority."

Dev: "Should a `VbeDebugLaunch` insert `Stop` when a requested `VbeBreakpoint` cannot be set?"
Domain Expert: "No. The launch fails instead of silently changing the requested debugging behavior."

Dev: "Does a `VbeDebugLaunch` require at least one `VbeBreakpoint`?"
Domain Expert: "No. With no participating breakpoints it runs the `DebugTargetProcedure` without adding an implicit stop-on-entry breakpoint."

Dev: "Does a `VbeRuntimeError` fail and terminate the `VbeDebugSession`?"
Domain Expert: "No. Only a `DebugSetupError` prevents launch. Once the target begins, runtime errors and break mode belong to Excel and the VBE, and the session remains active until its Excel process exits."

Dev: "Can Doctor prove debug readiness without touching Excel?"
Domain Expert: "No. `vba-debug-adapter doctor` uses a temporary Excel/VBE session to enter break mode at a native breakpoint, continue and verify harmless procedure completion, prove process and workspace ownership, and then remove all temporary state. `vba-dev doctor` remains an independent project-automation diagnostic."

Dev: "Should a failed adapter Doctor exit before returning its successful checks?"
Domain Expert: "No. Once command handling begins, schema `1.0` emits one complete or explicitly incomplete JSON object even on nonzero exit. The extension displays every check. Missing or malformed JSON is a Doctor-command infrastructure failure, not a failed check result."

Dev: "Should adapter Doctor wait indefinitely for a modal dialog as an interactive debug launch can?"
Domain Expert: "No. Its private fixture expects no interaction. Each active probe has a finite stage deadline; timeout is unverified, dependent checks are skipped, and process close or workspace deletion gets its own five-second cleanup boundary."

Dev: "Does one failed Doctor prevent the Command Palette action from running the other?"
Domain Expert: "No. `VBA Tools: Doctor` runs both independent commands and labels their results separately. Neither executable calls the other, and there is no `vba-tools doctor` CLI."

Dev: "Does `BreakpointTransfer` include a breakpoint that the user unchecked in VS Code?"
Domain Expert: "No. A user-disabled breakpoint remains recorded in VS Code but does not participate in transfer or launch failure."

Dev: "Does adding a VS Code breakpoint after procedure execution starts change the current VBE session?"
Domain Expert: "No. `BreakpointTransfer` is fixed before execution. Later breakpoint changes remain in VS Code for the next `VbeDebugSession`."

Dev: "Should `BreakpointTransfer` move a breakpoint from a comment to the next executable line?"
Domain Expert: "No. An in-scope breakpoint must map to the same executable statement. If it cannot, the breakpoint is unverified and the `VbeDebugLaunch` fails before procedure execution."

Dev: "Can `BreakpointTransfer` verify a native breakpoint by reading it back from VBIDE?"
Domain Expert: "No. A participating breakpoint remains unverified until exact source mapping and the native VBE breakpoint command both succeed. A missing, disabled, or failing command invalidates the whole launch."

Dev: "Can a VS Code breakpoint select the second colon-separated statement on one physical line?"
Domain Expert: "No. `VbeBreakpoint` is line-level. The VBE chooses the first stoppable position on that line; a continuation line that the VBE rejects invalidates launch rather than moving the breakpoint."

Dev: "Should a breakpoint in an inactive conditional-compilation branch move to the active branch?"
Domain Expert: "No. `DebugCompilationContext` comes from the snapshot-generated temporary workbook in its actual Excel host. An inactive target or breakpoint makes setup fail, and launch configuration cannot replace compiler constants."

Dev: "Does a `BreakpointSourceMap` compare `VERSION 1.0 CLASS`, its `BEGIN`/`END` block, and `Attribute VB_Name` as code?"
Domain Expert: "No. Those are export-only serialization records. The map projects each exported source kind onto its VBE code lines, models only the known UserForm leading blank, and requires the projected code to match exactly."

Dev: "Can a `VbeDebugLaunch` use unsaved editor source without changing project files?"
Domain Expert: "Yes. The client captures one immutable `DebugSourceSnapshot`; the separate adapter uses its debug metadata for target and breakpoint mapping and passes its build-neutral source inventory to `vba-dev build`."

Dev: "Should the extension create a temporary source directory and let `VbaDebugAdapter` delete it?"
Domain Expert: "No. The extension sends losslessly encoded source and sidecar bytes through DAP. The adapter validates them, materializes its own session directory, and alone owns that directory's lifecycle."

Dev: "Should completion include a procedure from another folder?"
Domain Expert: "Only when that folder belongs to the same `DocumentSourceSet` through `vba-project.json`. Without a `ProjectManifest`, an `AdHocVbaProject` indexes only the active source file's directory, so nested `common-modules` procedures are outside completion."

Dev: "Can two manifest documents point at the same or nested source directories to share VBA files?"
Domain Expert: "No. `DocumentSourceSetIsolation` makes the roots physically disjoint, so manifest order never chooses a source file's project context. `VbaDev` and the language server reject the manifest instead of guessing; model intentional sharing through CommonModules or another explicit distribution relationship."

Dev: "Must `new excel` reject every project root nested below another project root?"
Domain Expert: "No. Reject it when its filesystem identity falls within an ancestor project's `DocumentSourceSet`, because recursive discovery would give the child source two owners; fail closed when ancestor authority cannot be established. Scan the complete current ancestor chain before creating an artifact and again immediately before the initial manifest commit, including manifests that appeared during creation. Continue when the latest valid state remains physically disjoint even if it changed; otherwise roll back before commit. Do not acquire ancestor project leases or duplicate this authority in the extension, so the narrow post-check race remains the ordinary non-cooperating-writer boundary. A physically disjoint nested root may remain tolerated so an intentional layout is not blocked, but it is not a promoted or README-documented workflow, and it does not become a source-sharing mechanism."

Dev: "Should a standard module name appear at statement level so I can write `Lib_Common.New_Foo`?"
Domain Expert: "Yes, but as a `QualifierCompletionCandidate`, not as a value or callable definition. Once `Lib_Common.` exists, member completion should show the public members owned by that `ModuleIdentity`."

Dev: "Should `Excel.` and `Scripting.` behave differently from `Lib_Common.` in completion?"
Domain Expert: "No. They are also `QualifierCompletionCandidate`s when their owning `VbaProjectReferenceQualifier`s are active; after the qualifier is formed, member completion should show that reference qualifier's exposed definitions."

Dev: "Should the completion label include the dot, like `Lib_Common.`?"
Domain Expert: "No. The label is `Lib_Common`, while the insertion text is `Lib_Common.`. The label stays searchable as the qualifier name, and the inserted dot moves the editor into member completion."

Dev: "What if a module qualifier and a function have the same label, like `Foo`?"
Domain Expert: "They remain distinct when their effective insertion text or semantic role differs. A `Foo` qualifier inserts `Foo.` and carries qualifier detail, while a `Foo` function inserts `Foo` and carries callable detail. If the UI metadata is not enough to tell them apart, that is a presentation issue, not a name-resolution merge."

Dev: "Should catalog candidates sort above source candidates because there may be many Office members?"
Domain Expert: "No. Completion ordering follows source proximity before referenced-library candidates: procedure-local, current-module, public project, then standard or external catalog definitions. Explicit reference-qualified completion, such as `Excel.`, is already scoped to that reference and orders within the admitted surface."

Dev: "Should `Lib_Common` appear after a completed call like `ExampleFunc() |`?"
Domain Expert: "No. `QualifierCompletionCandidate`s follow the active `CompletionExpectation`; they appear where a qualified reference can start, not where the grammar has already closed the expression."

Dev: "Is the VS Code workspace folder always the `WorkbookBackedProject`?"
Domain Expert: "No. The `ProjectManifest` identifies the `WorkbookBackedProject`; a workspace can contain none, one, or several workbook-backed projects."

Dev: "What happens when I edit a loose `.bas` file outside any `vba-project.json`?"
Domain Expert: "It is an `AdHocVbaProject`: source definitions, `LanguageVocabulary`, and definitions from the always-active `VbaStandardLibraryReference` work, but no manifest-controlled external references are active. Create a `WorkbookBackedProject` when other reference-aware completions are needed."

Dev: "Should completion call `vba-dev` to resolve project references?"
Domain Expert: "No. `LanguageServerManifestResolution` reads the `ProjectManifest` directly for editor features. `VbaDev` owns project creation, reference changes, doctor/repair, build, test, publish, and export; background catalog refresh may use tooling, but synchronous editor requests do not invoke the CLI."

Dev: "Should Test Explorer show every `TestProcedure` before the first run?"
Domain Expert: "No. It should show runnable `WorkbookBackedProject` and `DocumentSourceSet` nodes first, then add procedure-level nodes after test output identifies them."

Dev: "Where should Go to Test on a discovered `TestProcedure` navigate?"
Domain Expert: "To its `TestProcedureSourceLocation`: the declaration name in the owning `DocumentSourceSet`'s exported VBA source. The same location is valid whether the test passed or failed."

Dev: "Should a `TestDiscoverySnapshot` keep its procedure nodes after their exported source changes?"
Domain Expert: "No. Invalidate the document's output-derived module and procedure nodes so stale locations and selectors are not presented. Keep the project and document scopes runnable so the next test run can create a new snapshot."

Dev: "Should editing source while a snapshot test runs cancel the run?"
Domain Expert: "No. Keep outcomes for the immutable source that ran, but do not commit its module/procedure discovery or locations when the captured document revision is stale. Report a non-failing Test Run warning and let a later run refresh discovery."

Dev: "Should a Test Explorer run use unsaved exported VBA source?"
Domain Expert: "Yes, for the normal build-before-test profile. Capture a caller-owned complete `BuildSourceSnapshot` without saving source and invoke `vba-dev test --source-snapshot`; `VbaDev` owns only its internal test workspace. A no-build run intentionally executes the existing bin workbook and cannot accept a snapshot."

Dev: "Should the no-build Test Explorer profile save dirty source before running the existing bin workbook?"
Domain Expert: "No. Run the existing bin unchanged and retain its outcomes and test identities. Omit navigation for source that was already dirty, report a non-failing warning, and never save, build, or rerun implicitly."

Dev: "Should `test --no-build` reject a module/reference name conflict found in the current source or manifest?"
Domain Expert: "No. It executes the existing bin workbook and does not claim that current source is buildable. Ordinary and snapshot tests inherit the build-stage preflight; no-build reports only failures that prevent the existing workbook from opening or executing, while Doctor owns current source health."

Dev: "Should failure to delete an internal snapshot-test workspace fail a passed unit test?"
Domain Expert: "No. Failure to release an owned Excel process is a command-level infrastructure error, but once process release is proved, a workspace that remains after bounded deletion retries is a housekeeping warning. Preserve every workbook-owned test outcome, report the retained absolute path, and do not rewrite any `testFinished` result."

Dev: "Should Go to Test on a module node navigate to its first procedure?"
Domain Expert: "No. A module node is a runnable grouping scope with multiple possible targets. Precise source navigation belongs to each discovered `TestProcedure` node."

Dev: "Should an unavailable `TestProcedureSourceLocation` make the test fail?"
Domain Expert: "No. Keep the node and outcome, and report a non-failing source-location warning in Test Run output. Do not turn navigation availability into a `TestItem` discovery error or popup notification."

Dev: "Is a workbook lock a failed `TestProcedure`?"
Domain Expert: "No. It is a `TestRunError` on the project or document scope because the test run could not reach individual test execution."

Dev: "Should a form module participate in rename and go to definition?"
Domain Expert: "Yes. A `.frm` file in the same folder is part of the same `VbaProject`."

Dev: "Is a `Public Enum` a definition?"
Domain Expert: "Yes. `Enum` and user-defined `Type` declarations are `VbaDefinition`s and should participate in completion, hover, rename, and go to definition."

Dev: "Is an `Event` only a declaration, or can it be referenced?"
Domain Expert: "An `Event` is a `VbaDefinition`. Event handler procedure names and `RaiseEvent` statements can both refer to it."

Dev: "Where do Office object model completions come from?"
Domain Expert: "They are `VbaProjectReferenceDefinition`s supplied by active `VbaProjectReference`s, even when the language server stores or discovers their metadata locally."

Dev: "Does enabling Access also enable DAO and ADO completions?"
Domain Expert: "No. The Access object library, DAO, and ADO are separate `VbaProjectReference`s, so each must be active before its definitions appear."

Dev: "If I install support for Word and PowerPoint, do their object models appear automatically?"
Domain Expert: "No. They appear only when the `VbaProjectReferenceSelection` includes those object libraries."

Dev: "Which Office object library should unqualified external references feel native to?"
Domain Expert: "Use the `MainVbaProjectReference` derived from the active document kind."

Dev: "If an Excel document's manifest omits the Excel object library, should Excel completions still appear?"
Domain Expert: "No. `MainVbaProjectReference` identifies the expected precedence winner, but absent references do not contribute definitions. Report `ManifestReferenceConsistency` through language-server output, status, or trace and through `EnvironmentDiagnostic`."

Dev: "If Excel and Word both define `Application`, which one does `Application` mean?"
Domain Expert: "Source `VbaDefinition`s still win first. Among referenced-library definitions, the `MainVbaProjectReference` definition wins; if only non-main references tie, `NameResolution` stays ambiguous."

Dev: "If a procedure declares `Dim ActiveCell As String`, does `ActiveCell` still mean Excel's active range?"
Domain Expert: "No. Procedure-local source definitions outrank current-module definitions, public project definitions, and every referenced-library definition. The local `ActiveCell` is a `RenameTarget`; Excel's catalog `ActiveCell` is used only when no higher-rank source definition wins."

Dev: "Should unqualified completion show both Excel and Word `Application`?"
Domain Expert: "No. Unqualified external completion follows `NameResolution`; use `Word.` for Word-specific qualified completion."

Dev: "Should syntax highlighting only color keywords and comments?"
Domain Expert: "No. `SyntaxHighlighting` includes lexical VBA coloring and `SemanticToken`s for parsed project meaning."

Dev: "Is an unresolved identifier a `SyntaxDiagnostic`?"
Domain Expert: "No. `SyntaxDiagnostic`s report malformed VBA grammar and source structure. Unknown names and ambiguous `NameResolution` are semantic concerns."

Dev: "Is `RaiseEvent Completed ""ok""` a `VbaValidationDiagnostic`?"
Domain Expert: "No. Parenthesis-free `RaiseEvent` arguments are malformed statement syntax, so that is a `SyntaxDiagnostic`. Duplicate parameter names and invalid named-argument ordering are `VbaValidationDiagnostic`s."

Dev: "Can `Event Changed(Optional value As Long)` or `RaiseEvent Changed(value:=1)` proceed to Event signature matching?"
Domain Expert: "No. A source Event cannot declare `Optional` or `ParamArray` parameters, and `RaiseEvent` cannot use named arguments. These are `SyntaxDiagnostic`s; the invalid Event declaration is not projected as a signature, and the invalid RaiseEvent argument is not reinterpreted as positional."

Dev: "Where should those Event syntax diagnostics point?"
Domain Expert: "Use one diagnostic for each forbidden Event modifier and each named RaiseEvent argument. Select only the `Optional` or `ParamArray` keyword for a declaration, and select the argument name through `:=` for a named RaiseEvent argument. Do not add generic call-shape diagnostics to the same invalid RaiseEvent."

Dev: "Are `RaiseEvent Ping()` and `RaiseEvent Changed(, 2)` valid?"
Domain Expert: "No. A zero-argument Event is raised as `RaiseEvent Ping`, without parentheses; select the complete `()` with `syntax.raiseEventEmptyArgumentListNotAllowed`. Any omitted argument slot makes the parenthesized list malformed; publish one `syntax.raiseEventOmittedArgumentNotAllowed` over the complete list, not one per slot. Do not pass either list to `CallArgumentMapping` or add the aggregate incompatible-call diagnostic. Independently malformed named arguments still receive their own syntax diagnostics."

Dev: "Can `RaiseEvent` run from a standard module or fire a TypeLib or built-in form Event?"
Domain Expert: "No. Admit `RaiseEvent` only inside a procedure in a class-module code section; otherwise publish `syntax.raiseEventStatementNotAllowedHere` on the keyword and do not resolve a target or map arguments. In valid placement, resolve only an Event declared in the enclosing class module. A same-named Sub, another class's Event, TypeLib Event, or intrinsic form or host Event is not a fallback and produces `validation.raiseEventTargetNotDeclaredInEnclosingModule` on the identifier. Keep an eligible local `RecoveredEventDeclaration` bound for navigation and repairing Rename, but treat its call compatibility as indeterminate."

Dev: "Must a `WithEvents` handler use the Event declaration's parameter names?"
Domain Expert: "No. Event and handler parameters correspond by ordinal position. `EventHandlerCompatibility` ignores their names but compares their count, canonical types, array shape, effective passing mechanism, and Optional or `ParamArray` shape."

Dev: "Can an Event parameter declared `As Long` match a handler parameter declared `As Integer` because VBA can convert the value?"
Domain Expert: "No. Event-to-handler comparison is declaration compatibility, not call-site conversion. Normalize spelling and resolve both types, then require the same canonical type identity. `Workbook` and `Excel.Workbook` can match when they resolve to the same TypeLib definition; `Object` and `Excel.Workbook`, a class and one of its interfaces, `Variant` and a concrete type, or distinct numeric types do not. If catalog, host, or resolution evidence cannot establish either identity, retain an indeterminate result rather than guessing."

Dev: "Is `EventHandlerCompatibility` only for conditional source Events?"
Domain Expert: "No. It consumes a `ResolvedEventSignatureSet`: one signature for a nonconditional source, resolved TypeLib Event, or projected host Event, and every physical signature for a conditional Event family. Unresolved, ambiguous, and non-Event targets are not compared, while missing signature metadata remains indeterminate. TypeLib and last-known-good host comparisons remain advisory for Hover and Signature Help; only a complete target set whose authorities are all `sourceDeclared` or `currentHostProjected` authorizes a handler error diagnostic."

Dev: "Does an invalid Event parameter make the Event name disappear from editor features?"
Domain Expert: "No. When its name and scope are recoverable, keep a `RecoveredEventDeclaration` for completion, Definition, References, and Rename, but do not project a `CallableSignature`, `ResolvedEventSignatureSet` entry, or Signature Help item. Its presence makes dependent call and handler compatibility indeterminate so the syntax error does not cascade."

Dev: "What happens to `Event Item_Changed(...)`?"
Domain Expert: "Publish one error-severity `syntax.eventNameCannotContainUnderscore` diagnostic over the complete Event identifier. Retain a `RecoveredEventDeclaration` for existing syntactically admitted `RaiseEvent` binding, Definition, References, and a repairing Rename, but exclude it from completion, callable signatures, Signature Help, and handler suffix resolution. Event Rename accepts an underscore-free repair and rejects every underscore-bearing requested name with `invalidName`."

Dev: "Where can a source Event be declared, and can it be `Private` or `Friend`?"
Domain Expert: "Only at module level in a class-module code section, with explicit `Public` or omitted visibility; omission also means Public. Use `syntax.eventDeclarationNotAllowedInModule` on the `Event` keyword for invalid placement and `syntax.eventVisibilityNotAllowed` on `Private` or `Friend`. Retain both independent diagnostics and a non-callable `RecoveredEventDeclaration`, but exclude invalid placement or visibility from completion and handler suffix resolution."

Dev: "Does one `WithEvents` keyword apply to every variable on a comma-separated declaration line?"
Domain Expert: "No. Preserve it per declarator. In `Private WithEvents publisher As Publisher, other As Publisher, WithEvents app As Excel.Application`, only `publisher` and `app` are `WithEventsVariableDeclaration`s; `other` is an ordinary module variable. Never infer a line-wide modifier from either occurrence."

Dev: "Where may a `WithEvents` variable be declared, and what survives invalid placement?"
Domain Expert: "Only at module level in a class-module code section, introduced by `Public`, `Private`, or `Dim`. Treat `.cls`, `.frm`, and document-module source exported as `.cls` as class modules. For every offending declarator in a standard module or procedure body, publish `syntax.withEventsDeclarationNotAllowedHere` over exactly its own `WithEvents` keyword. Retain a `RecoveredWithEventsVariableDeclaration` for ordinary variable Definition, References, Hover, and Rename, but let it establish no handler-prefix binding, `WithEventsEventBindingSet` entry, handler diagnostic, or dependent Rename of its own; it is neither `notWithEvents` nor `indeterminate` evidence."

Dev: "Can a `WithEvents` declarator be an array, use `As New`, use a type-declaration character, or omit its explicit type?"
Domain Expert: "No. Require `WithEvents IDENTIFIER As class-type-name`. Publish independent declarator-local `syntax.withEventsArrayNotAllowed`, `syntax.withEventsNewNotAllowed`, `syntax.withEventsTypeDeclarationCharacterNotAllowed`, and `syntax.withEventsTypeRequired` diagnostics over the complete array designator, exact `New`, exact suffix character, and either the identifier or type-less `As`, respectively. Retain every present violation. Recover the ordinary variable definition and surviving type metadata, but let that declarator establish no Event binding or dependent Rename of its own. A conditional-family sibling whose `WithEventsTypeEligibility` is `eligible` can still establish family-wide dependent edits. Do not inherit a type or `WithEvents` state from a comma-separated sibling."

Dev: "What declared types are valid for a syntactically admitted `WithEvents` variable?"
Domain Expert: "Classify each declarator independently with `WithEventsTypeEligibility` after ordinary Type Resolution. A valid type is a specific VBA-accessible class other than the enclosing class whose authoritative complete structural Event surface contains at least one valid Event. Conclusively invalid enclosing, non-class, inaccessible-class, and no-Event types receive exactly one of `validation.withEventsTypeCannotBeEnclosingClass`, `validation.withEventsTypeMustBeClass`, `validation.withEventsTypeMustBeAccessible`, or `validation.withEventsTypeMustExposeEvents` over the complete type reference, in that precedence. Preserve ordinary variable features but exclude that variant from Event binding and dependent Rename of its own. Unresolved, ambiguous, stale, missing, or incomplete Event evidence is `indeterminate`, receives no type diagnostic, and enters the binding set before suffix lookup. It suppresses aggregate handler diagnostics and Event-Rename convergence; an indeterminate-only handler candidate also makes upstream variable Rename fail with `analysisIncomplete`, while mixed resolved evidence keeps its existing safe projections. Creatability is neither required nor disqualifying, and `Implements` compatibility does not prove Event-source eligibility."

Dev: "Which TypeLib interfaces supply Events for an external `WithEvents` class?"
Domain Expert: "Require the declared external type to be a `TKIND_COCLASS` and derive its `TypeLibEventSurface` only from exactly one implemented interface carrying both `FDEFAULT` and `FSOURCE`. Ignore non-default `FSOURCE` interfaces without falling back even when only one exists; `FDEFAULTVTABLE` alone is not a substitute. Preserve every callable default-source member and its `FUNCFLAGS`, then derive separate structural, authoring, and existing-handler-recognition projections. A complete coclass with no default source or a structurally empty source produces `invalidNoEvents`. Multiple default sources or missing, unreadable, stale, or incomplete association metadata are `indeterminate`. A directly declared interface or dispinterface is `invalidNotClass`, and its methods are never reclassified as Events merely because another coclass uses that interface."

Dev: "Can a `.frm` file or a module named `ThisWorkbook` establish built-in host Events?"
Domain Expert: "No. An intrinsic form or document class uses `HostClassEventSurface`, which combines its valid source Event declarations with a complete, current `HostClassProjection` under `HostEventShadowing`. File extensions and module names never substitute for that projection. Missing, stale, or incomplete projection evidence is `indeterminate`, not `invalidNoEvents`, and an intrinsic module handler remains separate from `WithEvents` binding."

Dev: "What happens when an intrinsic form or document class declares an unguarded valid source Event named `Click` even though its host projection also contains `Click`?"
Domain Expert: "`HostEventShadowing` makes the source Event authoritative for `RaiseEvent`, external `WithEvents` authoring, suffix resolution, and signature guidance without requiring the two signatures to match or reporting a duplicate. The projected Event remains separate evidence for recognizing the intrinsic `UserForm_Click`-style handler. Renaming the source Event changes its declaration, `RaiseEvent` references, and external handlers, but never renames that intrinsic handler. A `RecoveredEventDeclaration` does not shadow the projected Event."

Dev: "What if that source `Click` Event is guarded by `#If`, including an apparently exhaustive `#If` / `#Else`?"
Domain Expert: "`RaiseEvent` still resolves only the source `ConditionalCallableFamily`, while an external `WithEvents` binding retains that family and the projected host Event as distinct configuration-dependent alternatives. Do not select an active branch, prove branch coverage, or merge the host Event into the source family. The intrinsic handler remains associated only with the host Event. If a source Event Rename would have to decide whether a shared external handler belongs to the source family or host Event, fail the complete Rename with `analysisIncomplete` rather than applying partial edits."

Dev: "Does external completion show separate source and host items for that conditional `Click`?"
Domain Expert: "No. Show one `Click` Event completion with `Event [#If]` detail and insert only `Click`. Signature Help retains every valid source-family and host signature as separate `[#If]` entries, without source/host provenance or condition text. `RaiseEvent` completion and Signature Help remain source-family-only. Existing external handlers may retain a host signature through `existingHandlerRecognizable` even when that Event is omitted from ordinary authoring by `authoringAvailable`."

Dev: "Who owns `HostClassProjection` refresh and storage?"
Domain Expert: "`HostClassList` performs one read-only, machine-readable inspection of the selected `ProjectDocument` source template and owns only that invocation and its Excel process. `VscodeExtension` owns the background `HostClassProjectionLifecycle` and supplies committed immutable snapshots to the language server. The manifest stores neither generated Event members nor projection state, and an `AdHocVbaProject` has no projection."

Dev: "Can a slow host-class refresh overwrite a newer project state?"
Domain Expert: "No. `VscodeExtension` binds each invocation to a `HostClassProjectionRefreshGeneration` and commits only while it remains current and the response's canonical absolute project root, manifest-resolved document name, and canonical absolute source-template path still match. A superseded result changes neither resolved entries nor deletion state. The generation remains consumer-local; `VbaDev` inspects the template copy selected at invocation start and serializes no VS Code request ID, source hash, mtime, or inspection timestamp."

Dev: "Should editing `Module1.bas` or refreshing a reference catalog start host-class inspection?"
Domain Expert: "No. A `HostClassProjectionRefreshTrigger` is initial project-document activation, an effective manifest document or source-template identity change, a create/change/delete event for the selected template file, or an explicit consumer refresh. Removing a document or changing its template identity cancels in-flight work and removes the old projection. A same-path template change advances the generation but preserves last-known-good on failure; temporary template absence is unavailable rather than authoritative deletion. Source edits, reference-only changes, editor changes, and bin or publish changes do not trigger inspection. Relevant source and manifest changes may still run `HostClassSourceAssociationReevaluation` against the current snapshot without starting Excel."

Dev: "Can several template saves start several Excel inspections at once?"
Domain Expert: "No. `HostClassProjectionRefreshScheduler` runs at most one `HostClassList` invocation extension-wide. Automatic file and manifest triggers use a one-second trailing-edge debounce; activation and explicit refresh do not. Each document retains only its newest pending generation. A trigger superseding that document's running inspection requests cooperative cancellation, discards any stale terminal result, and waits for CLI and Excel cleanup before replacement. Other documents retain FIFO order, queue waiting has no timeout, and extension shutdown cancels running work and drops the queue."

Dev: "Does a failed or unverified host-class refresh retry itself after a delay?"
Domain Expert: "No. `HostClassProjectionRefreshRecovery` waits for a later lifecycle trigger or explicit consumer refresh to start a new generation. Valid partial results retain their class-level meaning, invocation failures preserve applicable last-known-good state, and cancellation or supersession does not create another retry. Explicit refresh bypasses debounce but still uses the single-flight scheduler. Schema `1.0` adds no retryability or backoff fields."

Dev: "Where does a user see and retry a failed background host-class refresh?"
Domain Expert: "Use `VBA Tools: Refresh Host Events` for one chosen project document. It shows cancellable progress, bypasses debounce, and joins the same scheduler. Healthy background state stays quiet; queued or running work and attention-required `HostClassProjectionStatus` appear in a transient status-bar item whose click opens VBA Tools Output. Background failure never pops up, while an explicitly requested inspection failure shows one error with `Show Output`. If explicit inspection succeeds but source-association failures remain, keep the command successful and show one warning with `Show Output`. Output records generation, context, trigger, lifecycle transitions, counts, deletion, reason, and last-known-good use. Do not publish source diagnostics, add a dedicated Project Health view, or mix this state into `vba-dev doctor`."

Dev: "Does cancelling `VBA Tools: Refresh Host Events` discard inspection work that already completed?"
Domain Expert: "Not when the cancelled invocation still owns the current generation and returns a schema-valid terminal result after process release. Remove a queued request or cooperatively cancel only that document's running request; then apply resolved entries, preserve last-known-good for cancelled or unverified entries, and honor authoritative absence only when `classEnumerationComplete` is true. Invalid output preserves all prior state. A superseded generation is discarded whole, and user cancellation produces neither an error popup nor a retry."

Dev: "Does the language server consume `HostClassProjectionResult` directly or receive class deltas?"
Domain Expert: "Neither. `VscodeExtension` folds the result into one versioned `HostClassProjectionSnapshot` and sends the complete effective document state through `vba/hostClassProjectionSnapshot`. Entries are `current`, `lastKnownGood`, or `indeterminate`; only the first two carry a complete projection. A `present` snapshot replaces the document atomically, while `cleared` removes it. `HostClassProjectionSnapshotRevision` rejects stale notifications, matching project context is required, and the latest desired snapshot is replayed after language-client restart. Manifest synchronization is enqueued first. The notification is a coalescible workspace mutation and never blocks an editor request."

Dev: "Can a `lastKnownGood` host Event still prove a validation error or authorize Rename?"
Domain Expert: "No. It may continue to supply completion, hover, Signature Help, existing-handler association, and navigation, but `HostClassProjectionAuthority` remains indeterminate for semantic conclusions. Do not establish `invalidNoEvents`, handler incompatibility, result type, or type compatibility from stale evidence, and reject a mutation that needs current host evidence with `analysisIncomplete`. `current` alone is authoritative; an `indeterminate` entry supplies no projected candidates. The status bar reports stale use rather than annotating every editor item."

Dev: "What is the public CLI for obtaining a host-class projection?"
Domain Expert: "Use `vba-dev host-class list --project <path> --document <name> --format json`. It follows normal project discovery, defaults to the primary document when `--document` is omitted, defaults to human-readable text, and advertises JSON schema `1.0` as `featureVersions[\"hostClass.list\"]`. It starts an owned hidden Excel process but does not write the workbook, source, manifest, or a projection cache."

Dev: "Does `host-class list` open and lock the source template itself?"
Domain Expert: "No. It copies the selected source template into a unique `HostClassInspectionWorkspace`, opens only that copy with macros and Excel Events disabled, imports no source, changes no references, and never saves. It releases the owned Excel process before removing the workspace. If the copy cannot be prepared, the command fails without guessing a projection."

Dev: "Does failure to remove a `HostClassInspectionWorkspace` invalidate its projection?"
Domain Expert: "Not after owned-process release is proved. Hold machine-readable projection output until process release succeeds; failure to prove release is a command-level failure with no JSON projection. Then apply bounded deletion retries. If only deletion still fails, report the retained absolute path as a housekeeping warning while preserving the projection and successful exit status. Failure and cancellation use the same cleanup order."

Dev: "Does one unreadable host class invalidate every class in `host-class list`?"
Domain Expert: "No. Return a schema-valid `HostClassProjectionResult` after process release, even on nonzero exit. Mark each enumerated class `resolved` or `unverified`; `complete` is true only when enumeration and every class projection complete. Commit resolved classes independently. An unverified class preserves its `LastKnownGoodHostClassProjection`, or becomes `indeterminate` when none exists. If enumeration is incomplete, omitted classes are unknown, never authoritative empty surfaces. Malformed JSON, request-context mismatch, or process-release failure invalidates the whole result."

Dev: "Can source file-name fallback bind a form or document module to a `HostClassProjection`?"
Domain Expert: "No. Match `HostClassIdentity` only within the selected `ProjectDocument`, using case-insensitive `VBComponent.Name`, an explicit matching `Attribute VB_Name`, and a compatible `form` or `document` kind while retaining projection casing. A missing attribute, kind mismatch, or template/source name mismatch creates a `HostClassSourceAssociationFailure`, leaves only that source's host Event evidence `indeterminate`, and makes the document's `HostClassProjectionStatus` attention-required without invalidating other associations. Ordinary non-host source may still use `FallbackModuleIdentity` for analysis recovery only."

Dev: "Does a successful host-class inspection hide status even when one source cannot be associated with its projected host class?"
Domain Expert: "No. Current projection data and source association are separate facts. Preserve the current projection and every valid association, but keep the document's `HostClassProjectionStatus` attention-required until the `HostClassSourceAssociationFailure` is repaired; do not publish a source diagnostic, retry Excel automatically, or add the failure to either Doctor command."

Dev: "How does `HostClassSourceAssociationFailure` appear, and when does it clear?"
Domain Expert: "Keep the status-bar item compact: show the VBA Host Events warning and total failure count without paths. Its Hover shows project, document, total count, and counts by reason. Clicking opens VBA Tools Output, which lists every failure without truncation, including source URI, source kind, the present or missing `Attribute VB_Name`, any corresponding projection identity, the exact mismatch, and guidance to re-export or repair metadata. A relevant source or manifest change re-evaluates associations against a context-compatible current snapshot; when all succeed, clear the warning without another Excel inspection."

Dev: "What does an explicit Host Events refresh report when Excel inspection succeeds but source association still fails?"
Domain Expert: "Complete the command and inspection successfully, retain the attention-required status, and show one warning: `Host Events refreshed, but <N> source module(s) could not be associated.` Its only action is `Show Output`. A background refresh or source-only reassociation remains popup-free, a cancellation shows nothing, and an actual inspection failure keeps the existing error notification instead of also showing this warning."

Dev: "Can the intrinsic handler prefix be derived from `Sheet1`, `ThisWorkbook`, `UserForm1`, or the component kind?"
Domain Expert: "No. A resolved projection must supply `IntrinsicEventSourceName` from the VBE-equivalent object/Event association: for example `Worksheet` forms `Worksheet_Change`, `Workbook` forms `Workbook_Open`, and `UserForm` forms `UserForm_Initialize`. Match it case-insensitively while retaining projection casing. Do not infer it from `HostClassIdentity`, `form` or `document`, source file names, or optional base-type provenance; if inspection cannot establish it, the class is unverified and retains applicable last-known-good evidence."

Dev: "Is `Worksheet_Change` handled as a `WithEventsHandlerCandidate`?"
Domain Expert: "No. In its associated document module, an exact case-insensitive match of `IntrinsicEventSourceName`, `_`, and an `existingHandlerRecognizable` host Event forms an `IntrinsicHostHandlerCandidate`. A Sub becomes an `IntrinsicHostHandlerDeclaration`; a Function or Property accessor is a `nonSubProcedureAssociation`. It has no `WithEvents` variable-prefix reference or binding set, and a same-name source Event does not replace its projected host target. Both handler kinds share `EventHandlerCompatibility`; current host evidence permits procedure-kind and signature diagnostics, while last-known-good evidence supplies guidance only."

Dev: "What if several `Worksheet_Change` declarations appear under `#If`?"
Domain Expert: "Use the existing `ConditionalDeclarationFamily`, not an intrinsic-handler-specific family. Recognize and validate every physical candidate independently against the same projected host Event without evaluating conditions or selecting an active branch. A compatible sibling does not suppress another variant's `validation.eventHandlerMustBeSub` or `validation.incompatibleEventHandlerSignature`; last-known-good authority suppresses diagnostics for every variant. Definition and References retain the complete family, but it has no `WithEvents` or source-Event upstream Rename."

Dev: "What do editor features target inside the `Worksheet_Change` declaration name?"
Domain Expert: "The complete identifier remains the procedure definition. `Worksheet` and `_` have no independent semantic target; `Change` is an `EventReference` to the projected `HostEventIdentity`. Hover on that suffix shows the host Event signature and documentation. Definition uses navigable base-type provenance when available and otherwise returns no location; it never redirects to the handler itself. Host Event References include intrinsic and external handler suffixes bound to that same projected identity, excluding external handlers shadowed to a source Event. Ordinary complete-name calls still reference the procedure family. Signature Help inside the declaration parameters shows one complete `Worksheet_Change(...)` handler spelling, without `[#If]` merely because the declaration is guarded. Last-known-good evidence preserves the same guidance without an item-level stale marker."

Dev: "Can F2 rename a recognized `Worksheet_Change` procedure or just its `Change` suffix?"
Domain Expert: "No. With current projection evidence, the intrinsic handler name is a fixed host contract: Prepare Rename returns no target from the prefix, underscore, suffix, complete declaration, conditional sibling, non-Sub association, or ordinary complete-name reference. A direct non-no-op Rename fails with `notRenameTarget`, including a case-only change; an ordinally unchanged request remains a successful no-op. With last-known-good evidence only, direct mutation fails with `analysisIncomplete`. With neither current nor last-known-good association, the procedure is ordinary and normal Rename rules apply. Intentional detachment is a manual edit or a future Code Action, not meaning-preserving Rename."

Dev: "Should the language server derive host Events from the projected class's base TypeLib type?"
Domain Expert: "No. A resolved `HostClassProjection` carries the complete Event signatures observed for that class and remains the authoritative immutable snapshot. `HostClassBaseTypeProvenance` may identify the built-in base type for navigation or provenance, but a missing or unresolved catalog entry neither removes those signatures nor invalidates the projection."

Dev: "Can `host-class list --format json` return only a VBE-style Event signature label?"
Domain Expert: "No. Each `HostEventSignature` is structured as an Event name plus ordered parameter metadata for type, passing mechanism, array shape, and any available `Optional` or `ParamArray` flags, with parameter names and optional documentation retained for presentation. Consumers derive their own labels, and Event-handler compatibility never treats a parameter name or rendered label as identity."

Dev: "Can one host class expose two overload-like projected Events named `Change`?"
Domain Expert: "No. Within one `HostClassIdentity`, case-insensitive Event name is `HostEventIdentity`. Coalesce duplicate observations only when parameter count, canonical types, passing mechanisms, array and Optional or `ParamArray` shape, and both availability values agree. Normalize casing, parameter names, and documentation as presentation metadata. Any callable-contract or availability conflict makes the complete class `unverified` with `eventEnumerationFailure`; never publish multiple same-name signatures as an overload or `ConditionalCallableFamily`."

Dev: "If duplicate `Change` observations differ only in presentation metadata, can coalescing construct a signature by mixing their fields?"
Domain Expert: "No. Preserve one complete observed presentation. Prefer an observation with documentation, then choose the ordinal minimum of the Event name, ordered parameter names, and documentation using `OrdinalIgnoreCase` followed by `Ordinal`. Callable contract and availability already agree, so this deterministic choice changes no semantic result."

Dev: "Does `HostClassProjectionResult` preserve COM or VBIDE enumeration order?"
Domain Expert: "No. Order class entries by component kind and then `VBComponent.Name` using `OrdinalIgnoreCase` followed by `Ordinal`, without grouping by resolved status. Order each class's Events by Event name with the same comparison, retain parameter ordinal order, and use the same class and Event order in text and JSON. Enumeration order is not projection data."

Dev: "Can two enumerated document classes named `Sheet1` and `sheet1` be coalesced when their projections match?"
Domain Expert: "No. They have the same `HostClassIdentity`, which must occur at most once. Omit that ambiguous identity, add top-level `classEnumerationFailure`, set `complete: false`, and exit nonzero while continuing to inspect other unique identities. Do not add `inspectionStateUntrusted` unless separate evidence makes the shared state untrustworthy. A same-name `form` and `document` remain distinct because kind participates in identity."

Dev: "If `Sheet1` disappears while inspection of the remaining `Sheet2` fails, should its last-known-good projection remain?"
Domain Expert: "No, when `classEnumerationComplete` is true. That required result field makes absence authoritative independently of class-local inspection success, so remove an absent identity while preserving the last-known-good projection for a listed `unverified` class. When enumeration is incomplete, absence is unknown and removes nothing. `complete` is true only when class enumeration and every listed projection are complete; no deletion tombstone or diagnostic-code inference is used."

Dev: "Can a projected `Excel.Range` parameter use the text `Excel` or the local TypeLib path as its type identity?"
Domain Expert: "No. A TypeLib `HostEventTypeReference` uses the case-insensitive type name with library GUID, major/minor version, and LCID while retaining display casing separately. Intrinsic types use canonical VBA names. Human-visible reference names, VBA qualifiers, and registry paths are not identity, and an unresolved display name never proves type equality."

Dev: "Does one unresolved projected parameter type make its host class `unverified`?"
Domain Expert: "Not when inspection successfully observes the Event and its complete parameter structure but classifies that type as opaque. Keep the class `resolved`, use its Event names and other structural metadata, and make only compatibility involving that type `indeterminate`. A read failure, incomplete enumeration, or untrusted inspection instead makes the class `unverified` and preserves its `LastKnownGoodHostClassProjection`."

Dev: "Does every projected host Event belong to completion and existing-handler recognition?"
Domain Expert: "No. Its presence in `HostClassProjection` establishes structural existence. `HostEventAvailability.authoringAvailable` separately controls ordinary completion and retains eligibility evidence for future `MemberStubGeneration`, while `existingHandlerRecognizable` controls association, navigation, and signature guidance for an already-written handler. For example, a non-authorable Event may remain recognizable. Consumers use these projected behaviors rather than raw TypeLib flags."

Dev: "Does host Event authoring add an `Add Event Handler` command in this work?"
Domain Expert: "No. `MemberStubGeneration` is deferred as one broader feature for `WithEvents`, intrinsic host Events, and `Implements` members. This work supplies completion, Signature Help, and existing-handler recognition only. `BlockSkeletonInsertion` may close a complete header the user has already written, but it never invents a member declaration or its signature."

Dev: "What does completion insert after `Private Sub Worksheet_Ch`?"
Domain Expert: "In an associated document module, the exact `Worksheet_` prefix admits `ContractMemberNameCompletion` for `authoringAvailable` Events. Show the canonical full label `Worksheet_Change`, but replace only `Ch` with the projected suffix `Change`; do not insert parameters, a body, or `End Sub`. Offer it only in a `Sub` declaration name, not a Function, Property, ordinary expression, or call. Current and last-known-good projections provide the same advisory candidate without an item-level stale marker. An Event that is only `existingHandlerRecognizable` remains usable for an already-written handler but is absent from this authoring list."

Dev: "Should `Worksheet_Change` disappear from completion when another declaration has that name?"
Domain Expert: "Only when the source model proves a same-scope collision after excluding the declaration currently being edited. Suppress the candidate when either the existing declaration or the new declaration is unconditional. Keep it when every peer is guarded by `#If`, because they remain one `ConditionalDeclarationFamily` without branch evaluation; also keep it for another module or indeterminate collision evidence and let diagnostics report any later-proven problem."

Dev: "How should `Worksheet_Change` appear in the completion list?"
Domain Expert: "Use the complete canonical handler name as the label and `Event` as its kind detail, but use the projected suffix `Change` for filtering and name ordering so the typed `Ch` matches naturally. Put the projected Event signature and documentation in the detail pane rather than expanding the list row. Do not add `[#If]` merely because the new procedure is guarded. Selection inserts no parentheses or parameters; typing `(` then opens the already-defined complete-handler Signature Help."

Dev: "Does registering `_` make completion pop up for every VBA identifier containing an underscore?"
Domain Expert: "No. The LSP trigger character is registered globally and therefore sends a request for every typed underscore, but `_`-triggered completion returns candidates only in a callable declaration-name slot whose complete prefix resolves to an associated `IntrinsicEventSourceName`, a same-class `WithEvents` variable, or an interface named by this class's `Implements` statements. Other underscore-triggered requests return no candidates, while an explicit `Ctrl+Space` keeps ordinary completion behavior. The client does not duplicate those semantic relationships."

Dev: "What does the space after `Function`, `Sub`, or a Property accessor do?"
Domain Expert: "It admits `ContractPrefixCompletion` in an empty callable declaration-name slot. `Function` offers interface prefixes with at least one remaining Function; `Sub` offers intrinsic host, interface, and same-class `WithEvents` prefixes with at least one remaining Sub or authorable Event; and each complete Property Get, Let, or Set header offers interface prefixes with a remaining matching accessor. Selection inserts only `Worksheet_`, `IFoo_`, or `publisher_` and enters the same `ContractMemberNameCompletion` context as manual prefix entry; it never lists those contracts' complete member names at the first stage. `Property |` still completes Get, Let, or Set before any contract prefix is eligible."

Dev: "What if the user dismisses the first list and types `Sub publ|` or `Function IF|`?"
Domain Expert: "Keep the same first-stage completion active. Offer only case-insensitively prefix-matching `publisher_` or `IFoo_` candidates whose downstream members still survive, and replace only the partial declaration-name fragment. Do not expose member suffixes until the text exactly equals a viable semantic prefix including its underscore. At `publisher_|`, enter the second-stage member list and omit longer prefix candidates only when `publisher_` still has a downstream member. If it has none but `publisher_long_` remains viable, keep `publisher_long_` in the first-stage list rather than opening an empty member list."

Dev: "Should `Function |` offer `IFoo_` when its only downstream Function is `IFoo_Calculate` and a same-scope `Sub IFoo_Calculate` already exists?"
Domain Expert: "No. The existing unconditional declaration conclusively conflicts with that downstream Function, so `ContractMemberNameCompletion` omits `Calculate`; with no remaining member candidate, `ContractPrefixCompletion` omits `IFoo_` too. A compatible existing Function has the same filtering effect because it already satisfies the contract. If another admissible Function remains under `IFoo_`, the prefix stays and only the occupied suffix is absent at the second stage. The same rule applies to a `WithEvents` prefix whose handler names are all occupied."

Dev: "What if `IFoo_Calculate` already exists only inside `#If`?"
Domain Expert: "Keep the advisory name candidate only when the prospective declaration and every same-scope peer are conditionally guarded. If the new declaration or any peer is unconditional, suppress the candidate because some configuration can contain both. Do not compare condition text, branch ancestry, or nesting—even declarations in an apparently identical branch remain advisory candidates until diagnostics prove a duplicate. Apply this rule equally to interface implementations, `WithEvents` handlers, and intrinsic host handlers."

Dev: "Does the member candidate after `Function IFoo_|` receive a `[#If]` completion marker merely because that position is inside `#If`?"
Domain Expert: "No. The marker describes the candidate's `ConditionalContractProvenance`, not its insertion site. Add it when an applicable `Implements` statement, same-class `WithEvents` declaration, interface member or Public variable owning a derived accessor, source Event declaration, or retained configuration-dependent host-shadow alternative is conditional. Do not add it when only the completion position is guarded. Keep the generic marker even when equivalent declarations appear in every branch, because the language server neither evaluates the conditions nor proves exhaustive coverage, and never display the condition expression itself."

Dev: "Can Completion mark an Event or interface contract `[#If]` while Signature Help, Hover, or its diagnostic detail leaves the same contract unmarked?"
Domain Expert: "No. Those contract-facing projections share one `ConditionalContractProvenance` value. If either the relationship, the source contract declaration, the Public variable behind a derived accessor, or a retained host-shadow alternative makes the contract conditional, every projection marks it; a guarded handler, implementation, or completion location by itself marks none of them."

Dev: "Does `Function |` mark `IFoo_` with `[#If]` when only `IFoo.Calculate` is guarded?"
Domain Expert: "No. The first stage deliberately inspects downstream members to prove that at least one admissible member remains, but that is an existence filter rather than provenance aggregation. Mark `IFoo_` only when the applicable `Implements` relationship is guarded, and mark `publisher_` only when its `WithEvents` declaration is guarded; intrinsic host prefixes remain unmarked. Conditional provenance belonging only to the selected Event or interface member first appears on its second-stage `ContractMemberNameCompletion`."

Dev: "Does accepting `publisher_` immediately show its Event names?"
Domain Expert: "Yes in the first-party editor: prefix acceptance continues directly into the second-stage member list after the edit is applied. An editor that does not support that continuation still receives the same inserted prefix and can request ordinary completion explicitly; the language model and member candidates do not depend on editor-specific selection state."

Dev: "What if several contracts contribute the same `IFoo_` prefix?"
Domain Expert: "Show one case-insensitively coalesced prefix candidate. After it is accepted—or after the user types the same prefix manually—resolve every currently matching contract origin from the edited source and combine their second-stage member candidates. Never preserve the origin of the selected completion item as hidden binding state."

Dev: "What if those contracts also contribute the same complete member name with different signatures?"
Domain Expert: "Show one second-stage name candidate for the case-insensitively equal complete name and required declaration kind. Accepting it selects neither contract nor signature. Retain every contributing variant so typing `(` exposes all signatures through Signature Help and later Definition or validation can consider the complete target set."

Dev: "How does that coalesced member appear in the completion list?"
Domain Expert: "Use `Event`, `Interface Member`, or `Multiple Contracts` according to the contributing domains, and append `[#If]` when any contributor has conditional contract provenance. Unlike the prefix marker, this member marker describes the complete retained variant set rather than whether the name is available in every configuration. Apply the ordinal-minimum contributor spelling only to presentation and suffix insertion; never recase a prefix already present in source."

Dev: "Does the completion detail pane choose one representative signature?"
Domain Expert: "No. List every distinct signature presentation in the same stable order as the later Signature Help, treating otherwise-identical labels with different `[#If]` states as distinct while collapsing repeated identical presentations. Completion has no active signature or parameter, and the pane never exposes contract-origin names or conditional expressions."

Dev: "What if one displayed signature has different documentation variants?"
Domain Expert: "Ignore empty values and collapse identical nonempty documentation. Show one distinct value directly, or show every distinct value in stable contributor order under numbered `Documentation variants`. Do not select, concatenate, summarize, or label them by contract origin or conditional expression, and do not hide later variants behind a count limit."

Dev: "Which casing and detail does a coalesced prefix show?"
Domain Expert: "Retain one spelling that an actual contributor supplied. Group spellings with `OrdinalIgnoreCase`, then choose the ordinal-minimum exact spelling so source and enumeration order cannot change the label or inserted text. Show `Host Events`, `WithEvents`, or `Interface` when all contributors have one domain, and `Multiple Contracts` when domains differ. Do not expose signatures or individual members before the second stage."

Dev: "When does a coalesced prefix show `[#If]`?"
Domain Expert: "Consider only prefix origins that supply at least one surviving downstream member. Mark the prefix exactly when every such origin is guarded. One contributing unconditional `WithEvents`, `Implements`, or intrinsic host origin makes it unmarked, while an unconditional origin whose members are all occupied does not participate. Member-only conditionality remains deferred to the second stage."

Dev: "How much contract evidence is required before name completion offers a member?"
Domain Expert: "Require its complete contract name, compatible declaration kind, and authoring admission from domain-specific current or committed last-known-good evidence. Missing signature or documentation can leave the detail and later Signature Help incomplete without hiding the name. If identity, kind, or authoring admission is unknown, do not invent a candidate. Once the minimum facts are known, uncertain collision evidence keeps the advisory item, while a conclusively occupied unconditional name suppresses it even when incomplete signature evidence cannot yet classify the declaration as satisfied or conflicting."

Dev: "Do Event handlers and interface implementations use different name-completion admission or collision rules?"
Domain Expert: "No. Both use `ContractMemberNameCompletion`: establish the complete contract identity, compatible kind, and authoring admission from domain-specific evidence; exclude the declaration being edited; apply the declaration-kind, namespace, and Property-accessor collision matrix; admit the candidate when no same-scope otherwise-colliding peer exists; suppress it when a peer exists and either the prospective declaration or any peer is unconditional; and retain it as advisory only when the prospective declaration and every peer are guarded. Event `authoringAvailable` and interface validity, accessibility, and accessor metadata are evidence for the shared authoring-admission predicate, not different completion policies."

Dev: "Which declarations receive `validation.duplicateDeclaration` when several same-name declarations are involved?"
Domain Expert: "Give every physical declaration with at least one directly proven collision peer one error on its identifier. Use `Declaration '<name>' conflicts with another declaration in this scope.` and related information for only those direct peers, ordered stably. Do not call the earliest declaration correct, and do not imply that two guarded declarations collide merely because each separately collides with one unconditional declaration."

Dev: "Can an external TypeLib Property's value type decide whether `Property Let |` or `Property Set |` offers it?"
Domain Expert: "No. Preserve each physical invoke accessor and map property-get to Get, value-put to Let, and reference-put to Set. A Property supporting both put forms appears in both corresponding declaration contexts with its accessor-specific signature. Ordinary member access may still coalesce the accessors into one logical readable or writable Property. Missing invoke-kind metadata fails closed for implementation completion and validation; do not infer it from the value type, and refresh or regenerate legacy catalogs."

Dev: "Does `Implements ISettings` turn `Public Value As Variant` in `ISettings` into a physical Property?"
Domain Expert: "No. Keep the Public variable as the sole source definition and derive one `InterfaceVariableAccessorContract` for each required implementation accessor. Variant contributes Get, Let, and Set; Object or a named class contributes Get and Set; every other valid type contributes Get and Let. Completion and fulfillment evaluate those contracts separately, while Definition returns to the variable declaration. A guarded variable gives every derived contract `[#If]`; an invalid Public array or fixed-length String contributes no contract."

Dev: "Can a plain unresolved named type be assumed to require either `Property Let` or `Property Set`?"
Domain Expert: "No. It may later resolve to a value type requiring Let or to a named class requiring Set; if it never resolves, the source is invalid. Get is invariant across the valid outcomes, so retain only its contract while the type identity is unresolved and add neither speculative setter contract."

Dev: "Does `As New UnknownType` provide enough evidence to offer `Property Set` before `UnknownType` resolves?"
Domain Expert: "No. Although `As New` constrains valid VBA to a named class, keep the user-visible unknown-type rule uniform: offer only Get until the named identity resolves, then add Set. The syntax constraint does not by itself create a setter contract."

Dev: "Does a Public interface variable without an `As` clause always contribute Variant accessors?"
Domain Expert: "No. Determine the effective declared type for each declarator from its type-declaration character, then the applicable `DefType` in the interface module, and use Variant only when neither applies. An explicit `As` type belongs only to its own declarator, so in `Public first, second As String`, `first` still follows implicit type determination. Never use the implementing class's `DefType` to derive the interface contract."

Dev: "What value parameter does a derived Let or Set contract expose?"
Domain Expert: "Use one final required `PropertyValueParameterContract` with presentation name `AssignedValue` and the Public variable's exact canonical effective type. The name is for Signature Help and future stub generation, not fulfillment: `AssignedValue`, `rhs`, and `newValue` are equivalent. Normalize only this final value slot to effective ByVal, so written `ByVal`, `ByRef`, and an omitted mechanism are equivalent; indexed Property parameters retain their own mechanisms. Optional, ParamArray, array, coercion-compatible, or unresolved-type substitutes do not prove fulfillment."

Dev: "Do conditional Public-variable variants become a `ConditionalCallableFamily` of synthetic Properties?"
Domain Expert: "No. Derive contracts from each physical variable variant, then group them by implemented name and accessor kind in an `InterfaceVariableAccessorContractSet`. Completion shows one `[#If]` item per accessor kind, Signature Help retains only that kind's contributing contract variants, and Definition returns the complete owning variable family. The set never enters Name or Call Resolution; an implementation Property enters the ordinary physical Property and conditional-family model only after it exists in source."

Dev: "How does a conditional implementation Property fulfill a conditional accessor contract set?"
Domain Expert: "Use `InterfaceContractFulfillment` to compare every implementation variant with every same-kind contract variant. A contract variant is covered by any compatible implementation variant, and an implementation variant is compatible with any compatible contract variant; conclusive mismatches and indeterminate evidence remain per pair. Never compare or pair their `#If` conditions, so even swapped branch bodies can appear covered when the complete signature sets match. Conditional alignment remains the author's responsibility."

Dev: "Does one generic diagnostic explain every failed interface implementation?"
Domain Expert: "No. After applying the implemented-name cascade rule, classify each required callable or accessor kind independently. Report `validation.interfaceMemberNotImplemented` when an allowed-kind candidate is absent, `validation.interfaceMemberKindMismatch` for a same-named declaration under a disallowed procedure or accessor kind, including an extra accessor, `validation.incompatibleInterfaceMemberSignature` when one same-kind physical implementation conclusively matches no contract signature, and `validation.interfaceMemberContractNotFullyImplemented` when at least one contract variant is covered but another is conclusively uncovered. Suppress a conclusive diagnostic when the relevant comparison is indeterminate."

Dev: "If conditional contracts require `Parse(LongPtr)` and `Parse(Long)`, but the implementation supplies only the compatible `Parse(LongPtr)`, is that merely a signature mismatch?"
Domain Expert: "No. The implementation is compatible with one contract, so it is not an incompatible implementation, and a same-kind candidate exists, so the member is not wholly absent. Report the dedicated `PartiallyImplementedInterfaceMemberContractDiagnostic` for the uncovered `Long` contract. Keep `interfaceMemberNotImplemented` for no same-kind candidate and `incompatibleInterfaceMemberSignature` for an implementation compatible with no contract."

Dev: "Where does that partial-coverage diagnostic point, and how does it identify the gap?"
Domain Expert: "Emit one diagnostic per `Implements` relationship, implemented name, and required kind at the complete interface type reference in the applicable `Implements` directive. Use `Interface member '<implemented-name>' does not implement every required <kind> contract.` Add one `Required contract: <kind-specific-signature> [#If].` related-information item for each conclusively uncovered physical source contract, omitting the marker only when its `ConditionalContractProvenance` is unconditional. Do not choose a closest implementation or add `Mismatches:` because no single implementation variant is the semantic counterpart."

Dev: "What if a conclusively uncovered host or catalog contract has no definition location?"
Domain Expert: "Append `Required contract: <kind-specific-signature> [#If].` as one LF-separated line after the partial-coverage diagnostic's primary message, omitting the marker only when its `ConditionalContractProvenance` is unconditional. When the client supports related information, keep navigable contracts only there. Coalesce an exactly repeated unlocated presentation, and retain every distinct presentation in stable contract order without truncation. Do not use the incompatible-signature diagnostic's two-line `Expected signature` and `Mismatches` form because this diagnostic selects no found signature for comparison."

Dev: "If partial contract coverage and a physical implementation that matches no contract occur together, does one diagnostic hide the other?"
Domain Expert: "No. Keep the aggregate partial-coverage diagnostic at the `Implements` relationship for each conclusively uncovered contract, and independently report `validation.incompatibleInterfaceMemberSignature` at every physical implementation compatible with no contract. They describe different sides and repair locations of the fulfillment relation. If no contract variant is covered at all and every relevant comparison is conclusively incompatible, emit only the physical signature diagnostics because the state is total incompatibility rather than partial coverage."

Dev: "How many diagnostics represent missing interface implementations?"
Domain Expert: "Emit one `validation.interfaceMemberNotImplemented` per missing contract set keyed by the `Implements` relationship, implemented member name, and required callable or accessor kind, not per physical conditional contract variant. Select the applicable relationship's complete interface type reference in the implementing class's `Implements` directive and use a self-contained message such as `Interface member 'IFoo_Value' requires a Property Let implementation.` Related information points to each contributing source contract variant, using the Public variable name for a derived accessor, and shows its kind-specific signature with only the generic `[#If]` marker according to `ConditionalContractProvenance`."

Dev: "Can one wrong-kind interface member cascade into a missing diagnostic for every required kind?"
Domain Expert: "Not while every same-named implementation declaration has a disallowed kind. Report the kind mismatch and use its related information to show all expected kinds, suppressing missing diagnostics for that implemented name until the declaration is removed or repaired. Once at least one allowed-kind candidate exists, diagnose any still-missing sibling contract kinds normally and continue to report wrong-kind extras."

Dev: "Does a conditional family of wrong-kind interface members receive one aggregate mismatch?"
Domain Expert: "No. Emit `validation.interfaceMemberKindMismatch` independently for every conclusive physical wrong-kind declaration, including each conditional variant, because every declaration remains a separate repair location. A sibling's result does not hide it, while the implemented-name cascade rule continues to suppress missing-contract diagnostics when no allowed-kind candidate exists."

Dev: "Where does an interface member kind mismatch point, and what does it say?"
Domain Expert: "Select only the repairable declared kind: the exact `Sub` or `Function` keyword, or the complete `Property Get`, `Property Let`, or `Property Set` keyword span. Use the self-contained message `Interface member '<implemented-name>' requires <expected-kind-list>, not <actual-kind>.` Build the expected list from the union of represented contract kinds in the fixed order Sub, Function, Property Get, Property Let, Property Set. Do not include `[#If]`, condition text, modifiers, the member name, or parameters in the primary presentation."

Dev: "How does a kind mismatch expose the expected interface contracts?"
Domain Expert: "Add one related-information item for every contributing physical expected-contract variant, ordered first by canonical kind and then by deterministic source order. Point to the source interface member name, or the Public variable name for a derived accessor, and use `Required contract: <kind-specific-signature>.` A source variable may therefore contribute separate Get, Let, and Set items at the same location. Retain variants separately, append only `[#If]` when their `ConditionalContractProvenance` is conditional, and never expose condition expressions or branch paths."

Dev: "How do missing and kind-mismatch diagnostics expose an authoritative contract with no definition location?"
Domain Expert: "Append one `Required contract: <kind-specific-signature> [#If].` line per unlocated required contract after the primary message, omitting the marker when its `ConditionalContractProvenance` is unconditional. When the client supports related information, keep navigable contracts only there. Coalesce exact duplicate unlocated presentations at their first position, and retain every distinct signature without truncation. Use required-kind contract order for a missing diagnostic and canonical expected-kind order followed by stable contract order for a kind mismatch. Do not add `Mismatches:` because neither diagnostic compares a same-kind implementation signature."

Dev: "What does an Event or interface contract diagnostic show when the client cannot display diagnostic related information?"
Domain Expert: "Use `ContractDiagnosticDetailProjection` to append every navigable detail to the primary message together with any unlocated detail, without duplicating a contract. Signature mismatches use the two-line `Expected signature` and `Mismatches` form; missing, kind-mismatch, and partial-coverage diagnostics use one `Required contract` line. A supporting client keeps navigable details in related information and receives only unlocated fallback lines in the primary message, so VS Code presentation does not duplicate them."

Dev: "Does `validation.incompatibleCallArgumentList` lose its candidate signatures when the client cannot display related information?"
Domain Expert: "No. Apply the same `ContractDiagnosticDetailProjection`: a supporting client keeps each conclusively inapplicable physical signature and its reasons in related information, while a non-supporting client receives those details after the primary message. Applicable or indeterminate candidates never enter the diagnostic or its fallback, and a contract is not duplicated across surfaces."

Dev: "How is an inapplicable callable candidate presented?"
Domain Expert: "A navigable related item uses `Candidate signature: <callable-signature> [#If]. Mismatches: <reasons>.` An unlocated candidate, or every candidate on a client without related-information support, uses the same content as two LF-separated `Candidate signature` and `Mismatches` lines after the primary message. Include callable kind, parameters, and return type through `CallableSignature`; omit the marker and preceding space when unconditional. `Candidate` does not select an active conditional branch or bind the call to an overload."

Dev: "How does a call mismatch identify the source item that failed?"
Domain Expert: "Use caller-centric `CallMismatchReason` subjects. Label a whole-call failure `call context`; label each supplied positional argument `argument <source-ordinal>` and each named one `argument <source-ordinal> ('<written-name>')`; label a missing required input `parameter '<declared-name>'`, falling back to `parameter <declaration-ordinal>` only when metadata has no name. Every ordinal is one-based, and a candidate signature's different parameter mapping never changes a supplied argument's subject."

Dev: "What exact reasons explain argument mapping and a missing required parameter?"
Domain Expert: "Use `<argument-subject> mapping: named arguments are not accepted`, `<named-argument-subject> mapping: no parameter named '<written-name>'`, `<named-argument-subject> mapping: parameter '<declared-name>' is already supplied`, `<positional-argument-subject> mapping: no parameter accepts this argument`, and `<parameter-subject>: required argument is missing`. Give one supplied argument only its first applicable mapping reason in that order. Unknown named-argument support is indeterminate; omitted `Optional` parameters and unused `ParamArray` portions are valid; and do not add a missing-required cascade caused only by an earlier mapping failure. Textual duplicate names and positional arguments after a named argument retain their dedicated diagnostics instead."

Dev: "How does a call-context mismatch describe the callable kind?"
Domain Expert: "Use `call context: expected <allowed-kind-list>, found <candidate-kind>`. The expected list is `Sub or Function` for statement invocation, `Function or Property Get` for a value-producing read, `Property Let` for value assignment, `Property Set` for object assignment, and `Event` for `RaiseEvent`. Preserve `Sub`, `Function`, `Declare Sub`, `Declare Function`, `Property Get`, `Property Let`, `Property Set`, or `Event` as the physical found kind. This reports the syntactic role without selecting a conditional branch or overload."

Dev: "How does a call mismatch explain a `ByRef` argument?"
Domain Expert: "Only a conclusively direct-storage argument uses `<argument-subject> for <parameter-subject> ByRef type: expected <parameter-type>, found <argument-type>` or the corresponding `ByRef array shape` reason. Fall back from an unavailable parameter name to its one-based declaration ordinal, and put type before shape when both fail. Literals, expression or callable results, and explicit outer-parentheses temporaries use ordinary value compatibility instead. Never say `expected ByRef, found ByVal`; if direct storage versus temporary is unknown, keep the result indeterminate."

Dev: "How does a call mismatch explain ordinary value type and array-shape incompatibility?"
Domain Expert: "Use `<argument-subject> for <parameter-subject> type: expected <parameter-type>, found <argument-type>` and the corresponding `array shape` reason for a uniquely mapped ByVal argument or `ByRef` value temporary. Require a modeled Let or Set rule to prove conversion failure; unknown types, expression classifications, and unmodeled coercions stay indeterminate. Render resolved canonical type labels rather than source expressions or raw spelling, expand type-declaration characters, normalize intrinsic casing, and reference-qualify external types whenever distinct identities would otherwise look identical. Shape is only `scalar` or `array`; when both type and shape fail, show type first."

Dev: "How are several call-mismatch reasons combined?"
Domain Expert: "Leave every fragment without terminal punctuation, remove an exact duplicate at its first stable position, join the retained fragments with `; ` in category, source-argument, and declaration-parameter order, and put one final period on the enclosing `Mismatches:` sentence. Do not truncate, count-summarize, or stop at the first retained reason. Related information and primary-message fallback use the same sequence."

Dev: "How are source and external contract details ordered when both must be placed in the primary message?"
Domain Expert: "Use canonical kind order first when the diagnostic spans several kinds. Within each kind, list source-backed contracts in stable project declaration order, followed by unlocated host or catalog contracts in the authoritative contract set's stable signature order. This is a fixed presentation order that favors locally inspectable source, not a closest-signature or compatibility ranking."

Dev: "If several physical contracts render as the same primary-message fallback detail, should every identical line remain?"
Domain Expert: "No. Coalesce an exactly identical complete presentation at its first position and show no multiplicity label. Signature, generic `[#If]` marker, and every mismatch reason must all agree; any difference keeps separate details. This is presentation-only: retain every physical contract in semantic analysis, and keep source variants at distinct locations when the client supports related information."

Dev: "Where does a same-kind interface signature mismatch point, and what does it say?"
Domain Expert: "Select the complete signature source span from the implemented member identifier through its parameter list and any written return type, including a return type-declaration character. Exclude visibility, `Static`, the already-correct kind keyword, and the procedure body. Use the self-contained message `Interface member '<implemented-name>' signature does not match any required <kind> contract.` This wider range is required because Function and Property Get return types can be the mismatching component."

Dev: "How does that mismatch explain why none of the required signatures match?"
Domain Expert: "Add one related-information item for every conclusively incompatible physical contract variant, pointing to the source interface member name or to the Public variable name for a derived accessor. Use `Required contract: <kind-specific-signature> [#If]. Mismatches: <reasons>.`, omitting the marker and its preceding space only when the projected contract's `ConditionalContractProvenance` is unconditional. Each `ContractMismatchReason` uses `<subject> <dimension>: expected <contract-value>, found <source-value>`; multiple reasons join with `; ` and one final period. List every independently conclusive reason in stable order: parameter-list structure first; then each parameter by ordinal with type, array shape, passing, role, and default; then the Property value parameter; and finally the return. Use `parameter 1`, `value parameter`, and `return`, render roles as `required`, `Optional`, or `ParamArray`, shapes as `scalar` or `array`, missing defaults as `no default`, and passing by effective `ByVal` or `ByRef`. Compare Optional defaults by evaluated constant value rather than source spelling and ignore parameter names. If a structural mismatch prevents a later comparison, do not invent that secondary reason."

Dev: "What if an authoritative Event or interface contract has no definition location for related information?"
Domain Expert: "Do not hide it or point a misleading related item back to the error. Append `Expected signature: <signature> [#If].` and `Mismatches: <reasons>.` as two lines after the primary diagnostic, omitting the marker only when the projected contract's `ConditionalContractProvenance` is unconditional. When the client supports related information, include only unlocated contracts there and keep navigable contracts in related information. Coalesce an exactly repeated unlocated presentation, retain every distinct presentation in deterministic contract order without truncation, and do not create a virtual catalog document solely for this fallback."

Dev: "Should signature diagnostics show the closest contract first?"
Domain Expert: "No. That would imply semantic best-match selection and make items jump as the implementation is edited. Order navigable source contracts by stable project declaration order and unlocated host or catalog contracts by their authoritative contract set's stable signature order. Preserve the first position when identical unlocated presentations coalesce, never reorder by conditionality or mismatch details, and do not attempt to interleave primary-message fallback lines with related-information items."

Dev: "Does a navigable Event mismatch use different related-information wording from an interface mismatch?"
Domain Expert: "No. Point to the physical source Event identifier and use the same exact `Required contract: <signature> [#If]. Mismatches: <reasons>.` form, with `Event` included in the signature and the marker omitted only when that Event contract's `ConditionalContractProvenance` is unconditional. Keep each physical Event variant separate because its location remains meaningful. The primary Event diagnostic stays unchanged, while an unlocated host contract continues to use the `Expected signature` fallback instead."

Dev: "Can an unknown `HostEventAvailability` default to `false` or `true`?"
Domain Expert: "No. Every Event in a `resolved` class has both availability values. Defaulting false can silently hide valid behavior, while defaulting true can offer or bind unsupported behavior. If either value cannot be established, mark the class `unverified` and preserve its `LastKnownGoodHostClassProjection`; an inspected `false`/`false` pair remains a valid resolved result."

Dev: "Can an `UnverifiedHostClassEntry` include the Event signatures observed before failure?"
Domain Expert: "No. It contains only its `HostClassIdentity`, `unverified` status, stable reason code, and human-readable message. A machine consumer preserves the `LastKnownGoodHostClassProjection` or remains `indeterminate`; it never commits a partial Event array. A future diagnostic-only observation payload requires a new schema version and must remain separate from authoritative projection data."

Dev: "Which `reasonCode` values can an `UnverifiedHostClassEntry` contain?"
Domain Expert: "Use `eventEnumerationFailure`, `signatureReadFailure`, `availabilityReadFailure`, `inspectionTimeout`, `inspectionAborted`, `cancelled`, or the class-local fallback `inspectionFailure`. The message may identify the Event or inspection stage, but consumers never parse it. If class identities themselves cannot be completely enumerated, emit top-level `classEnumerationFailure`; source-template preparation and process-release failures remain invocation-invalidating rather than class reasons."

Dev: "What does `host-class list --format json` return after cooperative cancellation?"
Domain Expert: "After request scope is known, return a schema-valid terminal result only when process release and serialization succeed. Preserve classes resolved before cancellation; mark the in-progress and every known unprocessed class `unverified` with `cancelled`; leave undiscovered classes omitted and unknown. Add only top-level `operationCancelled`, set `complete: false`, and exit nonzero. Cancellation itself does not mean `classEnumerationFailure` or `inspectionAborted`; failure to prove process release or serialize leaves no usable JSON."

Dev: "What happens when shared `HostClassInspectionState` becomes untrusted during one class?"
Domain Expert: "Stop inspection without starting replacement Excel in the same invocation. Give the causal class its most specific reason, such as `inspectionTimeout`, and mark every known later unprocessed class `inspectionAborted`. Preserve earlier finalized `resolved` entries only when their isolation from the failure is established, add top-level `inspectionStateUntrusted`, set `complete: false`, and exit nonzero. If process release fails or earlier results may also be contaminated, the whole JSON result is unusable."

Dev: "Does one large document consume one shared timeout for every host class and Event?"
Domain Expert: "No. `HostClassList` reuses the ordinary Excel-start, workbook-open, and cleanup deadlines. Complete class-identity enumeration has one 60-second deadline, and each class receives a fresh 60-second deadline for its complete Event, signature, and availability inspection. There is no command-wide or per-Event deadline. A class deadline produces `inspectionTimeout` for that class and `inspectionAborted` for known later classes; an identity-enumeration deadline produces top-level `classEnumerationFailure` and `inspectionStateUntrusted`."

Dev: "How do hidden and restricted TypeLib flags affect `WithEvents`?"
Domain Expert: "A coclass marked `TYPEFLAG_FHIDDEN` remains valid when explicitly resolved, while `TYPEFLAG_FRESTRICTED` produces `invalidInaccessibleType`. Default-source members marked `FUNCFLAG_FHIDDEN` or `FUNCFLAG_FRESTRICTED` still count in `TypeLibStructuralEventSurface`, so a hidden-only or restricted-only coclass remains `eligible`. Omit those members from `TypeLibEventAuthoringSurface`, but retain them in `TypeLibExistingHandlerRecognitionSurface` so an already-written `variable_Event` name can preserve the VBE code-window association."

Dev: "Does every occurrence spelled `publisher_Changed` refer to the Event?"
Domain Expert: "No. A Sub, Function, or Property accessor declaration name first forms a procedure-kind-independent candidate only when its prefix resolves in the same class module to a module-level variable target with an available `WithEventsEventBindingSet`; that requires at least one `eligible` or `indeterminate` `WithEventsTypeEligibility`. Its prefix refers to the variable and each resolved suffix refers to the Event, while the complete name retains the original procedure or Property definition. Only a Sub becomes a valid `WithEventsHandlerDeclaration`; a Function or Property accessor is a `nonSubProcedureAssociation`. Ordinary occurrences of the complete name refer to the original definition, not directly to the Event."

Dev: "Do visibility or `Static` change whether a matching Sub is an Event handler?"
Domain Expert: "No. Public, Private, Friend, or omitted visibility and initial or trailing `Static` are valid handler forms. Retain those modifiers as declaration metadata, but exclude them from candidate identity, handler recognition, parameter compatibility, and conditional-family identity."

Dev: "What happens when a matching Function or Property accessor uses an Event-handler name?"
Domain Expert: "Keep its prefix variable binding and resolved suffix `EventReference` for navigation, but do not treat it as a handler or compare its parameters. Publish `validation.eventHandlerMustBeSub` on the `Function` keyword or complete `Property Get`, `Property Let`, or `Property Set` keyword span only when every variable binding entry conclusively resolves an Event whose authority is `sourceDeclared` or `currentHostProjected`. Suppress that diagnostic if any entry is `notWithEvents`, `notEvent`, `indeterminate`, `externalTypeLibAdvisory`, or `lastKnownGoodHostAdvisory`, and never add `validation.incompatibleEventHandlerSignature` to the same declaration."

Dev: "Should a TypeLib Event handler receive procedure-kind or signature errors?"
Domain Expert: "No. Preserve its `WithEvents` variable and Event associations, Definition, Hover, retained `CallableSignature`, and Signature Help, including for an already-written hidden or restricted Event name. Classify its validation authority as `externalTypeLibAdvisory`; neither `validation.eventHandlerMustBeSub` nor `validation.incompatibleEventHandlerSignature` is emitted, even when the metadata comparison is conclusively incompatible."

Dev: "Can a projected host Event handler receive those errors?"
Domain Expert: "Only from current evidence. Classify a target from the current authoritative host snapshot as `currentHostProjected`; it may participate in either compile-style handler diagnostic when every binding is resolved and every target is diagnostic-authoritative. Classify retained last-known-good evidence as `lastKnownGoodHostAdvisory`; it preserves association and signature guidance but suppresses both diagnostics until a current projection replaces it."

Dev: "How is `order_publisher_Changed` split when a `WithEvents` variable name contains underscores?"
Domain Expert: "Use `WithEventsHandlerNameDecomposition` and split at the final ASCII underscore, producing `order_publisher` and `Changed`. VBA Event names cannot contain underscores, while the variable prefix can. Do not enumerate alternative splits or consult variable and Event catalogs; missing metadata must not change the syntactic decomposition."

Dev: "What if conditional `WithEvents publisher` variants have different declared types?"
Domain Expert: "Bind the prefix to the complete variable family. Admit the family when at least one syntactically valid `WithEvents` variant has `eligible` or `indeterminate` type eligibility. Create one `WithEventsEventBindingSet` entry per remaining physical module-variable variant, but exclude syntax-invalid recovered variants and conclusive-invalid type variants. Retain resolved Event targets, conclusive non-Event suffixes, and indeterminate metadata separately. Do not manufacture a binding status for an excluded variant. Definition unions the resolved locations, while `ResolvedEventSignatureSet` projects only resolved entries without selecting a host branch or merging distinct Event identities."

Dev: "What if only some variants in that variable family use `WithEvents`?"
Domain Expert: "Retain `WithEvents` presence per declarator as variant metadata. An ordinary non-`WithEvents` module-variable variant becomes a conclusive `notWithEvents` entry before type or Event lookup, distinct from a `notEvent` entry for a type-eligible `WithEvents` class that lacks the suffix Event. Both are conclusive non-handler evidence, neutral for Rename convergence, and suppress the aggregate incompatible-handler diagnostic. Syntax-invalid recovered and conclusive-invalid type variants are excluded rather than classified. A family without any `eligible` or `indeterminate` `WithEventsTypeEligibility` never enters handler binding."

Dev: "When does a handler-shaped procedure become an Event handler if its conditional `WithEvents` variants disagree?"
Domain Expert: "At least one resolved Event entry gives every procedure kind its candidate navigation projections. A Sub becomes a `WithEventsHandlerDeclaration`; a Function or Property accessor becomes a `nonSubProcedureAssociation`. If every entry is `notWithEvents` or `notEvent`, treat it as an ordinary procedure with no handler semantics. If none resolves and at least one is indeterminate, retain the prefix variable binding but defer the Event suffix and diagnostics. Emit either kind or incompatible-signature diagnostics only when every binding is resolved and every target is `sourceDeclared` or `currentHostProjected`; an external TypeLib or last-known-good host association makes the complete diagnostic advisory-only."

Dev: "What if the complete candidate name has several `#If` variants with different procedure kinds or parameters?"
Domain Expert: "When every physical family variant is classified `resolvedHandler` or `nonSubProcedureAssociation`, use the existing `ConditionalDeclarationFamily`; do not create a separate handler-family kind. Definition and References cover every physical candidate variant, and an Event or `WithEvents` variable Rename updates the whole dependent family atomically. Compare only each Sub handler independently with every possible Event signature. Each Function or Property accessor instead retains its non-Sub association; an error diagnostic is derived from it only for a complete target set whose authorities are all `sourceDeclared` or `currentHostProjected`. Neither result claims that the `#If` branches correspond."

Dev: "What if that conditional family also contains an ordinary variable, procedure, or another noncandidate variant?"
Domain Expert: "Keep one unsplit family for Definition and References, but do not rename the unrelated variant or only the candidate subset. If any conclusive ordinary or noncandidate variant exists, classify the coverage `conclusiveMixed` and fail a non-no-op upstream Rename with `resolutionChanged`. If coverage is incomplete without such conclusive evidence, use `analysisIncomplete`. Conclusive meaning change takes precedence when both kinds of evidence exist. Prefix or convergent suffix target selection can remain available; the requested Rename performs the family-wide proof before producing edits."

Dev: "Can F2 on one handler suffix rename every distinct Event shown by Definition?"
Domain Expert: "No. Definition retains all resolved Event associations, but Rename requires `HandlerEventRenameConvergence`: every resolved association must identify the same source-owned logical Event target and none may be indeterminate. `notWithEvents` and `notEvent` entries are neutral. Never choose or synthesize a multi-Event Rename; an Event declaration Rename also fails closed if a shared dependent suffix has another Event target or indeterminate binding."

Dev: "What else changes when `WithEvents publisher` is renamed to `source`?"
Domain Expert: "Rename the variable declaration and references, change the prefix of every `resolvedHandler` or `nonSubProcedureAssociation` candidate owned by it, and rename every ordinary reference to each derived procedure, Property identity, or conditional family in one atomic `RenamePlan`. Preserve each Event suffix and every modifier. A Function or Property candidate retains `validation.eventHandlerMustBeSub` only when its complete validation authority remains diagnostic-authoritative—`sourceDeclared` or `currentHostProjected`; an external TypeLib or last-known-good host association remains diagnostic-free. Rename does not repair procedure kind. Fail the whole plan on a derived collision, changed binding, or incomplete analysis. An owned `indeterminateCandidate` specifically fails with `analysisIncomplete`, while an `ordinaryProcedure` remains unchanged."

Dev: "Can F2 on an ordinary `publisher_Changed` call independently rename the handler or non-Sub-associated Function or Property?"
Domain Expert: "No. Each is a `DependentRenameTarget`. Initiate Rename from the variable prefix, from a convergent Event suffix in its declaration, or from another occurrence of that underlying variable or Event target. The underscore and ordinary complete-name references have no Prepare Rename target, and a direct Rename request fails with `notRenameTarget` rather than guessing which upstream name to change. Deliberately detaching a non-Sub-associated Function or Property from the Event relationship is a manual edit or separate repairing Code Action."

Dev: "Can a conclusively recognized `IFoo_Bar` implementation be renamed as an ordinary procedure or Property?"
Domain Expert: "No. It is a `DependentRenameTarget`: Rename the source interface type to change every `IFoo_` prefix, or Rename the source interface member—or the owning Public variable behind a derived accessor—to change every `_Bar` suffix. The upstream `RenamePlan` changes all associated declarations, complementary Property accessors, conditional variants, and ordinary complete-name references atomically. A direct independent Rename of `IFoo_Bar` fails with `notRenameTarget`; deliberate detachment is a manual edit or future Code Action."

Dev: "What does F2 select inside the declaration `Private Sub IFoo_Bar()`?"
Domain Expert: "With one conclusive source contract, F2 inside `IFoo` selects exactly that prefix range and uses the source interface type family's canonical name as the placeholder. F2 inside `Bar` selects exactly that suffix range and uses the source member family's canonical name—or the owning Public-variable family's name for a derived accessor—as the placeholder. The semantic separator `_` has no target. If the relevant upstream identity is external, unresolved, ambiguous, or otherwise not a source `RenameTarget`, Prepare Rename returns no target rather than guessing."

Dev: "Can F2 inside an ordinary `Call IFoo_Bar` reference select its interface prefix or member suffix?"
Domain Expert: "No. That occurrence binds only the complete implementation procedure, Property, or conditional family; it contains no semantic substring occurrence for the interface type or member. Prepare Rename returns no target at every character, Definition and References continue to use the complete implementation identity, and only an upstream interface type or member `RenamePlan` changes the reference as a derived atomic edit. A bypassed non-no-op Rename fails with `notRenameTarget`, while an ordinally unchanged request remains the general successful no-op."

Dev: "Where can I start Rename for a source module, class, or form?"
Domain Expert: "From any resolved source-owned `ModuleIdentityOccurrence`: the name inside `Attribute VB_Name`, a type occurrence such as `Implements IFoo`, `As IFoo`, or `New Customer`, a standard-module qualifier such as `Module1.Run`, a predeclared/default-instance qualifier such as `UserForm1.Show`, or the conclusive `IFoo` prefix of an implementation declaration. `foo.Member` instead contains a variable occurrence and a member occurrence; it does not contain the `IFoo` identity merely because `foo` has that type."

Dev: "Does renaming `ModuleIdentity` `Customer` to `CustomerRecord` also rename `Customer.cls`?"
Domain Expert: "For project-local source, yes when the old basename and old identity match case-insensitively: keep the directory and extension and rename the basename, including the matching `.frx` with a form. This includes a case-only identity change, for which the final file-name casing follows the requested identity even on a case-insensitive filesystem. Preserve an already-different basename because it may be deliberate. Explorer F2 changes only the path and is not a semantic Rename entry point."

Dev: "Does that file-following rule require a `ProjectManifest`?"
Domain Expert: "No. An `AdHocVbaProject` applies the same rule within its one-folder project boundary when the source has an explicit `ModuleIdentity`. It neither invents CommonModules ownership nor host projection; its collisions, form sidecar, client-capability, and resource-conflict rules otherwise match ordinary workbook-backed project-local source."

Dev: "Does an atomic `RenamePlan` guarantee rollback if the client fails while renaming a file?"
Domain Expert: "No. It guarantees that the server emits every required text and resource operation or no plan. File-following `ModuleIdentity` Rename requires client support for ordered document changes and file Rename; otherwise it fails with `clientCapabilityMissing` before returning partial edits. A later client or filesystem application failure is `WorkspaceEditApplicationFailure` and uses client-owned Undo, retry, or repair."

Dev: "What if the source unit or destination has already changed while a file-following Rename is being planned?"
Domain Expert: "Return `resourceOperationConflict` with a structured `sourceMissing`, `sourceChanged`, `destinationExists`, or `sidecarConflict` condition, the affected path, and repair guidance, without returning any edit. A VBA declaration collision remains `sameScopeCollision`, and a failure after a valid plan reaches the client remains `WorkspaceEditApplicationFailure`."

Dev: "Should Rename save dirty editors or silently replan when source changes during the request?"
Domain Expert: "No. The request-start `VbaProject` snapshot treats then-current unsaved editor contents as authoritative without saving them. A later participating-source change produces `resourceOperationConflict` with `sourceChanged` and asks the user to run Rename again; it never switches the initiating request onto a newer snapshot automatically."

Dev: "Can F2 rename a module whose source has no `Attribute VB_Name` and is known only by its file name?"
Domain Expert: "No. That is only a `FallbackModuleIdentity` for analysis recovery. A non-no-op semantic Rename fails with `moduleIdentityNotExplicit`; it neither inserts identity metadata nor treats a file operation as semantic Rename. Re-export or repair the metadata first. Explorer file Rename remains an ordinary path operation."

Dev: "What if the source has two `Attribute VB_Name` records or one malformed record?"
Domain Expert: "Two records are an invalid duplicate in a procedural `.bas` module. In a `.cls` or `.frm` header, repeated valid records are allowed: the last is authoritative and each earlier one is `ShadowedModuleIdentityMetadata`, so F2 neither targets nor edits it. A malformed, misplaced, or overlength record remains `InvalidModuleIdentityMetadata` for every module kind and makes F2 fail with `moduleIdentityInvalid`."

Dev: "Can `ModuleIdentity` use the general 255-character `RenameName` limit?"
Domain Expert: "No. MS-VBAL limits a module name to 31 characters, counted as Unicode code points under its lexical conventions. A 32-code-point Rename fails with `invalidName`, and an existing overlength `VB_Name` record is malformed `ModuleIdentityMetadata`; other Rename targets retain the shared 255-character limit."

Dev: "Can module Rename compare its new name with `vba-project.json`'s `projectName`?"
Domain Expert: "No. That field is a tooling label, not `VBProject.Name`. A manifest-backed project compares against the source template's authoritative `VbaProjectName` and fails with `analysisIncomplete` when that name cannot be obtained. An `AdHocVbaProject` deliberately has no containing-project-name authority, so it skips only that collision check rather than disabling module Rename."

Dev: "Can Rename reuse a project name inspected from an older version of the source template?"
Domain Expert: "Only when the current request-start template content has the same fingerprint. A changed template invalidates the old authority and yields `analysisIncomplete` until its current name is available; after the request snapshot is captured, do not reread the template or move the semantic baseline."

Dev: "Should module Rename reject every name listed in a reference catalog's qualifier aliases?"
Domain Expert: "No. Reject a collision with the selected reference's authoritative `ReferencedVbaProjectName`, such as `VBA` or `Excel`, but do not manufacture collisions from a human-visible manifest reference name or a generated supplemental qualifier such as `MicrosoftExcel160ObjectLibrary`."

Dev: "Can module Rename proceed while one active manifest reference has no authoritative project name yet?"
Domain Expert: "No. Fail that `ModuleIdentity` Rename immediately with `analysisIncomplete` and ask the user to retry after reference metadata is ready. Do not wait inside Rename or infer a name from the manifest or qualifier aliases; Completion and non-module Rename may still use whatever catalog metadata they can prove."

Dev: "Can a stale-persisted reference catalog supply the project name for module Rename?"
Domain Expert: "No. Use an explicit bundled name or an identity-backed current persisted or generated catalog committed for the active `ReferenceSelectionFingerprint`. An in-flight refresh does not invalidate such committed authority, but a stale-persisted catalog remains advisory for other editor metadata and causes module Rename to fail `analysisIncomplete`."

Dev: "Does a project or library name conflict need a new Rename failure reason?"
Domain Expert: "No. It is the same semantic VBA namespace failure as another declaration collision, so keep `sameScopeCollision`. Return every conflict in `error.data.conflicts`, identifying each as `sourceDeclaration`, `containingProject`, or `referencedProject`, and name every conflicting identity in the actionable message."

Dev: "What if one requested Rename conflicts with several identities at once?"
Domain Expert: "Return one `sameScopeCollision` with the complete `conflicts` array, never only the first conflict or a singular compatibility field. Order source declarations by stable project declaration order, then the containing project, then references by active selection order; the message uses the same complete order."

Dev: "What if an externally edited source already names a module `Word` while the Word library is active?"
Domain Expert: "Publish `validation.moduleIdentityNameConflict` on the authoritative unquoted `VB_Name` payload, but retain the source definition for Definition, References, and a repairing Rename. Source-to-source module collisions remain `validation.duplicateDeclaration`, and incomplete project or reference-name evidence produces no speculative source diagnostic."

Dev: "What if the same module name conflicts with both the containing project and a referenced project?"
Domain Expert: "Publish one diagnostic on the `VB_Name` payload, not overlapping diagnostics. List every conflict with the containing project first and references in active selection order, retain the same ordered entries in `diagnostic.data.conflicts`, and do not invent related-information locations for binary or external project identities."

Dev: "Does F2 simply say there is no Rename target for a managed module identity?"
Domain Expert: "No. The semantic occurrence is known, so Prepare Rename returns an actionable `RenameFailure`: `managedModuleIdentity` directs an `InstalledCommonModule` to upstream Rename or explicit detach, while `hostManagedModuleIdentity` explains source-template ownership. Last-known-good host evidence remains `analysisIncomplete`, and a truly nonsemantic cursor position still returns `null`."

Dev: "Can F2 rename the `ModuleIdentity` of an `InstalledCommonModule` in this document?"
Domain Expert: "No. That is a `ManagedModuleIdentity`: rename it in the canonical CommonModules source or explicitly detach it into project-local source first. F2 never rewrites its manifest identity, mutates the configured repository, or silently detaches dependency ownership. Ordinary member Rename and local content edits remain available."

Dev: "Can F2 rename an associated UserForm or intrinsic document module?"
Domain Expert: "Not as an ordinary source Rename. A current form association and every intrinsic document source have `HostManagedModuleIdentity`; changing source text alone would not rename the source-template component. Last-known-good form evidence makes a non-no-op request `analysisIncomplete`. A form conclusively outside host association remains project-local and may rename its `.frm` and `.frx`; a host-managed identity needs a separate workbook-backed refactoring."

Dev: "Where should an incompatible Event handler diagnostic point?"
Domain Expert: "Use its complete parenthesized parameter list, or the handler identifier when the list is omitted. Keep the primary message self-contained, and attach one related item per conclusively incompatible source Event signature. Report ordered structural and type reasons, never parameter-name differences or conditional expressions. TypeLib signatures remain advisory and never cause this diagnostic."

Dev: "If an active reference has no usable catalog, should the editor mark source lines?"
Domain Expert: "No. The reference stays active but contributes no external definitions. Report `VbaProjectReferenceCatalogAvailability` through language-server output, status, or trace and through `EnvironmentDiagnostic`, not through source diagnostics."

Dev: "Should missing root-exposure metadata or unavailable host globals create source diagnostics?"
Domain Expert: "No. They affect editor intelligence availability, not source validity. Report catalog, reference, and refresh state through output, trace, status, or `EnvironmentDiagnostic`."

Dev: "Should completion wait while TypeLib metadata is being discovered?"
Domain Expert: "No. Completion, hover, and signature help use the best committed `LastKnownGoodReferenceCatalog`. `VbaProjectReferenceCatalogRefresh` runs in the background after project activation or an effective reference-selection change."

Dev: "Should root completion scan TypeLib metadata when many globals like `xlCenter` may be available?"
Domain Expert: "No. `CompletionCandidate` discovery reads only already-admitted source, vocabulary, and committed reference-catalog definitions. Prefix filtering can remain editor-owned until measurement shows a need for server-side incomplete completion."

Dev: "Should completion wait if the Excel reference catalog is currently refreshing?"
Domain Expert: "No. Editor requests use the current `LastKnownGoodReferenceCatalog` when one is committed. If no committed snapshot exists yet, that reference simply contributes no candidates until a later request sees a successful commit."

Dev: "Should every VBA `didChange` resolve the manifest and retry reference catalog work?"
Domain Expert: "No. It updates source analysis and diagnostics only. `VbaProjectReferenceCatalogLifecycle` belongs to project activation and effective reference-selection changes."

Dev: "What happens when two source files from the same project open with the same references?"
Domain Expert: "They share the same `ReferenceSelectionFingerprint` and automatic lifecycle revision, so persisted preload and discovery are scheduled at most once."

Dev: "What happens when a persisted catalog is missing or corrupt?"
Domain Expert: "That result is negative-cached for the current `ReferenceCatalogLifecycleRevision`. It does not create a source diagnostic and does not prevent an explicit retry or a changed selection from trying again."

Dev: "Does refreshing an Excel catalog invalidate a project that selects only Word?"
Domain Expert: "No. Project snapshots track revisions for their selected references, so only affected project scopes are rebuilt."

Dev: "Should `vba-project.json` store TypeLib GUIDs for references?"
Domain Expert: "No. The `ProjectManifest` stores the human-visible `VbaProjectReference` name from `Reference.Description`. After discovery resolves that name, catalogs and caches may use `VbaProjectReferenceCatalogIdentity` keys such as GUID, version, LCID, and path."

Dev: "What if one manifest reference name matches several TypeLib candidates?"
Domain Expert: "Registry ambiguity alone is not an error. Probe each candidate through `References.AddFromGuid` from a fresh temporary copy of the explicitly selected document's source template, or the primary document's source template when no document is specified. Use the returned `Reference` identity rather than the requested registry identity, and coalesce candidates that VBE maps to the same result. Adopt one distinct usable result; fail as unavailable when none remains and as ambiguous only when multiple distinct usable results remain."

Dev: "Should `reference list` open the source template merely to check whether it already contains a same-name reference?"
Domain Expert: "No. Zero registry matches are unavailable and one registry identity is adopted without Excel. Only a registry-ambiguous name starts the VBE-equivalent probe, where a same-name reference already present in the fresh baseline supplies its identity. Build, publish, and test builds may use the same shortcut because their materialization workbook is already open."

Dev: "Should `reference list --available` hide or disable a resolved library when its project name conflicts with a current source module?"
Domain Expert: "No. Both list modes are `VbaProjectReferenceResolutionInventory` operations, so they neither inspect nor annotate current source compatibility. Keep the resolved candidate selectable in CLI output, completion, and QuickPick; after dependency intent is recorded, Language Server validation, Doctor, and materialization preflight own conflict feedback."

Dev: "Should `ReferenceAddQuickPick` show ambiguous or unavailable names as disabled-looking items, or show resolved entries from an incomplete inventory?"
Domain Expert: "No. Populate it only from `resolved` entries of a schema-valid complete available inventory. Keep conclusive resolution issues in VBA Tools Output, report an empty actionable set when none resolve, and abort with Show Output when the inventory is partial or untrusted. Command Palette has no free-text fallback; direct CLI `reference add` remains available."

Dev: "Should `ReferenceAddQuickPick` also show an already-effective reference whose manifest entry has `requested: false`, so the user can promote it?"
Domain Expert: "Not in the initial UI. Keep Add Reference limited to names absent from the effective selection; an already-effective reference would make an Add picker misleading, and promotion has no runtime effect before auto-removal exists. Direct CLI `reference add` still supports promotion, while a future auto-remove feature may introduce an explicit directly-requested management action rather than overloading this picker."

Dev: "How much resolved TypeLib identity should each `ReferenceAddQuickPick` item display?"
Domain Expert: "Use the canonical human-visible name as the label and `TypeLib <major>.<minor>` as its description, and let search match both. Do not add a second detail line for the GUID or a path; retain the GUID in machine output and VBA Tools Output, and submit the item's retained canonical name rather than reconstructing it from display text."

Dev: "Should Add Reference show a separate progress notification before opening its QuickPick?"
Domain Expert: "No. After project and document selection, open the QuickPick immediately in a busy, selection-disabled discovery state. Populate it only when the complete inventory arrives; Esc cooperatively cancels inventory work and stays silent after cleanup, while failure closes the picker and offers Show Output."

Dev: "Should `ReferenceAddQuickPick` add only one reference at a time?"
Domain Expert: "No. Let the user select multiple resolved entries and pass their canonical names to one `reference add` in inventory order. Resolve the complete selection before one manifest save; if any selected name fails or cancellation wins before commit, add none. A name added concurrently with direct intent is a successful no-op, while one added only by CommonModules is promoted in the same atomic operation rather than causing a partial-operation error."

Dev: "After the user accepts the selected references, should the QuickPick stay busy through manifest mutation and a follow-up configured-reference list?"
Domain Expert: "No. Close the picker and run `reference add` under a separate cancellable progress notification. Cancellation before the atomic manifest commit is silent and changes nothing; once commit succeeds, a late cancellation cannot undo success. Preserve command output, show one concise document-specific success notification, and let `VbaProjectReferenceCatalogLifecycle` observe the manifest while refreshing only when the effective name/order selection changed, instead of running a redundant configured `reference list`. Failure leaves the manifest unchanged and offers Show Output."

Dev: "Should `VBA Tools: Remove Reference` offer only references that currently resolve?"
Domain Expert: "No. Replace free text with a multi-select `ReferenceRemoveQuickPick` containing every name in the selected document's `VbaProjectReferenceSelection`, in stored order and spelling, without consulting the registry, Excel, VBE, or the source template. This must remain a repair path for ambiguous, unavailable, or unverified references. Submit one atomic `reference remove`; when the selection is empty, report that the document has no configured references. Direct CLI removal remains the free-text path."

Dev: "Should the extension read `vba-project.json` itself to populate `ReferenceRemoveQuickPick`, or reuse ordinary resolving `reference list`?"
Domain Expert: "Neither. Use project-and-document-scoped `reference list --no-resolve`, which returns only the complete stored `VbaProjectReferenceSelection` in manifest order without registry or Excel work. Keep ordinary `reference list` as the resolving inventory, reject combining `--no-resolve` with `--available`, and support both text and JSON so the same repair view remains independently useful from the CLI."

Dev: "Should `reference list --no-resolve --format json` label every uninspected entry `unverified`?"
Domain Expert: "No. It is not a resolution inventory. Extend the schema `1.0` union with `mode: selection`; require request-matching project and document context, `complete: true`, and ordered `{ name }` entries without `status` or `identity`. Invalid context or manifest state emits no partial selection result and exits nonzero."

Dev: "Should `ReferenceRemoveQuickPick` fail when its displayed selection became stale before acceptance?"
Domain Expert: "No. Treat the picker contents as a selection snapshot rather than a version token, and let `reference remove` read the latest valid manifest when its invocation starts. Remove selected names that remain, treat already-absent names as successful no-ops, and preserve later-added or otherwise unselected entries. If the document disappeared or the latest manifest is invalid, fail without mutation instead of reopening or refreshing the picker."

Dev: "What should the user see while `ReferenceRemoveQuickPick` loads its non-resolving selection?"
Domain Expert: "Open the picker immediately in a busy, non-selectable loading state and run the one selection-list command without a second progress notification. Hiding it cancels discovery silently and late results stay hidden. On success, show the stored names in manifest order with no preselection or resolution metadata; close and inform the user when none exist. Invalid or mismatched output closes the picker and offers VBA Tools Output."

Dev: "Should removal keep the QuickPick open while the manifest mutation waits for ownership and commits?"
Domain Expert: "No. Close the picker and run `reference remove` under a separate cancellable progress notification, using the same atomic-commit cancellation boundary as addition. Cancellation that wins before commit is silent and changes nothing; commit wins over a later cancellation. Preserve command output, report success for the selected document, offer VBA Tools Output on failure, and let the manifest lifecycle refresh without a follow-up configured-reference list."

Dev: "Can the extension infer how many requested references actually changed from a successful mutation's human-readable output?"
Domain Expert: "No. Both Add and Remove expose a schema-versioned JSON mutation result that distinguishes actual changes from successful no-ops, while direct CLI use keeps human-readable text by default. The extension validates that result instead of parsing prose, reports the actual changed count or that no change was needed, and does not issue a follow-up list merely to discover the outcome."

Dev: "Does a reference mutation's JSON report only aggregate counts?"
Domain Expert: "No. It returns one ordered result for each trimmed, case-insensitively distinct requested name. Keep the requested spelling separate from the exact manifest spelling: Add reports `added`, `promoted`, or `alreadyPresent`; `promoted` means an existing CommonModules-introduced entry changed from not directly requested to directly requested. Remove reports `removed` or `alreadyAbsent`, and only `alreadyAbsent` has no stored name. Counts are derived from these exhaustive results rather than duplicated."

Dev: "May the default text output omit successful no-op requests and print only changed references?"
Domain Expert: "No. Render every mutation result in request order with a humanized status, preferring the exact stored name and showing a differing requested spelling parenthetically. Render `promoted` as marked directly requested rather than unexplained machine jargon, and let `alreadyAbsent` use its requested name. End with separate added, promoted, removed, and unchanged counts as applicable, including for an all-no-op command; scripts use JSON rather than parsing this prose."

Dev: "Must `reference add` resolve an explicitly requested name that is already selected in the invocation-start manifest?"
Domain Expert: "No. Its existing stored selection is environment-independent authority. If it is already directly requested and remains so at final rebase, return `alreadyPresent`; if it remains selected but was introduced only by CommonModules, change it to directly requested and return `promoted`. If another writer removed an invocation-start selection meanwhile, fail the complete Add with `referenceSelectionChanged` rather than restoring an unverified name. Resolve only invocation-start-missing names, still atomically, and treat one that another writer added before rebase as `alreadyPresent`."

Dev: "Does an Add whose final rebase needs no new reference entry fail merely because the document selected another source template meanwhile?"
Domain Expert: "No. Require the invocation-start canonical source-template path only when final rebase would append at least one name using that invocation's resolution result. If every request is already present, the template is irrelevant: directly requested entries are no-ops and CommonModules-introduced entries may be promoted without resolution. A missing document or invalid manifest still fails, and any mixed result that needs one addition applies the path check to the whole atomic operation."

Dev: "May one document's manifest contain differently cased or whitespace-padded duplicates of the same reference name?"
Domain Expert: "No. `VbaProjectReferenceSelection` is an ordered set: every name is nonempty, already trimmed, and unique under `OrdinalIgnoreCase`. Treat a violation as an invalid manifest for every project command, identify the document and conflicting spellings, and require explicit manifest repair rather than silently trimming or deduplicating. Remove remains a repair path for valid but unavailable or ambiguous names, not malformed selection structure."

Dev: "May a project manifest or reference command select `Visual Basic For Applications`?"
Domain Expert: "No. It is the sole reserved `VbaProjectReferenceSelection` name because `VbaStandardLibraryReference` is always active independently. Reject it under `OrdinalIgnoreCase` in the closed manifest schema and runtime validation; `reference add` and `reference remove` fail before resolution or mutation with `Visual Basic For Applications is always active and cannot be added to or removed from project reference selection.` Do not emit it from selected or available reference lists, Tab completion, `ReferenceAddQuickPick`, or `ReferenceRemoveQuickPick`. Language Server and Doctor continue to supply or report the always-active standard library independently. Do not reserve aliases such as `VBA`, and allow Excel, Office, OLE Automation, and every other baseline or protected reference through the ordinary selection contract."

Dev: "Are successful no-ops or a result whose commit cannot be trusted represented as mutation warnings?"
Domain Expert: "Neither. Per-name status represents ordinary no-op, while any loss of result or commit trust fails the complete mutation. `warnings` contains only structured non-fatal information. JSON success keeps that information inside its sole stdout object; text mode writes warnings to stderr. Unknown warning codes remain displayable because consumers never derive mutation success from them."

Dev: "Does a successful reference mutation with warnings produce both a success toast and a warning toast?"
Domain Expert: "No. Show one information notification when `warnings` is empty, or one warning notification summarizing the actual added, promoted, removed, and unchanged outcome as applicable together with the warning count. The warning notification offers VBA Tools Output without opening it automatically. Warnings do not undo the successful mutation or suppress manifest-driven refresh."

Dev: "If `reference remove` exits zero but returns malformed or request-mismatched JSON, may the extension call another CLI and try again?"
Domain Expert: "No. The manifest may already have committed. Validate the complete context and the exact one-result-per-request partition, reject unknown outcome discriminants while tolerating additive properties and warning codes, then report an untrusted completed result with Show Output. Do not claim failure or no change, retry, roll back, or fall back after execution; allow the manifest lifecycle to observe whatever actually committed."

Dev: "When every rebased request is already a no-op, what wins against a cancellation that arrives during cleanup if there is no manifest commit?"
Domain Expert: "The trusted complete no-op decision is a success boundary parallel to atomic commit. Perform the final cancellation check first; cancellation that wins remains silent, while a later cancellation cannot replace the already established no-op result. Complete owned cleanup and return every `alreadyPresent` or `alreadyAbsent` result."

Dev: "Can that no-op result ignore a non-cooperating editor that changes the manifest after the rebase because no atomic replacement follows?"
Domain Expert: "No. Apply the same raw-byte optimistic fence immediately before the no-op success boundary. An observed mismatch is an external-edit conflict with no success result, retry, or manifest change. As with replacement, the tiny interval after comparison remains outside the guarantee; participating `VbaDev` writers remain fully serialized by the lease."

Dev: "Does one logical manifest update count as atomic if `vba-project.json` is overwritten directly?"
Domain Expert: "No. Every manifest mutation first writes and flushes one complete validated sibling temporary file, then commits it with a same-volume atomic replace or initial move. Failure or cancellation before that boundary preserves the prior manifest, no unsafe direct-write fallback is allowed, and success at the boundary wins over later cancellation."

Dev: "May a `vba-project.failed-*.json` recovery manifest use a timestamped direct write because it is not the canonical manifest?"
Domain Expert: "No. It is a `ProjectManifestRecoveryArtifact` and manual recovery authority. Serialize the validated plan in canonical UTF-16LE-with-BOM form with a trailing newline, write and flush a create-new unique sibling temporary file, then atomically move it without overwrite to a name containing a UTC timestamp and collision-resistant random suffix. A collision chooses another name; only the committed final file is authoritative, while a crash-left temporary file is not. Never auto-apply it or print planned JSON when recovery persistence also fails, and retain the lease until that result is known."

Dev: "Should any manifest edit made while `reference add` resolves force the command to fail?"
Domain Expert: "No. Immediately before commit, reload the latest valid manifest and reapply the reference delta so unrelated edits and later reference selections survive. Add fails only when its selected document or canonical source-template selection changed in a way relevant to an actual addition, while Remove needs only the selected document to remain. A latest target with direct intent is a successful no-op; a latest CommonModules-only target is promoted. The invocation-start source-template bytes remain authoritative without a second disk check."

Dev: "Can two `vba-dev` processes both rebase from the same latest manifest and then replace each other's result?"
Domain Expert: "No. Serialize the final mutation window with one `ProjectManifestMutationLease` per canonical project. Reference commands acquire it only after long resolution work, then reload, rebase, and commit while holding it. CommonModules acquires it before the first source-file mutation and holds it through commit or recovery determination. Waiting is cancellable, and process death releases ownership without a stale marker permanently blocking later work."

Dev: "May `common-module add` or `update` save the manifest clone and copy plan it built before waiting for the lease?"
Domain Expert: "No. Treat the request as `CommonModulesMutationIntent`. After acquiring the lease, reload the latest valid manifest and partition the latest targets first. Installed-only Add and zero-target Update skip repository work. Otherwise capture a complete stable repository snapshot before deriving the final dependency, RequiredReferences, source-byte, destination, and manifest plan, and derive those facts only from the staged bytes; the earlier plan is only an early validation aid. Add reapplies the requested names to the latest selected document, and Update uses every latest installed set. Preserve unrelated changes, but fail before source mutation if the document disappeared or any latest authority cannot be validated. Do not fall back to the stale plan or enter an automatic retry loop."

Dev: "Should Ctrl+C stop CommonModules Add or Update after its first source file has been changed but before the manifest is committed?"
Domain Expert: "No. Check cancellation immediately before the first planned delete or copy. Before that `CommonModulesMutationCommitmentBoundary`, cancellation changes nothing. After it, latch and defer cancellation while completing the file plan and atomic manifest commit or established recovery-result determination. A successful commit remains success and adds `cancellationDeferred`; a transaction failure reports its real failure. Every plan without source-file mutation, including direct-request, reference-only, test-only, orphan-only, and complete no-op results, retains its normal atomic-commit or trusted-no-op cancellation boundary. An ordinary caller waits for this consistency-critical section; forced process loss follows crash recovery instead."

Dev: "Should the VS Code caller force-kill CommonModules Add or Update when cooperative cancellation has not completed after the ordinary grace period?"
Domain Expert: "No. The caller cannot safely observe whether `CommonModulesMutationCommitmentBoundary` has been crossed, so it sends cooperative cancellation and waits for the CLI to exit instead of applying its general command force-kill timer. During preflight the CLI may still force-terminate an owned Excel process after its bounded cleanup grace; once source mutation begins it defers cancellation through commit or recovery-result determination. Extension shutdown or other abrupt process loss remains crash recovery, not cooperative cancellation."

Dev: "May `common-module update` or `add --force` overwrite target source that changed after its in-lease plan, or copy directly from a repository changing during the operation?"
Domain Expert: "No. Capture the selected repository manifest, source, and form-sidecar bytes into invocation-owned staging, then prove every live input still matches before the first target mutation; use only that fixed snapshot afterward. Record every target's planned existence and raw bytes, and recheck the applicable precondition immediately before its delete, create, or atomic replace. Force authorizes only the observed target version. A pre-mutation conflict changes nothing; a conflict after earlier file changes preserves the external edit, fails without manifest commit, and reports partial paths and manual verification without automatic rollback. The unavoidable compare-to-mutation gap remains outside the guarantee."

Dev: "Does failure to delete a CommonModules mutation snapshot after a successful commit make the mutation fail?"
Domain Expert: "No. After releasing its handles, retry deletion for a bounded period. If only the invocation-owned, non-authoritative snapshot workspace remains, preserve exit zero and the complete result, add `commonModulesSnapshotCleanupFailed`, and identify the retained absolute directory. Before success is established, a retained snapshot is failure or cancellation diagnostic context rather than a partial success warning; uncertainty in source mutation, manifest commit, or recovery remains command failure."

Dev: "Does `ProjectManifestMutationLease` coordinate an independent editor that saves `vba-project.json`?"
Domain Expert: "No. Keep `VbaDev` independent from editor save state, but fingerprint the exact manifest bytes read inside the lease and compare them immediately before atomic replacement. An observed mismatch is a conflict and never gets overwritten: reference mutation fails unchanged, while CommonModules preserves the externally edited manifest, writes its planned recovery manifest, and reports that source files may already have changed. Do not claim protection from a non-cooperating write inside the final comparison-to-replace gap."

Dev: "How does another `VbaDev` process acquire `ProjectManifestMutationLease` without a stale PID permanently blocking the project?"
Domain Expert: "Exclusively open the canonical manifest's sibling `vba-project.json.vba-dev.lock` and hold that OS file handle, recording advisory owner metadata in the marker. Wait cancellably for at most 30 seconds, then fail with `manifestMutationBusy`; never force-unlock or kill the owner. Process death releases the handle, so an unowned leftover marker can be reused and later removed best-effort. Do not add a timeout option or manifest setting initially."

Dev: "May `VbaDev` edit Git exclusions so a crash-retained lease marker never appears as untracked?"
Domain Expert: "No. Keep the sibling marker for filesystem-scoped ownership, request delete-on-close where supported, and delete it safely on normal release. A harmless unowned marker that cannot be removed after successful mutation produces `leaseMarkerCleanupFailed`; it does not undo success. Document the optional `vba-project.json.vba-dev.lock` ignore rule, but do not create or edit `.gitignore` or `.git/info/exclude`, including during project creation."

Dev: "Does lease owner metadata copy the full command line or user environment into the project directory?"
Domain Expert: "No. Its schema records only a random lease ID, machine name, PID and process start time, stable command name without arguments, acquisition time, and tool version. Write and flush that complete advisory object after exclusive acquisition but before mutation; failure to establish it releases the lease and fails unchanged. Busy diagnostics use only readable fields and never treat metadata as ownership or stale authority."

Dev: "May `new excel` create its source directories and workbook before it acquires `ProjectManifestMutationLease`, since no manifest exists yet?"
Domain Expert: "No. Resolve the canonical target first and create its root only when needed to host the manifest-sibling marker. Acquire the lease before creating any project artifact, then revalidate that the manifest is absent and the root contains only the owned marker. Hold ownership through the initial atomic manifest move or, when failure or cancellation wins first, through rollback of unchanged in-target project artifacts. Release the lease before marker and empty-root cleanup, and determine the terminal result only after all cleanup has been classified; a concurrent creator waits and then fails instead of merging or overwriting the completed project."

Dev: "If `new excel` fails after creating its workbook, may it recursively delete the target root to undo the partial project?"
Domain Expert: "No. Finish non-Excel validation and the CommonModules copy plan first, then track every created artifact. Before initial manifest commit, first prove owned Excel release and, while retaining the lease, remove only unchanged command-created project artifacts inside the target. Then release the lease, remove only the invocation's still-unowned marker, and remove its empty target root and created ancestors leaf-to-root. Preserve unknown or externally changed content and every pre-existing directory. Only completely proved cleanup returns the original failure or exit `130` cancellation; uncertain release or any retained command-owned marker, artifact, or empty directory that should have been removable returns failure with `newProjectCleanupIncomplete`, every retained absolute path, and manual recovery guidance. An invocation-created directory retained solely because preserved foreign content makes it nonempty is expected `newProjectTargetChanged` state rather than cleanup failure. Initial manifest commit establishes success and is never rolled back by later cleanup trouble; only a post-commit marker unlink failure may retain success as `leaseMarkerCleanupFailed`."

Dev: "May direct `new excel --output` create a target whose intermediate parent directories do not exist?"
Domain Expert: "Yes. Record every missing directory created from the nearest existing ancestor through the target. Before manifest commit, rollback removes only invocation-created directories that remain empty, proceeding leaf-to-root and stopping at the first pre-existing or nonempty directory. Never remove a pre-existing directory merely because it is empty, and report any command-owned directory that cannot be removed as incomplete cleanup."

Dev: "Can a later `new excel` automatically finish or erase an initial project creation interrupted by process loss?"
Domain Expert: "No. The initial contract has no durable creation journal, automatic resume, or force cleanup, and lease metadata is not recovery authority. A root containing only an unowned lease marker remains reusable; any other content without a manifest blocks creation and is reported as possibly incomplete but not proven safe to delete. The user must inspect, move, or remove it or choose another output. An existing manifest means creation committed, so do not recreate it and direct health verification to Doctor instead."

Dev: "If a user or another tool adds an apparently unrelated file after `new excel` proves its target empty, may creation keep that file and commit the project?"
Domain Expert: "No. Immediately before the initial manifest move, recursively require the `InitialProjectTarget` to contain only the owned marker, owned temporary state, and every expected command-created artifact with its recorded type and raw bytes. Any foreign entry or missing, replaced, or changed artifact fails with `newProjectTargetChanged`; do not merge, adopt, overwrite, or retry. Roll back only unchanged command-owned artifacts and preserve every foreign or changed path. Foreign content alone, including the invocation-created root that must remain nonempty to preserve it, is not `newProjectCleanupIncomplete`; that code means owned rollback state or release trust also failed. When both occur, report both classifications and all retained paths. The narrow final-check-to-move race remains outside the guarantee, while the manifest move itself never overwrites."

Dev: "May `new excel` call `Workbooks.Add()` without a template argument and inherit the user's configured number of new sheets?"
Domain Expert: "No. Create the `ExcelProjectTemplateBaseline` with the explicit `xlWBATWorksheet` template constant so it contains exactly one empty worksheet. Do not add a bundled binary workbook or arbitrary initial-template option in the first contract, and do not promise byte-identical `.xlsm` serialization across Excel versions. Worksheet and VBA identity spelling are separate decisions."

Dev: "Should `new excel` let Excel choose localized worksheet and VBA names, or derive them from the manifest project name?"
Domain Expert: "Neither. Establish the locale-independent `InitialWorkbookIdentityBaseline`: `Sheet1` for the worksheet tab and document module, `ThisWorkbook` for the workbook document module, and `VBAProject` for the actual `VbaProjectName`. The manifest project name, document name, and workbook basename remain tooling and filesystem identities rather than implicit VBA rename requests. If the source template cannot establish those exact identities, fail before `InitialProjectCreation` commits."

Dev: "Should `new excel` always put Scripting Runtime and RegExp in the manifest, or copy every reference reported by its blank workbook?"
Domain Expert: "Neither. Build the `InitialVbaProjectReferenceSelection` from the baseline workbook's actual references in VBE order, but omit `Visual Basic For Applications` because its `VbaStandardLibraryReference` is always active and is not an unlisted protected-reference warning. Then traverse the committed initial CommonModules order and each row's requirement declaration order, appending only first-seen missing VBE-equivalent canonical names. Preserve an active baseline match's spelling, position, and direct intent. With no installed CommonModules requirement, do not add Scripting Runtime or RegExp, and never infer requirements by scanning source."

Dev: "Should a CommonModules-required reference used to probe VBE ambiguity remain saved in the initial source template?"
Domain Expert: "No. Keep the source template's actual baseline reference identities and order, and persist the complete intended selection in the manifest. Probe on a disposable copy or remove temporary additions, then prove the saved template returned to baseline before manifest commit. Build, test, and publish materialize the manifest selection into their copied workbook."

Dev: "Which CommonModules repository entries does `new excel` treat as directly requested?"
Domain Expert: "Every `runtime-baseline` and `test-foundation` entry is an initial root with `requested: true`, even when another root reaches it first as a dependency. Expand their complete dependency closure and mark an entry `requested: false` only when it is present solely because a root depends on it. Other categories do not select roots implicitly; a smaller initial foundation requires a future explicit template or profile."

Dev: "Can one repository entry combine several primary categories and let a test category take precedence?"
Domain Expert: "No. Require exactly one `CommonModulePrimaryRole`: `runtime-baseline`, `test-foundation`, `optional`, or `test-double`. Allow `public-udf` only as an additional modifier on either runtime role, so the complete valid category sets are the four single roles plus `runtime-baseline,public-udf` and `optional,public-udf`. Record `testOnly: true` exactly for the two test roles and false for the two runtime roles; the modifier never changes it. Any other set makes the complete repository snapshot invalid before project mutation."

Dev: "May a CommonModules manifest reader trim or recase `Categories` and `Dependencies`, or silently drop empty comma-separated items?"
Domain Expert: "No. Treat `Categories` as exactly one of `runtime-baseline`, `runtime-baseline,public-udf`, `test-foundation`, `optional`, `optional,public-udf`, and `test-double`, including casing and order. Treat `Dependencies` as either the zero-length cell or a whitespace-free comma-separated sequence whose items exactly match the spelling of target-row `ModuleFile` values. Preserve declaration order; reject leading, trailing, or repeated delimiters, whitespace padding, recasing requirements, `OrdinalIgnoreCase` duplicates, and self-dependencies rather than normalizing them."

Dev: "May comments and blank lines appear anywhere in `common-modules-manifest.tsv` because readers can ignore them?"
Domain Expert: "No. Permit only zero or more whole-line comments before the exact header; each begins with `#` in the first code unit and contains no tab, control character, or trailing whitespace. Follow the header with one or more contiguous data rows and then exactly one final CRLF. Reject blank or whitespace-only lines, indented or inline comments, comments after the header, a repeated or differently cased header, and extra trailing lines rather than normalizing them. A `#` inside a `RequiredReferences` JSON string remains ordinary cell data."

Dev: "May `ModuleFile` identify a repository subdirectory and rely on installation to flatten it?"
Domain Expert: "No. `ModuleFile` is the exact flat basename `<common module name>.bas`, `.cls`, or `.frm`, never a relative path. COLLECT finds every `vba-project.json` under the explicit `Collection Search Root`, resolves each document `sourcePath`, searches those source sets recursively for the basename, and selects the greatest `LastWriteTimeUtc`. At equal newest timestamps it compares only `Length`: equal lengths are treated as equivalent, preferring the CommonModules candidate when it is tied and otherwise using ordinal path order; different lengths use the CommonModules fallback. Collection writes the selected source unit at the distribution repository root, so source subdirectories do not change the flat output layout."

Dev: "How does COLLECT distinguish the canonical manifest from manifests in generated repositories?"
Domain Expert: "The canonical manifest is the one directly contained by the unique `CommonModules Authoring Source Set` discovered through a project document `sourcePath`. COLLECT writes to `common_modules_repo` directly under the `Wrapper Repository Parent`; neither the containing project's `commonModulesRepository` nor a generated repository manifest establishes wrapper output authority."

Dev: "Does a `tests` directory decide which CommonModules authoring source may be absent from the distribution manifest?"
Domain Expert: "No. Every file absent from the canonical manifest remains authoring-only regardless of directory and is not copied. Multiple project-source files matching one listed basename are normal candidates and use only their `LastWriteTimeUtc` and `Length` under the collection selection rule. The distributed repository remains a flat manifest-listed package."

Dev: "Does successful collection certify the complete CommonModules authoring project?"
Domain Expert: "No. It certifies package selection and the exact manifest-listed package inputs. All unlisted source remains authoring-only and is not a collection health target; `vba-dev build`, `vba-dev test`, and Doctor own its source validity."

Dev: "May COLLECT retain an old file in `common_modules_repo`, or may DIST distribute an extra entry not named by the manifest?"
Domain Expert: "No. A `CommonModulesRepository` is a closed flat package. COLLECT removes obsolete package entries, and DIST treats a missing listed entry, an unexpected entry, or a non-flat inventory as a `Distribution Global Failure` before touching any target."

Dev: "May collection combine manifest-listed files read before and after an authoring edit, or retry silently onto a newer generation?"
Domain Expert: "The current PowerShell implementation performs one ordinary discovery and newest-file selection pass before it starts copying. It does not provide a cross-file atomic snapshot or retry while files are being edited, so do not edit project sources during COLLECT; rerun COLLECT after an interrupted or concurrent edit."

Dev: "What determines the scope of distribution?"
Domain Expert: "The caller explicitly supplies the `Distribution Search Root`; DIST never infers it from the source repository. DIST reads its source from `common_modules_repo` directly under the `Wrapper Repository Parent`, while only existing repositories in immediate child project directories are opted-in targets."

Dev: "May COLLECT create a missing source repository, or may DIST search for one elsewhere?"
Domain Expert: "COLLECT may create the missing `common_modules_repo` under the `Wrapper Repository Parent`, but only after every preflight succeeds. DIST requires that same path to contain its existing source repository; if it is missing, DIST reports a `Distribution Global Failure` with exit `1` and neither searches for nor creates a substitute."

Dev: "What is the public argument surface of the COLLECT and DIST wrappers?"
Domain Expert: "Each `.ps1` and `.BAT` accepts exactly one first argument: its explicit Collection or Distribution Search Root. Resolve a relative argument against the `Wrapper Repository Parent`. A missing, empty, absent, non-directory, or unreadable argument is a global error with exit `1`; the explicit root itself may be a reparse path. Neither command infers its Search Root from the working directory, script location, source path, or output path, and neither accepts source or output overrides."

Dev: "How do the BAT wrappers preserve the PowerShell result?"
Domain Expert: "Invoke PowerShell with `-NoProfile`, capture `%ERRORLEVEL%` immediately after the child returns, and finish with `EXIT /B` using that captured value even when informational UI or `TIMEOUT` follows. Success and warning-only completion return `0`; every COLLECT global or copy failure and every DIST global or candidate failure return `1`, and the BAT layer never rewrites failure as success."

Dev: "How should focused tests cover the simplified COLLECT and DIST wrappers?"
Domain Expert: "Use temporary-directory PowerShell fixtures for required Search Roots and exit codes, COLLECT discovery, exclusions, manifest validation, newest-file selection and CommonModules fallback, closed-package update, DIST opt-in, warning skip, candidate continuation, and BAT exit preservation. An ordinary `UNCHANGED` fixture may use identical contents, timestamps, and lengths, but tests do not pin a deliberately different-content file with equal time and length. Do not add Excel, native API, lease, transaction, rollback, or state-machine test machinery."

Dev: "How does one immediate child project opt in to distribution?"
Domain Expert: "Zero case-insensitive `common_modules_repo` matches means no opt-in. Exactly one ordinal-exact ordinary directory is a `Distribution Target`; a case-only match, multiple matches, or a wrong entry type is a `Distribution Candidate Failure`, not absence or a target to repair."

Dev: "May the caller use the fixed central repository to distribute into an unrelated directory tree?"
Domain Expert: "Yes. The source repository under the `Wrapper Repository Parent` and the explicit `Distribution Search Root` are independent authorities. DIST excludes the source itself if it appears in that scope, but does not require its parent to be one of the root's children."

Dev: "Must the `Distribution Search Root` be a physical non-reparse directory?"
Domain Expert: "No. An explicitly supplied junction or symbolic-link path is a valid search authority when it resolves and can be enumerated. DIST does not promise physical-identity continuity if that link is retargeted during the invocation; repository roots and package contents retain their separate no-reparse rule."

Dev: "Does DIST descend into an immediate child project that is a junction or symbolic link?"
Domain Expert: "No. As with recursive COLLECT discovery, the explicitly supplied Search Root may itself be an alias, but a reparse child encountered during discovery is a `Distribution Warning Skip`. Pass that link itself as another explicit Search Root when its target should be processed."

Dev: "May source files, targets, or reparse links be changed concurrently while DIST runs?"
Domain Expert: "No continuity guarantee is provided for concurrent changes. Run DIST against a stable source and search tree; an alias of the central source ordinarily remains `UNCHANGED` through its matching names, lengths, and timestamps, but link retargeting or filesystem edits belong to a later invocation."

Dev: "Does recursive COLLECT discovery follow a junction or symbolic-link child found below its Search Root?"
Domain Expert: "No. The explicit `Collection Search Root` itself may be an alias, but discovery does not descend into a reparse child. To include the linked tree, invoke COLLECT separately with that link as the explicit root."

Dev: "Does COLLECT search generated or administrative directory trees for projects?"
Domain Expert: "No. Discovery excludes `.backups`, `.git`, `.out-of-scope`, `.tmp`, `.venv`, `.vs`, `.vscode-test`, `artifacts`, `bin`, `common_modules_repo`, `node_modules`, `obj`, `out`, `packages`, `publish`, `temp`, `TestResults`, and their descendants. `TestResults` is ordinary VSTest output, not a project source location."

Dev: "May COLLECT warning-skip a discovered project manifest or source set that it cannot read or validate?"
Domain Expert: "No. An unreadable or invalid `vba-project.json`, an invalid or unresolvable document `sourcePath`, or an absent or unreadable resolved source directory is a collection-global failure before output mutation. Skipping one could hide the newest module candidate and produce a stale central package."

Dev: "May COLLECT follow a reparse-point `vba-project.json` or canonical `common-modules-manifest.tsv`?"
Domain Expert: "No. Both authority files must be ordinary non-reparse files. Discovering either canonical basename as a symbolic link or another reparse file is a collection-global failure before output mutation, not a warning skip or authority to follow the link."

Dev: "May authority-file discovery silently ignore case-only basename differences?"
Domain Expert: "No. Require the ordinal-exact basenames `vba-project.json` and `common-modules-manifest.tsv`, while using a case-insensitive directory check to detect case-only or multiple matches. Either defect is a collection-global failure rather than an absent project or manifest."

Dev: "May one document source set contain several files with the same common-module basename?"
Domain Expert: "No. A case-insensitive basename duplicate within one resolved `sourcePath` is an ambiguous source-set identity and makes collection fail before output mutation. Equal basenames in different source sets are normal cross-project candidates and remain eligible for newest-`LastWriteTimeUtc` selection."

Dev: "Must a module candidate's basename casing exactly match the manifest `ModuleFile`?"
Domain Expert: "No. Accept exactly one case-insensitive basename match per source set and write the selected unit using the manifest's ordinal-exact `ModuleFile` casing. A second case variant in the same source set remains the prohibited duplicate rather than another candidate."

Dev: "How does COLLECT select a manifest-listed form that has an `.frx` sidecar?"
Domain Expert: "Treat the `.frm` and its optional same-directory, same-basename `.frx` as one candidate. Its selection time is the later `LastWriteTimeUtc` of the pair, and the winning pair is copied together; an orphan `.frx` is not a candidate. At equal newest times, equal sidecar presence and corresponding `.frm` and `.frx` lengths are treated as equivalent; any difference uses the CommonModules fallback. No form content or hash is read."

Dev: "Does COLLECT need staging, a transaction, or rollback for the central package?"
Domain Expert: "No. Complete manifest validation, project and source-set discovery, candidate selection, and tie validation before changing the central repository. If the package is not `UNCHANGED`, clear every existing repository entry, then copy the canonical manifest and every selected source unit and `.frx` sidecar sequentially with ordinary PowerShell. A deletion or copy error returns exit `1` and may leave a partial package, without differential patching, staging, retry, transaction, or rollback."

Dev: "Does COLLECT lock or recheck source files while materializing the selected package?"
Domain Expert: "No. Discover projects and read candidate `LastWriteTimeUtc` and `Length` once, then use ordinary sequential PowerShell copy without a source lock, snapshot, post-copy verification, or concurrency guarantee. A selected source that disappears or causes copy failure returns exit `1` and may leave a partial package; a normal copy return succeeds, and later edits belong to a later invocation against a stable tree."

Dev: "Does COLLECT compare file contents before updating the central package?"
Domain Expert: "No. Candidate selection and package matching use only exact ordinal filenames, `LastWriteTimeUtc`, and `Length`; they never read file contents or hashes. If the central package has the selected package's exact filename inventory and every corresponding time and length, report `UNCHANGED` and perform no write; otherwise update it."

Dev: "Must a document source set contain the canonical CommonModules manifest to participate in collection?"
Domain Expert: "No. Every document `sourcePath` resolved from a discovered valid `vba-project.json` is a candidate source set. Exactly one of those source-set roots must directly contain `common-modules-manifest.tsv`; that source set is `CommonModules Authoring Source Set`, while zero or multiple manifest-owning source sets make collection fail before output mutation."

Dev: "How does COLLECT resolve and deduplicate document `sourcePath` values?"
Domain Expert: "Resolve each relative value against the directory containing its `vba-project.json`, normalize it to an absolute full path, and compare those paths with Windows case-insensitive semantics. Scan equal normalized paths once even when several documents reference them. Do not add physical-file identity, native handles, or alias resolution merely to deduplicate source sets."

Dev: "May an explicitly resolved document `sourcePath` itself be a junction or symbolic link?"
Domain Expert: "Yes. Treat that manifest-resolved root like the explicit Collection Search Root and scan it, but do not descend into a reparse child directory encountered below it. Do not resolve physical alias identity, and require a stable link target for the duration of the invocation."

Dev: "May COLLECT use a reparse-point `.bas`, `.cls`, `.frm`, or `.frx` as a module candidate?"
Domain Expert: "No. Source units and form sidecars must be ordinary non-reparse files. A required CommonModules source unit or sidecar that is a reparse point is a collection-global error; a matching non-CommonModules reparse candidate produces a warning and uses the CommonModules fallback without following the link."

Dev: "What happens when ordinary newest-candidate selection cannot be completed?"
Domain Expert: "Every file listed by `common-modules-manifest.tsv` is mandatory in `CommonModules Authoring Source Set`: a missing file or unavailable `LastWriteTimeUtc` or `Length` makes collection fail before output mutation. A missing candidate in another project is ordinary. If another project's `LastWriteTimeUtc` or `Length` cannot be read, or equal newest timestamps have different lengths, select the CommonModules candidate instead of failing and ignore the uncertain non-CommonModules candidates. Equal newest timestamps and lengths are treated as equivalent, preferring CommonModules when it is tied and otherwise using ordinal path order."

Dev: "Does using the CommonModules fallback produce a warning or an error?"
Domain Expert: "A missing candidate in another project produces no warning. An unreadable `LastWriteTimeUtc` or `Length` in another project, or equal newest timestamps with different lengths, produces a warning naming the affected module and then uses the CommonModules candidate; fallback warnings alone retain exit `0`. A missing CommonModules candidate or unreadable required CommonModules attribute is a collection-global error with exit `1`."

Dev: "Where does COLLECT create or update its `CommonModulesRepository`?"
Domain Expert: "Capture the invocation-start working directory once as the `Wrapper Repository Parent` and use its direct `common_modules_repo` child. Do not consult any project manifest's `commonModulesRepository` value or retarget the output after an internal location change. A missing path may be created as an ordinary directory only after every preflight succeeds. An existing path must be an ordinary non-reparse directory whose entries, `LastWriteTimeUtc` values, and lengths are readable; a file, reparse point, enumeration failure, or unavailable required attribute is a collection-global failure before output mutation. The CommonModules project source set, not the generated repository, supplies the mandatory baseline and fallback candidates."

Dev: "Does DIST keep discovering targets while distribution is running?"
Domain Expert: "No. It fixes one path-ordered `Distribution Candidate Set` at invocation start. A later target waits for the next invocation, while a selected target that disappears before mutation is warning-skipped without rediscovery, retry, or retargeting."

Dev: "Must DIST compare file content before deciding that a target is unchanged?"
Domain Expert: "No. A `Distribution Package Match` uses exact flat inventory names and each corresponding file's exact `LastWriteTimeUtc` and length. Matching metadata is accepted without reading bytes or hashes; any inventory, timestamp, or length difference makes the target eligible for replacement."

Dev: "Must DIST reread copied files to prove their timestamp and length after `Copy-Item` returns normally?"
Domain Expert: "No. A normal copy return completes that candidate without an additional verification pass. A later DIST invocation applies `Distribution Package Match` again and replaces the target if its metadata does not match."

Dev: "Does distribution require a native lease, managed state machine, transactional workspace, rollback protocol, or custom cancellation channel?"
Domain Expert: "No. Keep the wrapper as a plain Windows PowerShell file-copy workflow. A `Distribution Candidate Failure` leaves that candidate unchanged or possibly partial, records final failure, and continues to later candidates; a `Distribution Global Failure` stops the invocation. Correct the reported problem and rerun rather than attempting automatic rollback or recovery."

Dev: "If an I/O error occurs while DIST is copying one target, does it always have the same scope?"
Domain Expert: "No. Failure to enumerate or read the central source is a `Distribution Global Failure` because no later copy can trust that package. Failure to remove or write one target is a `Distribution Candidate Failure`, so later targets remain eligible."

Dev: "May DIST follow a junction, symbolic link, or other reparse point while inspecting or replacing a repository?"
Domain Expert: "No. A reparse point at the central source root or in its package inventory is a `Distribution Global Failure`. A reparse point at a target root or anywhere below it is a `Distribution Candidate Failure`; DIST leaves that target untouched and continues without following the entry."

Dev: "Is every access failure a global distribution failure?"
Domain Expert: "No. Failure to enumerate the `Distribution Search Root` is global. Inability to determine whether one immediate child project opted in is a `Distribution Warning Skip`; once an exact target is admitted, inability to enumerate or compare its contents is a `Distribution Candidate Failure`."

Dev: "Does DIST need result rows, counts, summaries, or a JSON result envelope to report these classifications?"
Domain Expert: "No. Report updated and unchanged targets as ordinary messages, `Distribution Warning Skip` as a warning, and candidate or global failures as errors. Warnings alone retain exit `0`; any failure produces exit `1` without adding another result protocol."

Dev: "When is an invocation with no updated or unchanged target a zero-target error?"
Domain Expert: "It is a zero-target error only when initial discovery finds neither an eligible target nor a known candidate failure. Known candidate failures already make the invocation fail without another zero-target error; a target admitted initially and warning-skipped after disappearing does not retroactively create one. Warning-only uncertainty that admits no target still accompanies the zero-target error."

Dev: "What focused behavior must the distribution tests preserve?"
Domain Expert: "A nonempty source is copied to every eligible immediate-child target, stale target contents are removed, the source itself is not modified, and missing target directories are not created. Tests also distinguish candidate-local failure, pre-mutation warning skip, global failure, and the initial zero-target error without adding transaction or rollback behavior."

Dev: "If a repository Update changes only a CommonModule's canonical casing, should the installed spelling remain unchanged?"
Domain Expert: "No. It remains the same `CommonModuleName` under `OrdinalIgnoreCase`, but Update refreshes the installed manifest `name` and `moduleFile`, source basename, optional `.frx` basename, and source bytes to the repository spelling while retaining the containing directory and ordinary `requested`, `testOnly`, and `orphaned` rules. Plan that recasing as part of the existing source-unit mutation and fail before mutation on a destination conflict; do not classify it as orphaning, a new module, or a substantive source-identity change."

Dev: "May a runtime CommonModule depend on a test-only CommonModule if build and test can still materialize both?"
Domain Expert: "No. Reject every direct dependency from `testOnly: false` to `testOnly: true`, because publish would retain the dependent while excluding what it needs. Runtime-to-runtime, test-to-runtime, and test-to-test dependencies remain valid; direct-edge validation also proves the transitive rule. Collection and every repository-backed mutation validate the complete repository before project mutation, while build and publish do not acquire a new repository-consistency check."

Dev: "Must dependency-first ordering reject a cycle such as `ObjectList` and `ObjectSet`?"
Domain Expert: "No. Collapse every maximal mutually reachable set into one `CommonModuleDependencyComponent`. Order dependency components before their dependents, enumerate a component's outgoing dependencies by repository member order then each member's declaration order with first occurrence winning, and order members inside the component by repository row. A selected member brings in the whole component but keeps per-entry direct intent. Reject self-dependency; the runtime-to-test rule rejects a mixed-classification cycle. Existing installed positions remain stable when a mutation merges this canonical closure, while newly discovered entries follow the component order."

Dev: "Should `new excel` alphabetize its initial CommonModules roots?"
Domain Expert: "No. Traverse direct roots in repository row order and expand the `CommonModuleDependencyComponent` graph depth-first in its canonical outgoing-dependency order, placing dependency components first and each component's members in repository row order. Keep only the first position for each case-insensitive identity; a later direct-root encounter updates final requested intent without moving it. Use that same order for source copy, manifest, text, and JSON."

Dev: "May `new excel` read a repository manifest once and then copy each live CommonModules source file later?"
Domain Expert: "No. After owning the project lease and proving initial-target safety, capture and validate one complete stable repository snapshot before creating project artifacts. Derive roots, dependencies, reference requirements, classifications, copied bytes, and manifest state only from that snapshot. A change during capture fails unchanged; a later change belongs to another invocation, and automatic retry must not mix generations."

Dev: "Should `new excel --format json` return only the created project path?"
Domain Expert: "No. Return one schema `1.0` result envelope only after the initial project and mandatory ownership cleanup are trusted, and nest the exact committed schema-`1` `ProjectManifest` rather than inventing parallel document, CommonModules, or reference projections. Keep only the project root and manifest path absolute at the envelope level; preserve manifest-relative paths and ordered arrays exactly as committed. Failure and cancellation return no partial success object, and guided consumers do not run follow-up list commands to reconstruct the outcome."

Dev: "May guided creation ignore a new property anywhere inside a successful `new excel` result?"
Domain Expert: "Only in the additive-open command envelope and warning objects, where unknown additions cannot weaken known invariants. Every warning still requires nonempty string `code` and `message` properties. Recognized codes are unique and must retain their exact message, state relationship, and relative order; filter out unknown codes and validate that recognized subsequence. Preserve every unknown warning in received order, include it in the displayed warning count, and never deduplicate, sort, recase, or reinterpret it. The nested manifest is the closed, exact-case schema-1 `ProjectManifestSchema`; an unknown property there makes even an exit-zero result untrusted."

Dev: "Should a newly created manifest persist every CLI default that applied during creation?"
Domain Expert: "No. Omit `commandDefaults` until a caller records an explicit durable override; do not pin the redundant `test.format: \"text\"`. Include `commonModulesRepository` only when a repository was selected, but always include each document's complete `commonModules` and `references` arrays even when either selection is empty."

Dev: "Is the canonical `ProjectManifest` representation also a strict reader requirement?"
Domain Expert: "No. Writers and recovery artifacts normalize to UTF-16LE with BOM, two-space indentation, CRLF throughout, stable schema property order, and exactly one trailing CRLF. Readers may accept the supported encoding forms and semantically irrelevant whitespace or property ordering; canonical formatting owns rewritten bytes, not manifest validity."

Dev: "Which degraded `new excel` outcomes remain successful warnings?"
Domain Expert: "Only a conclusively absent canonical CommonModules repository, a retained non-authoritative CommonModules snapshot workspace after committed creation and proved handle release, and failure to remove an unowned lease marker after project creation and lease release are schema-`1.0` success warnings, in that fixed order. Snapshot deletion receives bounded retries and reports its normalized absolute retained path; before commit, retained staging belongs to failure or cancellation cleanup and never becomes a success warning. An absent repository commits no repository selection or CommonModules and keeps only baseline references. Repository uncertainty or invalidity, target conflict, incomplete rollback, busy ownership, reference-resolution failure, and unproved Excel or lease release are command failures. Cancellation before manifest commit rolls back; a late request after commit does not replace success or create a deferred-cancellation warning."

Dev: "Should default `new excel` text output remain a one-line project-path message?"
Domain Expert: "No. Print a human creation receipt with absolute project and manifest paths, the document and its project-relative source, template, build-target, and publish-target paths, every CommonModule and reference in committed order with its requested or dependency provenance, and a derived count summary. Label bin and publish as targets because they do not exist yet, show `(none)` for an empty collection, and keep warnings on stderr. Machine consumers use JSON instead."

Dev: "May guided creation trust exit code zero without validating the `new excel` result, or run follow-up commands to reconstruct it?"
Domain Expert: "Neither. Validate the schema, request-matching project and manifest path, envelope-to-manifest agreement, and every internally provable initial-state invariant, while accepting unknown additive properties and warning codes. Do not duplicate dependency or reference resolution, reread the manifest for outcome discovery, or run List or Doctor. A malformed or mismatched exit-zero result may follow a real commit, so report completed-but-untrusted with Show Output and do not claim success, open, retry, roll back, delete, or fall back."

Dev: "How far should guided creation inspect the nested initial manifest before trusting it?"
Domain Expert: "Require the submitted name and root, sole Excel document, exact conventional paths, omitted initial command defaults, complete unique CommonModule and reference arrays, initial `orphaned: false`, and the canonical repository-warning relationship. Do not reread files or duplicate CommonModules closure, baseline-reference, registry, or VBE resolution; those facts come from the CLI's exhaustive projection."

Dev: "Should guided creation wait until `new excel` has begun before discovering that Excel or Trust Center access is unavailable?"
Domain Expert: "No. Before asking for a name or folder, run cancellable `vba-dev doctor --scope environment --format json` without project discovery. Require complete trusted passes for Windows, Excel COM, actual `VBProject` access, and owned-process cleanup. Failure, unverified state, or invalid output ends the flow with Open Setup Instructions, Retry, and Show Output but no Run Anyway; cancellation is silent. Do not run debug-adapter Doctor or modify Trust Center, and let `new excel` validate its actual creation process again."

Dev: "Which environment Doctor checks constitute a successful guided-creation preflight?"
Domain Expert: "Require exactly one each of `platform.windows`, `excel.comStartup`, `excel.processOwnership`, `excel.vbideProjectAccess`, and `excel.processCleanup`, in that order. Blocked checks remain visible as `skipped`, and cleanup is still attempted after any started Excel process even when VBIDE access failed. Bind to these IDs rather than messages, let unknown additive checks coexist without substituting for them, and proceed only when every required check and the overall complete result pass."

Dev: "How can guided creation prove that a Doctor result came from its environment-only request?"
Domain Expert: "Require `scope: \"environment\"` and `project: null` in `vba-dev doctor` schema `1.0`; normal project Doctor instead reports `scope: \"project\"` and its canonical absolute root, without a selected document. Reject a mismatched pair before reading checks. Keep the independent debug-adapter Doctor schema unchanged."

Dev: "Where should Open Setup Instructions lead when different environment checks can fail?"
Domain Expert: "Always open a rendered Markdown preview of the installed extension's version-matched README at Getting Started > 2 - Prepare Excel in the current window. Keep that bundled page usable offline and let it cover Windows, desktop Excel, trusted VBIDE access, and process troubleshooting, with optional user-selected links to Microsoft details; keep exact failed IDs and evidence in Output rather than routing to separate pages. Opening it changes no setting and starts no Retry, while a preview-navigation failure reports its own retryable action error without changing preflight state."

Dev: "How should guided creation summarize a blocking environment preflight?"
Domain Expert: "Use one warning saying `Excel VBA project prerequisites need attention.` for a trusted complete overall warning; one error saying `Excel VBA project prerequisites are not ready.` for a trusted failure, unverified result, or incomplete result; and one error saying `VBA Tools could not verify Excel VBA project prerequisites.` for missing, invalid, inconsistent, or abnormally terminated output. Offer Open Setup Instructions, Retry, then Show Output, but put check details only in unopened Output. Treat contradictory aggregation as untrusted, and keep explicit cancellation silent."

Dev: "If a preflight cancellation races with its terminal result, which one wins?"
Domain Expert: "The local request always prevents project input and caching, then waits for child close. Exit `130`, or a late trusted complete pass or warning after that request, ends silently even when delivery failed. An untrusted exit-zero result, operational failure, other nonzero result, or abnormal termination still shows its ordinary blocking notification so cancellation cannot hide integrity or cleanup trouble. Exit `130` is silent even without a recorded local request."

Dev: "Must every guided creation repeat that environment preflight?"
Domain Expert: "No. Reuse only a schema-valid, trusted, complete overall pass whose required environment checks all passed, and bind it to the current `CompanionExecutableResolution` result for this window's Extension Host activation. Do not identify it by executable path, `toolVersion`, or `contractVersion`; any new resolution result invalidates it even when those values are unchanged. Never persist or share it, and never reuse a warning, failure, unverified or skipped required check, incomplete or cancelled run, or invalid output. Retry first discards any reusable pass and always starts a fresh Doctor. `new excel` still validates its actual Excel and VBIDE process, so a later environment change fails safely."

Dev: "Does leaving or completing guided project input consume its reusable environment pass?"
Domain Expert: "Not by itself. Preserve the pass after name, folder, or pre-invocation path cancellation, a trusted creation success, or exit `130` with proved rollback and cleanup. Invalidate it after every creation failure, abnormal termination, or untrusted success because stderr is not classified and environment degradation cannot be excluded. A creation success retains but never refreshes the Doctor evidence; the existing activation, executable-resolution, Retry, and window boundaries still apply."

Dev: "Where should a user start guided project creation?"
Domain Expert: "Expose command ID `vbaTools.newExcel` as `VBA Tools: Create Excel VBA Project`. Keep it visible in an Empty Window, an ordinary workspace, a workspace that already contains projects, and Restricted Mode, with no initial `when` clause. A trusted invocation runs the environment preflight; it does not require an active project or let the extension automate Excel itself."

Dev: "May guided creation resolve or launch `vba-dev` while the current window is in Restricted Mode?"
Domain Expert: "No. Declare limited untrusted-workspace support so source viewing and language assistance that executes no workspace VBA and launches neither `vba-dev` nor Excel remain available, but restrict `vbaTools.devtool.path` and block every managed CLI, Excel/VBIDE, Doctor, debug-adapter, and vba-dev-terminal launch before executable resolution. `VBA Tools: Create Excel VBA Project` stays discoverable and shows `Excel VBA project creation is unavailable in Restricted Mode because it starts vba-dev and Microsoft Excel. Trust this workspace or run the command from a trusted Empty Window.` with Manage Workspace Trust and Open Empty Window. Neither action resumes creation; dismissal does nothing, no Output exists, and a later trusted invocation performs fresh CompanionExecutableResolution and Doctor after discarding any prior resolution and guided-preflight pass. Gate each invocation from the current trust value; VS Code exposes a grant event but no in-process revocation event, so a reload or extension stop that removes trust while a child is running remains established abrupt caller loss rather than invented cooperative cancellation."

Dev: "Can the same window start another guided creation while one is awaiting input or running?"
Domain Expert: "No. Treat preflight and Retry, every input cycle, creation, and terminal classification as one window-scoped single-flight workflow. A second command invocation only says `Excel VBA project creation is already in progress in this window.` with no action, focus change, queue, or second command. Release ownership at cancellation or terminal classification, not when a later notification is dismissed; other windows remain independent and same-target serialization belongs to the project lease."

Dev: "How does guided creation choose the project name and output path?"
Domain Expert: "After preflight, ask for the project name and then its parent folder. Invoke `new excel` with an explicit name and the exact child root `<parent>/<project-name>`, aligning the folder, manifest project, primary document, and workbook basenames. These are tooling and filesystem identities, not `VBProject.Name` or module identities. Direct CLI callers may still choose independent `--name` and `--output` values."

Dev: "Does direct `new excel` treat an empty or whitespace-only `--name` as though the option were omitted?"
Domain Expert: "No. Only an absent option requests derivation. Preserve and validate every explicitly supplied spelling without trimming; derive an omitted name from the normalized output root or invocation-start working directory's final non-root component, and fail rather than inventing a name when that component does not exist. An absent output uses the working directory or its named child according to whether `--name` is present, while an explicitly empty output is invalid rather than defaulted."

Dev: "If `--output` reaches a target through a junction or symlink, should `new excel` replace the displayed path with its physical path?"
Domain Expert: "No. Preserve the lexically normalized `RequestedProjectRoot` in text, JSON, and navigation, but use the filesystem-canonical `ProjectRootIdentity` for lease, collision, ownership, and safety. Alias paths to one target share one identity; if an absent target's identity cannot be established from an existing ancestor or remains unproved at commit, fail rather than expose or persist a guessed physical path."

Dev: "Does each project manifest need a `$schema` field or a workspace setting before VS Code can validate it?"
Domain Expert: "No. The installed extension associates its bundled draft-07 `ProjectManifestSchema` only with the exact canonical `vba-project.json` basename. Keep schema-1 object properties closed and case-sensitive, leave recovery artifacts unmatched, and keep byte encoding, identity uniqueness, cross-field relationships, and other domain validation under `VbaDev` authority."

Dev: "Is the project name a VBA identifier, and may the UI silently fix an invalid name?"
Domain Expert: "No to both. `ProjectNameLexicalContract` preserves the exact well-formed UTF-16 spelling without Unicode normalization, trimming, sanitization, or case conversion. It rejects either-end Unicode `White_Space`, Unicode control ranges, isolated surrogates, dot segments, Windows-invalid basename characters, a trailing dot, and the complete Windows reserved-device set including superscript COM/LPT digits and extension-like suffixes. It allows internal whitespace, valid surrogate pairs, and non-control format characters. `new excel` then applies `ExcelWorkbookPathContract`, so `[` and `]` are rejected inline as Excel incompatibilities rather than host-neutral name errors. MS-VBAL identifier syntax and the 31-code-point module-name limit do not apply."

Dev: "Should guided creation reject every path over `MAX_PATH` or inspect an existing target before invoking `new excel`?"
Domain Expert: "Neither. Before invocation, lexically normalize each derived source-template, bin, and publish workbook absolute path without symlink or short-name substitution. Reject `[` or `]` in any component and require at most 218 UTF-16 code units across the drive or UNC prefix, separators, basename, and extension, excluding a terminator; do not add a generic 260-character rule or extended-path workaround. The Extension and CLI are pinned to one versioned validation-vector corpus, while `new excel` remains authoritative for the complete path plan and target eligibility under `ProjectManifestMutationLease` and `InitialProjectTarget`."

Dev: "Does requiring `new excel` result schema `1.0` prove that guided creation and the CLI validate input identically?"
Domain Expert: "No. Require `featureVersions[\"projectCreation.pathValidation\"] == \"1.0\"` separately for exact name rules, Excel brackets, path measurement, reason precedence, and shared vectors. The command schema versions only its successful result; UI wording and project mutation belong to their own contracts."

Dev: "If one project name violates several lexical or Excel path rules, may the Extension and CLI report different errors?"
Domain Expert: "No. `ProjectNameLexicalContract` and `ExcelWorkbookPathContract` each return their first failure under one fixed cross-runtime precedence. The name InputBox shows that reason directly; a parent-only bracket failure offers another parent but not a name change, while a length failure may offer either. CLI stderr may expose the same stable reason for support, but it is not a failure schema and the Extension never parses it."

Dev: "Does guided creation need a custom wizard with Back navigation and a final confirmation page?"
Domain Expert: "No. Use an empty Project Name InputBox with `MyProject` as a nonpersistent placeholder, inline validation, then the VS Code standard `Select Parent Folder for \"{name}\"` dialog whose action is `Create Here` and whose initial location prefers the active resource's `file:` workspace or the sole `file:` workspace. Cancel or Escape at either input silently ends the flow rather than acting as Back. A parent-only Excel bracket rejection preserves the name and offers another parent; a path-length rejection preserves both inputs and permits changing either one. After a changed name becomes valid, revalidate the retained parent directly. Save no input history outside this invocation and start cancellable creation immediately after valid input, without an overwrite-style confirmation."

Dev: "What does the Project Name InputBox tell the user before any value exists?"
Domain Expert: "Title it `Create Excel VBA Project`, explain that the project folder, document, and workbook use the entered name, and show `MyProject` only as a placeholder. Do not show an initial blank error; begin blocking validation when blank is accepted or editing starts, preserve spelling exactly, and never imply that VBA project, worksheet, or host-component identities are renamed with it."

Dev: "Can guided creation turn a remote or virtual workspace URI into the `--output` path?"
Domain Expert: "No. Accept only a `file:` URI that yields a Windows drive or UNC path reachable by the local CLI. Ignore non-file resources when choosing the dialog's initial location, but keep the command available so a local parent can still be selected. If a non-file URI is returned, retain the name and offer another parent or cancellation without starting `VbaDev`; direct CLI input likewise accepts paths rather than URIs."

Dev: "Should guided creation parse CLI logs into live stages or close progress as soon as cancellation is requested?"
Domain Expert: "Neither. Show an indeterminate `Checking Excel VBA project prerequisites` notification only for an uncached preflight, then a separate indeterminate `Creating Excel VBA project \"{name}\"` notification after valid input. Capture command output without opening it, but infer no percentage or stage from prose; a future stage feed needs a versioned structured-progress contract. After Cancel, say `Cancellation requested; waiting for vba-dev to finish…`, ignore repeats, use the established delivery-failure message when necessary, and keep progress open through child close and authoritative terminal classification."

Dev: "Should guided creation automatically switch the current VS Code window to the new project?"
Domain Expert: "No. Keep the current window and workspace unchanged. Compare the trusted result's displayed `RequestedProjectRoot` and `manifestPath` lexically as local `file:` URIs against current local workspace folders, using case-insensitive Windows component boundaries. If the manifest is contained, offer `Open Manifest`; otherwise offer `Open Folder in New Window`. Do not resolve junctions, symbolic links, short names, alternate UNC spellings, or remote and virtual workspace folders for this navigation-only choice, so a physical alias with different spelling remains outside. Dismissal does nothing, and creation does not automatically add a workspace folder, open or reveal files, or run Doctor, Build, or List."

Dev: "If the user selects the post-creation location action and VS Code cannot open it, did project creation fail?"
Domain Expert: "No. Preserve the trusted creation outcome and report one separate navigation-action failure with Retry and Show Output. Retry only the same open request; never rerun creation, modify the project or workspace, or silently fall back to another navigation. Dismissal leaves the created project untouched."

Dev: "Should creating a project suppress or immediately trigger the existing first-run Doctor prompt?"
Domain Expert: "Neither. A manifest created in the current window does not trigger the activation-scoped prompt, and guided creation changes none of its workspace state. If the user explicitly opens an outside project in a new window, that window may offer its ordinary one-time combined Doctor prompt. The environment preflight did not establish completed-project or debug-adapter health, and Doctor still requires explicit consent."

Dev: "Should a trusted guided-creation result show separate success and warning notifications?"
Domain Expert: "No. Show exactly one information notification for warning-free success or one warning notification containing `Created Excel VBA project \"{projectName}\".` followed by the grammatical nonzero CLI warning count. Always offer the applicable location action, add Show Output only when warnings exist, and never auto-open Output. If cancellation delivery also failed, append `Cancellation request could not be delivered.` without incrementing that count; when the CLI count is zero, omit `0 warnings.` and use only the created-project sentence plus that transport sentence. Keep paths and module or reference counts in Output and JSON rather than the toast."

Dev: "May an exit-zero but untrusted guided-creation result use the normal success notification or navigation action?"
Domain Expert: "No. Show one error with exact text `Excel VBA project creation may have completed, but its result could not be verified. Inspect the target and VBA Tools Output.` and only Show Output. Claim neither success nor failure, expose no navigation, Retry, rollback, deletion, or follow-up inspection action, and do not suppress independent manifest observation."

Dev: "If guided creation requested cancellation, may the extension silently treat any later process result as cancelled?"
Domain Expert: "No. Exit `0` remains trusted committed success even when cancellation arrived after the initial manifest move. Exit `130` alone means cancellation won before commit and complete rollback plus Excel and lease release were proved; it has no success JSON and guided creation stays silent. Exit `1`, any other nonzero or abnormal termination, and especially `newProjectCleanupIncomplete` remain failures with one error and Show Output even if cancellation was requested. The caller's cancellation flag requests cooperation but is not terminal-outcome authority."

Dev: "Should `new excel --format json` return a failure object that guided creation can interpret?"
Domain Expert: "Not in schema `1.0`. Keep stdout as the trusted complete-success channel only; cancellation and every failure leave it empty and write diagnostics to stderr. Preserve stable diagnostic spellings for support, but do not parse them in the extension, and include retained absolute paths plus manual-recovery guidance when cleanup is incomplete. Add a separately versioned failure envelope only when it can prove unchanged-target or complete-rollback state for a safe workflow."

Dev: "How should `newProjectCleanupIncomplete` tell a user what may remain?"
Domain Expert: "On stderr, show the original failure, the cleanup-incomplete code and summary, the user-visible `RequestedProjectRoot`, then every conclusively known retained absolute path in stable case-insensitive order and manual inspection guidance. If the retained set cannot be bounded, do not guess: direct inspection of the whole target before retrying. Tell users to move or remove only independently verified-safe content, and give guided creation no parser, target-opening shortcut, or deletion action."

Dev: "After `new excel` has started and returns a failure, should guided creation keep the inputs and offer Retry?"
Domain Expert: "No. Show one `Excel VBA project creation failed for \"{name}\".` error whose only action is Show Output. Do not infer safety from stderr, retain inputs, reopen the wizard, retry, delete the target, or run a repair command automatically: the target may be partial, externally changed, or ownership may be uncertain. Retry remains valid for the artifact-free preflight, and Change Name or Choose Another Parent remains valid before CLI invocation; creation retry needs a future machine-readable proof of an unchanged target or complete rollback."

Dev: "May System.CommandLine or the VS Code caller force-terminate `new excel` after an ordinary cancellation grace period?"
Domain Expert: "No. Keep System.CommandLine termination handling cooperative without its two-second forced completion, and exempt guided creation from the caller's command force-kill timer just like CommonModules mutation. The CLI may force-terminate only its owned Excel process after the established five-second cleanup grace, then must finish project rollback and lease classification before returning `130` or failure. Only extension, terminal, process, or operating-system loss enters abrupt creation recovery."

Dev: "How does an extension-spawned `vba-dev` command request cooperative cancellation without force-terminating the CLI?"
Domain Expert: "After confirming versioned capability support, opt into the hidden caller-neutral `--cancellation-transport stdin-v1` channel and write the exact UTF-8 `cancel\n` frame once. Receipt idempotently cancels the command action's token, while EOF alone means no cancellation and cannot establish exit `130` because caller loss closes the same pipe. Without that option the CLI does not read stdin and terminal users keep Ctrl+C; stdout and stderr remain command-result channels, and the debug adapter keeps stdin for DAP."

Dev: "May a malformed `stdin-v1` control frame cancel or fail the project command?"
Domain Expert: "No. Version `1.0` recognizes only BOM-less `cancel` followed by LF; repeated valid frames remain idempotent. EOF, a missing LF, CRLF, a BOM, and unknown, incomplete, or oversized input have no cancellation effect and are discarded with bounded memory. They add no project-command failure or stdout/stderr diagnostic, so only an explicit valid frame can affect the command's existing outcome contract."

Dev: "Does successfully writing `cancel\n`, or failing to write it, determine the command outcome?"
Domain Expert: "Neither. End stdin with the one frame and use no acknowledgement; write completion proves only the caller's transport operation. A write error does not set cancellation state, and protected project mutations still wait rather than force-killing the CLI. Accumulate stdout and stderr through the child `close` event instead of resolving at `exit`, then classify only the authoritative terminal code and trusted result."

Dev: "How should the extension present a failed cancellation-frame write?"
Domain Expert: "While waiting, update progress to `Cancellation request could not be delivered; waiting for vba-dev to finish.` and record the actual error in Output without a popup. Exit `130` remains silent; failure or untrusted output keeps its one existing error. If the command instead returns trusted success, use one warning notification and append `Cancellation request could not be delivered.` after any nonzero CLI warning count; when that count is zero, omit `0 warnings.` entirely. Retain the applicable location action, offer Show Output, and neither add the transport condition to the CLI warning array nor create a second terminal toast."

Dev: "Should stdin cancellation support be repeated in every command capability?"
Domain Expert: "No. Advertise the executable-wide feature once as `featureVersions[\"invocation.stdinCancellation\"] == \"1.0\"` and require that exact version in the extension-owned CLI contract, while allowing unknown additional features. Also require `new excel` output schema `1.0` for guided creation. The feature guarantees request delivery, not command-specific commit or exit semantics. A configured CLI mismatch follows the existing actionable-warning and session-wide bundled fallback; if the bundled CLI is also incompatible, start no managed command rather than mixing tools or degrading to force-kill."

Dev: "If a later CommonModules package stops declaring a reference, should `common-module update` remove the old entry automatically?"
Domain Expert: "Not in the initial contract. References introduced only by CommonModules are recorded as not directly requested, while source-template references and an explicit `reference add` are directly requested. Add or Update may add newly required entries but never auto-remove an existing reference or downgrade direct intent. The flag is groundwork for a future fully revalidated auto-remove operation, not present deletion authority."

Dev: "How does one CommonModules manifest row encode zero or more `CommonModuleRequiredReference` names?"
Domain Expert: "Use the required fourth TSV column `RequiredReferences` whose cell is a standalone JSON string array. Write `[]` for none, preserve declared item order, require every decoded name to be nonempty and already trimmed, and reject case-insensitive duplicates instead of silently normalizing them. JSON string escaping keeps commas and quotes inside externally owned reference names unambiguous."

Dev: "Should a module row repeat the external-reference requirements of every CommonModule it depends on?"
Domain Expert: "No. Declare only references used directly by that row's own source entry. Resolve the full CommonModule dependency closure first, then take the ordered case-insensitive union of every included row's `RequiredReferences`; if two source entries directly use the same library, both may declare it without copying all transitive requirements into their parents."

Dev: "May `RequiredReferences` declare `Visual Basic For Applications` because every CommonModule uses the VBA standard library?"
Domain Expert: "No. That `VbaStandardLibraryReference` is always active outside `VbaProjectReferenceSelection`, so it cannot become a selected or not-directly-requested manifest entry. Reject its canonical human-visible name under case-insensitive comparison during complete repository validation rather than ignoring it. Ordinary Excel, Office, OLE Automation, and other baseline references remain valid declarations and preserve direct intent when already selected."

Dev: "Which text encoding owns the shared CommonModules manifest once reference names may contain Unicode?"
Domain Expert: "Canonicalize both source and distributed `common-modules-manifest.tsv` as strict UTF-16LE with a required BOM. Readers do not infer ACP or accept another encoding as fallback; collection preserves the validated bytes so every consumer sees the same Unicode names."

Dev: "Should tooling edit `.gitattributes` so Git displays UTF-16LE manifests as text?"
Domain Expert: "No. The encoding contract owns working-file bytes, not repository configuration. Neither `common-modules-manifest.tsv` workflow nor `VbaDev` project-manifest workflow creates or modifies Git attributes; each repository owner decides how Git stores or renders `common-modules-manifest.tsv` and `vba-project.json`."

Dev: "May CommonModules collection copy a source manifest after only extracting its `ModuleFile` column because a separate validator exists?"
Domain Expert: "No. Successful collection itself guarantees a complete structurally valid source manifest: run the canonical manifest validation before any distribution write, leave the prior output unchanged on any error, and byte-copy only the validated UTF-16LE artifact. Producers share one validation meaning, while downstream consumers independently revalidate the same public grammar rather than trusting package provenance."

Dev: "May CommonModules Add or Update record a newly required reference name first and let a later build discover whether it resolves?"
Domain Expert: "No. Existing selected names remain environment-independent authority, but every missing `CommonModuleRequiredReference` must reach the same conclusive VBE-equivalent resolution as explicit Reference Add before CommonModules source or manifest mutation. Registry-unique names need no Excel; registry ambiguity may use the selected document's source-template probe in an owned `AutomationExcelProcess`. Any unavailable, ambiguous, unverified, failed, or cancelled result aborts the whole mutation unchanged, while `new excel` may use its already owned initial workbook."

Dev: "Should CommonModules hold the project mutation lease while a missing required reference runs an Excel/VBE ambiguity probe?"
Domain Expert: "No. Resolve every invocation-start missing requirement and finish probe cleanup before acquiring the lease. Inside the lease, reload the latest manifest and first partition the latest targets. Installed-only Add and zero-target Update skip repository capture; otherwise capture a complete stable repository snapshot and derive the final repository-dependent plan only from its staged bytes. Selected references need no evidence, while a still-missing requirement may use only `CommonModulesReferenceResolutionEvidence` for the same document and unchanged canonical source-template selection. A newly required name, removal of an initially selected requirement, or relevant template-selection change fails before source mutation and asks for rerun. Discard unused evidence, do not recheck the fixed template bytes, start no in-lease probe, and do not release and automatically retry."

Dev: "Should each way that CommonModules reference-resolution evidence becomes unusable have a different failure code?"
Domain Expert: "No. Aggregate every latest-plan mismatch under `commonModulesRequiredReferencePlanChanged`: a new missing requirement, concurrent removal of an initially selected requirement, a relevant source-template selection change, or otherwise absent document-scoped evidence. Report the affected documents, names, and changed paths in deterministic human diagnostics, but keep one stable remedy: no source or manifest mutation, no partial success JSON, and rerun against the latest state. Evidence that became unnecessary and a requirement concurrently added to selection are not failures."

Dev: "May CommonModules Add or Update report `No CommonModules changes.` whenever it copied no source files?"
Domain Expert: "No. Treat an installed CommonModule, an updated CommonModule source, a dependency promoted to directly requested, a metadata change, and a newly added CommonModules-required reference as distinct observable changes. Render every actual change with an operation-specific description and finish with separate counts for module installation, source update, direct-request promotion, metadata update, required-reference addition, and unchanged requests. Do not report an already-selected reference as newly added. Reserve `No CommonModules changes.` for a rebased result that changes neither source nor manifest."

Dev: "May VS Code or another automated caller parse CommonModules Add or Update text output to learn which changes completed?"
Domain Expert: "No. Both commands default to human-readable `text` but also expose `--format json` with schema version `1.0` advertised through capabilities. JSON emits one complete result only after atomic success or a trusted no-op; failure or cancellation emits no partial success object. Machine callers consume that result rather than interpreting prose."

Dev: "Can the CommonModules mutation JSON list only change events and omit modules that were processed but unchanged?"
Domain Expert: "No. Use one Add/Update envelope with an exhaustive per-document `modules` result for every affected InstalledCommonModule. Each module has `status: changed` with one or more structured changes, or `status: unchanged` with an empty change list, so simultaneous effects and trusted no-ops are both explicit. Keep document-level required-reference results separate because one reference may be shared by several modules. Derive summaries from these exhaustive results rather than serializing redundant counts or a no-op flag."

Dev: "Does the CommonModules mutation summary's unchanged count refer only to the names typed after `common-module add`?"
Domain Expert: "No. Count unchanged CommonModule targets in the same dependency-expanded or Update-wide `modules` result used for every other module outcome. For `add A`, an unchanged A and newly installed dependency B produce one unchanged CommonModule and one installation, so the operation itself is not a no-op. Call this `Unchanged CommonModules`, not unchanged requests."

Dev: "If an installed CommonModule disappears and a differently named entry appears in the repository, should Update migrate the old source identity to the new one?"
Domain Expert: "No. Treat the old entry as an `OrphanedInstalledCommonModule` and retain its source and manifest authority; treat the new name as a separate CommonModule installed only through an explicit Add or the current dependency closure. Do not infer a successor from repository proximity. Orphaning remains advisory: build still includes the retained source, publish still follows its recorded test-only classification, directly requested entries are not future auto-remove candidates, and dependency entries remain until a future purge proves removal safe."

Dev: "Should the manifest record a general repository status object or a separate orphan collection?"
Domain Expert: "Neither. Give every InstalledCommonModule a required `orphaned` fact. A successful stable repository reconciliation sets it when that retained name is conclusively absent and clears it only when the same identity reappears and refresh succeeds. Repository access, validation, or snapshot failure changes no marker. The fact is neither a live availability promise nor removal authority, so record no timestamp, repository version, or inferred successor."

Dev: "Should Update fail after conclusively finding that an InstalledCommonModule has become orphaned?"
Domain Expert: "No. Complete the atomic mutation, retain the source, record the orphan transition, and return a complete success with one aggregate `orphanedCommonModulesRetained` warning whenever any final target remains orphaned. A newly orphaned module is changed; an already orphaned module can be unchanged while still contributing the warning. Clear the warning for a module only after the same identity reappears and its refresh succeeds."

Dev: "Does Doctor fail merely because an InstalledCommonModule is orphaned or its recorded orphan marker has not yet caught up with the current repository?"
Domain Expert: "No. When its one authoritative source unit remains structurally valid, report orphaning or a stale marker as repository-synchronization warnings and direct the user to CommonModules Update. Reserve failure for invalid manifest or repository authority and missing, duplicate, or otherwise ambiguous installed source. If any directly requested root is orphaned, do not claim that dependency entries are unreachable; only a complete current root closure may identify a retained dependency as a future prune candidate."

Dev: "Must `common-module add OldModule` fail when OldModule is already installed as an orphaned dependency but is absent from the repository?"
Domain Expert: "No. Existing installed identity is enough to record direct intent: promote `requested` without reading or copying repository source, retain `orphaned`, and report the orphan warning. Add never refreshes or reactivates an existing orphan; Update owns that reconciliation. If the same batch also contains an uninstalled name, resolve the complete missing-name closure and commit nothing, including promotions, when that repository work fails."

Dev: "Can Update interpret `Feature.bas` becoming `Feature.cls` as retirement plus a separately installed replacement?"
Domain Expert: "No. Both claim the same stable `CommonModuleName`, so a substantive exported source-identity difference is a repository identity conflict, not orphaning or metadata refresh. Fail before mutation and require a genuinely new CommonModuleName for retirement-plus-new-install semantics. A case-only spelling difference remains the same identity but refreshes the installed canonical spelling and source unit from the repository while preserving its containing-directory placement."

Dev: "Does CommonModules Update rewrite every targeted source unit and call it updated even when repository and target bytes already match?"
Domain Expert: "No. Compare the complete raw-byte source unit and mutate only a missing or different target. Treat a form and its sidecar as one unit, so a sidecar-only creation, replacement, or deletion is one source update. Exact equality performs no file operation or timestamp churn and contributes an unchanged module when no manifest fact changes."

Dev: "May one CommonModules mutation module result hide final intent or classification behind a generic metadata-change event?"
Domain Expert: "No. Every result exposes final `requested`, `testOnly`, and `orphaned` facts and uses the closed change vocabulary `installed`, `sourceUpdated`, `directRequestPromoted`, `testOnlyChanged`, and `orphanedChanged`. Installation subsumes initial state; existing entries list every simultaneous change in canonical order. Source changes expose only the normalized DocumentSourceSet-relative source-unit path, never hashes, absolute paths, or separate form-sidecar events."

Dev: "Should a CommonModules mutation document result repeat every already-selected external reference required by its target modules?"
Domain Expert: "No. Name the document-level array `referenceChanges` and include only references actually appended by this mutation, each as canonical `name`, `kind: added`, and `requested: false`. Existing references remain absent from this change projection regardless of direct intent; `reference list` owns the complete selection and resolution inventory."

Dev: "May CommonModules Add or Update alphabetize its result arrays independently of manifest and dependency order?"
Domain Expert: "No. Add starts from the first occurrence of each trimmed, case-insensitively distinct requested name and expands uninstalled work through canonical `CommonModuleDependencyComponent` order; place each newly discovered CommonModule at its first component encounter, while an existing entry retains its established position and a later explicit request changes only final direct intent. Update orders documents case-insensitively with an ordinal tie-break and orders each document's modules by the final rebased manifest: retain existing order and append newly discovered entries in component order. JSON and text share that order, while reference changes retain committed append order and module changes retain their canonical kind order."

Dev: "Should an unchanged CommonModules target or an uncertain completion be represented as a mutation warning?"
Domain Expert: "No. `unchanged` represents an ordinary no-op, while any loss of result or commit trust fails the complete command. `warnings` contains only structured non-fatal information. JSON success keeps warnings inside its sole stdout object; text mode writes warnings to stderr. Unknown warning codes remain displayable because consumers never derive mutation success or module outcomes from them."

Dev: "May CommonModules success warnings follow exception timing or expose arbitrary cleanup failures as new codes?"
Domain Expert: "No. Schema `1.0` produces at most one each, in fixed order, for retained orphan state, deferred cancellation, retained non-authoritative snapshot workspace, and released-lease marker cleanup. Their codes are `orphanedCommonModulesRetained`, `cancellationDeferred`, `commonModulesSnapshotCleanupFailed`, and `leaseMarkerCleanupFailed`. Counts and normalized absolute retained paths belong only to stable human messages and are never parsed; probe, atomic-mutation, recovery, or lease-release uncertainty remains command failure. Consumers remain open to future warning codes without treating them as outcomes."

Dev: "If CommonModules Add or Update exits zero but returns malformed or request-mismatched JSON, may the extension retry or run a follow-up list to reconstruct the result?"
Domain Expert: "No. Source and manifest changes may already have committed. Report that the command completed but returned an untrusted result, offer VBA Tools Output, and make no success or no-op claim. Do not retry, roll back, fall back, or reconstruct the result with another command; allow the manifest lifecycle to observe whatever actually committed."

Dev: "Must the extension independently reconstruct a CommonModules dependency closure before trusting a mutation result?"
Domain Expert: "No. Validate schema, request context, recognized result shapes, uniqueness, and every internally provable final-state and change invariant. For Add, require exactly one selected-document result and exactly one final `requested: true` module for every explicit request, while permitting dependency results. For Update, require project-wide shape and unique nonempty document targets. Treat the CLI's exhaustive projection as authority for the dependency-expanded and latest-manifest target sets; do not duplicate repository planning or claim to prove dependency or append order from stdout alone."

Dev: "Should VS Code list every CommonModules change kind in a terminal notification or run a follow-up List to summarize the mutation?"
Domain Expert: "Neither. Derive one concise notification from the trusted mutation result: count changed and unchanged CommonModules once per module, count added references separately, identify Add by document and Update by the project-root folder name, and distinguish an Update with no installed targets from an all-unchanged Update. Use one information notification without warnings or one warning notification with the warning count and Show Output; never show separate success and warning notifications, auto-open Output, or run List merely to reconstruct the outcome. Detailed change kinds and names remain in VBA Tools Output."

Dev: "Does `requested: false` prevent `reference remove` from deleting a reference introduced by CommonModules?"
Domain Expert: "No. `reference remove` remains an environment-independent repair path and removes the selected manifest entry regardless of `requested`, without consulting CommonModules metadata. A later CommonModules Add or Update may re-add a still-required name with `requested: false`; the flag records groundwork for future automatic-removal eligibility rather than ownership or deletion protection."

Dev: "What result does `reference add` return when the name already exists with `requested: false`?"
Domain Expert: "Return `promoted`, not `added` or `alreadyPresent`, after changing it to `requested: true`. The machine result distinguishes a newly effective reference, a direct-intent promotion, and a no-op; human output says that the existing reference was marked as directly requested rather than exposing `promoted` as unexplained jargon."

Dev: "Should `reference add` reject a registry-unique library because an exported module currently has the same project name?"
Domain Expert: "No. `reference add` resolves and records dependency intent; it does not preflight every source identity or the template's project name. Language Server validation, Doctor, and materializing commands report incompatibility separately. If an ambiguity probe itself cannot resolve a candidate because its required VBE baseline rejects the name, leave the manifest unchanged and report that probe failure."

Dev: "Should an `ExplicitWorkbookImport` let `VBComponents.Import` discover name conflicts after it has flushed the target?"
Domain Expert: "No. Before mutation, compare the staged authoritative `ModuleIdentity` values with each other and with the live target's actual `VBProject.Name`, active reference project or library names, and retained component names. Invalid source metadata, incomplete target inspection, or any conflict closes the workbook without saving and leaves the target file unchanged."

Dev: "Can build, publish, or a test build complete `WorkbookMaterializationNamePreflight` before it opens Excel?"
Domain Expert: "Only the source-metadata and source-to-source part. The final active-reference set may include protected references that remain after normalization, so finish the decision in the temporary materialization workbook. It may flush replaceable old components and normalize references there, but it must re-inspect final authority and reject conflicts before source import, save, or output replacement."

Dev: "Should a test-only or `'#ExcludePublish` module with a conflicting `ModuleIdentity` block `publish`?"
Domain Expert: "No. `WorkbookMaterializationNamePreflight` evaluates the effective source set selected for that output profile. Build and build-before-test include test source and may fail, while publish ignores name defects confined to excluded source; Language Server validation and Doctor may still report project-source health independently. Structural failures that prevent the command from selecting a trustworthy flat source set remain command failures."

Dev: "Should `vba-dev doctor` estimate materialization name compatibility from the manifest and registry alone?"
Domain Expert: "No. For each document, use one disposable source-template copy to remove replaceable old components, normalize references, and inspect the actual final project, active-reference, and retained-component authority without importing or saving source. Evaluate build and publish source profiles separately; an authority gap or conflict fails the affected profile, while native VBE debugging remains the independent adapter Doctor's concern."

Dev: "Should a name preflight stop at the first invalid source or namespace conflict?"
Domain Expert: "No. Collect every conflict that the established authority can prove and fail the command once with a deterministic complete report. Group a source-to-source collision once, order groups by effective exported-source order, then list source or retained-component conflicts, the containing project, and active references; invalid source metadata prevents that document's Excel phase but does not stop Doctor from running independent diagnostics or checking other documents."

Dev: "Should a missing project source template make registry-unique `reference list` results fail, or fall back to a blank workbook?"
Domain Expert: "Neither. Registry-unique results do not touch the template and may succeed. If ambiguity requires the selected template and its baseline cannot be prepared, do not substitute a blank workbook: mark every probe-dependent name `probeAborted`, report `probeBaselineUnavailable`, and return an incomplete result."

Dev: "Can `reference list --available` run when upward discovery finds no project manifest?"
Domain Expert: "Yes, but only when neither project nor document was explicitly selected. It warns, reports environment scope with null project and document, and uses a blank Excel workbook only for ambiguity probes. Every distinct registered description remains a candidate, including references already checked in that blank workbook. An explicit or malformed project never falls back, and language-server catalog refresh accepts only project scope."

Dev: "Should one timed-out `References.AddFromGuid` attempt restart Excel and continue probing?"
Domain Expert: "No. Each attempt has its own deadline, but a timeout makes the owned probe process untrustworthy. Report `probeTimeout` for the affected name, `probeAborted` for later probe-dependent names that were not attempted, and a shared `probeProcessUntrusted` diagnostic. Let an explicit later invocation start a fresh probe. Conclusive candidate rejection may continue in the same process."

Dev: "Should cancelling `reference list --available` make the remaining names look like a failed probe?"
Domain Expert: "No. Once scope is known, cooperative cancellation reports unfinished names as `cancelled` with an `operationCancelled` diagnostic and an incomplete result. `probeAborted` means infrastructure made continuation untrustworthy, not that the user intentionally stopped the command."

Dev: "What should Tab completion do when it cannot completely scan the TypeLib registry?"
Domain Expert: "Skip an individually malformed or unusable registration. If a catalog-level failure makes the scan incomplete, emit no dynamic reference-name candidates and no completion diagnostic rather than presenting a partial catalog. Static command and option completion remains available. Explicit `reference add`, `reference list --available`, and `doctor` invocations report the actionable registry problem normally."

Dev: "Should `reference list --available` silently drop a readable TypeLib name when all of its identities are malformed?"
Domain Expert: "No. Keep the name as `unavailable / noUsableIdentity`. Skip only records that cannot form a name, aggregate individually skipped records as a non-failing warning, and mark the catalog incomplete only when enumeration may have missed whole names."

Dev: "Which spelling wins when equivalent registry descriptions differ only by casing?"
Domain Expert: "Compare trimmed names with `OrdinalIgnoreCase` and use the ordinal-minimum registry spelling for available lists, completion, and a newly added manifest entry. Configured lists preserve existing manifest spelling; an already-listed add preserves that spelling whether it is a no-op or a direct-intent promotion."

Dev: "Should one ambiguous manifest reference make the language server discard resolved siblings returned by the same `reference list` invocation?"
Domain Expert: "No. When schema, project scope, project, document, mode, and `complete: true` are trustworthy, consume each entry independently even if the command exits nonzero. Commit resolved siblings and preserve only each conclusively ambiguous or unavailable reference's `LastKnownGoodReferenceCatalog`. An unverified entry makes the whole response incomplete and therefore preserves every affected catalog."

Dev: "Is source formatting only about casing?"
Domain Expert: "No. `SourceFormatting` includes `CasingNormalization` and `IndentationFormatting`, but it is not a semantic refactor."

Dev: "Is `String` a host definition when formatting casing?"
Domain Expert: "No. Intrinsic words such as `String`, `True`, and `Nothing` belong to `LanguageVocabulary`."

Dev: "Is automatic body and terminator insertion after Enter the same as `EndStatementCompletion`?"
Domain Expert: "No. `EndStatementCompletion` remains an explicit completion candidate. The automatic editor action is `BlockSkeletonInsertion`."

Dev: "Is each physical line of a continued `Function` declaration a separate header?"
Domain Expert: "No. The continued logical declaration is one `BlockDeclarationHeader`, and only its final physical line can trigger `BlockSkeletonInsertion`."

Dev: "Is a trailing `Loop While condition` the terminator of a `While` block?"
Domain Expert: "No. It terminates a post-condition `Do...Loop`; `While...Wend` is a separate form. Both forms are outside `BlockSkeletonInsertion`."

Dev: "Should a space after a completed expression keep general completion open?"
Domain Expert: "No. A completed expression has no `CompletionExpectation`, so an LSP trigger cannot manufacture `CompletionCandidate`s for it."

Dev: "Should `+` and `+ ` produce different completion lists?"
Domain Expert: "No. Irrelevant trivia does not change the `CompletionExpectation`, so semantic resolution must admit the same `CompletionCandidate`s at both positions."

Dev: "Can a normal apostrophe comment appear in hover?"
Domain Expert: "No. Hover uses the complete `DocumentationComment`; Signature Help projects only its `@param` documentation. Interface documentation may be inherited through `Implements` when the implementation has none."

Dev: "Should a private helper with a `DocumentationComment` appear in hover?"
Domain Expert: "Yes. Visibility does not hide an attached `DocumentationComment`."

Dev: "Can `Range` be renamed?"
Domain Expert: "No. Excel object model members are `VbaProjectReferenceDefinition`s, not `RenameTarget`s."

Dev: "Should F12 on `Range` or `xlCenter` open the synthetic `vba-reference://` URI?"
Domain Expert: "No. `ExternalDefinitionNavigation` stays disabled until a read-only virtual catalog document provider exists. Returning an URI that the editor cannot open is not a useful go-to-definition result."

Dev: "Does an Excel document kind activate `ActiveWorkbook` when the Excel object library is absent?"
Domain Expert: "No. `ActiveWorkbook` is a `HostGlobalReferenceDefinition` supplied only when the Excel object library is the active `MainVbaProjectReference`; document kind alone does not synthesize the missing reference."

Dev: "Are `Application` and `ActiveWindow` Excel-only globals?"
Domain Expert: "No. They are host-generic `HostGlobalReferenceDefinition`s supplied by the active `MainVbaProjectReference` catalog when that host exposes them. Excel supplies Excel-typed values, Word supplies Word-typed values, and an ad-hoc project supplies neither."

Dev: "Are `ActiveCell`, `ActiveSheet`, `ActiveWorkbook`, and `ThisWorkbook` also host-generic?"
Domain Expert: "No. They are Excel-specific host globals and appear only when the Excel object library is the active `MainVbaProjectReference` and its catalog exposes them."

Dev: "Should `ThisWorkbook.cls` be merged with the Excel `ThisWorkbook` host global?"
Domain Expert: "No. Real Excel projects reserve the workbook document module name, and the language server does not infer document-module identity from that spelling. `ThisWorkbook` is handled as the Excel catalog's read-only host global; source document-module modeling is a separate concern."

Dev: "Should `ActiveCell` be modeled as a global variable so that it can appear in completion?"
Domain Expert: "No. It is a read-only property `HostGlobalReferenceDefinition`; its project-reference origin makes it available as a value while keeping it outside `RenameTarget`."

Dev: "Should assigning to a read-only host global create a new source diagnostic in this work?"
Domain Expert: "No. `HostGlobalReferenceDefinition` records that the value is read-only, but assignment diagnostics are outside this scope."

Dev: "Should `ActiveCell` hover as `Property ActiveCell As Range`?"
Domain Expert: "No. A root host global is presented as a value reference, so its `DeclarationLabel` is `ActiveCell As Range`; callable or indexed properties use `CallableSignature` when that richer shape is available."

Dev: "Should `ActiveSheet.` show both worksheet and chart members?"
Domain Expert: "No. `ActiveSheet` is a read-only `HostGlobalReferenceDefinition`, but its type is intentionally unavailable because the runtime object kind varies. Member completion after `ActiveSheet.` stays empty rather than guessing a union."

Dev: "Should `ThisWorkbook.` or `ActiveCell.` inspect the currently open Excel workbook before showing members?"
Domain Expert: "No. Typed host globals participate in `MemberChainResolution` through their declared catalog types, such as `Workbook` or `Range`. Completion does not depend on live Excel state, workbook contents, sheet names, or the active cell."

Dev: "Does `ActiveWindow` exist in an ad-hoc project because several Office hosts expose that name?"
Domain Expert: "No. Each host supplies its own typed `ActiveWindow` through its active `MainVbaProjectReference`; an ad-hoc project or a project missing that main reference has no such definition."

Dev: "Should every member found under an Excel `Window` or `Workbook` be considered an unqualified host global?"
Domain Expert: "No. `ReferenceDefinitionGlobalExposure` distinguishes ordinary type members from library globals and globals supplied only by the active main host."

Dev: "Should hidden or restricted TypeLib members appear in normal completion?"
Domain Expert: "No. Completion should show names users normally write. Hidden or restricted catalog members are suppressed unless `ReferenceDefinitionGlobalExposure` has explicitly selected them as exposed root definitions, such as a main-host global supplied through application-object binding. A hidden owner such as `_Global` is not exposed wholesale. For TypeLib Events, this means excluding them from `TypeLibEventAuthoringSurface`; the separate structural and existing-handler-recognition projections still retain the facts required for `WithEvents` eligibility and an already-written handler name."

Dev: "Should an older persisted catalog expose root globals by guessing from `_Global` or `Application` owner names?"
Domain Expert: "No. Missing `ReferenceDefinitionGlobalExposure` metadata fails closed for root globals. The catalog can still supply ordinary type and member metadata that it proves, but root exposure waits for a refreshed catalog."

Dev: "Can the language server recognize host globals by looking for an owner named `Application`?"
Domain Expert: "No. `ReferenceDefinitionGlobalExposure` preserves the referenced library's application-object and library-global binding semantics; owner spelling does not establish global visibility."

Dev: "Are `vbCrLf` and `xlCenter` the same kind of completion?"
Domain Expert: "They are both structured `LibraryGlobalReferenceDefinition`s, but their owning references have different activation rules: `vbCrLf` comes from the always-active `VbaStandardLibraryReference`, while `xlCenter` is available only while its Excel `VbaProjectReference` is active."

Dev: "Should `VBA.` work in an ad-hoc project?"
Domain Expert: "Yes. `VbaStandardLibraryReference` is always active, so its `VBA` qualifier is available in every `VbaProject`, including an `AdHocVbaProject`. It still follows `NameResolution`, so a higher-rank source definition named `VBA` can shadow the qualifier."

Dev: "Should `vbCrLf` require TypeLib discovery or an installed Office application?"
Domain Expert: "No. `VbaStandardLibraryReference` is bundled baseline metadata. It is available immediately and independently of COM registry state, Office installation state, and `VbaProjectReferenceCatalogLifecycle`."

Dev: "Is `vbCrLf` only a completion label because it is available in every project?"
Domain Expert: "No. It is an external constant definition owned by `VBA.Constants`, with its declared `String` type, constant completion presentation, hover declaration, canonical casing, and semantic-token facts. Like every `VbaProjectReferenceDefinition`, it is not a `RenameTarget`, and a same-named source `VbaDefinition` still wins through `NameResolution`."

Dev: "Should `xlCenter` hover as `XlHAlign` when I use it in an alignment property?"
Domain Expert: "No. `DeclarationLabel` uses the catalog-provided type for the enum member itself and does not infer a contextual enum type from the use site. `vbCrLf` hovers as `Const vbCrLf As String`; Excel `xlCenter` hovers as `xlCenter As Long` when the Excel catalog records that member as `Long`."

Dev: "Is `Application` ambiguous because the host exposes both a global value and a class with that name?"
Domain Expert: "No. A value `CompletionExpectation` selects the read-only `HostGlobalReferenceDefinition`, while a type or creatable-type expectation selects the class `VbaProjectReferenceDefinition`; completion does not show both in one context."

Dev: "What happens when two public modules expose the same name?"
Domain Expert: "`NameResolution` treats equal-rank matches as ambiguous, so hover and go to definition should stay silent for that reference."

Dev: "If `Customer.cls` says `Attribute VB_Name = \"CustomerRecord\"`, what is the class name?"
Domain Expert: "The `ModuleIdentity` is `CustomerRecord`; the file name is only a fallback."

Dev: "Should `Set ws = Worksheets(1)` make `ws.` show worksheet members?"
Domain Expert: "Not in the MVP. `TypeResolution` uses explicit declarations such as `Dim ws As Worksheet`."

Dev: "If both a source class and Excel define `Range`, what should `Dim r As Range` mean?"
Domain Expert: "The source `VbaDefinition` wins. Use a reference-qualified annotation such as `Dim r As Excel.Range` to force a `VbaProjectReferenceDefinition`."

Dev: "Should `Application.ActiveWorkbook.Worksheets(1).Range(\"A1\").Find(` be treated as several unrelated qualified references?"
Domain Expert: "No. That is `MemberChainResolution`: each resolved member's declared result type supplies the receiver type for the next member access."

Dev: "Can `Me.CreateCustomer().DisplayName` participate in `MemberChainResolution`?"
Domain Expert: "Yes, inside class and form modules. `Me` is the current instance root, and private members remain visible within that same module."

Dev: "Is `Application.ActiveWorkbook _` followed by `.Worksheets(1)` on the next line a different kind of lookup?"
Domain Expert: "No. It is a `ContinuedMemberChain`: one `MemberChainResolution` expression split across physical lines, with each member still tied to its original source range."

Dev: "Is `Find( _` followed by arguments on later lines a `ContinuedMemberChain`?"
Domain Expert: "No. It is a `ContinuedArgumentList`: the receiver chain has already selected the callable, and the continued lines keep signature help active while identifying the active parameter."

Dev: "Inside `With Application.ActiveWorkbook.Worksheets(1).Range(\"A1\")`, what does `.Find` mean?"
Domain Expert: "The `WithReceiver` is the resolved range expression, so `.Find` is resolved as a member chain on that receiver. If the `WithReceiver` type is missing or ambiguous, no guessed member result is produced."

Dev: "Can the `WithReceiver` expression itself be split across physical lines?"
Domain Expert: "Yes. The receiver expression can be a `ContinuedMemberChain`; once that receiver resolves, leading-dot members inside the block still use the `WithReceiver`."

Dev: "Should `Constructor.New_Foo` resolve across modules?"
Domain Expert: "Yes. It is a `QualifiedReference`; after `Constructor` resolves to a `ModuleIdentity`, `New_Foo` resolves to a public member in that module."

Dev: "Does `Word.Application` mean the same thing as unqualified `Application`?"
Domain Expert: "No. `Word.Application` is a `QualifiedReference` through the active Word `VbaProjectReferenceQualifier`; unqualified `Application` follows `MainVbaProjectReference` precedence."

Dev: "What should `Word.` complete?"
Domain Expert: "If no source definition named `Word` wins first, it completes the active Word reference catalog's public root surface: root types, exposed constants, and explicit root exposure definitions."

Dev: "Should `Excel.` show the same list in `Dim r As Excel.` and `x = Excel.`?"
Domain Expert: "No. The reference qualifier exposes the catalog's public root surface, but `CompletionExpectation` still filters by role. Type contexts see type-compatible candidates, value contexts see value-compatible candidates, and creatable-type contexts see creatable class candidates."

Dev: "Should `Excel._Global` or other hidden owner names become a way to browse internal catalog members?"
Domain Expert: "No. A `VbaProjectReferenceQualifier` exposes the public root surface of the reference catalog. Hidden owners and restricted internal members are not completion entry points."

Dev: "Where does the `Word` qualifier name come from?"
Domain Expert: "From a `VbaProjectReferenceQualifier` supplied by the `VbaProjectReferenceCatalog`. It is not written in the `ProjectManifest` and is not parsed from `Reference.Description` alone."

Dev: "If there is a source module named `Word`, does `Word.Application` still force the Word host?"
Domain Expert: "No. Source `VbaDefinition`s outrank reference qualifier names, so `Word` resolves to the source module first. The reference qualifier is not an absolute escape hatch; rename the source definition or remove the collision if the external reference qualifier is needed."

Dev: "Does `Button_Click` resolve without reading form designer metadata?"
Domain Expert: "Only at a Sub, Function, or Property accessor declaration name in the same class module where `Button` is explicitly declared as a module-level `WithEvents` variable, its type eligibility is `eligible`, and its class resolves `Click` as an Event. The prefix is a variable reference and the suffix is an `EventReference`. Only the Sub is a valid handler; Function and Property candidates receive procedure-kind validation when the binding is conclusive. An ordinary `Button_Click` occurrence remains a reference to its complete procedure or Property definition."

Dev: "Do form designer properties create completion candidates?"
Domain Expert: "No. A `FormDesignerBlock` is not parsed into `VbaDefinition`s in the MVP."

Dev: "How much source does an incremental parse replace?"
Domain Expert: "It replaces the affected `ModuleMember`, not individual expression nodes."

Dev: "Does a later `didChange` cancel an earlier completion or hover request?"
Domain Expert: "No. Once the earlier read captures its immutable revision, the later `didChange` receives a later `InputSequence` and may commit through the ordered lane while that read continues on the pinned revision. Only explicit `$/cancelRequest`, host abort, EOF, or terminal runtime failure signals `RequestCancellationOwnership`."

Dev: "Does explicit cancellation have to wait behind the request it cancels?"
Domain Expert: "No. The input reader signals the matching owner immediately outside the ordered mutation-and-capture lane, but `VbaLspRequestExecution` still writes exactly one normal or `RequestCancelled` response through the serialized transport."

Dev: "Did continuous admission change VBA document synchronization?"
Domain Expert: "No. The server still advertises full-text synchronization. `VbaInteractiveWorkScheduler` changes admission and cancellation ownership, not the document text contract or the C# language-server authority."

Dev: "Should `BuildSourceSnapshot` bytes be passed unchanged to `VBComponents.Import`?"
Domain Expert: "No. They remain the authoritative captured source, while `VbaDev` derives a separate `VbeImportSourceSet` that losslessly round-trips text through the operation ACP and preserves `.frx` bytes for the VBE boundary."

Dev: "Should a document-scoped Command Palette action use the selected project's primary document when README is active?"
Domain Expert: "Only when it is the project's sole manifest document. Multiple documents require explicit choice even when one is primary, and the extension passes the chosen name to `VbaDev` as `--document`."

Dev: "Which document receives chooser focus when open VBA sources belong to several documents?"
Domain Expert: "The eligible source that was `activeTextEditor` before the chooser opened wins. Without that evidence, mixed visible or open documents focus `PrimaryOfficeDocument`; focus never confirms the choice."

Dev: "Can a manifest-mutating Command Palette action pass a dirty `vba-project.json` buffer to `VbaDev` and merge the result afterward?"
Domain Expert: "No. `ProjectManifestMutationPreflight` either saves that manifest with explicit consent and re-resolves the target from disk or cancels before process launch; `VbaDev` never consumes VS Code editor state."

Dev: "Should a successful manifest mutation reload an editor that became dirty while `VbaDev` was running?"
Domain Expert: "No. `ProjectManifestPostMutationCoherence` automatically synchronizes only a safe buffer with no competing observed revision. A later edit is preserved and compared with the post-invocation disk snapshot through explicit recovery."

Dev: "Can another Reference mutation run after the user keeps editing a diverged manifest?"
Domain Expert: "No. `ProjectManifestEditorDivergence` blocks another mutation until passive synchronization proves clean snapshot equality without competing evidence, or an explicit recovery choice is followed by a fresh applicable equality proof. Manifest list and project-diagnostic commands may continue only when they identify disk as their source."

Dev: "Should a second mutation for the same manifest wait behind one already running in this VS Code window?"
Domain Expert: "No. `ProjectManifestMutationBusyGuard` rejects it as busy because queued inputs and editor revisions become stale; other manifests remain independent, and cross-process serialization belongs to `ProjectManifestMutationLease`."

Dev: "Does a cancelled manifest mutation prove that `vba-project.json` stayed unchanged?"
Domain Expert: "No. `ProjectManifestMutationOutcome` compares pre-launch and post-exit disk bytes. A changed manifest still enters coherence and recovery even when the child was cancelled or exited nonzero."

Dev: "Does an unchanged `vba-project.json` prove that the whole CommonModules operation was a no-op?"
Domain Expert: "No. It proves only that manifest editor reconciliation is unnecessary. Source-file effects and operation success remain described by the command's schema-valid result."

Dev: "Can Auto Save make an edit during a manifest mutation safe to ignore because the buffer is clean at command exit?"
Domain Expert: "No. `ProjectManifestPostMutationCoherence` retains distinct intermediate content revisions as competing evidence even after Auto Save; a terminal dirty flag alone cannot authorize reload."

Dev: "Should the extension rewrite a clean manifest buffer when VS Code has not observed the post-invocation disk change yet?"
Domain Expert: "No. It waits two seconds for native synchronization, proves buffer, current-disk, and snapshot equality, then offers explicit `Reload from Disk` if convergence is still unproved; it does not become a second manifest writer."
