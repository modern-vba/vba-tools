---
status: accepted
---

# Validate snapshot generations and retain diagnostic origins

- Date: 2026-09-10
- Issue: #401
- Extends: [ADR 0057](0057-share-project-semantic-validation-with-ordinary-build.md)

## Context

A snapshot build must diagnose the bytes it would import, even when saved sources
or editor buffers change later. Returning diagnostics for a deleted generation
directory makes Problems navigation unusable. VbaDev cannot recover the original
editor identity and must not depend on a particular snapshot consumer.

## Decision

Ordinary and snapshot Build share the same analysis completion gate. Snapshot
capture retains parsed trees, recoverable source failures, successful admitted
units and original bytes. A capture alone is not an admitted generation input.
Only complete, error-free source analysis with the selected project's captured
template, references, host evidence and identity issues that authority. Required
input failure preserves recoverable source findings and marks analysis incomplete.
Generation uses those accepted bytes and evidence, never persistent source text.

Capture cleanup remains at the existing ownership boundaries, including before
the output transaction. A cleanup failure preserves the original analysis report
and operational cause. Rejected snapshots cannot replace prior caller outputs,
alter caller sources or templates, or start runnable Excel/VBE work.

VbaDev retains actual admitted source file URIs and exported-source ranges in the
caller-neutral `sourceAnalysis` schema `3.0`. It advertises
`build.sourceSnapshotAnalysis: 1.0`. Snapshot encoding remains schema `2`,
`build.sourceSnapshot` and `test.sourceSnapshot` remain `2.0`, and the global
command contract remains `1.0`.

The debug adapter captures an immutable association from its admitted transported
sources to the generation's snapshot file URIs before invoking VbaDev. It retains
that association and the exact stdout, stderr and exit code through cleanup,
including failed Build and successful Build with no diagnostics. The adapter
requires the new CLI feature before claiming a session workspace. It advertises
`snapshotBuild.diagnostics: 1.0` without changing DAP protocol `2.0`.
The launch envelope accepts VS Code's optional nonempty string `__sessionId` as
opaque client metadata. It never supplies the adapter's session identity,
workspace ownership or restart authority; the existing CLI lease does so.

The versioned `vba/snapshotBuild` DAP event contains:

- `schemaVersion: "1.0"`;
- `projectRoot`, `documentName`, and integer `generation`;
- integer `exitCode`, `stdout`, and `stderr`;
- `origins`, with `snapshotUri` and optional/null `sourceUri` for each captured
  source (binary sidecars have no editor identity).

VS Code accepts reports only for the bound session's current generation, project
and document. It validates the complete report and origin association before
replacing any Problems contribution. Primary and related locations use the exact
captured association with the shared Windows path identity rules. Their exported
source coordinates are unchanged: no basename lookup, live editor read, line
ending conversion, form-header stripping, or scratch-directory lookup occurs.

An unsupported or absent original URI omits that navigation target and reports
the actual source identity in the Output channel. Malformed reports or duplicate
origin keys retain existing Problems. Empty successful results clear only the
`debug-build` contribution for the selected project and document. Normal cleanup
does not remove the retained authoring-source diagnostics.

## Consequences

Neutral syntax/semantics and VbaDev retain no reverse dependency on VS Code or the
debug adapter. Saved-source command selection and native VBE debug execution
semantics remain unchanged. Origin projection can also be reused by consumers of
caller-owned snapshots, without granting them admission authority.
