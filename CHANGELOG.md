# Changelog

All notable user-facing changes to the VBA Tools extension are recorded here.
The extension history is versioned independently from the bundled `vba-dev`
CLI history.

## [0.1.8] - 2026-10-24

### Changed

- Signature Help for Implements, WithEvents, and intrinsic event declarations
  follows the required contract while parameters are incomplete or being
  repaired. Empty lists select the first parameter; extra slots retain the
  signature without highlighting an unrelated parameter.
- Build, Test, and Debug preparation share explicit automation release proofs.
  Completed Build and Debug preparation survive deletion-only cleanup failures
  with warnings. Complete Test results survive purely late trusted cancellation;
  failed tests remain failed.
- Native debugging shares verified breakpoint transfer and current VBE command
  context checks across source and retained diagnostic sessions. Session ending
  revokes inspection access before cleanup and preserves the observed outcome.
- Bundle independently versioned `vba-dev` 0.1.2 and its matching standalone
  Windows x64 ZIP. See the [CLI changelog](https://github.com/modern-vba/vba-tools/blob/main/tools/vba-dev/CHANGELOG.md#012---2026-10-24).

### Fixed

- Reference Add and CommonModules Add/Update accept harmless concurrent reference
  additions, reduced or reordered requirements, and newly eligible documents
  with already-selected references. Unresolved required changes stop before
  source mutation; explicit reference intent, spelling, and order are preserved.
- Host compilation classification accepts 32-bit Excel on 64-bit Windows,
  derives Win64 from the host process, and prioritizes known contradictions over
  missing information without inventing live custom constants.
- Rename validates source revisions, authority, templates, paths, and file
  capabilities consistently. Confirmation is consumed once and cannot be reused;
  cancellation and stale response failures cannot dispose a newer operation.

### Breaking Changes

- Separately configured companions and custom integrations must support CLI
  `build.sourceWorkbook: 2.0`, `test.sourceWorkbook: 2.0`, and
  `debug.sourceWorkbookPreparation: 3.0`; debug adapters must support
  `debug.sourceWorkbook: 2.0`. Update the extension and its companions together.
  The top-level CLI contract remains `1.0` and Test NDJSON remains `1.2`.

### Known Limitations

- Native debugging remains limited to its existing supported targets and
  requires an available, correctly bound VBE command context.
- User VBA can still save or cause other effects during Test and Debug.

### Source-workbook Compatibility

- Build imports into the manifest-selected source workbook and saves it,
  including other unsaved workbook edits after consent. An open workbook keeps
  its existing Excel process and window.
- Test uses that same source workbook without initiating Save. A borrowed
  workbook stays open; an owned hidden workbook closes without saving.
- Debug F5 and Restart reuse it without dirty-workbook confirmation or Save.
  Source editors are not automatically saved. Stop resets native execution
  while leaving the workbook open.
- Legacy `binPath` remains optional and deprecated; it does not select the
  ordinary Build, Test, or Debug execution workbook.

Publish and standalone Import remain unchanged. Existing source-snapshot v2
and active-Windows-code-page admission requirements remain in effect alongside
this release's updated source-workbook capability versions.

### Requirements

- Windows x64, VS Code 1.137.0 or later, and desktop Excel with trusted VBIDE
  access for workbook operations. Bundled executables are self-contained.

## [0.1.7] - 2026-10-21

### Changed

- Bundle the independently versioned `vba-dev` 0.1.1 and its matching standalone
  Windows x64 ZIP. The CLI command contract remains `1.0`.

### Fixed

- Unqualified library calls no longer bind to unrelated public class or form
  instance members during source analysis and editor name resolution. Typed
  receivers and enclosing-instance access retain their existing behavior.
- Generated `vba-project.json` manifests and recovery files keep readable
  Japanese names and paths in canonical UTF-16LE with BOM and CRLF.
- Release packages exclude local diagnostic captures and keep packaged
  operator-guide contribution links usable outside the source checkout.

### Source-workbook Compatibility

- Build still saves the source workbook, including other unsaved workbook edits
  after consent. The manifest selects that workbook. An open workbook keeps its
  existing Excel process and window.
- Test still uses that same source workbook without initiating Save; a borrowed
  workbook stays open and an owned hidden workbook closes without saving.
- Debug F5 and Restart reuse it without dirty-workbook confirmation or Save.
  Source editors are not automatically saved. Stop resets execution while
  leaving the workbook open. VBA itself can still save or cause other effects.
- Legacy `binPath` remains optional and deprecated. It does not select the
  ordinary Build, Test, or Debug execution workbook.

Publish and standalone Import remain unchanged. Existing source-snapshot and
source-workbook capability admission requirements remain in effect.

## [0.1.6] - 2026-10-13

This is the first public VBA Tools pre-release candidate. Versions 0.1.0,
0.1.1, 0.1.2, 0.1.3, 0.1.4, and 0.1.5 were tagged but were not published to the VS Code Marketplace or as a
GitHub Release. This section carries forward their unreleased capabilities.

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

- Generated `vba-project.json` manifests preserve readable Japanese names and
  paths using canonical UTF-16LE with BOM and CRLF.
- Artifact assembly preserves a failed prerequisite's stdout and stderr, waits
  for both streams to drain, and decodes split UTF-8 diagnostics correctly.
  Build failures remain failures; existing outputs are not used as substitutes.
- Release builds pin SDK `10.0.402` exactly and pair it with reviewed CLI ILLink
  `10.0.13` locks. A later SDK patch cannot silently change the locked restore
  dependency graph. Locked restore remains enabled.
- Release automation reads Marketplace JSON from stdout alone and retains
  stderr separately. Initial lookup, visibility polling, and final verification
  use the same direct pinned-vsce invocation; npm notices cannot become JSON.
  Failed commands, malformed or incomplete responses, and identity mismatches
  remain errors rather than permission to publish. Only the pinned vsce's exact
  missing-extension token is treated as definitive absence.
- Release automation resolves unpublished drafts through paginated exact-tag
  listings and Release ID lookup instead of the published-release tag endpoint.
  Initial staging, resume and pre-finalization retain fail-closed identity,
  checksum and provenance checks; missing, ambiguous or published drafts stop.
- CommonModules Add and Update no longer treat a completed result as untrusted
  when the selected project and CLI receipt use equivalent Windows short and
  long paths. Side-effecting operations are not retried.
- Redirected CLI output uses UTF-8, allowing the debug adapter to read Japanese
  warnings without a decoding failure.
- Hosted CommonModules integration verification compares the receipt with the
  filesystem-canonical project path rather than its original 8.3 alias. No
  product operation, response-time requirement, or test gate is skipped.
- Separate real-workbook Extension Host checks from the Excel-free hosted
  release gate. The explicit Windows Excel gate still executes native Build,
  Publish, snapshot Build and Test diagnostics; missing Excel fails that profile.

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
- One initial VM Test on the preceding candidate terminated abnormally in
  `VbaLexer.Tokenize`; its cause is unconfirmed. Ten subsequent targeted Test
  invocations passed with virtual CPU 0 excluded, but this is not proof of a fix,
  unconditional stability, or equivalence to a host CPU exclusion. The maintainer
  accepted the original failure as a known release risk
  ([verification and acceptance](https://github.com/modern-vba/vba-tools/issues/246#issuecomment-6145865089)).

## [0.1.5] - 2026-10-13

Unpublished release attempt. The protected tag remains at `c4f1aad`; its first
self-contained CLI publish failed when SDK patch roll-forward selected
`10.0.402` against ILLink `10.0.12` locks, producing NU1004. The artifact runner
omitted stdout diagnostics from its error. No release assets, draft or
Marketplace version were created. Its capabilities and source-workbook
migration notes are carried forward in 0.1.6 above.

## [0.1.4] - 2026-10-12

Unpublished release attempt. The protected tag remains at `9298738`; hosted
verification, artifact assembly, executable inspection, provenance, draft
staging, and publishing-identity checks passed. Marketplace state lookup mixed
npm stderr notices into its JSON input and failed before publication. The draft
and its three attested assets are retained unchanged. Its capabilities and
source-workbook migration notes are carried forward in 0.1.6 above.

## [0.1.3] - 2026-10-12

Unpublished release attempt. The protected tag remains at `13eb153`; hosted
verification, artifact assembly, executable inspection and provenance creation
passed. A draft with all three assets was created, but its subsequent lookup
used the published-release tag endpoint and failed with HTTP 404. Marketplace
publication did not run; the draft and attested artifacts are retained unchanged.
Its capabilities and source-workbook migration notes are carried forward in
0.1.6 above.

## [0.1.2] - 2026-10-12

Unpublished release attempt. The protected tag remains at `aa4a2e5`; hosted
verification attempted real Excel fixture creation on the Excel-free runner
and failed before artifact creation or Marketplace publication. Its capabilities
and source-workbook migration notes are carried forward in 0.1.6 above.

## [0.1.1] - 2026-10-10

Unpublished release attempt. The protected tag remains at `bafe1c4`; hosted
verification failed on a test-only short-path comparison before artifact
creation or Marketplace publication. Its capabilities and source-workbook
migration notes are carried forward in 0.1.6 above.

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
