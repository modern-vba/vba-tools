# Changelog

All notable user-facing changes to the VBA Tools extension are recorded here.
The extension history is versioned independently from the bundled `vba-dev`
CLI history.

## [0.1.0] - Unreleased

### Added

- VBA language server features for exported `.bas`, `.cls`, and `.frm` source,
  including diagnostics, formatting, completion, navigation, and symbols.
- Workbook-backed build, test, publish, export, CommonModules, and reference
  workflows through the bundled self-contained `vba-dev` CLI.
- Test Explorer integration for workbook-backed VBA test projects.
- A separately bundled self-contained `vba-debug-adapter.exe` for native VBE
  debugging of supported public parameterless procedures and exact breakpoints.
- An independent `vba-debug-adapter doctor --format json` diagnostic that proves
  native breakpoint, Continue, process ownership, and cleanup readiness without
  project input or persistent project changes.
- A combined `VBA Tools: Doctor` action that labels project automation and VBE
  diagnostics separately, validates both machine-readable diagnostic results, and
  cooperatively cancels VBE checks without bypassing terminal cleanup.
- Consumer-owned Host Event projection snapshots with extension-wide
  single-flight inspection, explicit refresh, status and Output recovery,
  last-known-good retention, and Excel-free synchronous editor requests.
- Two-stage, name-only completion for intrinsic Host Event, `WithEvents`, and
  `Implements` declarations, with coalesced contract names, retained signature
  variants, and stateless semantic continuation in the VS Code client.
- Semantic module-identity Rename across authoritative exported metadata,
  resolved uses, and matching source-unit files, with CommonModules and host
  ownership boundaries, project/reference collision authority, form-sidecar
  and case-only handling, and complete-plan preflight safeguards.
- Immutable debug snapshots that include dirty editor content without saving and
  run in disposable same-filename workbooks without changing persistent outputs.
- Bound Restart Debugging with generation-scoped snapshots, owned process-tree
  termination, session leases, crash cleanup, and next-start stale reaping.
- Windows x64 Marketplace packaging with bundled self-contained executables, so
  a separately installed .NET runtime is not required.

### Known Limitations

- The initial extension package targets Windows x64.
- Workbook automation and native VBE debugging require desktop Excel and trusted
  access to the VBA project object model. Editor-only language features do not
  require Excel.
