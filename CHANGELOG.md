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
- Native VBE debugging for supported public parameterless procedures and exact
  exported-source breakpoints.
- Windows x64 Marketplace packaging with bundled self-contained executables, so
  a separately installed .NET runtime is not required.

### Known Limitations

- The initial extension package targets Windows x64.
- Workbook automation and native VBE debugging require desktop Excel and trusted
  access to the VBA project object model. Editor-only language features do not
  require Excel.
