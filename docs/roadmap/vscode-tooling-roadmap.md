# VS Code tooling roadmap

This roadmap records the planned development path from the unified VBA Tools
repository toward Marketplace-ready VS Code tooling for workbook-backed VBA
projects. It complements ADR 0004, which records the repository boundary
decision.

## Product direction

VBA Tools should feel like one VS Code product, even though it is implemented by
several components:

- the VS Code extension;
- the VBA language server;
- VS Code Test Explorer integration;
- the `vba-dev` companion CLI;
- CommonModules package restore/update flows backed by `xls-common-devtools`
  GitHub Releases.

The extension is the primary user-facing surface. `vba-dev` remains usable
as a standalone command, but the extension treats it as the command layer for
workbook-backed project operations.

## Initial implementation sequence

The first implementation batch should complete the Phase 1 command bridge before
starting Test Explorer work. Test Explorer integration depends on a shared,
validated command runner and project selection flow.

1. Resolve the bundled or configured `vba-dev` executable and validate
   `vba-dev capabilities --format json`.
2. Pass that exact absolute path to the C# language server through
   `--vba-dev`; have the server validate the supplied capabilities once at
   startup, including `reference list` JSON schema `1.0`.
3. Resolve and independently validate the bundled or explicitly configured
   `vba-debug-adapter` against `vba-debug-adapter-contract.json`; keep an
   invalid explicit adapter override strict.
4. Discover `ProjectManifest` candidates from the active file or workspace and
   pass the selected project root to the CLI explicitly.
5. Build the shared command runner with Output Channel logging, error handling,
   and cancellation handling.
6. Implement `VBA Tools: Doctor` by running `vba-dev doctor` and then
   `vba-debug-adapter doctor --format json`, continuing with the second result
   when the first fails, followed by the first-run "Run Doctor?" prompt.
7. Implement `Build`, `Test`, `Publish`, and `Export` Command Palette entries
   through the shared runner.
8. Implement CommonModules and reference command bridge entries.

## Phase 1: Extension command bridge

Build the VS Code-side command layer that detects a workbook-backed project and
invokes `vba-dev`.

Expected capabilities:

- detect the nearest `vba-project.json` from the active file, require a usable
  on-disk `CommandPaletteManifestSelectionProjection`, and fail rather than
  falling past an existing unusable nearest manifest; only when no containing
  manifest exists, use the sole selection-capable workspace project or ask the
  user to choose a `CommandPaletteProjectTarget`, without replacing full
  `VbaDev` validation;
- resolve a document-scoped command to a `CommandPaletteDocumentTarget` from
  the active `ExportedVbaSource`, the selected project's sole document, or an
  explicit document QuickPick whose initial focus follows
  `CommandPaletteDocumentFocus`;
- pass `--project` and `--document` explicitly for every document-scoped
  Command Palette invocation, while keeping project-only commands such as
  Doctor and `common-module update` free of an artificial document selector;
- apply ADR 0033's preflight, outcome, post-mutation coherence, divergence, and
  same-manifest busy guards around extension-managed Reference and CommonModules
  manifest mutations without sending editor state to `vba-dev` or automatically
  merging manifest content;
- resolve the bundled `vba-dev` executable by default, or an explicit
  `vbaTools.devtool.path` override when configured;
- check the companion CLI command contract expected by the extension, warn when
  an explicit CLI override is missing or incompatible, and fall back to the
  compatible bundled CLI for the whole extension session;
- report configured and effective CLI paths through `VBA Tools: Doctor` when a
  fallback occurs;
- resolve `vba-debug-adapter.exe` independently, allow only the explicit
  `vbaTools.debugAdapter.path` override or bundled artifact, and do not fall back
  when that explicit override is invalid;
- obtain CLI `toolVersion`, `contractVersion`, and per-command schema versions
  from `vba-dev capabilities --format json`;
- require CLI feature `build.sourceSnapshot` version `1.0` for the debug adapter
  without coupling it to the CLI tool version or complete command contract;
