---
status: accepted
---

# Centralize VbaDev source admission

This decision introduces the source-admission boundary through the
`ExplicitWorkbookImport` slice in issue #335. For explicit import, it supersedes
the shared UTF-8-first source-decoding rule in ADR 0027 and the tool-local
workbook-backed command model. Issue #339 extends that boundary to ordinary
saved-source Build. Source ownership, VBE import verification, and owned
Excel-process lifecycle contracts remain accepted.

## Explicit import admission

`VbaDev` uses one internal sealed `VbaSourceAdmission` module with one production
implementation. Its initial closed intent is `ExplicitImport`; callers do not
compose decoding, capture, or projection policies and there is no public
substitutable admission interface. One command invocation fixes `GetACP`
exactly once before source capture, fixes one recursive inventory, and reads
each selected `.bas`, `.cls`, `.frm`, and matching same-directory `.frx` at most
once. A successful capture contains each selected file's original bytes. A
read failure fails admission; capture does not rescan, retry, or perform a
closing stability check. Later caller edits cannot replace admitted facts.

A recognized UTF-8, UTF-16 LE, or UTF-16 BE BOM selects its strict Unicode
decoder. Every source without a BOM is interpreted strictly in the
operation-fixed active Windows ANSI code page. Admission never probes BOM-less
UTF-8 before or after ACP decoding; ACP 65001 is canonical UTF-8. Bytes that
are valid as both UTF-8 and another ACP retain the ACP interpretation. A
malformed or unsupported Unicode BOM, strict-decode failure, or failure to
reproduce the exact original bytes fails closed without an encoding fallback.

Admission retains immutable original bytes, decoded Unicode, module identity
and kind, syntax facts, deterministic flat import order, sidecar pairing, and
caller-visible provenance. Source-only namespace preflight, VBE projection,
import verification, and diagnostics consume those same admitted facts. They
do not independently decode or reread caller source. Flat exported-file-name
collisions, invalid source identity, and source-to-source identity conflicts
fail before Excel starts. A `.frx` remains opaque binary content associated
with its inventoried form; orphan sidecars are not import inputs.

`VbeImportSourceSet` remains a separate invocation-owned module at the VBE
boundary. For `ExplicitImport`, it consumes admitted Unicode and the fixed ACP,
strictly encodes text in that ACP, decodes it again, and requires exact Unicode
equality before Excel starts. An unrepresentable or best-fit-only character is
a failure. The mirror preserves captured `.frx` bytes exactly beside the
matching `.frm`. It neither calls `GetACP`, chooses a source encoding, nor
rereads caller source or sidecars. Caller-owned bytes remain unchanged.

Explicit import copies the existing target into an invocation-owned workbook
transaction. It inspects the copy's actual project, reference, and retained
component names before flushing replaceable components, then imports and
verifies the admitted sources. Only the private copy is saved. The target is
atomically replaced only after mirror cleanup, workbook verification and save,
and release of the owned Excel process have succeeded. Any earlier failure or
cancellation leaves the original target bytes intact. Cleanup failures report
the original failure and any retained private artifact paths; they cannot
turn an incomplete import into a successful target update.

Before copying the target, import opens a read-only `FileStream` with
`FileShare.Read`. It holds that guard through staging, Excel processing, and
owned-process release, excluding concurrent writes and delete-based
replacement of the original target. It releases the guard immediately before
the synchronous atomic replacement. This protects the processing interval;
it is not a persistent compare-and-swap guarantee across the final
guard-release-to-replacement gap.

## Ordinary Build admission

Ordinary manifest-selected Build uses the closed `Build` intent of the same
admission module. One invocation fixes ACP, the effective recursive source
inventory, source bytes, and matching sidecars before source-only preflight or
Excel startup. Identity, kind, syntax, Unicode, encoding provenance, and VBE
projection consume those admitted facts; neither later checks nor import reopen
the authoring sources. The VBE mirror uses the admitted ACP without another
`GetACP` call and retains captured sidecar bytes exactly.

Build retains its existing source-selection rules: installed CommonModules in
manifest order, including test-only and orphaned entries, followed by remaining
sources in case-insensitive filename order. A manifest entry whose source is
absent does not add a new Build failure; Doctor retains its consistency checks.
An empty Build source set remains valid, unlike ExplicitImport's empty-input
rejection. Template and source-directory admission remain Build responsibilities.

BOM-less Build source now uses only the captured ACP, including ACP 65001 as
the canonical UTF-8 case. Supported BOMs, strict byte reproduction, and lossless
VBE projection use the same rules as explicit import. Malformed source or a
non-lossless projection fails before Excel and preserves existing output.

Later edits, deletions, or additions to authoring paths cannot change the
current admitted build. This is fixed-input ownership, not an atomic snapshot
of concurrent authoring activity: a selected file that cannot be read fails
capture. There is no retry, repeated inventory, closing stability check, or
new authoring lock. Existing workbook staging, verification, cleanup, and output
commitment remain unchanged, including cancellation before commitment and the
authoritative successful result after commitment.

Ordinary Test with BuildFirst already invokes ordinary BuildCommand, so its
build stage receives the same admitted Build behavior. This does not introduce
shared admission for test execution or result-location lookup, nor does it
change `--no-build` navigation. It does not add a parallel legacy Build route.

## Rollout boundary

Issue #335 introduced `ExplicitImport`; issue #339 adds ordinary Build and the
ordinary Build stage reused by Test. Publish, snapshot build and test, and
Doctor retain their existing UTF-8-first decoding and capture paths until
their own migration slices. Test execution and result-location authority also
remain on their existing paths.
Their existing `VbeImportSourceSet` entry point remains responsible for that
legacy admission behavior. The language server and snapshot producers also
retain their current contracts. This decision does not claim that every
`VbaDev` source path already uses `VbaSourceAdmission`.

The current `build.sourceSnapshot`, `test.sourceSnapshot`, and
`sourceSnapshot.activeWindowsCodePage` capabilities remain version `1.0`.
Adapter protocol and snapshot-schema contracts are unchanged. A future
snapshot encoding migration must coordinate producer, adapter, and CLI
compatibility in its own complete slice. `ExecutedSourceIndex`-based test
locations, changes to `test --no-build` navigation, whole-run Doctor admission,
and language-server source decoding are deferred; none is enabled by #335.

## Conformance

`fixtures/vba-source-encoding/cases.json` is a repository-neutral, data-only
`CrossProductConformanceFixture` containing exact byte payloads, ACP values,
and expected text, encoding classifications, or failures. Deterministic cases
cover ACP 932, 1252, and 65001, BOM authority, ACP authority for ambiguous
BOM-less bytes, malformed and unsupported BOMs, exact byte round trips, and
lossless VBE encoding. Products own their loaders and assertions and do not
reference or link another product's tests. Later migration slices may consume
the same corpus without changing its ownership.

## Consequences

- Explicit import of native VBE exports uses the operation ACP without an
  encoding guess. UTF-8 text intended for a host whose ACP is not 65001 must
  have a supported BOM or be converted to that ACP by its author.
- Explicit-import preflight, projection, verification, and diagnostics share
  one captured source authority even if caller files change afterward.
- Ordinary Build shares the same admitted authority without adding a second
  source inventory, ACP decision, or authoring-file read downstream.
- The previous target survives failures during source admission, workbook
  mutation, verification, save, and owned-process release.
- Other source workflows keep their released behavior during the staged
  rollout; extending admission requires an explicit follow-up change.
