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
- the `vba-devtool` companion CLI;
- CommonModules package restore/update flows backed by `xls-common-devtools`
  GitHub Releases.

The extension is the primary user-facing surface. `vba-devtool` remains usable
as a standalone command, but the extension treats it as the command layer for
workbook-backed project operations.

## Phase 1: Extension command bridge

Build the VS Code-side command layer that detects a workbook-backed project and
invokes `vba-devtool`.

Expected capabilities:

- detect `project.json` from the active workspace or active VBA file;
- resolve the bundled or configured `vba-devtool` executable;
- check the companion CLI version expected by the extension;
- register VS Code commands for `doctor`, `build`, `test`, `publish`, `export`,
  CommonModules actions, and reference actions;
- show command output in a dedicated Output Channel;
- surface clear errors when Excel, VBIDE trust access, workbook locks, or
  project manifest problems block automation.

## Phase 2: Test Explorer integration

Connect `vba-devtool test --format ndjson` to the VS Code Testing API.

Expected capabilities:

- discover test modules and test procedures from exported VBA source;
- create a test tree grouped by project, document, module, and procedure;
- run all tests, one document's tests, one module's tests, or a single test;
- stream `ndjson` results into VS Code test states;
- map failures back to source locations when the test output provides enough
  identity;
- support both default build-before-test behavior and explicit no-build runs.

## Phase 3: Diagnostics and Problems integration

Turn command and language-server feedback into actionable VS Code diagnostics.

Expected capabilities:

- keep language-server syntax and semantic diagnostics in Problems;
- map `vba-devtool doctor` failures and warnings into project-level diagnostics;
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
`project.json` manually.

Expected capabilities:

- list installed CommonModules for the selected document;
- show `requested: true` roots and `requested: false` dependencies distinctly;
- run `common-module add`, `common-module update`, and future restore/source
  commands from VS Code;
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
- decide whether the extension bundles `vba-devtool.exe` or downloads a pinned
  CLI release on first use;
- publish standalone `vba-devtool.exe` artifacts from GitHub Releases;
- verify the CLI works on Windows 11 without requiring a separately installed
  .NET runtime;
- verify first-run setup on a clean Windows 11 machine with Office installed;
- guide users through `doctor` results instead of failing silently;
- document how projects pin CommonModules package versions.

## CommonModules package distribution

`xls-common-devtools` remains outside this repository and should publish a
versioned `common_modules_repo.zip` release artifact.

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

`vba-devtool` should restore that package explicitly instead of allowing normal
build/test/update commands to perform implicit network access. User projects
should pin the CommonModules package source and version so repeated builds stay
reproducible.

## Release channels

The long-term release model has two channels:

- VS Code Marketplace for the extension, language server, Test Explorer
  integration, and bundled or managed companion tooling;
- GitHub Releases for standalone `vba-devtool` artifacts and release notes.

The two channels must share version compatibility rules. The extension should
verify the companion CLI version before invoking project operations that depend
on a specific command contract.