- validate adapter contract `1.0`, protocol `1.1`, stdio transport,
  lowercase-hex-32 session IDs, cleanup and Doctor commands, and Doctor schema
  `1.0` from its side-effect-free capability response;
- bind Restart preparation to the adapter session, canonical project, manifest
  document, original target module and procedure, DAP request sequence, and
  session-local generation; capture the bound document rather than the active
  editor, and retain the old session on stale identity or a removed target;
- have the language server validate the supplied effective CLI once at startup,
  continue with registry-only fail-closed reference discovery if that validation
  fails, and never rediscover or replace the executable itself;
- avoid implicit `PATH` discovery for the companion CLI, while allowing an
  explicit terminal command to prepend the resolved CLI directory to that
  terminal's environment;
- register VS Code commands for guided Excel VBA project creation, `doctor`,
  `build`, `test`, `publish`, `export`, CommonModules actions, reference actions,
  and opening a `vba-dev` terminal;
- keep Excel COM, VBIDE, workbook import/export, workbook save, and
  workbook-backed test execution inside `vba-dev`; the extension must not
  automate Excel directly;
- activate on VBA files, workbook-backed project manifests, and explicit
  `vbaTools.*` commands rather than using always-on activation;
- run project discovery during command execution so limited activation does not
  prevent commands from finding the active `WorkbookBackedProject`;
- expose initial Command Palette entries for `Create Excel VBA Project`,
  `Doctor`, `Build`, `Test`, `Publish`, `Export`, `Add Common Module`,
  `Update Common Modules`, `Add Reference`, `Remove Reference`,
  `List References`, and `Open vba-dev Terminal`;
- expose `Create Excel VBA Project` without a context-dependent `when` clause so
  it remains available from an Empty Window and workspaces with or without an
  existing project, while keeping future restore commands, internal capabilities
  checks, and no-build test runs out of the initial user-facing command palette;
- show command output in a dedicated Output Channel;
- surface clear errors when Excel, VBIDE trust access, workbook locks, or
  project manifest problems block automation.
- wire VS Code command and Test Run cancellation to the spawned `vba-dev`
  process, and rely on CLI-side cleanup for workbooks, Excel instances, and
  temporary outputs;
- for initial `doctor` command integration, show full output in the dedicated
  Output Channel, run the project CLI diagnostic before the independent debug
  diagnostic while continuing after either failure, and use a notification only
  when blocking issues are found;
- consume adapter Doctor JSON schema `1.0` even on nonzero exit, display every
  stable check, and distinguish a reported failed or unverified check from
  malformed, missing, or incomplete command output;
- enforce adapter Doctor's fixed per-stage deadlines and cancellation contract,
  classifying a stage timeout as unverified while keeping process close and
  workspace deletion on separate five-second cleanup boundaries;
- after detecting a `WorkbookBackedProject` for the first time in a workspace,
  prompt the user to run `doctor` instead of running it automatically;
- remember a workspace-level "do not ask again" choice for the first-run doctor
  prompt.

## Phase 2: Test Explorer integration

Connect `vba-dev test --format ndjson` to the VS Code Testing API.

Expected capabilities:

- create initial Test Explorer nodes for each discovered `WorkbookBackedProject`
  and `DocumentSourceSet`;
- add module and `TestProcedure` nodes after a project or document test run
  reports them;
- treat `DocumentSourceSet` test output as the source of truth for runnable leaf
  tests;
- run all tests, one document's tests, one known module's tests, or one known
  test procedure;
- capture a caller-owned complete source snapshot without saving editor buffers
  and invoke `vba-dev test --source-snapshot <snapshot-directory> --format
  ndjson` for the default run profile;
- fix debug and test snapshot inventories at capture start from one complete
  disk inventory overlaid by every then-open source-set-contained dirty
  file-backed editor, including an in-scope path not yet on disk; capture each
  value once without final stability checks or automatic retry, reject pathless
  participating documents and unreadable selected paths, and keep later changes
  for the next invocation without adding editor awareness to `vba-dev`;
