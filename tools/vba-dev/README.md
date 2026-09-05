# vba-dev

`vba-dev` is a Windows-only command-line tool for workbook-backed VBA projects.

```text
vba-dev <command> [options]
```

Print the independent standalone CLI release version without inspecting Excel,
VBIDE, a workbook, or a project:

```text
vba-dev --version
```

`vba-dev capabilities --format json` reports the same three-part SemVer as
`toolVersion`, independently from the VS Code extension version. Snapshot-aware
callers can require the `build.sourceSnapshot` or `test.sourceSnapshot` feature
version `2.0` before supplying those command inputs. Windows snapshot producers
can also require `sourceSnapshot.activeWindowsCodePage` version `1.0` and read
the accompanying positive `activeWindowsCodePage` value captured from `GetACP`.
Managed callers can require `invocation.stdinCancellation` version `1.0` before
opting into the hidden `--cancellation-transport stdin-v1` control channel. In
that mode only, exact BOM-less UTF-8 `cancel\n` requests cooperative command
cancellation; ordinary terminal invocations do not read standard input.
Consumers of built-in UserForm Event catalogs can require `hostEvent.list`
version `1.0` before invoking `host-event list --format json`.

## PowerShell completion

Load completion into the current Windows PowerShell 5.1 or PowerShell 7
session:

```powershell
vba-dev completions script pwsh | Out-String | Invoke-Expression
```

The command writes a self-contained registration script to stdout. It does not
edit a PowerShell profile or create a sidecar module. The script embeds the
absolute path of the `vba-dev` executable that generated it, so registered
completion continues to work when `PATH` changes. Regenerate and reload the
script after moving or replacing that executable.

## Commands

| Command | Scope | Description |
| --- | --- | --- |
| `new excel` | project creation | Create an Excel workbook-backed VBA project. |
| `common-module add` | document | Copy CommonModules entries into the selected document source set. |
| `common-module list` | document | List CommonModules entries for the selected document. |
| `common-module update` | project | Update installed CommonModules entries. |
| `completions script pwsh` | current PowerShell session | Generate PowerShell completion registration. |
| `reference add` | document | Add VBA project references to the selected document manifest. |
| `reference list` | document or available-mode environment fallback | List configured, stored-selection, or available VBA project references. |
| `reference remove` | document | Remove VBA project references from the selected document manifest. |
| `host-event list` | environment | Inspect the built-in UserForm Event catalog from one generated blank workbook. |
| `build` | document | Build the selected document into bin output. |
| `test` | document | Run VBA unit tests for the selected document. |
| `publish` | document | Publish the selected document. |
| `export` | document/path | Export modules from a workbook into source. |
| `import` | path | Import VBA sources into an existing workbook. |
| `check` | project | Validate deterministic project facts without starting Excel. |
| `doctor` | project or environment | Actively check project or ordinary Excel-environment readiness. |

Document-scoped commands use the manifest `primaryDocument` when `--document` is omitted.

Every non-debug Excel or VBIDE automation path delegates process launch,
private-desktop ownership, STA dispatch, deadlines, cleanup, and release proof
to the same sealed `AutomationExcelProcessRuntime`. It creates each owned Excel
process suspended on a unique invocation-scoped private Windows desktop.
Exact-PID observation starts before primary-thread resume, native object-model
binding is restricted to that desktop, and the desktop remains owned until the
complete Job process tree exits. This contract covers project creation, build,
every test mode, publish, import, export, Host Event discovery, reference probes,
and active Doctor probes. It has no command switch, best-effort mode, or
caller-desktop fallback. A blocked prompt remains private and becomes a bounded
failure with available PID, HWND, desktop, class, title, and lifecycle-phase
evidence.

Interactive debugging is deliberately different. Its preparatory
`vba-dev build` uses the private automation path, then the separate debug
adapter opens a new visible `DebugExcelProcess` for VBE interaction.

## Document source sets

A `DocumentSourceSet` is recursive, but exported VBA source identity is flat. `.bas`, `.cls`, and `.frm` files may live in nested organization directories under `sourcePath`, but their extension-including file names must be unique case-insensitively within that one source set.

