# Publishing

This document is for vba-devtool maintainers who need to produce the Windows executable.

## Release Build

`VbaDevTool.Cli.csproj` is configured so `dotnet publish` produces a Windows x64, self-contained, single-file executable by default.

```powershell
dotnet publish src\VbaDevTool.Cli\VbaDevTool.Cli.csproj -c Release
```

The expected executable is:

```text
src\VbaDevTool.Cli\bin\Release\net10.0\win-x64\publish\vba-devtool.exe
```

PDB files are expected in the publish directory for diagnostics. Distribute the executable as the runnable command; keep the PDB files with release artifacts when stack traces or crash analysis are needed.

## Runtime Target

The published executable targets Windows x64 and includes the .NET runtime. It should run on Windows 11 machines that do not have .NET installed.

Do not enable trimming for the CLI. Workbook automation and COM-related behavior should remain conservative unless a dedicated compatibility pass proves trimming is safe.

## Verification

Before distributing a build, run:

```powershell
dotnet test VbaDevTool.slnx
dotnet publish src\VbaDevTool.Cli\VbaDevTool.Cli.csproj -c Release
```

Then run the published executable directly:

```powershell
src\VbaDevTool.Cli\bin\Release\net10.0\win-x64\publish\vba-devtool.exe --help
src\VbaDevTool.Cli\bin\Release\net10.0\win-x64\publish\vba-devtool.exe doctor --help
```
