---
status: accepted
---

# Bundle vba-dev with the VS Code extension

The Marketplace extension should bundle a self-contained Windows `vba-dev.exe`
for the initial release instead of downloading the companion CLI on first use.
This keeps first-run setup deterministic on Windows 11 machines without a
separately installed .NET runtime, works better in offline or restricted
corporate networks, and lets the extension verify one known command contract.
The same executable can still be published as a standalone GitHub Releases
artifact, and advanced users may override the bundled CLI path for development
or diagnostics. The extension resolves the bundled executable by default, uses
an explicit `vbaTools.devtool.path` override when configured, verifies the
companion CLI contract before invoking project operations, and does not
automatically search `PATH` so an unrelated installed CLI version is not picked
up accidentally. The VSIX should contain the published executable under
`bin/vba-dev/win-x64/vba-dev.exe`; the `tools/vba-dev` source tree
stays excluded from the VSIX package.
