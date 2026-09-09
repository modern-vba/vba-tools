---
status: accepted
---

# Share project semantic validation with ordinary Build

- Date: 2026-09-10
- Issue: #400
- Extends: [ADR 0056](0056-validate-captured-saved-sources-before-workbook-generation.md)

## Context

The editor diagnoses project-wide source errors that the saved-source Build gate
previously missed. Implementing a second resolver in VbaDev would make conditional
declarations, TypeLib parameter contracts, and related-location explanations drift.
Treating failed metadata discovery as an empty catalog would instead hide errors
and incorrectly claim that required analysis had completed.

## Decision

`VbaTools.Semantics` owns the existing source projection, semantic models, name,
type, member and call resolution, conditional declaration families, callable and
Event contracts, and project-semantic diagnostic producers. It depends only on
`VbaTools.Syntax` and the BCL. It discovers no files, launches no Excel or tool
process, and references no editor or VbaDev product assembly.

Both products supply immutable source trees and accepted reference selection,
catalogs, TypeLib identities, intrinsic host Event facts, and authoritative project
names. Editor scheduling, cached acquisition, LSP publication, completion edits,
rename edits and document lifecycle remain in the language server. VbaDev owns its
input discovery and workbook lifecycle. Its tests do not use language-server
implementation or test assemblies. Independently loaded literal corpora pin source
findings at both product boundaries.

The shared project diagnostic inventory is:

- `validation.duplicateDeclaration`;
- `validation.withEventsTypeCannotBeEnclosingClass`,
  `validation.withEventsTypeMustBeClass`, `validation.withEventsTypeMustBeAccessible`,
  and `validation.withEventsTypeMustExposeEvents`;
- `validation.eventHandlerMustBeSub` and
  `validation.incompatibleEventHandlerSignature`, for source and intrinsic Events;
- `validation.incompatibleCallArgumentList` and
  `validation.raiseEventTargetNotDeclaredInEnclosingModule`;
- `validation.interfaceMemberKindMismatch`,
  `validation.incompatibleInterfaceMemberSignature`,
  `validation.interfaceMemberContractNotFullyImplemented`, and
  `validation.interfaceMemberNotImplemented`;
- `validation.moduleIdentityNameConflict`, including the producer outside the
  primary project diagnostic index.

Ordinary Build analyzes the admitted captured sources before materialization.
The source template is captured as a whole package and also supplies generation.
An owned, read-only inspection of that capture supplies the actual project name
and installed reference GUIDs, versions and VBA namespaces. The runtime project
name must agree with persisted project metadata when it exists. Already installed
required references use these observed identities; only missing manifest references
use normal name resolution and ambiguity probing. This prevents an unrelated newer
registered VBA runtime from replacing the Excel workbook's actual standard-library
identity. When VBIDE supplies an installed library path, the metadata service reads
its actual GUID, version and LCID. GUID and version must match the observed reference;
an ordinary registry entry is not required for that library. This also covers an
Office-provided VBA library absent from the process's normal registry view. Missing
virtual paths can be resolved only to one matching DLL actually loaded by
the exactly owned Excel process; the independent TypeLib identity check still
applies. This selected-reference inspection is separate from ordinary reference
listing and normalization. Exact registered identities remain the location source
when no observed path is available,
and missing references use one invocation's registry snapshot. Observed namespaces
are checked before analysis. Intrinsic host Event acquisition uses the existing owned
automation service and the same admission policy as `host-event list`.
Only a captured form source requires the intrinsic UserForm catalog; custom
source Events and external TypeLib Events use their own declared contracts.

Excel can save an initial macro-enabled workbook without persisting a VBA project
part. The neutral package reader distinguishes that conclusive absence from
malformed or contradictory VBA metadata. This absence allows the owned inspection
to supply the project name without inventing a persisted identity. The inspection
never imports, normalizes or saves. Other metadata failures remain
incomplete analysis. The editor retains its existing unavailable-identity policy
and does not launch Excel for this case.

Ambiguous reference discovery probes copies of the same captured template. A
probe with unproved process release retains its workspace and reports its path;
it cannot grant generation authority. Generation installs the accepted reference
identities instead of resolving them again, and checks the containing project
name and reference GUID, version and VBA namespace before source import and
before saving. It does not claim to verify a live reference's LCID.

A successfully read empty TypeLib is valid evidence. A missing identity, failed
required catalog or host acquisition, or failed required processing is incomplete
analysis and fails Build even without a conclusive source Error. Original
operational causes and lifecycle evidence survive diagnostic reporting. Modeled
indeterminacy, late binding, unsupported static inferences and conditional
alternatives retain their existing suppression; they do not become acquisition
failures. The input/direction, pointer-depth and source ByRef rules accepted in
#390 are preserved.

Recoverable source-local failures allow independent files to contribute findings.
A project-fatal stop retains earlier findings and its reason. Missing source
inventory cannot prove bindings that an unavailable source might shadow; direct
declaration collisions remain reportable. Source errors and incomplete analysis
stop before workbook materialization. Discovery can use its normal owned process
lifecycle, but user templates, sources and prior completed output stay unchanged.

The Build output schema is `3.0`. Its existing `sourceAnalysis` record adds
optional standard-shaped `relatedInformation` entries with original source URI,
range and explanation. Expected/found explanations without a navigable declaration
remain in the diagnostic message. The VS Code adapter validates the complete
versioned payload before replacing its scoped Problems contribution. Malformed or
incompatible payloads retain the previous contribution; a successful corrected
rerun clears resolved findings. Static Build does not require an active editor or
language-server process.

## Consequences

Required discovery failures now stop ordinary Build instead of silently reducing
its diagnostic scope. The extension and CLI share semantic rules without sharing
product lifecycle or protocol DTOs. Static validation does not prove native VBE
compilation and introduces no new compiler rules, active-branch-only policy,
Project Health, progress stream or dirty-editor policy. Snapshot and Publish gates
were separate follow-up work; [ADR 0058](0058-validate-snapshot-generations-and-retain-diagnostic-origins.md)
extends the gate to snapshots. `test --no-build` continues to skip Build.