Read-side commands such as `build`, `publish`, and `import` discover `.bas`, `.cls`, and `.frm` files recursively and sort them by exported file name. `.frx` files are not independent source inputs and are not preflighted separately; same-directory form sidecar handling is delegated to the underlying form import/export behavior. Write-side commands that place form files, such as `export` and `common-module add/update`, colocate `.frx` sidecars beside the selected `.frm` path.

## Help

### Root

```text
vba-dev

Usage:
  vba-dev <command> [options]

Commands:
  new            Create an Excel workbook-backed VBA project.
  common-module  Copy CommonModules entries into the selected document source set.
  completions    Generate shell completion setup.
  reference      Add VBA project references to the selected document manifest.
  host-event     Inspect built-in UserForm Events for the current environment.
  build          Build the selected document into bin output.
  test           Run VBA unit tests for the selected document.
  publish        Publish the selected document.
  export         Export modules from a workbook into source.
  import         Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use vba-project.json.
  check          Validate deterministic project facts without starting Excel.
  doctor         Check project and machine prerequisites.
```

### new excel

```text
vba-dev new excel

Create an Excel workbook-backed VBA project.

Usage:
  vba-dev new excel [options]

Options:
  --name <name>, -n <name>       Project and document base name.
  --output <dir>, -o <dir>       Project root output directory.
  --format <text|json>, -f <text|json> Creation receipt format.
```

`--output` selects the project root directory. `--name` selects the generated project and document base name; when omitted, it is derived from the output directory. `--format json` emits the version `1.0` success receipt; failures never emit a partial success receipt.

The initial manifest records the generated workbook's actual non-standard baseline references plus references required by the selected CommonModules package. It does not add Scripting Runtime or VBScript Regular Expressions unless a selected package entry requires them.

When a CommonModules repository is available, the command selects its `runtime-baseline` and `test-foundation` roots, resolves dependencies in deterministic component order, and copies the selected entries under the generated document source set's `common-modules` directory. A conclusively absent package produces a warning and a baseline without CommonModules; invalid or unstable package state fails creation.

Before the initial manifest commit, failure or cancellation removes only unchanged files created by this invocation, then its still-empty directories. Changed or replaced files, files with another hard link, reparse points, and foreign content are not adopted as rollback state. Unproved cleanup is reported for manual recovery; an unproved deletion rollback is never described as successful preservation. Pre-existing directories are never rollback targets. Once the manifest is committed, later cancellation cannot roll back the created project.

### common-module add

```text
vba-dev common-module add

Copy CommonModules entries into the selected document source set.

Usage:
  vba-dev common-module add [modules...] [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --force                        Overwrite conflicting source files.
  --format <text|json>, -f <text|json> CommonModules mutation output format.
```

CommonModuleName values are extensionless module base names resolved through the CommonModules manifest. Dependencies are copied with the requested entries and recorded in `vba-project.json`.

`common-module add` searches the selected document source set recursively for existing `.bas`, `.cls`, and `.frm` files with the same exported file name. Without `--force`, any match is a conflict. With `--force`, exactly one match is overwritten in place, no match copies to the source set's `common-modules` directory using the entry's file name, and multiple matches fail before file or manifest mutation.

### common-module list

```text
vba-dev common-module list

List CommonModules entries for the selected document.

Usage:
  vba-dev common-module list [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|json>, -f <text|json> CommonModules output format.
```

### common-module update

```text
vba-dev common-module update

Update installed CommonModules entries.

Usage:
  vba-dev common-module update [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --format <text|json>, -f <text|json> CommonModules mutation output format.
```

`common-module update` is project-scoped. It updates manifest-listed installed CommonModules entries and preserves the manifest `requested` intent.

Update uses the same recursive flat source identity as add. Existing installed entries are overwritten in place when exactly one matching source file exists; missing installed entries are copied to the source set's `common-modules` directory using the entry's file name; duplicate matches fail before mutation.

For `.frm` CommonModules, add and update first remove every same-name `.frx` under the target source set. If the canonical CommonModules repository has a matching `.frx`, exactly one sidecar is written beside the destination `.frm`; if it has no sidecar, no same-name `.frx` remains in the target source set.

