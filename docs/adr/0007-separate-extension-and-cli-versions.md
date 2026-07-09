---
status: accepted
---

# Version the extension and companion CLI separately

The VS Code extension and `vba-dev` should have separate release versions
because UI, language-server, Test Explorer, and workbook automation changes do
not always move together. The extension must declare the CLI command contract it
can use, bundle a CLI version tested against that contract, and reject an
explicit `vbaTools.devtool.path` override when the resolved CLI does not satisfy
the required contract. The command contract is identified separately from the
CLI tool version, such as a `contractVersion` and per-command output schema
versions returned by `vba-dev capabilities --format json`. Capabilities
inspection must be fast and side-effect free, so it is separate from `doctor`,
which may inspect the local Office, VBIDE, workbook, or project environment.
