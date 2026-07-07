---
status: accepted
---

# Workbook-backed project command model

`vba-devtool` will model Office macro automation as a Windows-only, workbook-backed command-line workflow centered on a `project.json` project manifest. The manifest identifies the project and primary document, supplies command defaults, and anchors path resolution so commands can behave consistently from project subdirectories and future multi-document projects.

## Decision

Project manifests use `schemaVersion: 1`, are generated as UTF-16LE with BOM, and are the source of truth for resolving the project root, primary document, document source set, bin output, publish output, and default command options. The reader accepts UTF-16LE with BOM and UTF-8 inputs for compatibility, but generated or rewritten manifests normalize to UTF-16LE with BOM.

The initial source layout is document-oriented even though only one primary Excel document is supported at first: `src/<document-name>/` contains the source template `.xlsm` and exported VBA source, while `bin/<document-name>/` and `publish/<document-name>/` contain generated workbooks. Project-scoped commands accept `--project` and `--document`; omitted values resolve by walking upward to the nearest `project.json` and then using the manifest's `primaryDocument`.

Common modules are copied from a canonical `common_modules_repo`. `new` creates a usable project even when the repository is missing, but reports a warning; `doctor` reports the missing repository as an environment or project problem. CommonModules dependencies and classifications are expected to come from a machine-readable manifest owned by `xls-common-devtools`, not from hard-coded `vba-devtool` rules or scraped product documentation.

`build`, `publish`, `test`, and `export` use a dedicated hidden Excel COM instance instead of attaching to a user's existing Excel session. `build` and `publish` create a temporary workbook from the source template, flush existing standard modules/classes/forms, import source deterministically, save to a temporary output, and replace the target output only after success. `publish` excludes CommonModules entries classified as test-only by the CommonModules manifest and excludes project-local source marked with `'#ExcludePublish`.

`test` does not build implicitly unless `--build` is supplied. It emits `ndjson` or `text`, with `ndjson` as the default for VS Code-friendly streaming. `publish` does not require tests to pass; callers can compose `build`, `test`, and `publish` explicitly.

`export` defaults to reading the manifest-resolved bin workbook and writing to the manifest-resolved document source set. `--from` may point at another workbook, and `--to` may point at another directory. The default document source set export deletes existing `.bas`, `.cls`, `.frm`, and related `.frx` files before export; explicit `--to` exports do not clean the destination.

## Consequences

The command model favors predictable project automation over ad hoc workbook manipulation. It also commits the project manifest encoding and source layout early, which reduces ambiguity for future tools but requires explicit encoding handling in the C# implementation.

Excel and VBIDE automation remain a hard runtime dependency for workbook-backed commands. Environment problems should be surfaced through `doctor` and through clear command failures rather than hidden recovery behavior.