Do not edit, replace, or synchronize target source files or `vba-project.json` from another application while Add or Update is running. Participating `vba-dev` commands coordinate through the project mutation lease, but external writers do not participate. Detected target-precondition conflicts stop the command; changes made in the final comparison-to-mutation interval are not guaranteed to survive, including with `--force`. Existing files retain atomic replacement, and new destinations remain no-overwrite; the command does not introduce an intermediate backup handoff or automatically restore earlier source changes.

Multi-entry add and update commands preflight the full file plan and planned manifest before deleting sidecars, copying files, or saving `vba-project.json`. The manifest is saved last. If file deletion or copy fails after file mutation begins, the command reports that the manifest was not saved and that source files may have been partially updated; no file rollback is attempted. If manifest saving fails after successful file operations, the planned manifest is written as UTF-16LE with BOM to `vba-project.failed-YYYYMMDD-HHMMSS-fff.json` beside `vba-project.json`, and the command prints only that recovery file path.

Copy and update output reports the actual destination path relative to the document source set, such as `common-modules/Feature.bas` for a new placement or `nested/Feature.bas` for an in-place overwrite.

Add and Update default to human-readable text. `--format json` emits exactly one complete schema `1.0` success object after a trusted commit or no-op. The project-scoped envelope contains the absolute `project`, Add's selected `document` or Update's `null`, `operation`, `complete: true`, `warnings`, and ordered `documents`. Failures and uncertain mutation, commit, or recovery states emit no partial success object.

Each document result contains exhaustive `modules` and `referenceChanges`. Add reports its dependency-expanded affected closure; Update reports every targeted installed module in final manifest order, including unchanged and orphaned entries. Every module exposes final `name`, `moduleFile`, `requested`, `testOnly`, and `orphaned`, plus `status` and ordered `changes`. New modules use `installed`; existing modules may use `sourceUpdated`, `directRequestPromoted`, `testOnlyChanged`, and `orphanedChanged` in that order. `unchanged` means the change list is empty. Reference changes contain only newly appended CommonModules requirements as canonical `added` names with `requested: false`.

Successful warnings use `orphanedCommonModulesRetained`, `cancellationDeferred`, `commonModulesSnapshotCleanupFailed`, and `leaseMarkerCleanupFailed` in fixed order. JSON keeps them inside the success object; text writes them to stderr. An ordinary no-op is not a warning.

### reference add

```text
vba-dev reference add

Add VBA project references to the selected document manifest.

Usage:
  vba-dev reference add [references...] [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|json>, -f <text|json> Reference mutation output format.
```

Reference names are human-visible `Reference.Description`-style names. The command edits `vba-project.json` only. `Visual Basic For Applications` is always active and cannot be selected explicitly.

### reference list

```text
vba-dev reference list

List VBA project references for the selected document.

Usage:
  vba-dev reference list [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --available                    List registered references not selected by the document.
  --no-resolve                   List the stored document reference selection without resolving references.
  --format <text|json>, -f <text|json> Reference output format.
```

Without `--available`, the command resolves the selected document's configured
references in manifest order. With `--available`, project scope lists registered
descriptions not present in the selected document. If no project or document was
specified and upward discovery finds no manifest, available mode warns and lists
the current environment instead. JSON output uses schema version `1.0` and marks
that fallback with `scope: "environment"` and null project/document fields.

`--no-resolve` is mutually exclusive with `--available`. It uses the ordinary
document-scoped project resolution rules, never falls back to environment scope,
and reads only the valid manifest selection. It does not inspect the registry,
Excel, VBE, or the source template, so unresolved references remain removable.
Text output preserves stored spelling and order. JSON extends schema version
`1.0` with `mode: "selection"`, `scope: "project"`, `complete: true`, an empty
`warnings` array, and ordered reference entries containing only `{ "name": ... }`.

### reference remove

```text
vba-dev reference remove

Remove VBA project references from the selected document manifest.

Usage:
  vba-dev reference remove [references...] [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|json>, -f <text|json> Reference mutation output format.
```

Removing an absent reference succeeds and leaves the manifest unchanged.

Add and remove trim and case-insensitively deduplicate names, then apply one rebased crash-atomic manifest mutation. JSON output uses schema version `1.0` and returns one ordered result per normalized request. Add statuses are `added`, `promoted`, and `alreadyPresent`; remove statuses are `removed` and `alreadyAbsent`.

