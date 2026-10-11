# Changelog

All notable user-facing changes to the independently versioned `vba-dev` CLI
are recorded here. Extension changes remain in the repository root changelog.

## [0.1.0] - 2026-10-06

### Added

- Standalone Windows x64 self-contained packaging with the executable, symbols,
  CLI documentation, MIT license, and command contract.
- Canonical `--version` and capabilities metadata from one independent release
  version source.
- Workbook-backed build, test, publish, import, export, dependency, reference,
  Excel-free project check, and scoped active Doctor command surfaces.
- Caller-owned `build.sourceSnapshot` 2.0 and `test.sourceSnapshot` 2.0 immutable
  snapshot build/test inputs with active Windows code-page admission.
  Snapshot-output Build keeps its paired caller-selected output; snapshot Test
  prepares the selected source workbook without initiating Save.
- Source-workbook capability features `build.sourceWorkbook` 1.0,
  `test.sourceWorkbook` 1.0, and `export.sourceWorkbook` 1.0, plus
  `projectManifest.optionalBinPath` 1.0 for bin-free manifests.
- Managed `debug.sourceWorkbookPreparation` 2.0 for exact retained-workbook
  preparation without dirty-workbook confirmation or Save. Immutable capture,
  bound readiness, verified import, recovery, and cancellation remain mandatory.
- Bounded local diagnostic evidence for incomplete Build, Publish, and
  build-before-test source analysis, with the report path printed on failure.

### Changed

- Ordinary Build imports saved source into the exact `templatePath` workbook
  and saves it in place. It reuses an already-open Excel process/window and
  leaves it open; a closed source workbook opens hidden and closes afterward.
- Test, including no-build and snapshot modes, uses that source workbook without
  initiating Save. Borrowed workbooks remain open; owned hidden workbooks close
  without saving remaining changes. Normal/snapshot Test retains source analysis
  and import; no-build runs current workbook VBA without external-source import.
- Project Export reads an exact already-open source workbook's live VBA without
  saving or closing it. Closed-source Export reads its saved contents.
- New projects omit workbook-bin output and `binPath`. Supported legacy settings
  remain accepted with a deprecation/removal warning; manifest edits preserve an
  omitted setting and do not delete or move old workbook-bin files.
- Build/Test use terminal `[y/N]` consent for a dirty selected workbook, with
  `--interactive true` by default. `--interactive false` refuses required consent
  without waiting, mutation, or implicit approval; EOF declines. Debug preparation
  never prompts, and its retained `--interactive` option is compatibility-only.

### Breaking Changes

- Build saves the actual source workbook and its other unsaved workbook edits
  after consent, rather than producing a disposable development copy. Review
  saved source before Build and remove only deprecated `binPath` properties.
- Debug preparation replaces unsaved VBE-direct code without another
  confirmation, preserves cells and workbook lifetime, and omits only the
  independent source-analysis gate. Test and Debug never initiate Save, but VBA
  itself can save or cause irreversible effects; no whole-workbook rollback is
  promised.
- Select companion tools by their required capabilities, not the unchanged
  `vba-dev 0.1.0` label. A Debug preparation provider advertising feature `1.0`
  can still prompt and is incompatible with required feature `2.0`.

Publish and standalone Import remain unchanged. Explicit snapshot-output Build
and explicit-workbook Export keep their separate output and lifetime contracts.
The CLI contract remains `1.0`; Build/Publish result schemas are `3.0`, Test
NDJSON is `1.2`, and Debug preparation and New receipts remain `1.0`. Manifest
schema remains `1`; behavior features are versioned independently of these schemas.