- expose a separate non-default `Run Tests Without Build` profile that invokes
  `vba-dev test --no-build --format ndjson` for explicit fast reruns against
  existing generated output;
- support `vba-dev test --timeout-seconds` and
  `commandDefaults.test.executionTimeoutSeconds` with CLI-over-manifest-over-600
  precedence for the test macro execution stage in every test mode;
- add no separate VS Code test timeout or shorter watchdog; use the CLI timeout
  outcome and process cancellation contract;
- never save or snapshot source for the no-build profile; retain outcomes and
  test identities but omit navigation with a non-failing warning when scoped
  source is already dirty;
- consume the initial batched `runStarted`, `testStarted`, `testFinished`, and
  `runFinished` NDJSON replay after mandatory owned-process cleanup; reserve
  true real-time streaming for a later schema revision;
- treat a valid matching `runFinished` as authoritative for a completed run:
  nonzero with failed/error test outcomes is not an infrastructure error, while
  nonzero without the terminal record is;
- use `testFinished` project, document, module, and procedure identity to add
  discovered leaf tests after a run;
- report `testFinished` failures as individual test failures;
- report build failures, Excel automation failures, VBIDE trust failures,
  workbook locks, manifest errors, reference-resolution failures, and abnormal
  CLI exits as project-level or document-level test run errors rather than
  individual test failures;
- report user cancellation as a cancelled run scope, not as skipped tests or
  failed assertions;
- map every discovered `TestProcedure` node and failure message to the
  declaration-name range when `testFinished` provides a
  `TestProcedureSourceLocation`;
- keep project, document, and module nodes as runnable scopes without assigning
  a guessed source target;
- invalidate a document's output-derived module and procedure nodes when its
  exported source or project definition changes;
- retain outcomes from an immutable snapshot run when source changes during
  execution, but omit its stale module/procedure discovery and locations and
  report a non-failing Test Run warning;
- preserve exact disk bytes for clean source and sidecars, encode dirty source
  as UTF-8 with or without BOM, BOM-marked UTF-16 LE or BE, or the
  operation-fixed active Windows ANSI code page according to its current editor
  encoding, require a lossless round trip, and remove the caller-owned snapshot
  directory after `vba-dev test` exits;
- apply bounded retries to extension-owned snapshot deletion and report a
  retained absolute path as a housekeeping warning without changing completed
  test outcomes;
- detect every ordinary, explicit-import, and snapshot text source through a
  recognized BOM, strict UTF-8, then strict operation-fixed `GetACP` encoding
  without replacement-character or detection fallback; choose UTF-8 for
  dual-valid bytes and canonicalize ACP 65001 as UTF-8;
- before implementing that encoding path, probe `VBComponents.Import` in real
  Excel with equivalent non-ASCII ACP, BOM-less UTF-8, BOM-marked UTF-8, and
  BOM-marked UTF-16 LE and BE `.bas`, `.cls`, and `.frm` plus `.frx` inputs;
  require `CodeModule` text matching `VbaCodeModuleProjection.CodeModuleLines`
  and matching UserForm state both immediately and after save/reopen, exclude
  document modules, and record the environment's VBE export encoding;
- use the recorded Excel 16.0 / ACP 932 result as a blocking input to the
  import-representation design: raw ACP passed all component kinds, BOM-less
  UTF-8 corrupted non-ASCII code, UTF-8 BOM corrupted component headers, and
  UTF-16 LE and BE were rejected;
- for every ordinary or snapshot `VBComponents.Import` path, preserve the
  accepted source bytes but derive an invocation-internal VBE-facing mirror in
  the operation-fixed ACP; require strict source decoding, strict ACP encoding,
  and exact ACP decode-back equality before Excel, while copying `.frx`
  sidecars byte-for-byte beside their staged `.frm`;
- after every component import and before save, verify its name, kind, and exact
  `VbaCodeModuleProjection`; exclude class `VERSION`/`BEGIN`/`END`, `Attribute`,
  UserForm designer, and terminal-newline export records, model the known
  UserForm leading blank, and assume no automatic VBE insertion or
  normalization beyond that projection;
