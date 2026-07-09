---
status: accepted
---

# Unified VBA tools repository

The product boundary is shifting from a standalone VBA language server toward a
Visual Studio Code development experience for workbook-backed VBA projects. That
experience needs the VS Code extension, the language server, Test Explorer
integration, and the `vba-devtool` command to evolve together.

## Decision

This repository is the integration home for VBA developer tooling and is named
at the product level as **VBA Tools**. The existing VS Code extension and
language server remain at the repository root for the initial migration, while
the `vba-devtool` C#/.NET command is imported under `tools/vba-devtool`.

The `vba-devtool` command remains a standalone CLI, but within this repository
it is treated as the VS Code extension's companion command layer. The extension
will use it for workbook-backed project operations such as `build`, `test`,
`publish`, `export`, `doctor`, CommonModules management, and VBA project
reference management.

Excel COM, VBIDE, workbook import/export, workbook save, and workbook-backed
test execution stay inside `vba-devtool`. The VS Code extension invokes the CLI
and maps its output into VS Code UI surfaces; the language server stays focused
on source parsing and editor intelligence.

Long-running workbook operations are cancelled by terminating the `vba-devtool`
process from the extension. `vba-devtool` owns cleanup for opened workbooks,
Excel instances, and temporary outputs so cancellation does not leave workbook
locks or orphaned automation state behind.

Repository-level verification must include both stacks:

- TypeScript compilation and language-server tests for the VS Code extension.
- .NET build/test coverage for `tools/vba-devtool`.

`xls-common-devtools` is not merged into this repository. It remains the
upstream provider of CommonModules source packages. The intended distribution
shape is a versioned GitHub Release artifact such as `common_modules_repo.zip`
containing the CommonModules manifest and source files. `vba-devtool` consumes
that package through explicit restore/update flows so user projects can pin the
CommonModules package version they depend on.

## Consequences

Language-server protocol changes, VS Code extension commands, Test Explorer
integration, and `vba-devtool` command contracts can now be changed and tested
together in one repository.

The repository contains both TypeScript and .NET toolchains. Root scripts should
make the common verification path obvious, while component-specific scripts
remain available for focused work.

Marketplace packaging should not accidentally include the `vba-devtool` source
tree. A future packaging decision can add a built `vba-devtool.exe` as a
bundled companion artifact or download a pinned CLI release on first use.

The previous `vba-devtools` repository can become read-only after open work and
issue references have been migrated. New user-facing work should be tracked in
the unified VBA Tools repository.
