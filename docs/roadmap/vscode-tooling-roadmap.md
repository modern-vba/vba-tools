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
2. Discover `ProjectManifest` candidates from the active file or workspace and
   pass the selected project root to the CLI explicitly.
3. Build the shared command runner with Output Channel logging, error handling,
   and cancellation handling.
4. Implement `VBA Tools: Doctor` and the first-run "Run Doctor?" prompt.
5. Implement `Build`, `Test`, `Publish`, and `Export` Command Palette entries
   through the shared runner.
6. Implement CommonModules and reference command bridge entries.

## Phase 1: Extension command bridge

Build the VS Code-side command layer that detects a workbook-backed project and
invokes `vba-dev`.

Expected capabilities:

- detect `vba-project.json` by walking upward from the active VBA, manifest, or
  workbook-related file;
- use the manifest directory as the `WorkbookBackedProject` root and pass it to
  `vba-dev` explicitly as `--project`;
- choose from workspace `vba-project.json` candidates when no active file determines
  one project unambiguously;
- resolve the bundled `vba-dev` executable by default, or an explicit
  `vbaTools.devtool.path` override when configured;
- check the companion CLI command contract expected by the extension and reject
  incompatible explicit CLI path overrides;
- obtain CLI `toolVersion`, `contractVersion`, and per-command schema versions
  from `vba-dev capabilities --format json`;
- avoid implicit `PATH` discovery for the companion CLI, while allowing an
  explicit terminal command to prepend the resolved CLI directory to that
  terminal's environment;
- register VS Code commands for `doctor`, `build`, `test`, `publish`, `export`,
  CommonModules actions, reference actions, and opening a `vba-dev` terminal;
- keep Excel COM, VBIDE, workbook import/export, workbook save, and
  workbook-backed test execution inside `vba-dev`; the extension must not
  automate Excel directly;
- activate on VBA files, workbook-backed project manifests, and explicit
  `vbaTools.*` commands rather than using always-on activation;
- run project discovery during command execution so limited activation does not
  prevent commands from finding the active `WorkbookBackedProject`;
- expose initial Command Palette entries for `Doctor`, `Build`, `Test`,
  `Publish`, `Export`, `Add Common Module`, `Update Common Modules`,
  `Add Reference`, `Remove Reference`, `List References`, and
  `Open vba-dev Terminal`;
- keep project creation, future restore commands, internal capabilities checks,
  and no-build test runs out of the initial user-facing command palette; users
  who need project creation can run `vba-dev new excel` explicitly from the
  `vba-dev` terminal;
- show command output in a dedicated Output Channel;
- surface clear errors when Excel, VBIDE trust access, workbook locks, or
  project manifest problems block automation.
- wire VS Code command and Test Run cancellation to the spawned `vba-dev`
  process, and rely on CLI-side cleanup for workbooks, Excel instances, and
  temporary outputs;
- for initial `doctor` command integration, show full output in the dedicated
  Output Channel and use a notification only when blocking issues are found.
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
- invoke `vba-dev test` directly for the default run profile, leaving
  build-before-test behavior inside the CLI command contract;
- expose a separate non-default `Run Tests Without Build` profile that invokes
  `vba-dev test --no-build --format ndjson` for explicit fast reruns against
  existing generated output;
- stream `runStarted`, `testStarted`, `testFinished`, and `runFinished`
  `ndjson` events into VS Code test states;
- use `testFinished` project, document, module, and procedure identity to add
  discovered leaf tests after a run;
- report `testFinished` failures as individual test failures;
- report build failures, Excel automation failures, VBIDE trust failures,
  workbook locks, manifest errors, reference-resolution failures, and abnormal
  CLI exits as project-level or document-level test run errors rather than
  individual test failures;
- report user cancellation as a cancelled run scope, not as skipped tests or
  failed assertions;
- map failures back to source locations when the test output provides enough
  identity;
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
- warn before overwriting or cleaning the manifest-resolved DocumentSourceSet;
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
- add and remove references by human-visible `Reference.Description` name;
- surface ambiguous or missing reference names clearly;
- connect reference-related `doctor`, `build`, and `publish` failures to
  command output and Problems;
- add candidate search only after the local resolver behavior is stable enough
  to avoid misleading users.

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
- exclude the `tools/vba-dev` source tree from VSIX packaging while
  including only the published CLI artifact path;
- publish standalone `vba-dev.exe` artifacts from GitHub Releases;
- support a user or developer override for the companion CLI path;
- verify the resolved CLI command contract before project operations;
- verify the CLI works on Windows 11 without requiring a separately installed
  .NET runtime;
- verify first-run setup on a clean Windows 11 machine with Office installed;
- guide users through `doctor` results instead of failing silently;
- document how projects pin CommonModules package versions.

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
extension should ask the user before invoking restore. User projects should pin
the CommonModules package source and version if package acquisition becomes
available so repeated builds stay reproducible.

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
