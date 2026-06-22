# AGENTS.md

## Encoding and Line Endings

- All plain text files are UTF-8 without BOM, including `.md`, source files, configuration files, scripts, and extensionless text files.
- Do not apply text encoding rules to binary files.
- Use LF line endings for newly created or edited plain text files unless a tool-specific format requires otherwise.
- Plain text files should end with a trailing newline when they are created or edited.
- If `apply_patch` cannot safely preserve encoding or line endings, use PowerShell with `[System.IO.File]::ReadAllText` / `WriteAllText` and an explicit `[System.Text.UTF8Encoding]::new($false)`.

## Documentation Language Policy

- VBA-LanguageServer documentation is written in English.
- ADRs, `CONTEXT.md`, PRDs, issues, issue comments, GitHub Projects text, and agent-facing documentation must be written in English.
- Code identifiers must remain in English.
- Commit messages must follow Conventional Commits and must be written in English.
- Commit messages must use this structure: title, blank line, details, blank line, and optional footer.
  - Keep the details section to 200 characters or fewer.
  - TODO updates, unit test additions, and unit test results do not need to be mentioned.
  - For breaking API changes, write `BREAKING CHANGE:` in the footer or add `!` after the type/scope.

## Scope

This `AGENTS.md` applies only to the `VBA-LanguageServer` repository. The parent `excel-macros-workspace` `AGENTS.md` contains development rules for the Excel macro repositories and does not apply directly to this repository.

The shared process explanation for GitHub Issues, GitHub Projects, triage labels, and issue lifecycle lives in the parent `excel-macros-workspace` `AGENTS.md`. VBA-LanguageServer-specific tracker settings live in `docs/agents/issue-tracker.md`.

VBA-LanguageServer is related to VBA tooling. When a change must stay compatible with exported Excel/VBA modules, Doxygen-style VBA comments, or Excel macro import/export workflows, also check the parent repository `AGENTS.md` and the relevant repository `CONTEXT.md`.

## Agent skills

### Issue tracker

Issues and PRDs are tracked in GitHub Issues for `tkmr-akhs/VBA-LanguageServer`. The repository uses GitHub Projects v2 user project #7, `VBA-LanguageServer main project`. See `docs/agents/issue-tracker.md`.

### Triage labels

The repository uses the standard mattpocock/skills triage labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, and `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Treat this repository as a single-context codebase. See `docs/agents/domain.md`.

## Development and Verification

- Before making domain-sensitive changes, read the available domain documentation described in `docs/agents/domain.md`.
- When changing language-server behavior, add or update tests that pin representative VBA inputs and expected diagnostics, symbols, completion behavior, or protocol responses.
- Keep implementation and verification commands aligned with the technology stack actually present in this repository. Do not assume the parent Excel macro repository's workbook import or Excel COM test flow applies here.
- Generated artifacts, dependency folders, caches, and build outputs should not be edited directly during normal development.
