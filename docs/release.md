# Release

This document describes the release checks for the VS Code extension, the C# VBA
language server, and the standalone `vba-dev` artifact.

## VSIX package

The first Marketplace release is Windows-only. The extension bundles and
launches the C# `VbaLanguageServer` executable from
`bin/vba-language-server/win-x64/vba-language-server.exe`; there is no
TypeScript language-server runtime or fallback path in the package. The
TypeScript code in the extension is only the VS Code adapter layer.

Run the package verification before creating a Marketplace VSIX:

```powershell
npm run package:verify
```

The verification publishes the Windows x64 CLI and language server into the
extension bundle layout, compiles the extension, checks the VSIX file list, and
runs bundled executable probes:

- `bin/vba-dev/win-x64/vba-dev.exe` must be present.
- `bin/vba-language-server/win-x64/vba-language-server.exe` must be present.
- `tools/vba-dev/**` must be absent from the VSIX file list.
- `tools/vba-language-server/**`, `server/**`, and `client/src/**` must be
  absent from the VSIX file list.
- bundled runtime sidecars such as `.dll`, `.deps.json`, `.runtimeconfig.json`,
  and `.pdb` files must be absent from `bin/**`.
- The bundled executable must answer `capabilities --format json` with the
  command contract required by the extension.
- The bundled C# language server must run directly and answer `--version`.
- `VbaDev.Cli.csproj` must publish a Windows x64 self-contained single-file
  executable.
- `VbaLanguageServer.Cli.csproj` must publish a Windows x64 self-contained
  single-file executable.

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

## Clean Windows 11 language-server smoke

For a final Marketplace release candidate, run this smoke on a Windows 11
machine without a separately installed .NET runtime:

1. Install the generated VSIX.
2. Open a workspace containing an exported `.bas`, `.cls`, or `.frm` file.
3. Confirm the extension activates without a platform-support error.
4. Run the bundled executable directly:

   ```powershell
   .\bin\vba-language-server\win-x64\vba-language-server.exe --version
   ```

5. Confirm the output starts with `vba-language-server `.
6. Open a VBA source file and confirm completion or document symbols are served
   by the bundled C# executable.

This smoke is the release gate for the Windows-only, C#-only support boundary.
