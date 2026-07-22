# vba-dev

`vba-dev` is a Windows-only command-line tool for workbook-backed VBA projects.

```text
vba-dev <command> [options]
```

Print the independent standalone CLI release version without inspecting Excel,
VBIDE, a workbook, or a project:

```text
vba-dev --version
```

`vba-dev capabilities --format json` reports the same three-part SemVer as
`toolVersion`, independently from the VS Code extension version.

## Commands

| Command | Scope | Description |
| --- | --- | --- |
| `new excel` | project creation | Create an Excel workbook-backed VBA project. |
| `common-module add` | document | Copy CommonModules entries into the selected document source set. |
| `common-module list` | document | List CommonModules entries for the selected document. |
| `common-module update` | project | Update installed CommonModules entries. |
| `reference add` | document | Add VBA project references to the selected document manifest. |
| `reference list` | document | List VBA project references for the selected document. |
| `reference remove` | document | Remove VBA project references from the selected document manifest. |
| `build` | document | Build the selected document into bin output. |
| `test` | document | Run VBA unit tests for the selected document. |
| `publish` | document | Publish the selected document. |
| `export` | document/path | Export modules from a workbook into source. |
| `import` | path | Import VBA sources into an existing workbook. |
| `doctor` | project/machine | Check project and machine prerequisites. |

Document-scoped commands use the manifest `primaryDocument` when `--document` is omitted.

## Document source sets

A `DocumentSourceSet` is recursive, but exported VBA source identity is flat. `.bas`, `.cls`, and `.frm` files may live in nested organization directories under `sourcePath`, but their extension-including file names must be unique case-insensitively within that one source set.

Read-side commands such as `build`, `publish`, and `import` discover `.bas`, `.cls`, and `.frm` files recursively and sort them by exported file name. `.frx` files are not independent source inputs and are not preflighted separately; same-directory form sidecar handling is delegated to the underlying form import/export behavior. Write-side commands that place form files, such as `export` and `common-module add/update`, colocate `.frx` sidecars beside the selected `.frm` path.

## Help

### Root

```text
vba-dev

Usage:
  vba-dev <command> [options]

Commands:
  new            Create an Excel workbook-backed VBA project.
  common-module  Copy CommonModules entries into the selected document source set.
  reference      Add VBA project references to the selected document manifest.
  build          Build the selected document into bin output.
  test           Run VBA unit tests for the selected document.
  publish        Publish the selected document.
  export         Export modules from a workbook into source.
  import         Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use vba-project.json.
  doctor         Check project and machine prerequisites.
```

### new excel

```text
vba-dev new excel

Create an Excel workbook-backed VBA project.

Usage:
  vba-dev new excel [options]

Options:
  --name <name>, -n <name>       Project and document base name.
  --output <dir>, -o <dir>       Project root output directory.
```

`--output` selects the project root directory. `--name` selects the generated project and document base name; when omitted, it is derived from the output directory.

The initial manifest includes the references already present in the generated workbook plus `Microsoft Scripting Runtime` and `Microsoft VBScript Regular Expressions 5.5`, which support the standard CommonModules and unit-test foundation.

When a CommonModules repository is available, initial CommonModules are copied under the generated document source set's `common-modules` directory using each entry's file name.

### common-module add

```text
vba-dev common-module add

Copy CommonModules entries into the selected document source set.

Usage:
  vba-dev common-module add [modules...] [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --force                        Overwrite conflicting source files.
```

CommonModuleName values are extensionless module base names resolved through the CommonModules manifest. Dependencies are copied with the requested entries and recorded in `vba-project.json`.

`common-module add` searches the selected document source set recursively for existing `.bas`, `.cls`, and `.frm` files with the same exported file name. Without `--force`, any match is a conflict. With `--force`, exactly one match is overwritten in place, no match copies to the source set's `common-modules` directory using the entry's file name, and multiple matches fail before file or manifest mutation.

### common-module list

```text
vba-dev common-module list

List CommonModules entries for the selected document.

Usage:
  vba-dev common-module list [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|json>, -f <text|json> CommonModules output format.
```

### common-module update

```text
vba-dev common-module update

Update installed CommonModules entries.

Usage:
  vba-dev common-module update [options]

Options:
  --project <path>               Project root containing vba-project.json.
```

`common-module update` is project-scoped. It updates manifest-listed installed CommonModules entries and preserves the manifest `requested` intent.

Update uses the same recursive flat source identity as add. Existing installed entries are overwritten in place when exactly one matching source file exists; missing installed entries are copied to the source set's `common-modules` directory using the entry's file name; duplicate matches fail before mutation.

