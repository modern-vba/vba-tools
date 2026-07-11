# VBA Tools

Edit exported VBA source files in Visual Studio Code with language-server
features, formatting, Test Explorer integration, and explicit workbook build
commands for Excel VBA projects.

VBA Tools is designed for source-controlled `.bas`, `.cls`, and `.frm` files.
For workbook-backed projects, the extension uses a bundled `vba-dev` command to
build, test, publish, export, and validate Excel macro workbooks from a
`project.json` manifest.

---

## Key Features

- Edit VBA in VS Code with syntax highlighting for `.bas`, `.cls`, and `.frm`
  files.
- Get diagnostics while editing, including parser errors and supported
  validation rules.
- Navigate with completion, hover, signature help, document symbols, workspace
  symbols, go to definition, find references, and rename.
- Use semantic highlighting for declarations and resolved references.
- Format VBA source with the built-in document formatter.
- Run workbook-backed VBA tests from VS Code Test Explorer.
- Run project commands from the Command Palette: Doctor, Build, Test, Publish,
  Export, CommonModules, and VBA project reference operations.
- Keep `project.json` as the manifest for templates, source folders, generated
  workbooks, publish output, CommonModules, references, and command defaults.

---

## Installation

Launch VS Code Quick Open (`Ctrl+P`), paste this command, and press Enter:

```text
ext install tkmr-akhs.vba-tools
```

### 1 - Open exported VBA source

Open a folder that contains `.bas`, `.cls`, or `.frm` files. Language features
activate when a VBA file is opened.

### 2 - Prepare Excel for workbook-backed commands

Workbook automation requires desktop Excel and trusted access to the VBA project
object model:

```text
Excel -> File -> Options -> Trust Center -> Trust Center Settings -> Macro Settings
```

Enable `Trust access to the VBA project object model`.

### 3 - Open a workbook-backed project

For build, test, publish, export, CommonModules, and reference commands, open a
workspace containing a `project.json` manifest. The manifest defines the source
folder, template workbook, generated workbook, publish workbook, references, and
CommonModules entries for each document.

### 4 - Run Doctor

Run `VBA Tools: Doctor` from the Command Palette. Doctor checks project paths,
manifest state, CommonModules state, reference declarations, and machine
prerequisites. Results are written to the VBA Tools output channel and surfaced
as VS Code diagnostics where applicable.

---

## Command Palette Commands

| Command | Description |
| --- | --- |
| `VBA Tools: Doctor` | Check workbook project and machine prerequisites. |
| `VBA Tools: Build` | Generate the selected workbook document from template and source. |
| `VBA Tools: Test` | Build, then run VBA unit tests for the selected workbook document. |
| `VBA Tools: Publish` | Generate the publish workbook for the selected document. |
| `VBA Tools: Export` | Export VBA modules from the selected workbook into source. |
| `VBA Tools: Add Common Module` | Add CommonModules entries to the selected document. |
| `VBA Tools: List Common Modules` | List CommonModules entries for the selected document. |
| `VBA Tools: Update Common Modules` | Update installed CommonModules entries. |
| `VBA Tools: List References` | List manifest-defined VBA project references. |
| `VBA Tools: Add Reference` | Add a manifest-defined VBA project reference. |
| `VBA Tools: Remove Reference` | Remove a manifest-defined VBA project reference. |

---

## Workbook Project Workflow

### Build

`VBA Tools: Build` creates the configured bin workbook from the template
workbook, applies manifest-defined references, imports exported source files,
and writes the generated workbook output.

### Test

`VBA Tools: Test` runs `vba-dev test` for the selected workbook document. By
default, tests build first so the workbook under test matches the source tree.

### Publish

`VBA Tools: Publish` creates the publish workbook and excludes test-only
CommonModules and source files marked for publish exclusion.

### Export

`VBA Tools: Export` pulls modules from the selected workbook into the configured
source folder. It is an explicit command, not a live save-time sync.

### CommonModules and References

CommonModules commands edit and update manifest-listed common module entries.
Reference commands edit desired VBA project references in `project.json`; build
and publish apply those references to generated workbooks.

---

## Test Explorer

Workbook-backed projects appear in VS Code Test Explorer when the workspace
contains a readable `project.json` manifest.

| Profile | Behavior |
| --- | --- |
| `Run Tests` | Invokes `vba-dev test --format ndjson` and keeps build-before-test behavior. |
| `Run Tests Without Build` | Invokes `vba-dev test --no-build --format ndjson` for explicit fast reruns against existing generated output. |

Missing or unusable generated output is reported as a test run error in the
no-build profile.

---

## Code Formatter

Set VBA Tools as the default formatter for VBA files and enable format on save:

```json
{
  "[vba]": {
    "editor.defaultFormatter": "tkmr-akhs.vba-tools",
    "editor.formatOnSave": true
  }
}
```

The formatter normalizes VBA keyword and intrinsic word casing, normalizes
resolved source reference casing to the matching definition, and rewrites
leading whitespace according to VBA block depth. It does not rename
declarations, edit sibling files, or rewrite comments and strings.

---

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `vbaLanguageServer.trace.server` | `off` | Controls LSP trace output for the VBA language server. |
| `vbaTools.devtool.path` | empty | Overrides the bundled `vba-dev` executable path for development or diagnostics. |

---

## Troubleshooting

| Problem | Check |
| --- | --- |
| Language features do not start | The first release supports Windows only. Open the VBA Tools output channel and check whether the bundled language server launched. |
| Workbook commands fail before opening Excel | Run `VBA Tools: Doctor` and confirm that the workspace contains `project.json`. |
| Excel blocks workbook automation | Enable trusted access to the VBA project object model in Excel Trust Center settings. |
| Tests do not appear in Test Explorer | Confirm that `project.json` is in the opened workspace and reload the VS Code window after changing project layout. |
| Format on save does not run | Set `editor.defaultFormatter` for `[vba]` to `tkmr-akhs.vba-tools`. |
| You need to test a custom CLI build | Set `vbaTools.devtool.path` to the full path of the replacement `vba-dev.exe`. |

---

## System Requirements

- Windows 10 or Windows 11.
- VS Code 1.125.0 or later.
- Desktop Microsoft Excel for workbook-backed commands.
- Trusted access to the VBA project object model for workbook automation.
- No separate .NET runtime is required for the bundled Windows executables.

Standalone editing features are available for exported VBA source files. Excel
is only required when running workbook-backed automation commands.

---

## Bundled Tools

Detailed tool documentation is kept with each tool rather than in this
Marketplace README:

- [`vba-dev`](https://github.com/modern-vba/vba-tools/blob/main/tools/vba-dev/README.md)
  - workbook-backed project CLI.
- [`vba-language-server`](https://github.com/modern-vba/vba-tools/blob/main/tools/vba-language-server/README.md)
  - C# LSP server used by the extension.

---

## Version History

See [GitHub Releases](https://github.com/modern-vba/vba-tools/releases) for
published extension changes.
