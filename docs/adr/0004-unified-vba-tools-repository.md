---
status: accepted
---

# Unified VBA tools repository

The product boundary is shifting from a standalone VBA language server toward a
Visual Studio Code development experience for workbook-backed VBA projects. That
experience needs the VS Code extension, the language server, Test Explorer
integration, and the `vba-dev` command to evolve together.

## Decision

This repository is the integration home for VBA developer tooling and is named
at the product level as **VBA Tools**. The existing VS Code extension and
language server remain at the repository root for the initial migration, while
the `vba-dev` C#/.NET command is imported under `tools/vba-dev`.

The product-level name remains host-neutral because Excel is the first Office
automation target, not the permanent product boundary. Future Word,
PowerPoint, or Access support may extend the same product without requiring an
Excel-specific rename.

The `vba-dev` command remains a standalone CLI, but within this repository
it is treated as the VS Code extension's companion command layer. The extension
will use it for workbook-backed project operations such as `build`, `test`,
`publish`, `export`, `doctor`, CommonModules management, and VBA project
reference management. Under ADR 0027, this is a .NET-style project-command
boundary rather than the host for the product's debug adapter.

Excel COM and VBIDE work needed by project commands, including workbook
import/export, workbook save, snapshot-aware build, and workbook-backed test
execution, stays inside `vba-dev`. A separate debug component owns DAP, visible
Excel and VBE interaction, breakpoint transfer, Restart, and debug-session
lifetime; it composes `vba-dev build` instead of extending the CLI with a debug
session. The VS Code extension maps both components into VS Code UI surfaces,
while the language server stays focused on source parsing and editor
intelligence.

The debug component invokes snapshot-aware `vba-dev build` only through the
public subprocess contract. It does not reference the CLI's application or
infrastructure assemblies. Each component therefore owns its process and Excel
lifetime independently and can version its compatibility surface separately.

Long-running workbook operations use two-stage cancellation. The extension
first requests cooperative cancellation so `vba-dev` can close opened
workbooks and Excel and remove incomplete temporary outputs. If the command
does not finish within a bounded grace period, the extension terminates the
command and its strongly owned Excel process tree. `vba-dev` never attaches
ordinary automation commands to a user's existing Excel session, and a forced
cleanup preserves previously completed bin and publish outputs.

Repository-level verification must include both stacks:

- TypeScript compilation and language-server tests for the VS Code extension.
- .NET build/test coverage for `tools/vba-dev`.

`xls-common-devtools` is not merged into this repository. It remains the
upstream provider of CommonModules source packages. The intended distribution
shape is a versioned GitHub Release artifact such as `common_modules_repo.zip`
containing the CommonModules manifest and source files. The initial
`vba-project.json` contract records installed module metadata but does not pin a
package version, artifact hash, or lock file. `vba-dev` consumes the currently
configured CommonModules repository. A future explicit restore/update flow may
select a release artifact, but introducing project-level package pinning
requires a later decision.

## Consequences

Language-server protocol changes, VS Code extension commands, Test Explorer
integration, and `vba-dev` command contracts can now be changed and tested
together in one repository.

The repository contains both TypeScript and .NET toolchains. Root scripts should
make the common verification path obvious, while component-specific scripts
remain available for focused work.

Marketplace packaging must not include either .NET source tree. It bundles the
self-contained Windows x64 artifacts `bin/vba-dev/win-x64/vba-dev.exe` and
`bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe` separately and verifies
their independent capability contracts before release.

The previous `vba-devtools` repository can become read-only after open work and
issue references have been migrated. New user-facing work should be tracked in
the unified VBA Tools repository.