- stop per-command verification at component identity, kind, and projected
  code; do not re-export every component or present partial COM-visible
  metadata checks as exhaustive;
- treat `VBComponents.Import` as authoritative for export-only metadata and
  UserForm/`.frx` state in a supported environment, while using representative
  real-Excel import/save/reopen fixtures to detect compatibility regressions
  rather than claim per-input runtime proof;
- make those fixtures semantic: compare expected class and member attributes,
  UserForm control structure and selected properties, and readability of
  sidecar-backed binary values immediately and after reopen; do not require
  byte-identical re-exported text or `.frx` serialization;
- verify components and projected code after import and before save, but do not
  reopen every generated workbook solely to repeat that check; treat save
  failure as command failure and leave save/close/reopen fidelity to the
  release-blocking real-Excel fixture;
- include valid non-default class and member attributes, host-ACP-representable
  non-ASCII text, a nested intrinsic `Frame`/`Label`/`TextBox` control tree, and
  an intrinsic `Image` or equivalent `.frx`-backed value in the minimum fixture;
  exclude third-party ActiveX controls from the baseline;
- place the fixture in the existing `WindowsExcelIntegration` category so
  `test:windows-excel-integration` and `verify:release:windows-excel` run it,
  without adding an installed-Excel dependency to ordinary unit or pull-request
  suites;
- do not whitelist runtime ACPs: accept a `GetACP` code page when .NET provides
  its strict encoding and all import round trips and post-import verification
  pass; keep Excel 16.0 / ACP 932 as the initial release-blocking empirical
  baseline;
- test code-page selection and strict conversion deterministically for ACP 932,
  1252, and 65001 without Excel, record Excel version and ACP in real-Excel
  results, and extend rather than overstate the tested environment matrix;
- fail snapshot build and test before Excel when any clean or dirty text source
  cannot strict-decode and re-encode to the same source bytes or cannot
  losslessly round-trip through the VBE-facing ACP; keep only later
  source-location mapping failures non-failing;
- report unavailable or ambiguous source locations as non-failing Test Run
  output warnings without changing test outcomes or showing popup
  notifications;
- treat failure to release an owned test Excel process as a command-level error,
  but preserve test outcomes and report only a warning when an internal
  workspace remains after bounded post-release deletion retries;
- avoid showing standalone VBA files that do not belong to a `ProjectManifest`
  in Test Explorer;
- keep missing or unusable no-build generated output as a command error instead
  of implicitly building.

## Phase 3: Diagnostics and Problems integration

Turn command and language-server feedback into actionable VS Code diagnostics.

Expected capabilities:

- keep language-server syntax and semantic diagnostics in Problems;
- map `vba-dev doctor` failures and warnings into project-level diagnostics;
- promote `doctor` output to Problems only after a stable machine-readable
  output format can provide diagnostic owner, severity, URI, and range mapping;
- map build, reference-resolution, and CommonModules dependency failures into
  actionable messages;
- add Quick Fix entries only when the repair operation is deterministic and
  non-destructive.

## Phase 4: Safe workbook round-trip workflows

Make source/workbook synchronization predictable for daily development.

Expected capabilities:

- expose `export` with clear source-set and explicit destination behavior;
- keep `vba-dev export` non-interactive so direct CLI invocation remains
  automation-safe and constitutes consent to its documented destination
  semantics;
- before the Command Palette invokes any cleanup-enabled export, including the
  manifest-resolved DocumentSourceSet and an explicit `--to` destination, show
  a modal confirmation containing the resolved absolute destination and stating
  that existing exported source may be overwritten and stale `.bas`, `.cls`,
  `.frm`, and `.frx` files deleted;
- when that confirmation is cancelled, do not start `vba-dev`; do not show the
  confirmation for an export mode that does not clean its destination;
- detect workbook locks and active Excel automation blockers;
- keep hidden Excel COM automation isolated from the user's interactive Excel
  session;
- document how source edits, workbook edits, `build`, and `export` should be
  sequenced to avoid accidental loss.