### host-event list

```text
vba-dev host-event list

List built-in UserForm Events for the current environment.

Usage:
  vba-dev host-event list [options]

Options:
  --format <text|json>, -f <text|json> Host-event output format.
```

The command accepts no project or document selector. Its shared sealed
`AutomationExcelProcessRuntime` may briefly open one generated macro-free
`.xlsx` bootstrap, which is confined to the invocation's private desktop and
closed and deleted before
catalog discovery. The catalog phase then creates one unsaved generated blank
workbook and one temporary empty UserForm on the same private desktop; it opens
and imports no user source, never attaches to a user process or workbook, and
closes without saving. Text is the default. JSON uses the closed schema version
`1.0` and is published only after component and workbook cleanup, exact
process-tree and private-desktop release, bootstrap-artifact cleanup, and STA
dispatcher retirement are proved. See
[Host-event list and JSON schema 1.0](docs/host-event-list.md) for the catalog
shape, failure and cancellation behavior, safety boundary, and consumer
responsibilities.

### build

```text
vba-dev build

Build the selected document into bin output.

Usage:
  vba-dev build [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --source-snapshot <dir>        Complete caller-owned source snapshot directory.
  --output <workbook>            Caller-owned workbook output path for snapshot builds.
```

`build` creates the bin workbook from the source template, normalizes manifest-defined VBA project references, recursively imports source files, and writes the selected document's bin output. Project-local source files are imported after CommonModules dependency ordering, sorted by extension-including exported file name. Duplicate `.bas`, `.cls`, or `.frm` file names fail before source import. `.frx` files are not imported or validated independently.

An ordinary saved-source build fixes the active Windows ANSI code page and one
recursive source inventory, then captures each selected source and matching
form sidecar once. A supported UTF-8, UTF-16 LE, or UTF-16 BE BOM selects that
strict decoder; BOM-less source uses only the fixed ACP, including UTF-8 when
ACP is 65001. There is no BOM-less UTF-8 probe. On another ACP, UTF-8 source
needs a BOM or author-controlled conversion to that ACP. Malformed or unsupported
BOMs, invalid bytes, inexact byte round trips, and non-lossless VBE projection
fail before Excel starts and preserve the previous output.

Preflight, import, and verification use the same captured Unicode, identity,
encoding provenance, and sidecar bytes without reopening authoring files.
Changes after capture cannot affect the current build. This does not guarantee
an atomic snapshot of concurrent authoring changes: an unreadable selected file
fails capture, and build neither retries nor rewrites source files. Empty source
sets remain valid. The ordinary build stage of `test` uses these same rules;
Publish shares this admission with its own exclusion rules. Snapshot Build/Test
uses the same BOM-or-ACP admission for the complete caller-owned inventory and
advertises both snapshot features as `2.0`; the command contract and
`sourceSnapshot.activeWindowsCodePage` remain `1.0`. Project Doctor uses the
same BOM-or-ACP admission and shares one captured source authority across its
source diagnostics and Build/Publish profiles. VbaDev independently admits bytes supplied by any caller; it neither
requires nor reads an adapter, extension, editor state, or consumer proof.

Before Excel starts, build stages every selected source, requires its authoritative exported module identity, and reports all case-insensitive source conflicts. In the disposable workbook it checks the actual project, retained-component, and active-reference namespaces, removes replaceable components, normalizes references, then checks the final protected and VBE-adopted reference identities again. Any authority gap or conflict fails before source import, save, or atomic output replacement and preserves the source template and previous output. Build-before-test uses this same profile and preflight.

Supplying `--source-snapshot` and `--output` together instead builds from that complete recursive source inventory without reading the persistent document source set. Snapshot builds preserve caller bytes in invocation scratch, reject filesystem-canonical output aliases to caller or manifest-owned inputs and outputs, and atomically replace only the selected caller output. Neither option is valid by itself.

### test

```text
vba-dev test

Run VBA unit tests for the selected document.

Usage:
  vba-dev test [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|ndjson>, -f <text|ndjson> Test output format.
  --no-build                     Skip building before running tests.
  --source-snapshot <dir>        Complete caller-owned source snapshot directory.
  --timeout-seconds <seconds>    Test macro execution timeout in positive whole seconds.
  --module <name>                Run tests from one test module.
  --procedure <name>             Run one test procedure. Requires --module.
```

