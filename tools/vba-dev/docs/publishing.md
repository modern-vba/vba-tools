# Publishing

This document is for vba-dev maintainers who need to version and package the
standalone Windows CLI.

## Release Version

`tools/vba-dev/Directory.Build.props` is the source of truth for the independent
`vba-dev` release version. Update `VbaDevReleaseVersion` to the next canonical
three-part SemVer without changing the VS Code extension version. The value is
propagated to .NET package and informational metadata, `vba-dev --version`, and
`capabilities --format json`.

Use `vba-dev-vX.Y.Z` for an independent CLI-only release tag. Extension tags
remain in the separate `vba-tools-vX.Y.Z` namespace.

## Release Build

`VbaDev.Cli.csproj` is configured so `dotnet publish` produces a Windows x64, self-contained, single-file executable by default.

```powershell
dotnet publish src\VbaDev.Cli\VbaDev.Cli.csproj -c Release
```

The expected executable is:

```text
src\VbaDev.Cli\bin\Release\net10.0\win-x64\publish\vba-dev.exe
```

PDB files are expected in the publish directory for diagnostics. Distribute the executable as the runnable command; keep the PDB files with release artifacts when stack traces or crash analysis are needed.

## Runtime Target

The published executable targets Windows x64 and includes the .NET runtime. It should run on Windows 11 machines that do not have .NET installed.

Do not enable trimming for the CLI. Workbook automation and COM-related behavior should remain conservative unless a dedicated compatibility pass proves trimming is safe.

## Verification

Before distributing a build, run:

```powershell
dotnet test VbaDevTool.slnx
npm run package:devtool
```

Run `package:devtool` from the repository root. It publishes the self-contained
Windows x64 executable, creates
`.tmp/release/vba-dev-win-x64-X.Y.Z.zip`, extracts it into a clean temporary
directory, and verifies all of the following before succeeding:

- `--version` exactly matches `VbaDevReleaseVersion`;
- capabilities `toolVersion` and command contract versions agree;
- the archived executable is byte-for-byte identical to the published binary;
- every published PDB is present;
- the CLI README, root MIT license, and command contract are present.

The release artifact set must list both the versioned standalone ZIP and the
versioned `win32-x64` VSIX in `SHA256SUMS`. Extension release notes must record
the exact bundled `vba-dev` version even when that CLI version has not changed.

The published executable can also be probed directly:

```powershell
bin\vba-dev\win-x64\vba-dev.exe --version
bin\vba-dev\win-x64\vba-dev.exe capabilities --format json
```
