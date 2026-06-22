# Domain Docs

Rules for engineering skills when reading domain documentation in this repository.

## Layout

Treat this repository as a single-context codebase.

- `CONTEXT.md`: domain glossary and assumptions for VBA-LanguageServer. This file may not exist yet; if it is absent, continue with the available context.
- `docs/adr/`: architecture decision records. This directory may not exist yet; if it is absent, continue without treating it as a blocker.

## Related Context

- Treat VBA-LanguageServer as an independent repository. Normal work should not assume the parent repository's domain documentation.
- The shared explanation of GitHub Issues, GitHub Projects, triage, and issue lifecycle lives in the parent repository `AGENTS.md`.
- For changes related to exported Excel/VBA module syntax, Doxygen-style VBA comments, or Excel macro import/export workflows, also check the parent repository `AGENTS.md` and the relevant repository `CONTEXT.md`.

## Reading Rules

- Before starting domain-sensitive work, read the relevant `CONTEXT.md` and ADRs when they exist.
- If a file or ADR directory does not exist, do not treat that as a blocker. Continue with the available context.
- Prefer terms from `CONTEXT.md` in issues, design proposals, test names, and refactoring proposals after that file exists.
- If a proposal conflicts with an existing ADR, identify the conflicting ADR explicitly.
- For user-visible language-server behavior, also check `README.md`, issue text, and tests.
