# Release

This document is the release runbook for VBA Tools. Follow it when preparing a
VS Code Marketplace release, the matching GitHub Release, and the standalone
`vba-dev` artifact.

## Release Policy

- Treat the VS Code Marketplace extension as the primary release unit.
- Use `package.json` `version` as the Marketplace extension version.
- Use GitHub Releases as the public version history and artifact archive.
- Tag extension releases as `vba-tools-vX.Y.Z`.
- Keep `vba-dev` versioning and command contracts separate from the extension
  version, as described in ADR 0007. The extension release still bundles one
  tested `vba-dev.exe`.
- Attach the standalone `vba-dev` Windows artifact to the same GitHub Release
  when it should be available outside the extension.
- Do not rewrite published release tags. Fix release mistakes with a new patch
  release.
- Keep `main` releasable. Introduce `release/vX.Y` branches only when hotfix
  maintenance requires them.

## Release Inputs

Before starting, decide:

- the extension version, such as `0.0.1`;
- the release tag, such as `vba-tools-v0.0.1`;
- whether the release includes a standalone `vba-dev` artifact;
- known limitations that must appear in the GitHub Release notes;
- any breaking changes in the extension, `vba-dev` command contract, project
  manifest, or language-server behavior.

## Prepare the Release Change

1. Update `package.json` `version`.
2. Update the Marketplace README `What's New in X.Y.Z` section.
3. Update `vba-dev` version or contract metadata only when the CLI surface
   changes.
4. Update `vba-language-server --version` output only when the language-server
   release identity changes.
5. Update user-facing docs when commands, settings, requirements, or known
   limitations change.
6. Commit the release preparation with an English Conventional Commit message.

## Local Verification

Run the normal client, C# devtool, language-server, syntax-core, and packaging
suite during development:

```powershell
npm test
```

Before preparing an artifact, run the non-Excel release verification surface:

```powershell
npm run verify:release
```

This runs the client, Extension Host, C# unit, language-server, explicit syntax
core, packaging, and compatibility suites, then republishes both bundled
executables and verifies the planned VSIX. It intentionally does not opt in to
real Excel automation.

On a configured Windows host with desktop Excel and trusted VBIDE access, run
the complete release verification including the serialized real Excel suite:

```powershell
npm run verify:release:windows-excel
```

The explicit `test:windows-excel-integration` step sets the required opt-in
environment variable and filters to `Category=WindowsExcelIntegration`. Do not
add it to ordinary `npm test`; the suite owns visible Excel/VBE processes and may
wait for interactive modal prompts.

Run package verification:

```powershell
npm run package:verify
```

`package:verify` publishes the Windows x64 CLI and language server into the
extension bundle layout, compiles the extension, checks the VSIX file list, and
runs bundled executable probes:

- `bin/vba-dev/win-x64/vba-dev.exe` must be present.
- `bin/vba-language-server/win-x64/vba-language-server.exe` must be present.
- `tools/vba-dev/**` must be absent from the VSIX file list.
- `tools/vba-language-server/**`, `server/**`, and `client/src/**` must be
  absent from the VSIX file list.
- bundled runtime sidecars such as `.dll`, `.deps.json`, `.runtimeconfig.json`,
  and `.pdb` files must be absent from `bin/**`.
- `package.json`, `client/out/extension.js`, and `vba-dev-contract.json` must be
  present.
- packaged metadata must point `main` at the compiled extension, activate
  dynamic VBA debug resolution, contribute the supported launch schema and user
  commands, and keep `module` and `procedure` atomic.
- the bundled `vba-dev.exe` must answer `capabilities --format json` with the
  command contract required by the extension.
- the advertised `debug-adapter --stdio` entry point must start successfully and
  match the required adapter protocol version.
- the bundled C# language server must run directly and answer `--version`.
- `VbaDev.Cli.csproj` must publish a Windows x64 self-contained single-file
  executable.
- `VbaLanguageServer.Cli.csproj` must publish a Windows x64 self-contained
  single-file executable.

Create the VSIX:

```powershell
npm run package
```

`npm run package` runs the same verification before `vsce package`.

Probe bundled executables directly:

```powershell
bin/vba-dev/win-x64/vba-dev.exe --help
bin/vba-dev/win-x64/vba-dev.exe capabilities --format json
bin/vba-language-server/win-x64/vba-language-server.exe --version
```

## Clean Windows Smoke