## Phase 5: CommonModules UX

Make CommonModules management understandable without requiring users to inspect
`vba-project.json` manually.

Expected capabilities:

- list installed CommonModules for the selected document;
- show `requested: true` roots and `requested: false` dependencies distinctly;
- run `common-module add`, `common-module update`, and a future
  `common-module restore` or equivalent explicit package-restore command from
  VS Code;
- show what files and manifest entries changed after add/update;
- visualize missing dependencies and unreachable dependency entries reported by
  `doctor`.

## Phase 6: VBA project reference UX

Expose manifest-defined VBA project references through VS Code commands.

Expected capabilities:

- list references for the selected document;
- list registered references not yet added to the selected document through
  `reference list --available`, with VBE-equivalent resolution applied only
  where registry identity remains ambiguous;
- compare trimmed reference names with `OrdinalIgnoreCase`, choose the
  ordinal-minimum registry spelling for available output, completion, and new
  manifest entries, and preserve existing manifest spelling on configured
  output and add no-ops;
- when project and document are both implicit and upward discovery finds no
  manifest, let `reference list --available` warn and fall back to environment
  scope, using a blank Excel workbook only for required ambiguity probes;
- in environment scope, list every distinct registered description without
  subtracting references already checked in the blank probe workbook;
- do not use that fallback for explicit project/document selection, a malformed
  manifest, or a document absent from a valid manifest;
- return reference-list JSON schema `"1.0"` with required `scope`, nullable
  project/document fields, and a required warnings array; a successful
  environment fallback remains complete and exits zero, while language-server
  refresh accepts only project scope;
- ignore unknown additive JSON properties and warning/diagnostic codes, but
  reject unknown control discriminators, missing or mistyped required
  properties, and known status-inconsistent properties;
- serialize identities with lowercase brace-free GUID `D` form and integer
  versions from 0 through 65535, deduplicate by that identity, and order
  candidates by GUID, major, then minor ascending independently from
  version-fallback order;
- avoid opening a source template for `reference add`, either list mode, or
  Doctor when registry resolution is zero or unique; use a same-name baseline
  reference only after an ambiguity probe is already required;
- add and remove references by human-visible `Reference.Description` name;
- expose read-only manifest-reference resolution through
  `reference list --format json` for background catalog refresh;
- consume a schema-valid, complete configured-reference response per entry even
  on nonzero exit, committing resolved siblings while preserving each
  conclusively ambiguous or unavailable reference's last-known-good catalog;
  treat an unverified entry as incomplete and reject the whole response when
  its schema, project, document, mode, or completeness is untrustworthy;
- apply the 60-second reference-normalization deadline independently to each
  `References.AddFromGuid` attempt, without a whole-list deadline;
- continue after conclusive candidate rejection, but stop further VBE work and
  do not relaunch Excel in the same invocation after timeout, process loss, or
  another failure that makes the owned probe process untrustworthy;
- report the root entry's specific unverified reason, mark later unattempted
  probe-dependent entries as `probeAborted`, and include one
  `probeProcessUntrusted` top-level diagnostic;
- do not inspect a project source template for registry-only results or replace
  an unavailable project baseline with a blank workbook; mark probe-dependent
  entries `probeAborted` and add `probeBaselineUnavailable` when either the
  project or environment probe baseline cannot be prepared;
- after scope establishment, serialize cooperative cancellation when possible,
  preserve conclusive entries, mark unfinished entries `cancelled`, add an
  `operationCancelled` diagnostic, and reserve `probeAborted` for
  infrastructure-driven loss of probe trust;
- share one neutral registry catalog contract between `vba-dev` and the
  language server: hexadecimal TypeLib version/LCID parsing, one merged shared
  `HKEY_CLASSES_ROOT\TypeLib` scan, GUID version lineages, and no process-bitness
  preference or `Registry32`/`Registry64` union;
- skip registry records that cannot form a description, resolve readable names
  from any remaining valid identities, and retain a readable name with no valid
  identity as `unavailable / noUsableIdentity`;
