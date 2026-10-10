---
status: accepted
---

# Retire project workbook bin configuration

- Date: 2026-10-10
- Issue: #450
- Depends on: source-workbook Build/Export in [ADR 0061](0061-build-and-export-the-source-workbook-in-place.md),
  Debug in [ADR 0062](0062-debug-the-retained-source-workbook.md), and Test in
  [ADR 0026's #449 follow-up](0026-run-tests-from-command-owned-snapshot-workbooks.md#issue-449-follow-up-source-workbook-test).
- Supersedes only workbook-bin requirements and initial workbook-bin layout in
  the [workbook-backed command model](https://github.com/modern-vba/vba-tools/blob/main/tools/vba-dev/docs/adr/0001-workbook-backed-project-command-model.md).

## Context

The authoring round trip now uses the exact manifest-selected source workbook.
Requiring a separate workbook `binPath` would leave new projects depending on
an artifact that ordinary Build, Debug, Test and project Export no longer use.
The final cutover must preserve supported older projects and their files, not
turn a deprecated setting into an immediate migration failure.

## Decision

Manifest schema `1` admits documents without `binPath`. A supported configured
legacy value remains valid and produces an actionable warning that the setting
is deprecated and scheduled for removal. Removing that property is the advised
configuration change; it does not authorize deleting its workbook or rewriting
any path. Absence stays absent through ordinary loads, round trips, mutation
and recovery serialization. Existing structural, byte, identity and isolation
validation remains authoritative for other manifest facts.

`new excel` emits source and Publish paths only, with source layout
`src/<document>/<document>.xlsm` and Publish output `publish/<document>.xlsm`.
It creates no workbook-output `bin` directory and emits no `binPath` in initial
configuration, examples, text instructions or success receipts. New receipt
schema remains `1.0`; the envelope still carries project/document identity,
operation/template/completion facts, ordered warnings, manifest path and the
exact committed schema-1 manifest. Only that nested document's source directory,
source template and Publish output are configured workbook paths; no parallel
bin target is added. The additive CLI feature
`projectManifest.optionalBinPath: 1.0` allows a consumer to prove support for
bin-free projects independently of an unchanged tool version.
The packaged extension requires that feature before selecting a companion CLI.
Successful Debug description forwards only recognized legacy-bin warning lines
through the existing console lifecycle sink after selected-source validation,
once per launch; preparation/control stderr is not console output.

Ordinary Build, Debug, Test/no-build and project Export select the exact source
workbook declared by `templatePath`, including custom paths. Their independent
analysis, consent, Save/no-Save, execution and lifetime policies do not change.
Project Doctor no longer requires a workbook bin directory. Existing bin files
are neither read as authoring targets nor automatically deleted, rewritten or
moved. This is configuration retirement, not file migration or cleanup.

Publish keeps every existing saved-input, source-filtering, reference and
separate-output feature. Public paired
`build --source-snapshot ... --output ...` remains a deliberate separate-output
exception. Its caller/manifest-output safety rules still protect manifest,
source/template and Publish paths and a legacy bin path when one is configured.
Omitting a legacy setting supplies no invented bin path to protect.

Executable packaging paths such as `bin/vba-dev/win-x64`, compiler `bin`/`obj`
outputs and generated-directory discovery exclusions are different concepts
and remain unchanged. Historical fixtures and measurements remain evidence of
the routes they exercised, not candidates for blanket bin removal.

## Consequences

One newly generated project supports source Build, Debug, Test/no-build and
project Export without workbook-bin configuration. Compatibility warnings are
nonfatal and actionable; no removal date or automatic deletion is promised.
Regression coverage must prove old/new admission across CLI, extension and
schema, warning presence, absence-preserving edits and serialization, New
layout/receipts, Doctor, custom source paths, untouched legacy files and the
unchanged Publish and paired snapshot-output exceptions. This decision does
not authorize a release or remove later integration/verification gates.