This smoke is required for a release that introduces or changes the native VBE
debug workflow. For a release that does not affect debugging, an unavailable
clean Windows environment may be recorded as `Clean Windows smoke: not run` in
the GitHub Release notes.

When the environment is available, run this smoke on Windows 11 with desktop
Excel installed and without a separately installed .NET runtime.

1. Install the generated VSIX.
2. Open a workspace containing exported `.bas`, `.cls`, or `.frm` files.
3. Confirm the extension activates without a platform-support error.
4. Confirm completion or document symbols are served by the bundled C# language
   server.
5. Run `Format Document` on a VBA file.
6. Open a workbook-backed sample workspace that contains `vba-project.json`.
7. Enable trusted access to the VBA project object model in Excel.
8. Run `VBA Tools: Doctor`.
9. Run `VBA Tools: Build`.
10. Run `VBA Tools: Test`.
11. Confirm Test Explorer shows workbook-backed test nodes.
12. Run the Test Explorer default `Run Tests` profile.
13. Create or open a debug sample whose standard module contains `Option Private
    Module` and a public parameterless `Sub` that records a harmless completion
    marker.
14. Set an enabled ordinary line breakpoint on an executable statement in that
    procedure.
15. Press F5 without a saved launch configuration. Confirm the packaged dynamic
    configuration builds the sample, opens a dedicated visible Excel/VBE
    process, transfers the breakpoint, and stops in native VBE Break mode.
16. Continue from the VBE. Confirm the completion marker is recorded and VS Code
    keeps the debug session active after procedure completion.
17. Exit the owned Excel process. Confirm VS Code displays the final process-exit
    termination message and no owned Excel process remains.
18. Repeat with a saved `launch.json` target. Confirm Restart performs a fresh
    save/build/open/transfer/run and Stop can terminate the session while an
    interactive prompt is visible without leaving an orphan.
19. Run the bundled language-server executable directly:

    ```powershell
    .\bin\vba-language-server\win-x64\vba-language-server.exe --version
    ```

Treat failures in the required native VBE smoke as release blockers. If an
unrelated release legitimately skips the smoke, record the reason and the last
successful `verify:release:windows-excel` result in the release notes.

## Commit, Tag, and Push

After verification passes:

```powershell
git status --short --branch
git pull --rebase origin main
git tag vba-tools-vX.Y.Z
git push origin main
git push origin vba-tools-vX.Y.Z
```

Do not force-push release tags after publication. If the tag is wrong before
publication, delete and recreate it only after confirming no one has consumed
it.

## GitHub Release

Create a draft GitHub Release from the release tag before Marketplace publish.
Keep it as a draft until Marketplace publishing succeeds.

Release title:

```text
VBA Tools X.Y.Z
```

Recommended assets:

- `vba-tools-X.Y.Z.vsix`
- `vba-dev-win-x64.zip`, when the standalone CLI should be distributed
- `SHA256SUMS`

Standalone `vba-dev` artifact:

```powershell
npm run publish:devtool
```

Upload the files from `bin/vba-dev/win-x64/` directly or as
`vba-dev-win-x64.zip`. Keep PDB files with GitHub Release artifacts for stack
traces and crash analysis, even though VSIX packaging excludes runtime sidecars.

Release notes should use this structure:

```md
## Added

## Changed

## Fixed

## Known Limitations

## Requirements

## Bundled Tools

- vba-dev:
- vba-dev contract:
- vba-language-server:

## Artifacts

- VSIX:
- Standalone vba-dev:
```

Include breaking changes under a clearly labeled `Breaking Changes` section.

## Marketplace Publish

Publish the verified VSIX to the VS Code Marketplace:

```powershell
vsce publish --packagePath vba-tools-X.Y.Z.vsix
```

If Marketplace publish succeeds:

1. Publish the draft GitHub Release.
2. Confirm the Marketplace page shows the expected README and version.
3. Confirm the GitHub Releases page is no longer empty for the published
   version.

If Marketplace publish fails:

1. Keep the GitHub Release as a draft.
2. Fix the issue in a new commit.
3. Re-run local verification and clean-machine smoke as needed.
4. Recreate the VSIX.

## Post-Release Checks

- Install the Marketplace version into a normal VS Code profile.
- Open an exported VBA file and confirm language features activate.
- Open a workbook-backed sample and run Doctor.
- Confirm the GitHub Release assets are downloadable.
- Confirm README `Version History` points to GitHub Releases.
- Open an issue for any manual follow-up that was intentionally deferred.
