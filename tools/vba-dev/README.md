# vba-dev

`vba-dev` is a Windows-only command-line tool for workbook-backed VBA projects.

```text
vba-dev <command> [options]
```

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
| `export` | document | Export modules from a workbook into source. |
| `doctor` | project/machine | Check project and machine prerequisites. |

Document-scoped commands use the manifest `primaryDocument` when `--document` is omitted.

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

### common-module add

```text
vba-dev common-module add

Copy CommonModules entries into the selected document source set.

Usage:
  vba-dev common-module add [modules...] [options]

Options:
  --project <path>               Project root containing project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --force                        Overwrite conflicting source files.
```

CommonModuleName values are extensionless module base names resolved through the CommonModules manifest. Dependencies are copied with the requested entries and recorded in `project.json`.

### common-module list

```text
vba-dev common-module list

List CommonModules entries for the selected document.

Usage:
  vba-dev common-module list [options]

Options:
  --project <path>               Project root containing project.json.
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
  --project <path>               Project root containing project.json.
```

`common-module update` is project-scoped. It updates manifest-listed installed CommonModules entries and preserves the manifest `requested` intent.

### reference add

```text
vba-dev reference add

Add VBA project references to the selected document manifest.

Usage:
  vba-dev reference add [references...] [options]

Options:
  --project <path>               Project root containing project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

Reference names are human-visible `Reference.Description`-style names. The command edits `project.json` only.

### reference list

```text
vba-dev reference list

List VBA project references for the selected document.

Usage:
  vba-dev reference list [options]

Options:
  --project <path>               Project root containing project.json.
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
  --project <path>               Project root containing project.json.
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
  --project <path>               Project root containing project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

`build` creates the bin workbook from the source template, normalizes manifest-defined VBA project references, imports source files, and writes the selected document's bin output.

### test

```text
vba-dev test

Run VBA unit tests for the selected document.

Usage:
  vba-dev test [options]

Options:
  --project <path>               Project root containing project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --format <text|ndjson>, -f <text|ndjson> Test output format.
  --no-build                     Skip building before running tests.
```

`test` builds before running tests by default. The default output format is `text`. Use `--no-build` to run against the existing bin workbook, and use `--format ndjson` for machine-readable newline-delimited JSON.

### publish

```text
vba-dev publish

Publish the selected document.

Usage:
  vba-dev publish [options]

Options:
  --project <path>               Project root containing project.json.
  --document <name>, -d <name>   Document name from the project manifest.
```

`publish` creates the publish workbook from the source template, normalizes manifest-defined VBA project references, imports publishable source files, and writes the selected document's publish output.

### export

```text
vba-dev export

Export modules from a workbook into source.

Usage:
  vba-dev export [options]

Options:
  --project <path>               Project root containing project.json.
  --document <name>, -d <name>   Document name from the project manifest.
  --from <path>                  Workbook to export from.
  --to <dir>                     Directory to export to.
```

Without `--from`, `export` reads the selected document's bin workbook. Without `--to`, it writes to the selected document source set and cleans existing exported module files first.

### doctor

```text
vba-dev doctor

Check project and machine prerequisites.

Usage:
  vba-dev doctor [options]

Options:
  --project <path>               Project root containing project.json.
```

`doctor` checks manifest paths, CommonModules repository state, manifest-defined CommonModules dependencies, manifest-defined VBA project references, and machine prerequisites.

## project.json

`project.json` is the project manifest generated by `vba-dev new excel`. Commands use it to resolve the project root, document definitions, selected document, source directory, template workbook, bin output, publish output, CommonModules repository, installed CommonModules, VBA project references, and command defaults.

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
| `documents.<document>.sourcePath` | DocumentSourceSet directory containing the template workbook and exported VBA source. |
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
