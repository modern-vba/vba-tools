# Release

This document describes the release checks for the VS Code extension and the
standalone `vba-dev` artifact.

## VSIX package

Run the package verification before creating a Marketplace VSIX:

```powershell
npm run package:verify
```

The verification publishes the Windows x64 CLI into the extension bundle
layout, compiles the extension, checks the VSIX file list, and runs the bundled
CLI contract probe:

- `bin/vba-dev/win-x64/vba-dev.exe` must be present.
- `tools/vba-dev/**` must be absent from the VSIX file list.
- The bundled executable must answer `capabilities --format json` with the
  command contract required by the extension.
- `VbaDev.Cli.csproj` must publish a Windows x64 self-contained single-file
  executable.

Create the VSIX with:

```powershell
npm run package
```

`npm run package` runs the same verification before `vsce package`.

## Standalone VbaDev artifact

The same publish output is also the standalone GitHub Releases input:

```powershell
npm run publish:devtool
```

Release maintainers should upload the files from
`bin/vba-dev/win-x64/` to the GitHub Release, either directly or as a
`vba-dev-win-x64.zip` archive. The executable is the runnable command; keep
the PDB files with the release artifacts for stack traces and crash analysis.

Before publishing the standalone artifact, run:

```powershell
bin/vba-dev/win-x64/vba-dev.exe --help
bin/vba-dev/win-x64/vba-dev.exe capabilities --format json
```

For a final release candidate, verify the executable on a clean Windows 11
machine with Office installed and without a separately installed .NET runtime.
That machine check confirms the self-contained distribution promise beyond the
repository-level publish settings.
