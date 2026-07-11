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

Run the full test suite:

```powershell
npm test
```

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
- the bundled `vba-dev.exe` must answer `capabilities --format json` with the
  command contract required by the extension.
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

This smoke is optional until a clean Windows release environment is available.
When the environment is not available, skip this section and record `Clean
Windows smoke: not run` in the GitHub Release notes.

When the environment is available, run this smoke on Windows 11 with desktop
Excel installed and without a separately installed .NET runtime.

1. Install the generated VSIX.
2. Open a workspace containing exported `.bas`, `.cls`, or `.frm` files.
3. Confirm the extension activates without a platform-support error.
4. Confirm completion or document symbols are served by the bundled C# language
   server.
5. Run `Format Document` on a VBA file.
6. Open a workbook-backed sample workspace that contains `project.json`.
7. Enable trusted access to the VBA project object model in Excel.
8. Run `VBA Tools: Doctor`.
9. Run `VBA Tools: Build`.
10. Run `VBA Tools: Test`.
11. Confirm Test Explorer shows workbook-backed test nodes.
12. Run the Test Explorer default `Run Tests` profile.
13. Run the bundled language-server executable directly:

    ```powershell
    .\bin\vba-language-server\win-x64\vba-language-server.exe --version
    ```

Do not block a release solely because this smoke environment is unavailable.
Once the environment exists, treat failures in this smoke as release blockers.

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
