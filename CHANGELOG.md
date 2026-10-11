# Changelog

All notable user-facing changes to the VBA Tools extension are recorded here.
The extension history is versioned independently from the bundled `vba-dev`
CLI history.

## [0.1.1] - 2026-10-10

This is the first publicly available VBA Tools pre-release. Version 0.1.0 was
tagged but was not published to the VS Code Marketplace or as a GitHub Release.

### Added

- Windows x64 VBA language assistance for exported `.bas`, `.cls`, and `.frm`
  files, including diagnostics, formatting, completion, navigation, symbols,
  and semantic Rename.
- Workbook build, test, publish, import/export, CommonModules, and reference
  workflows through bundled `vba-dev`, with Test Explorer integration.
- Native VBE debugging for supported public parameterless procedures, Doctor
  diagnostics, and an environment-scoped UserForm Event catalog.
- Self-contained companion executables and a standalone `vba-dev` 0.1.0 ZIP;
  no separately installed .NET runtime is required.

### Changed

- Ordinary Build imports saved source into the exact manifest-selected source
  workbook and saves it in place, including other unsaved workbook edits after
  required consent. An already-open workbook retains its Excel process/window;
  a closed workbook opens hidden and closes after processing.
- Test runs in that same source workbook without initiating Save. An already-open
  workbook remains open; a closed workbook opens hidden and closes without saving
  remaining import/test changes. Dirty-workbook consent still applies to Build
  and Test, including execution-only no-build Test.
- Debug F5 and Restart reuse the exact source workbook and Excel process/window
  without dirty-workbook confirmation or automatic workbook/editor saving.
  Debug alone omits the independent pre-launch source-analysis gate; immutable
  capture, encoding, target/breakpoint safety, recovery, and native VBE checks
  remain. Stop resets execution without closing or saving the workbook.
- Project Export reads the selected source workbook, including live VBE changes
  when it is already open, without saving or closing that session.

### Breaking Changes

- Ordinary Build no longer creates a disposable workbook-bin output. Review the
  saved exported source before building: `templatePath`, conventionally
  `src/<document>/<document>.xlsm`, now identifies the workbook changed and saved.
- Debug replaces unsaved VBE-direct code with captured source without an extra
  confirmation. Cell edits remain, but neither Test nor Debug is a sandbox or
  whole-workbook rollback: VBA itself can explicitly save or cause other effects.
- Legacy `binPath` is optional, deprecated, and scheduled for removal. Supported
  existing values emit a warning; remove only the property to retire the setting.
  New projects omit it, and commands do not automatically delete or move old
  workbook-bin files or the configured source workbook.
- Update the extension and companion tools together. Source-workbook features
  and no-confirmation Debug preparation must pass capability admission; the same
  tool release version or unchanged result schema does not prove compatibility.

Publish and standalone Import remain unchanged. Explicit snapshot-output Build
and explicit-workbook Export retain their separate output and lifetime contracts.

### Fixed

- CommonModules Add and Update no longer treat a completed result as untrusted
  when the selected project and CLI receipt use equivalent Windows short and
  long paths. Side-effecting operations are not retried.

### Requirements

- Windows 10 or 11 on x64 hardware and VS Code 1.137.0 or later. Workbook
  automation and native VBE debugging require desktop Excel with trusted VBA
  project access; editor-only language features do not require Excel.

### Known Limitations

- The extension targets Windows x64. Worksheet and `ThisWorkbook` code-behind
  and control-instance Event intelligence are not supported.
- Intermittent source-analysis and companion-process failures observed on an
  affected Windows host remain under investigation ([#409], [#415]). The
  capability-probe retry does not fix their underlying cause.

## [0.1.0] - 2026-10-06

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
- One environment-scoped built-in UserForm Event catalog acquired
  asynchronously from a generated blank workbook, with explicit refresh,
  status and Output recovery, and Excel-free synchronous editor requests.
- Two-stage, name-only completion for intrinsic Host Event, `WithEvents`, and
  `Implements` declarations, with coalesced contract names, retained signature
  variants, and stateless semantic continuation in the VS Code client.
- Semantic module-identity Rename across authoritative exported metadata,
  resolved uses, and matching source-unit files, with CommonModules ownership,
  project/reference collision authority, source-owned form-sidecar
  and case-only handling, and complete-plan preflight safeguards.
- Immutable debug snapshots that include dirty editor content without saving and
  run in disposable same-filename workbooks without changing persistent outputs.
- Bound Restart Debugging with generation-scoped snapshots, owned process-tree
  termination, session leases, crash cleanup, and next-start stale reaping.
- Windows x64 Marketplace packaging with bundled self-contained executables, so
  a separately installed .NET runtime is not required.

### Fixed

- A temporarily unreadable closed source reports `disk-source-unavailable`
  without stopping diagnostics for other open VBA documents.
- VBA source breakpoints are available in the editor, and extension activation
  completes before the Doctor prompt is considered ready.
- Abnormal companion-process exits are identified in VBA Tools Output by
  executable and exit status. Only a side-effect-free startup capability probe
  can be retried once; workbook operations and debug execution are not replayed.

### Requirements

- Windows 10 or Windows 11 on x64 hardware and VS Code 1.137.0 or later.
- Workbook automation and native VBE debugging require desktop Excel and trusted
  access to the VBA project object model.

### Known Limitations

- The initial extension package targets Windows x64.
- Workbook automation and native VBE debugging require desktop Excel and trusted
  access to the VBA project object model. Editor-only language features do not
  require Excel.
- Worksheet and `ThisWorkbook` code-behind and control-instance Event
  intelligence are not supported; exported UserForms remain supported.
- Intermittent source-analysis and companion-process failures observed on an
  affected Windows host remain under investigation ([#409], [#415]). The
  capability-probe retry does not fix their underlying cause.

[#409]: https://github.com/modern-vba/vba-tools/issues/409
[#415]: https://github.com/modern-vba/vba-tools/issues/415
