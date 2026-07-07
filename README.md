# vba-devtools

```text
vba-devtools <command> [options]
```

## Commands

| Commands  | Description |
|-----------|-------------|
| `new`     | Create a workbook-backed VBA project. |
| `add`     | Copy CommonModules entries into the selected document source set. |
| `build`   | Build the selected document into bin output. |
| `test`    | Run VBA unit tests for the selected built document. |
| `publish` | Publish the selected document. |
| `update`  | Update installed CommonModules entries. |
| `export`  | Export modules from a workbook into source. |
| `doctor`  | Check project and machine prerequisites. |

## new

Create a workbook-backed VBA project.

### Usage

```text
  vba-devtools new <project-name> [--document <name>]
```

### Options

| Options             | Description |
|---------------------|-------------|
| `--document <name>` | Document name. Defaults to the project name. |

## add

Copy CommonModules entries into the selected document source set.

### Usage

```text
  vba-devtools add [modules...] [options]
```

### Options

| Options             | Description |
|---------------------|-------------|
| `--project <path>`  | Project root containing project.json. |
| `--document <name>` | Document name from the project manifest. |

## build

Build the selected document into bin output.

### Usage

```text
  vba-devtools build [options]
```

### Options

| Options             | Description |
|---------------------|-------------|
| `--project <path>`  | Project root containing project.json. |
| `--document <name>` | Document name from the project manifest. |

## test

Run VBA unit tests for the selected built document.

### Usage

```text
  vba-devtools test [options]
```

### Options

| Options                   | Description |
|---------------------------|-------------|
| `--project <path>`        | Project root containing project.json. |
| `--document <name>`       | Document name from the project manifest. |
| `--format <ndjson\|text>` | Test output format. |
| `--build`                 | Build before running tests. |

## publish

Publish the selected document.

### Usage

```text
  vba-devtools publish [options]
```

### Options

| Options             | Description |
|---------------------|-------------|
| `--project <path>`  | Project root containing project.json. |
| `--document <name>` | Document name from the project manifest. |

## update

Update installed CommonModules entries.

### Usage

```text
  vba-devtools update [options]
```

### Options

| Options             | Description |
|---------------------|-------------|
| `--project <path>`  | Project root containing project.json. |
| `--document <name>` | Document name from the project manifest. |

## export

Export modules from a workbook into source.

### Usage

```text
  vba-devtools export [options]
```

### Options

| Options             | Description |
|---------------------|-------------|
| `--project <path>`  | Project root containing project.json. |
| `--document <name>` | Document name from the project manifest. |
| `--from <path>`     | Workbook to export from. |
| `--to <dir>`        | Directory to export to. |

## doctor

Check project and machine prerequisites.

### Usage

```text
  vba-devtools doctor [options]
```

### Options

| Options             | Description |
|---------------------|-------------|
| `--project <path>`  | Project root containing project.json. |
| `--document <name>` | Document name from the project manifest. |