`test` builds before running tests by default. The private-desktop build process
exits before a distinct private-desktop execution process starts. `--no-build`
starts only the execution process. The default output format is `text`. Use
`--format ndjson` for machine-readable newline-delimited JSON.

Supplying `--source-snapshot` builds and tests a same-filename workbook inside a unique command-owned workspace without reading persistent source or touching the manifest bin workbook. It cannot be combined with `--no-build`, and `test` does not accept `--output`. Snapshot declaration ranges come from the fixed snapshot bytes while emitted locations use the corresponding persistent source URIs. The command releases its owned Excel processes before removing the workspace; a post-release deletion failure is warning-only and reports the retained absolute path without changing test outcomes, exit status, or the complete NDJSON 1.2 batch.

The snapshot supplies only the complete VBA source inventory. The selected project and document, template, references, test selector, and output format still come from the project manifest and the ordinary `test` options. Snapshot test locations reuse the same admitted syntax as the build without another file read, encoding decision, or ACP acquisition. Ordinary/no-build location lookup is unchanged.

`--timeout-seconds` changes only the test macro execution deadline. When omitted, `test` uses `commandDefaults.test.executionTimeoutSeconds`, then a built-in 600-second default. Every value must be positive whole seconds; workbook open/save and cleanup retain their independent deadlines.

### publish

```text
vba-dev publish

Publish the selected document.

Usage:
  vba-dev publish [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

`publish` creates the publish workbook from the source template, normalizes manifest-defined VBA project references, recursively imports publishable source files, and writes the selected document's publish output. It uses the same flat file-name ordering and duplicate-source failure behavior as `build`. Publish excludes installed CommonModules whose project-manifest entries record `testOnly: true` and project-local source files whose first scanned lines contain `'#ExcludePublish`. Build and publish do not read the current CommonModules repository; they continue to trust retained manifest entries and sources when `orphaned` is `true`, while `doctor` owns repository consistency checks.

Publish runs the same two-phase namespace preflight as build over only that publishable source profile. Identity defects confined to excluded `testOnly` or `'#ExcludePublish` source do not block publish, while duplicate flat file names and other structural profile-selection failures still do.