For `.frm` CommonModules, add and update first remove every same-name `.frx` under the target source set. If the canonical CommonModules repository has a matching `.frx`, exactly one sidecar is written beside the destination `.frm`; if it has no sidecar, no same-name `.frx` remains in the target source set.

Multi-entry add and update commands preflight the full file plan and planned manifest before deleting sidecars, copying files, or saving `vba-project.json`. The manifest is saved last. If file deletion or copy fails after file mutation begins, the command reports that the manifest was not saved and that source files may have been partially updated; no file rollback is attempted. If manifest saving fails after successful file operations, the planned manifest is written as UTF-16LE with BOM to `vba-project.failed-YYYYMMDD-HHMMSS-fff.json` beside `vba-project.json`, and the command prints only that recovery file path.

Copy and update output reports the actual destination path relative to the document source set, such as `common-modules/Feature.bas` for a new placement or `nested/Feature.bas` for an in-place overwrite.

### reference add

```text
vba-dev reference add

Add VBA project references to the selected document manifest.

Usage:
  vba-dev reference add [references...] [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

Reference names are human-visible `Reference.Description`-style names. The command edits `vba-project.json` only.

### reference list

```text
vba-dev reference list

List VBA project references for the selected document.

Usage:
  vba-dev reference list [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|json>, -f <text|json> Reference output format.
```

### reference remove

```text
vba-dev reference remove

Remove VBA project references from the selected document manifest.

Usage:
  vba-dev reference remove [references...] [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

Removing an absent reference succeeds and leaves the manifest unchanged.

### build

```text
vba-dev build

Build the selected document into bin output.

Usage:
  vba-dev build [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

`build` creates the bin workbook from the source template, normalizes manifest-defined VBA project references, recursively imports source files, and writes the selected document's bin output. Project-local source files are imported after CommonModules dependency ordering, sorted by extension-including exported file name. Duplicate `.bas`, `.cls`, or `.frm` file names fail before source import. `.frx` files are not imported or validated independently.

### test

```text
vba-dev test

Run VBA unit tests for the selected document.

Usage:
  vba-dev test [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|ndjson>, -f <text|ndjson> Test output format.
  --no-build                     Skip building before running tests.
  --module <name>                Run tests from one test module.
  --procedure <name>             Run one test procedure. Requires --module.
```

`test` builds before running tests by default. The default output format is `text`. Use `--no-build` to run against the existing bin workbook, and use `--format ndjson` for machine-readable newline-delimited JSON.

### publish

```text
vba-dev publish

Publish the selected document.

Usage:
  vba-dev publish [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

`publish` creates the publish workbook from the source template, normalizes manifest-defined VBA project references, recursively imports publishable source files, and writes the selected document's publish output. It uses the same flat file-name ordering and duplicate-source failure behavior as `build`. Publish excludes CommonModules entries classified as test-only by the CommonModules manifest and project-local source files whose first scanned lines contain `'#ExcludePublish`.

### export

```text
vba-dev export

Export modules from a workbook into source.

Usage:
  vba-dev export [options]

Options:
  --project <path>               Project root containing vba-project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --from <path>                  Workbook to export from; skips project resolution when supplied.
  --to <dir>                     Directory to export to; defaults to the selected document source set, or the current directory with --from.
```

Without `--from`, `export` is project-aware: it reads the selected document's manifest-resolved bin workbook and writes to the selected document source set unless `--to` names another destination. With `--from`, export is explicit-workbook mode: it does not resolve `vba-project.json`, rejects `--project` and `--document`, and writes to the current directory when `--to` is omitted.

Cleanup is enabled when the destination is manifest-owned or when `--to` is supplied. Cleanup-enabled export records existing `.bas`, `.cls`, and `.frm` relative paths, exports the workbook to a temporary directory first, and leaves the destination untouched if workbook export fails. After a successful temporary export, it recursively deletes existing `.bas`, `.cls`, `.frm`, and `.frx` files only; empty directories and unrelated files remain. Exported file names that match previous source files are restored to those previous relative paths, new exported file names are placed at the destination root, and exported form `.frx` files are written beside their `.frm`.

When cleanup is not enabled, export writes directly to the destination. It overwrites file paths it writes, but it does not delete unrelated files or displaced `.frx` files elsewhere in the destination.

### import

```text
vba-dev import

Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use vba-project.json.

Usage:
  vba-dev import [options]

Options:
  --from <dir>                   Source directory containing .bas, .cls, and .frm files.
  --to <path>                    Existing workbook file to update in place.
```

`import` updates the target workbook in place. It requires both `--from` and `--to`, resolves relative paths from the current directory, and does not accept `--project` or `--document`. The source directory is scanned recursively for `.bas`, `.cls`, and `.frm` files and treated as one flat source file set ordered by extension-including exported file name. Relative paths are not ordering tie-breakers because duplicate exported file names fail before the workbook is opened. The command also fails before opening the workbook when no importable source files exist.

`.frx` files are not independent import inputs and are not preflighted separately. A matching same-directory `.frx` may be consumed by the form import mechanism for its `.frm`; orphan `.frx` files are ignored.

Before import, existing standard modules, class modules, and form modules are removed from the workbook. Document modules such as `ThisWorkbook` and worksheet modules are left in place. The workbook is saved only after flush and import complete successfully.

Unlike `build`, `import` does not add, remove, or normalize manifest-defined references, does not resolve CommonModules dependencies, does not interpret `'#ExcludePublish`, and does not validate whether the workbook compiles.

### doctor

```text
vba-dev doctor

Check project and machine prerequisites.

Usage:
  vba-dev doctor [options]

Options:
  --project <path>               Project root containing vba-project.json.
```

`doctor` checks manifest paths, recursive source identity, CommonModules repository state, manifest-defined CommonModules dependencies, manifest-defined VBA project references, and machine prerequisites. Duplicate `.bas`, `.cls`, or `.frm` exported file names in one document source set are failures. A `.frx` with no same-directory `.frm` is a warning only when a same-name `.frm` exists elsewhere in the same source set; `.frx` files with no same-name `.frm` anywhere are ignored. CommonModules drift checks find installed source files in nested directories and fail when an installed CommonModule has multiple matching source files.

## vba-project.json

`vba-project.json` is the project manifest generated by `vba-dev new excel`. Commands use it to resolve the project root, document definitions, selected document, source directory, template workbook, bin output, publish output, CommonModules repository, installed CommonModules, VBA project references, and command defaults.

Generated manifests are written as UTF-16LE with BOM. Paths are relative to the project root unless an absolute path is required.

Example:

```json
{
  "schemaVersion": 1,
  "projectName": "SampleProject",
  "primaryDocument": "SampleProject",
  "documents": {
    "SampleProject": {
      "kind": "excel",
      "sourcePath": "src/SampleProject",
      "templatePath": "src/SampleProject/SampleProject.xlsm",
      "binPath": "bin/SampleProject/SampleProject.xlsm",
      "publishPath": "publish/SampleProject/SampleProject.xlsm",
      "commonModules": [
        {
          "name": "Runtime",
          "requested": true
        },
        {
          "name": "CommonDependency",
          "requested": false
        }
      ],
      "references": [
        {
          "name": "Visual Basic For Applications"
        },
        {
          "name": "Microsoft Excel 16.0 Object Library"
        }
      ]
    }
  },
  "commonModulesRepository": "../common_modules_repo",
  "commandDefaults": {
    "test": {
      "format": "text"
    }
  }
}
```

| Field | Description |
| --- | --- |
| `schemaVersion` | Manifest schema version. Current value is `1`. |
| `projectName` | Project name. |
| `primaryDocument` | Default document used by document-scoped commands when `--document` is omitted. |
| `documents` | Document definitions keyed by document name. |
| `documents.<document>.kind` | Document kind. Currently only `excel` is supported. |
| `documents.<document>.sourcePath` | Recursive DocumentSourceSet directory containing the template workbook and exported VBA source. `.bas`, `.cls`, and `.frm` file identity is flat by exported file name. |
| `documents.<document>.templatePath` | Source template workbook used by `build` and `publish`. |
| `documents.<document>.binPath` | Workbook generated by `build` and used by default by `test` and `export`. |
| `documents.<document>.publishPath` | Workbook generated by `publish`. |
| `documents.<document>.commonModules[]` | Installed CommonModules entries for the document. |
| `documents.<document>.commonModules[].name` | Extensionless CommonModuleName resolved through the CommonModules manifest. |
| `documents.<document>.commonModules[].requested` | `true` when explicitly requested; `false` when installed as a dependency. |
| `documents.<document>.references[]` | Desired VBA project references for the document. |
| `documents.<document>.references[].name` | Human-visible `Reference.Description`-style reference name. |
| `commonModulesRepository` | CommonModules repository path, or `null` when no repository is discovered. |
| `commandDefaults.test.format` | Default test output format. The generated value is `text`. |
