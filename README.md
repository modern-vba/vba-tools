# VBA Tools

VBA Tools provides Visual Studio Code tooling for exported VBA source files and
workbook-backed VBA projects.

This repository is the home for:

- the VS Code extension and VBA language server;
- future VS Code Test Explorer integration;
- the `vba-dev` companion CLI under `tools/vba-dev`.

The first VS Code extension release is Windows-only. It launches the bundled
C#/.NET `VbaLanguageServer` executable from
`bin/vba-language-server/win-x64/vba-language-server.exe`; there is no
TypeScript language-server fallback path.

`vba-dev` remains usable as a standalone command-line tool, but it is also
the command layer that the VS Code extension will use for workbook-backed
project actions such as `build`, `test`, `publish`, `export`, and `doctor`.

## Repository Layout

```text
client/              VS Code extension client
syntaxes/            TextMate grammar and language assets
tools/vba-dev/       C#/.NET companion CLI
tools/vba-language-server/
                     C#/.NET VBA language server
docs/adr/            Architecture decision records
```

## Development

Build and test the VS Code extension:

```text
npm run test:extension
```

Build and test the companion CLI:

```text
npm run build:devtool
npm run test:devtool
```

Run the full repository test set:

```text
npm test
```

The planned path from VS Code extension commands through Test Explorer,
distribution, and first-run setup is tracked in
[`docs/roadmap/vscode-tooling-roadmap.md`](docs/roadmap/vscode-tooling-roadmap.md).

## CommonModules

CommonModules source packages are not vendored into this repository.
`xls-common-devtools` remains the upstream provider and should publish
`common_modules_repo.zip` from GitHub Releases. `vba-dev` consumes that
package through explicit restore/update flows so projects can pin the
CommonModules version they use.

## Formatting

The extension provides `Format Document` for `.bas`, `.cls`, and `.frm` files.
Formatting is opt-in through normal VS Code settings; the extension does not
change user or workspace settings during activation.

To format on save for VBA files, set this extension as the language-specific
formatter and enable `editor.formatOnSave`:

```json
{
  "[vba]": {
    "editor.defaultFormatter": "tkmr-akhs.vba-language-server",
    "editor.formatOnSave": true
  }
}
```

Formatting normalizes VBA keyword and intrinsic word casing, normalizes resolved
source reference casing to the matching `VbaDefinition`, and rewrites leading
whitespace according to VBA block depth. It does not rename declarations, edit
sibling files, or rewrite comments and strings.