Publish fixes ACP and one recursive inventory before selection, using the same
supported-BOM and BOM-less ACP rules as [build](#build). Flat filename collisions
fail before any content is read or filtered. Manifest test-only sources and
sidecars are excluded without reads; other installed CommonModules ignore
source markers. Each project-local source must strictly decode in full and
reproduce its original bytes before its first 32 physical lines are checked
for a case-insensitive `'#ExcludePublish` prefix after VBA-leading whitespace
trimming. A marker cannot excuse invalid bytes later in the source.

A proved marker exclusion bypasses import eligibility, lossless ACP projection,
and sidecar reads, so excluded BOM-marked Unicode need not be representable in
ACP. Included sources and sidecars are captured at most once; selection,
preflight, import, and verification share those admitted facts. Included
CommonModules retain manifest order, then remaining sources use case-insensitive
filename order. An empty effective source set remains valid. Later authoring
changes cannot alter the admitted publication; an unreadable selected file
fails without a retry, closing stability check, or authoring lock. Existing
warnings and output commitment, including cancellation handling, are unchanged.

### export

```text
vba-dev export

Export modules from a workbook into source.

Usage:
  vba-dev export [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --from <path>                  Workbook to export from; skips project resolution when supplied.
  --to <dir>                     Directory to export to; defaults to the selected document source set, or the current directory with --from.
```

Without `--from`, `export` is project-aware: it reads the selected document's manifest-resolved bin workbook and writes to the selected document source set unless `--to` names another destination. With `--from`, export is explicit-workbook mode: it does not resolve `vba-project.json`, rejects `--project` and `--document`, and writes to the current directory when `--to` is omitted.

Cleanup is enabled when the destination is manifest-owned or when `--to` is supplied. Cleanup-enabled export records existing `.bas`, `.cls`, and `.frm` relative paths, exports the workbook to a temporary directory first, and leaves the destination untouched if workbook export fails. After a successful temporary export, it recursively deletes existing `.bas`, `.cls`, `.frm`, and `.frx` files only; empty directories and unrelated files remain. Exported file names that match previous source files are restored to those previous relative paths, new exported file names are placed at the destination root, and exported form `.frx` files are written beside their `.frm`.

When cleanup is not enabled, export still stages the complete workbook export before applying a recoverable overlay. It overwrites file paths it writes, but it does not delete unrelated files or displaced `.frx` files elsewhere in the destination.

### import

```text
vba-dev import

Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use vba-project.json.

Usage:
  vba-dev import [options]

Options:
  --from <dir>                   Source directory containing .bas, .cls, and .frm files.
  --to <path>                    Existing workbook file to update in place.
```

`import` updates the existing workbook at the requested target path. It requires both `--from` and `--to`, resolves relative paths from the current directory, and does not accept `--project` or `--document`. The source directory is inventoried once recursively for `.bas`, `.cls`, and `.frm` files and treated as one flat source file set ordered by extension-including exported file name. Relative paths are not ordering tie-breakers because duplicate exported file names fail before Excel starts. The command also fails before Excel starts when no importable source files exist.

Each selected source and matching same-directory `.frx` sidecar is captured once. A read failure stops import; later edits to source files do not change the captured input. `.frx` files remain opaque binary content associated with their `.frm`, and orphan sidecars are ignored.

Import fixes the active Windows ANSI code page once. A UTF-8, UTF-16 LE, or UTF-16 BE BOM selects that strict decoder; every BOM-less file is decoded only in the fixed ANSI code page. ACP 65001 means UTF-8. Import does not try UTF-8 as an alternative for BOM-less bytes. To import UTF-8 text on a host with another ACP, save it with a UTF-8 BOM or convert it to that ACP first. Malformed or unsupported BOMs, invalid bytes, inexact byte round trips, and characters that cannot pass losslessly through the host ACP fail before Excel starts. Caller source and sidecar bytes remain unchanged.

Close the target workbook before import and keep it closed until the command finishes. Import holds the original target against writes and deletion while processing a private copy. Before flushing any component, it requires authoritative incoming module identities and compares them case-insensitively with the copy's actual `VBProject.Name`, active `Reference.Name` values, and retained document-module names. Existing standard modules, class modules, and forms are then replaced; document modules such as `ThisWorkbook` and worksheet modules remain. The target is atomically replaced only after source-mirror cleanup, imported-component verification, private workbook save, and release of the owned Excel process succeed. Any earlier failure or cancellation leaves the target bytes unchanged. If private artifacts cannot be removed, the error reports their retained paths.

These encoding rules apply to explicit `import`, ordinary and snapshot `build` and `test`, included `publish` sources, and project Doctor's source inspection. Snapshot Build/Test capabilities are version `2.0`; the command contract, active-code-page capability, and Doctor schema remain `1.0`.

Unlike `build`, `import` does not add, remove, or normalize manifest-defined references, does not resolve CommonModules dependencies, does not interpret `'#ExcludePublish`, and does not validate whether the workbook compiles.

### check

```text
vba-dev check

Validate deterministic project facts without starting Excel.

Usage:
  vba-dev check [options]

Options:
  --project <path>               Project root containing vba-project.json.
```

`check` resolves the project and evaluates deterministic manifest, recursive
source-set identity, CommonModules, and command-default facts. It emits text
and uses a nonzero exit for a failed, unverified, or skipped static check. It
never starts Excel, so it is suitable for CI hosts without Excel.

This surface does not prove VBA compilation, live COM or VBIDE access,
reference materialization, source import, workbook save, or native debugger
readiness. Use project Doctor for active project readiness and the independent
adapter Doctor for native debugging readiness.

### doctor

```text
vba-dev doctor

Check project and machine prerequisites.

Usage:
  vba-dev doctor [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --scope <project|environment>  Diagnostic scope. Default: project.
  --format <text|json>           Output format. Default: text.
```

Project scope is the default. It requires an explicit project or one resolved
deterministically from the current directory, reports its absolute root, and
exhaustively checks manifest paths, recursive source identity, CommonModules
repository and dependency state, every selected reference, disposable template
materialization, and the active environment evidence applicable to ordinary
project automation. Duplicate `.bas`, `.cls`, or `.frm` exported file names in
one document source set are failures. A `.frx` with no same-directory `.frm` is
a warning only when a same-name `.frm` exists elsewhere in the same source set;
`.frx` files with no same-name `.frm` anywhere are ignored. CommonModules drift
checks find installed source files in nested directories and fail when an
installed CommonModule has multiple matching source files. Project Doctor never
invokes `vba-debug-adapter doctor` and does not claim compilation, import, save,
or native-debug readiness.

When `commonModulesRepository` is configured, project Doctor validates its complete closed package before comparing installed entries. A missing, unreadable, or invalid package is a failure. A retained `orphaned: true` entry that is still absent is advisory, while an absent non-orphaned identity or a reappeared orphan identity is a stale-reconciliation warning directing the user to `common-module update`. Doctor never changes the marker or removes retained source. If any requested root is orphaned, dependency reachability remains indeterminate instead of producing prune-candidate advice.

For each document, project Doctor evaluates build and publish namespace profiles independently. It runs source preflight before Excel, then uses one disposable template copy to remove replaceable components, normalize references, and inspect actual final project, retained-component, protected-reference, and VBE-adopted identities. It imports no source, saves no workbook, deletes the copy, and reports each profile's conflicts in deterministic order.

One project Doctor run fixes Windows ACP once, then captures each document's
recursive inventory, text sources, and matching same-directory `.frx` bytes
once. Source layout, installed CommonModules drift, and both profiles use this
same evidence without rereading caller files. The external CommonModules
repository retains its independent package authority. Unpaired sidecars are
inventoried for layout findings but their bytes are not read.

Build includes test-only and locally excluded sources. Publish ignores manifest
test-only sources before interpreting their captured content; local exclusion
markers require successful strict decoding of the entire source, but may
exclude later identity, kind, sidecar, or lossless-ACP-projection failures.
Included CommonModules ignore local markers. A failed or incomplete capture is
reported without encoding fallback, retries, or an empty-success substitute.
Later authoring edits belong to the next run; Doctor does not lock or protect
concurrent authoring activity or change durable source/workbook/output files.

Environment scope rejects `--project`, performs no project discovery or
project/document access or source ACP acquisition, and starts one dedicated
owned Excel instance for its active probes. It always attempts to release that owned instance and never
terminates a pre-existing interactive Excel process. Its output contains
exactly these checks in order:

1. `platform.windows`
2. `excel.comStartup`
3. `excel.processOwnership`
4. `excel.vbideProjectAccess`
5. `excel.processCleanup`

Each check has a stable ID, `pass`, `warning`, `fail`, `unverified`, or
`skipped` status, machine-readable `details`, and a nonnegative duration. Only
a complete result in which all five checks pass is reusable proof for guided
project creation. The stable detail keys, in check order, are `isWindows`,
`dedicatedInstanceStarted`, `ownedByInvocation`, `projectAccessSucceeded`, and
`ownedProcessReleased`. A passing check uses `true`, a failed check uses
`false`, and a warning, unverified, or skipped check uses `null`.

JSON format is the closed Doctor schema `1.0`: one object with
`schemaVersion`, `toolVersion`, `scope`, nullable-or-absolute `project`,
aggregate `status`, `complete`, and ordered `checks`. Each check contains only
`id`, `status`, `message`, `durationMilliseconds`, and `details`. After command
handling begins, expected failures and incomplete diagnostics still emit one
valid result. A complete `pass` or `warning` exits `0`; `fail`, `unverified`,
or incomplete execution exits `1`. Aggregate priority is `fail`, then
`unverified` or `skipped`, then `warning`, then `pass`. Project JSON ends with
the exact five environment checks in their stable order. Direct cancellation
exits `130` only when owned-resource cleanup is proven and no observed failure
would be hidden.

## vba-project.json

`vba-project.json` is the project manifest generated by `vba-dev new excel`. Commands use it to resolve the project root, document definitions, selected document, source directory, template workbook, bin output, publish output, CommonModules repository, installed CommonModules, VBA project references, and command defaults.

Generated manifests are written as UTF-16LE with BOM. Paths are relative to the project root unless an absolute path is required.

Example:

```json
{
  "schemaVersion": 1,
  "projectName": "SampleProject",
  "primaryDocument": "SampleProject",
  "documents": {
    "SampleProject": {
      "kind": "excel",
      "sourcePath": "src/SampleProject",
      "templatePath": "src/SampleProject/SampleProject.xlsm",
      "binPath": "bin/SampleProject/SampleProject.xlsm",
      "publishPath": "publish/SampleProject/SampleProject.xlsm",
      "commonModules": [
        {
          "name": "Runtime",
          "moduleFile": "Runtime.bas",
          "requested": true,
          "testOnly": false,
          "orphaned": false
        },
        {
          "name": "CommonDependency",
          "moduleFile": "CommonDependency.cls",
          "requested": false,
          "testOnly": true,
          "orphaned": false
        }
      ],
      "references": [
        {
          "name": "Microsoft Excel 16.0 Object Library",
          "requested": true
        }
      ]
    }
  },
  "commonModulesRepository": "../common_modules_repo",
  "commandDefaults": {
    "test": {
      "format": "text"
    },
    "excelAutomation": {
      "workbookOpenTimeoutSeconds": 300,
      "workbookSaveTimeoutSeconds": 300
    }
  }
}
```

| Field | Description |
| --- | --- |
| `schemaVersion` | Manifest schema version. Current value is `1`. |
| `projectName` | Project name. |
| `primaryDocument` | Default document used by document-scoped commands when `--document` is omitted. |
| `documents` | Document definitions keyed by document name. |
| `documents.<document>.kind` | Document kind. Currently only `excel` is supported. |
| `documents.<document>.sourcePath` | Recursive DocumentSourceSet directory containing the template workbook and exported VBA source. `.bas`, `.cls`, and `.frm` file identity is flat by exported file name. |
| `documents.<document>.templatePath` | Source template workbook used by `build` and `publish`. |
| `documents.<document>.binPath` | Workbook generated by `build` and used by default by `test` and `export`. |
| `documents.<document>.publishPath` | Workbook generated by `publish`. |
| `documents.<document>.commonModules[]` | Installed CommonModules entries for the document. |
| `documents.<document>.commonModules[].name` | Extensionless CommonModuleName resolved through the CommonModules manifest. |
| `documents.<document>.commonModules[].moduleFile` | Flat extension-including source file identity recorded when the module is installed or updated. |
| `documents.<document>.commonModules[].requested` | `true` when explicitly requested; `false` when installed as a dependency. |
| `documents.<document>.commonModules[].testOnly` | `true` when publish excludes the source; build still imports it normally. |
| `documents.<document>.commonModules[].orphaned` | `true` when the latest successful reconciliation conclusively retained an identity absent from the complete repository package. |
| `documents.<document>.references[]` | Desired VBA project references for the document. |
| `documents.<document>.references[].name` | Human-visible `Reference.Description`-style reference name. |
| `documents.<document>.references[].requested` | `true` when selected directly; `false` when retained only as a CommonModules dependency. |
| `commonModulesRepository` | CommonModules repository path, or `null` when no repository is discovered. |
| `commandDefaults.test.format` | Default test output format. The generated value is `text`. |
| `commandDefaults.test.executionTimeoutSeconds` | Optional test macro execution timeout in positive whole seconds. The built-in default is `600`. |
| `commandDefaults.excelAutomation.workbookOpenTimeoutSeconds` | Optional workbook-open timeout in positive whole seconds. The built-in default is `300`. |
| `commandDefaults.excelAutomation.workbookSaveTimeoutSeconds` | Optional workbook-save timeout in positive whole seconds. The built-in default is `300`. |

Manifest mutation commands transiently own the sibling marker `vba-project.json.vba-dev.lock`. `vba-dev` never edits ignore files, but repository owners may optionally add that exact entry to `.gitignore`.

Workbook open and save timeouts are project-level manifest defaults. `vba-dev`
does not expose per-invocation command-line options for these two values.
Build and publish use a dedicated hidden Excel process on an invocation-scoped
private desktop with a 30-second startup deadline, a 60-second deadline for each
reference attempt, a 30-second deadline for each module import, and a 5-second
cooperative cleanup grace period. They preserve an existing completed output on
failure or cancellation and atomically replace only the selected output after
the staged workbook and owned process have completed successfully.