- aggregate individually skipped malformed registrations into a non-failing
  `malformedRegistrationsSkipped` warning, but use
  `registryCatalogIncomplete` and `complete: false` when enumeration may have
  missed whole names;
- surface ambiguous or missing reference names clearly;
- connect reference-related `doctor`, `build`, and `publish` failures to
  command output and Problems;
- expose lightweight registry-name completion without starting Excel or VBE,
  while leaving final VBE-equivalent reference validation to the explicit
  `reference add` invocation;
- skip individually malformed TypeLib registrations during completion, but
  return no dynamic reference candidates when a catalog-level registry failure
  makes the scan incomplete; keep completion quiet and report that failure only
  from explicit reference or Doctor commands.

## Phase 7: Language-server practical features

Continue improving the editor intelligence that makes exported VBA source feel
native in VS Code.

Priority capabilities:

- document symbols and workspace symbols;
- go to definition and find references coverage for more source forms;
- rename support for safe source-defined targets;
- hover and signature help improvements;
- completion refinements for host object models and project source;
- diagnostics that fail closed rather than guessing when source is malformed;
- formatter improvements after the semantic model is stable.

## Phase 8: Distribution and first-run setup

Prepare the Marketplace and GitHub Releases distribution path.

Expected capabilities:

- package the VS Code extension for Marketplace publication;
- bundle a self-contained Windows `vba-dev.exe` in the Marketplace
  extension by default under `bin/vba-dev/win-x64/vba-dev.exe`;
- bundle the independently versioned self-contained Windows
  `vba-debug-adapter.exe` under
  `bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe`;
- exclude the .NET source trees from VSIX packaging while including only the
  two published executable artifact paths;
- publish standalone `vba-dev.exe` artifacts from GitHub Releases;
- support the fallback-capable CLI override and the independently strict debug
  adapter override;
- verify both resolved capability contracts independently before their
  operations;
- package `vba-debug-adapter-contract.json` independently from
  `vba-dev-contract.json`, whose target contract advertises
  `featureVersions.build.sourceSnapshot` and no debug-adapter protocol;
- verify both executables work on Windows 11 without requiring a separately
  installed .NET runtime;
- verify first-run setup on a clean Windows 11 machine with Office installed;
- guide users through `doctor` results instead of failing silently;
- document that the initial project contract does not pin a CommonModules
  package version, artifact hash, or lock file.

## CommonModules package distribution

`xls-common-devtools` remains outside this repository. A future release flow may
publish a versioned `common_modules_repo.zip` release artifact, but network
package acquisition is a future option rather than an implementation commitment
for the initial extension work.

The intended artifact shape is:

```text
common_modules_repo.zip
  common-modules-manifest.tsv
  VERSION
  SHA256SUMS
  *.bas
  *.cls
  *.frm
  *.frx
```

If network package acquisition is implemented later, `vba-dev` should own
package download, extraction, manifest validation, and source placement through
an explicit future `common-module restore` command or an equivalent restore
command. The VS Code extension should invoke that command instead of
implementing ZIP download or CommonModules manifest interpretation itself.

Normal build/test/publish commands should not perform implicit network access.
If a CommonModules package is missing and a restore command exists, the
extension should ask the user before invoking restore. The initial project
contract continues to use the currently configured package without recording a
package source, version, artifact hash, or lock file. Any future pinning
contract requires a separate decision.

## Release channels

The long-term release model has two channels:

- VS Code Marketplace for the extension, language server, Test Explorer
  integration, and bundled or managed companion tooling;
- GitHub Releases for standalone `vba-dev` artifacts and release notes.

The Marketplace extension bundles the companion `vba-dev.exe` by default,
while GitHub Releases also publish the same CLI as a standalone artifact. The
extension and CLI have separate release versions. The extension should declare
the CLI contract it requires, bundle a CLI version tested against that contract,
and verify the companion CLI contract before invoking project operations,
including when a user or developer overrides the bundled CLI path. The CLI
contract should be versioned separately from the CLI tool version so patch-level
tool fixes do not imply command or output schema incompatibility.
