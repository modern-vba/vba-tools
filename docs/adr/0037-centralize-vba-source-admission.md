---
status: accepted
---

# Centralize VbaDev source admission

This decision introduces the source-admission boundary through the
`ExplicitWorkbookImport` slice in issue #335. For explicit import, it supersedes
the shared UTF-8-first source-decoding rule in ADR 0027 and the tool-local
workbook-backed command model. Issue #339 extends that boundary to ordinary
saved-source Build; issue #340 adds Publish, #344 adds snapshot Build/Test, and
#345 adds project Doctor. Issue #350 makes the exact admission paired with a
successfully materialized test workbook the sole authority for built-test
source locations. Issue #351 routes Doctor's observational workbook inspection
through `WorkbookMaterializer` without changing source admission. Issue #393
establishes purpose-specific admission as the final selection and order authority.
Issue #397 consolidates live and captured selection into one private policy
while retaining their separate input acquisition and evidence ownership.
Source ownership, VBE import verification, and owned Excel-process lifecycle
contracts remain accepted.

## Purpose-specific admission and final order

Successful source admission is authoritative for both selection and final VBE
import order. The sealed `VbaSourceAdmission` module exposes four purpose-specific
operations: `AdmitProjectBuild`, `AdmitProjectPublish`, `AdmitSourceSnapshotBuild`,
and `AdmitExplicitImport`. Callers supply source and installed-state facts
required by that purpose, not an open-ended mode, callback, or strategy. Doctor's
captured Build and Publish profiles use the corresponding project operations;
inspection does not add another write intent.

Only complete admission can issue `AdmittedVbaSourceSet`. Its immutable sources
already have their final order, and ordinary callers cannot reconstruct the
result with another purpose or reorder its sources. The source objects retain
the same identity, kind, syntax, Unicode, original bytes, provenance, and
sidecars. Selection and final order follow these contracts:

| Purpose | Final order and selection | Empty source set |
| --- | --- | --- |
| ProjectBuild | Present installed CommonModules in stored manifest order, including test-only and orphaned entries, then remaining local sources by case-insensitive exported filename. Missing installed source does not add a Build failure; Doctor owns consistency checks. | Valid |
| ProjectPublish | Included installed CommonModules in stored manifest order, then included local sources by case-insensitive exported filename. Installed test-only entries are excluded before reading; included CommonModules ignore local Publish markers. | Valid |
| SourceSnapshotBuild | Complete caller-owned inventory by case-insensitive exported filename, without installed-project CommonModules classification or Publish marker filtering. | Valid |
| ExplicitImport | Complete caller-provided inventory by case-insensitive exported filename, without project CommonModules classification or Publish marker filtering. | Rejected |

Project order comes from the stored installed selection, not a new dependency
resolution or CommonModules repository lookup. The snapshot purpose also serves
snapshot Test's build stage. The selected project still supplies the applicable
template and reference configuration; that does not give it authority to
reclassify snapshot source files.

For live admission, ACP remains fixed once per invocation. It checks the
recursive inventory for duplicate exported filenames before source bytes or Excel work, retaining
the established case-insensitive filename encounter order for reads and
failures. Each selected source and matching sidecar is read at most once.
Manifest-first final ordering happens only after complete successful admission.
It preserves source-admission read and failure order without rereading or
reparsing source.

The remaining `WorkbookSourcePlanner`, its composition wiring, and its
reconstruction-only tests are removed. Materialization, source-snapshot capture,
Import, VBE mirror creation, and captured Doctor profile consumers preserve the
issued order directly. Template existence is a materialization input concern;
source-directory validity remains an admission concern. Project Build and
Publish now complete source admission before checking template existence. If
both are invalid, the source-admission error is reported first. A subsequent
template failure still releases already captured generation input through the
same cleanup path, retaining any concurrent cleanup failure.

`IAdmittedWorkbookGenerationSourceInput` remains the lifetime boundary. A
persistent admission wrapper has no disposable capture to release, while
`BuildSourceSnapshotCapture` owns invocation scratch and cleanup evidence. Both
expose final-order admission without transferring or erasing cleanup ownership.
Existing failure paths still release owned generation input before output or
Excel work; moving template validation does not bypass that release.

The #393 ordering change left live and captured selection implementations
separate. Issue #397 subsequently consolidates their policy as described below.
Neither change introduces a new source diagnostic gate, compile check, editor
fence, output schema, or capability version.

## Shared live and captured selection policy

One private selection operation now owns duplicate exported-name precedence,
installed CommonModules classification, Build/Publish membership, exclusion
decisions, selected admission, and final successful ordering. The four
purpose-specific entry points and private-constructor admitted result issuers
remain the boundary for callers. No public selection interface, caller-composed
policy callback, or strategy catalog is introduced.

Two private input Adapters supply facts to that operation in separate stages:

| Demand | Live input | Captured Doctor input |
| --- | --- | --- |
| Source identity from inventory | Already resolved source and matching sidecar paths; no content read | Already captured source-file identity |
| Decoded text | Read source once, check cancellation, strictly decode once, retain exact bytes and decoded facts | Return retained decoded text or rethrow its source-read/decode failure |
| Selected admission | Reuse those exact bytes and decoded facts, derive admission and read the selected sidecar | Return retained admission or rethrow its admission/sidecar failure |

Constructing either Adapter reads nothing. The live decoded-state cache
distinguishes an unread source from successfully decoded empty text. The shared
policy visits candidates by case-insensitive filename and excludes installed
test-only CommonModules for Publish before demanding either content stage.
Other candidates must pass whole-file decoding before a local marker can
exclude them; installed CommonModules ignore local markers. Only included
candidates demand selected admission. A source that fails cannot produce a
partial successful admitted set. Final manifest-first project ordering reuses
the existing rule only after complete admission; it does not change the order
in which failures are encountered. The two flat purposes retain their existing
ordering, no CommonModules classification, and ExplicitImport-only empty rule.

Input acquisition remains Adapter-specific. Live admission fixes ACP and
inventory once and fails fast before reading later candidates. Installed
test-only sources and sidecars are unread for Publish; local-marker candidates
are fully decoded before exclusion and do not read their sidecars. Doctor
still captures each document's bytes and per-file stage failures once under its
run-wide ACP. Its capture failure is reported before profile selection, while
retained per-file failures are exposed only when the shared policy demands
their stage. Build and Publish can therefore be evaluated independently in
either order without source rereads or one failed profile changing the other.
Static Doctor layout and installed-state consistency diagnostics retain their
independent evidence requirements. This extraction does not make a profile's
exclusion suppress those diagnostics or turn inspection into a write workflow.

## Explicit import admission

Issue #372 removed the legacy raw-source resolution and preflight entry points.
Issue #393 removes the remaining `WorkbookSourcePlanner` and its downstream order
reconstruction. VBE mirror creation requires a non-null `AdmittedVbaSourceSet`
and preserves its final source order. It has no raw source-list overload,
independent decoder, ACP acquisition, or `ExpectedUnicodeText` compatibility
bridge. The direct VbaDev.App C# surface is not a supported external contract,
so no deprecation shim replaces removed entry points. Public CLI grammar,
output, result schemas, and exit behavior remain unchanged.

`VbaDev` uses one internal sealed `VbaSourceAdmission` module with purpose-specific
operations and no public substitutable admission interface. Explicit Import
selects its dedicated operation; callers do not compose decoding, capture,
projection, or ordering policies. One command invocation fixes `GetACP` exactly
once before source capture, fixes one recursive inventory, and reads each
selected `.bas`, `.cls`, `.frm`, and matching same-directory `.frx` at most once.
A successful capture
contains each selected file's original bytes. A read failure fails admission;
capture does not rescan, retry, or perform a closing stability check. Later
caller edits cannot replace admitted facts.

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
boundary. For explicit Import admission, it consumes admitted
Unicode and the fixed ACP, strictly encodes text in that ACP, decodes it again,
and requires exact Unicode equality before Excel starts. An unrepresentable or
best-fit-only character is a failure. The mirror preserves captured `.frx`
bytes exactly beside the matching `.frm`. It neither calls `GetACP`, chooses a
source encoding, nor rereads caller source or sidecars. Caller-owned bytes
remain unchanged.

Explicit import copies the existing target into an invocation-owned workbook
transaction. It inspects the copy's actual project, reference, and retained
component names before flushing replaceable components, then imports the
admitted sources and verifies their component names, kinds, and projected code.
Captured form sidecars are staged exactly from admitted bytes; sidecar-backed
state remains covered by the real-Excel semantic fixture rather than exhaustive
per-command runtime proof. Only the private copy is saved. The target is
atomically replaced only after mirror cleanup, workbook verification and save,
and release of the owned Excel process have succeeded. Any earlier failure or
cancellation leaves the original target bytes intact. Cleanup failures report
the original failure and any retained private artifact paths; they cannot turn
an incomplete import into a successful target update.

Issue #349 moves that target workflow into the distinct closed
`WorkbookMaterializationIntent.ExplicitImport`. The materialization intent
consumes the source authority already issued by explicit Import admission;
it performs no second source
inventory, filesystem read, or encoding decision and resolves no project
context or manifest reference normalization.

Before copying the target, import opens a read-only `FileStream` with
`FileShare.Read`. It holds that guard through target staging, Excel processing,
owned-process release, saved-staging output validation, and the final
cancellation fence, excluding concurrent writes and delete-based replacement
of the original target during that interval. It releases the guard immediately
before the synchronous atomic replacement. This is not a persistent
compare-and-swap guarantee across the final guard-release-to-replacement gap
and adds no retry or rollback of competing external changes.

## Ordinary Build admission

Ordinary manifest-selected Build uses `AdmitProjectBuild`. One invocation fixes
ACP, the effective recursive source inventory, source bytes, and matching
sidecars before source-only preflight or Excel startup. Identity, kind, syntax,
Unicode, encoding provenance, and VBE
projection consume those admitted facts; neither later checks nor import reopen
the authoring sources. The VBE mirror uses the admitted ACP without another
`GetACP` call and retains captured sidecar bytes exactly.

Build retains its existing source-selection rules: installed CommonModules in
manifest order, including test-only and orphaned entries, followed by remaining
sources in case-insensitive filename order. A manifest entry whose source is
absent does not add a new Build failure; Doctor retains its consistency checks.
An empty project Build source set remains valid, unlike explicit Import's
empty-input rejection. Source-directory validity belongs to admission; template
existence belongs to materialization input validation. Successful admission
already contains the final manifest-first import order.

BOM-less Build source now uses only the captured ACP, including ACP 65001 as
the canonical UTF-8 case. Supported BOMs, strict byte reproduction, and lossless
VBE projection use the same rules as explicit import. Malformed source or a
non-lossless projection fails before Excel and preserves existing output.

Later edits, deletions, or additions to authoring paths cannot change the
current admitted build. This is fixed-input ownership, not an atomic snapshot
of concurrent authoring activity: a selected file that cannot be read fails
capture. There is no retry, repeated inventory, closing stability check, or
new authoring lock. The admitted Build profile enters the closed `ProjectBuild`
materialization intent. The materializer re-inspects live authority after import
verification and, after owned-process release, requires readable, non-empty
saved staging before commitment. Cancellation before commitment preserves the
previous output; the successful committed result is authoritative afterward.

Ordinary Test with BuildFirst uses the same closed ProjectBuild intent. Its
successful internal materialization result pairs the committed workbook path
with the exact admission that produced it. `TestCommand` copies immutable
module, callable-range, and persistent-URI facts from that admission into an
`ExecutedSourceIndex` before executing the workbook. Result-location lookup
uses only that index and does not reread or decode current source. This internal
pairing does not change public Build output or add a parallel legacy Build
route. A no-build test receives no admission or index.

## Publish admission

Ordinary Publish uses `AdmitProjectPublish` with manifest-owned installed
CommonModules metadata. It fixes ACP and one recursive inventory
before selection. Case-insensitive flat exported-filename collisions fail
across all candidates before content reads or exclusion decisions. Discovery,
including hidden and tool-named directories, and same-directory form-sidecar
pairing retain their existing rules.

Installed CommonModules with `testOnly: true` are excluded before reading their
source or sidecar bytes. Other installed CommonModules remain included even
when their source contains a publish-exclusion marker. Orphaned entries retain
their manifest-owned treatment; an absent entry adds no new Publish failure.
Doctor remains responsible for CommonModules consistency checks.

Each project-local candidate is read once and its entire byte sequence must
strict-decode and reproduce exactly before the existing marker decision. The
first 32 physical lines, split on CRLF, LF, or CR, are checked after VBA-leading
whitespace trimming for a case-insensitive `'#ExcludePublish` prefix. An early
marker cannot excuse invalid bytes later in the source. This marker grammar is
shared with Doctor's profiles, which use the same captured decoding facts.

A proved marker exclusion precedes declared-kind, identity, syntax-derived
import eligibility, ACP projection, and sidecar capture. Consequently,
supported BOM-marked Unicode that is not representable in ACP may still prove
exclusion, and excluded identity or kind defects do not block Publish. Included
sources must meet the same strict admission and lossless ACP projection rules
as Build. Preflight, mirror generation, import verification, and diagnostics
share their admitted Unicode, syntax, identities, provenance, and captured
sidecars without rereading authoring files.

Successful Publish admission orders included CommonModules by manifest order,
followed by remaining sources in case-insensitive filename order, and accepts an
empty effective source set. Consumers preserve that order directly. Later
authoring changes do not alter the admitted publication.
As with Build, this is fixed-input ownership, not an atomic concurrent-author
snapshot: unreadable selected files fail without retries, another inventory,
closing stability checks, or new locks. The admitted Publish profile enters the
closed `Publish` materialization intent and retains its distinct collision,
exclusion, marker, and ordering rules. It uses the same post-import authority
and released saved-staging gates as Build. Existing warnings and public output
schema remain unchanged. Cancellation before commitment preserves prior output;
successful commitment remains authoritative if cancellation arrives afterward.

## Doctor source inspection

Project Doctor fixes ACP once for the whole run and captures each document's
recursive inventory, source bytes, and matching same-directory sidecars once.
It retains decoded Unicode, syntax, identity, kind, and failures at their
applicable admission stage. Build and Publish select from those same facts;
neither profile rereads authoring files or changes the other's input. Source
layout and installed CommonModules drift diagnostics also use this captured
document evidence. The external CommonModules repository remains an independent
package authority, not part of the document capture.

The Doctor pipeline passes each existing `CapturedDoctorSourceSet` to the
diagnostic adapter. The adapter constructs one `ProjectInspectionIntent` for
the document, and `WorkbookMaterializer.InspectAsync` derives its Build and
Publish admissions from that capture. Neither the adapter nor the materializer
recaptures the inventory, rereads source or sidecars, or makes another ACP or
encoding decision.

Both captured project admissions already contain their final manifest-first
order. `InspectAsync` consumes them without reconstructing or reordering an
admitted set. Since #397, the captured input Adapter uses the same private
selection operation as live admission, preserving independent profile failures
and the separate capture lifetime established before the ordering migration.

Build includes every source. Publish keeps ordinary Publish's filename-collision,
manifest test-only, and local-marker ordering. Doctor captures test-only bytes
for Build, but a read, decoding, or admission failure in an excluded test-only
unit cannot fail the Publish profile. As before, unavailable evidence needed
by a static source diagnostic (for example a CommonModules drift read failure)
makes the whole run incomplete before profile inspection; exclusion is not a
fallback for that independent diagnostic. A local marker requires successful
whole-file strict decoding before exclusion; a proved marker can exclude identity, kind, sidecar,
or lossless-ACP-projection failures. Included CommonModules ignore the marker.
Ordinary Publish still skips test-only files before reading them.

Capture records orphan or displaced `.frx` paths for layout diagnostics without
reading their bytes. Failed capture or admission yields failed or incomplete
existing diagnostics, never an empty-success or alternate-decoding fallback.
Cancellation does not publish a partial capture. This is fixed-input ownership,
not concurrent-editor protection: no retry, closing check, new lock, or fence is
introduced, and later author edits belong to the next run.

For each document with an available source template and at least one profile
that passes source preparation, `InspectAsync` owns exactly one disposable
source-template copy and one Excel automation session shared by the viable
profiles. Existing component removal, reference normalization, and final live
authority inspection are confined to that unsaved copy. The inspection path
never imports or verifies source, saves a workbook, opens an output transaction,
commits output, or mutates durable caller files. The materializer closes the
session and removes the copy under its existing cleanup policy; unconfirmed
cleanup makes the inspection incomplete. The adapter maps the returned profile
evidence to the existing Doctor diagnostics. Check IDs, schema `1.0`, formats,
and exit semantics remain unchanged. Environment-only Doctor performs no
project capture and obtains no source ACP.

## Rollout boundary

Issue #341 applies the same closed-source encoding decisions independently
inside the language server, as described below.

Issue #335 introduced explicit Import; issue #339 added ordinary Build and the
ordinary Build stage reused by Test; issue #340 added Publish. Issue #344 added
snapshot Build/Test. Issue #393 separates its SourceSnapshotBuild admission
purpose from ProjectBuild. The complete caller-owned inventory is authoritative,
including an empty set, without Publish exclusions, installed CommonModules
classification, or comparison with persistent source. Successful snapshot
admission fixes flat exported-filename order. Invocation scratch preserves
original captured bytes, sidecars, and that order; preflight and the VBE import
mirror consume the same admission without decoding the scratch copy again.

Snapshot test materialization returns that exact admission with its committed
workspace workbook. `TestCommand` copies its already-admitted syntax and
source-set-relative provenance into an immutable `ExecutedSourceIndex`, which
maps ranges to persistent URIs without retaining path-backed content authority.
The ordinary build-before-test path creates the same index from its own exact
materialization admission. Later edits, replacements, and deletions cannot
change either built run's locations, and result resolution performs no file
read, existence check, encoding decision, decode, or parse. Missing or ambiguous
mapping omits only optional locations and reports a non-failing built-run
warning without changing workbook-owned identities or outcomes. Issue #345
admits Doctor as described above. Issue #350 adds this result-location boundary;
`--no-build` creates no index, inspects no project source for navigation, always
omits locations, and emits one fixed non-failing warning per completed run.

Issue #347 consumes this admission through the closed `ProjectBuild` and
`Publish` materialization intents. Issue #348 adds the closed
`SourceSnapshotBuild` intent for public snapshot Build and the build stage of
snapshot Test. It consumes the command-owned capture and the same immutable
admitted facts without rereading persistent source, redetecting encoding, or
accepting consumer proof. Issue #349 adds the distinct closed
`WorkbookMaterializationIntent.ExplicitImport`; it consumes the same immutable
explicit-import admission without source reinspection and retains the existing
target guard and one-shot commitment contract described above. Issue #351
moves Doctor's observational workbook mechanism to the separate
`WorkbookMaterializer.InspectAsync(ProjectInspectionIntent)` path. It consumes
the already-captured per-document source, owns at most one disposable copy and
session per document, and exposes profile evidence rather than a materialized
artifact. It is not a fifth write intent or a second generic materialization
pipeline.

The coordinated compatibility matrix is:

| Contract | Version |
| --- | --- |
| VbaDev command contract | `1.0` (unchanged) |
| `build.sourceSnapshot`, `test.sourceSnapshot` | `2.0` |
| `sourceSnapshot.activeWindowsCodePage` | `1.0` (unchanged) |
| Adapter capability contract | `1.0` (unchanged) |
| Adapter protocol | `2.0` |
| DAP `sourceSnapshot` schema | integer `2` |
| Adapter-required CLI `build.sourceSnapshot` | `2.0` |

Issue #344 updates providers, extension requirements, DAP, and packaging in one
release-ready slice. Mixed old/new versions fail closed before Excel; no
incompatible intermediate main or package is supported. The extension validates
both providers before snapshot capture or temporary artifacts, then uses the
chosen compatible paths. Restart revalidates those same paths before its new
capture. Ordinary commands remain CLI-only.

The extension preserves clean bytes and sidecars and strictly encodes dirty
text using a supported BOM or the matching captured ACP. `utf8` without BOM is
valid only under ACP 65001 and `windows-65001` is never emitted. The adapter
independently validates schema, safe paths, identity, base64, encoding token,
BOM, strict decode/reproduction, ACP relationship, sidecars, and complete
inventory before materialization. It independently requires the CLI Build
snapshot feature and invokes only the public CLI process. VbaDev independently
admits those materialized bytes and proves lossless ACP projection before Excel;
it reads no extension manifest, DAP contract, adapter capability, or consumer
proof. No runtime DTO, implementation assembly, or product test is shared.
ADR 0041 consolidates adapter analysis independently: one generation parses
each transported text source once and gives its builder only an opaque exact-byte
source set. It does not change `VbaDev` or weaken this provider-owned admission.

## Language-server closed source

The language server owns its own `DiskSourceDecoding` implementation. Its normal
startup obtains Windows `GetACP` directly once before the protocol loop; the
existing process-wide decoder retains that authority. Supported UTF-8, UTF-16
LE, and UTF-16 BE BOMs select strict Unicode decoding with exact original-byte
reproduction. BOM-less Windows source uses only the fixed ACP, including
ACP 65001 as canonical UTF-8. No UTF-8 probe, fallback, locale inference, or
companion executable participates. Non-Windows closed source without a BOM is
rejected, including ASCII and empty input, because no Windows ACP authority
exists.

Unsupported or malformed BOMs, invalid bytes, and failed byte reproduction are
syntax-free `invalid-disk-source-encoding` facts. They never become empty,
replacement, guessed, or last-known-good semantic source. Cold capture, watched
reload, reconciliation, and close-to-disk transitions use the same local
decoder and existing inventory lifecycle. Open LSP Unicode remains authoritative
and bypasses disk decoding; accepted valid reload or deletion clears an invalid
source diagnostic. Closing the buffer invalidates its previously hidden disk
failure, so the next capture publishes a still-invalid source again or accepts
the current repaired bytes. Existing watcher-first freshness and reconciliation
ownership are unchanged.

The language-server decoder and `VbaSourceAdmission` do not call or reference
each other. They share the neutral data-only conformance corpus through
product-owned loaders and assertions, not another product's runtime or test
assembly. Existing unrelated language-server catalog dependencies are unchanged.
The language server does not project text for VBE import: supported BOM-marked
Unicode remains valid even when it cannot be represented in the Windows ACP.

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

- Successful admission fixes final source selection and import order. Consumers
  cannot reconstruct that authority or add another ordering stage.
- Explicit import of native VBE exports uses the operation ACP without an
  encoding guess. UTF-8 text intended for a host whose ACP is not 65001 must
  have a supported BOM or be converted to that ACP by its author.
- Explicit-import preflight, projection, verification, and diagnostics share
  one captured source authority even if caller files change afterward.
- Ordinary Build, Publish, snapshot Build/Test, and Doctor share admitted
  authority without a second source inventory, ACP decision, or authoring-file
  read downstream.
- Doctor's `ProjectInspectionIntent` shares one disposable copy and Excel
  session across the viable Build and Publish profiles for a document, without
  import, save, output transaction, or commitment.
- Ordinary and snapshot built tests derive optional locations only from an
  immutable `ExecutedSourceIndex` copied from the exact admission paired with
  their committed workbook; no-build omits locations without source inspection.
- The previous target survives failures during source admission, workbook
  mutation, verification, save, and owned-process release.
- `WorkbookMaterializationIntent.ProjectBuild`,
  `WorkbookMaterializationIntent.Publish`,
  `WorkbookMaterializationIntent.SourceSnapshotBuild`, and
  `WorkbookMaterializationIntent.ExplicitImport` re-inspect live authority
  after import verification and validate the released saved staging workbook
  as readable and non-empty before commitment.
- Explicit import retains its existing narrow `FileShare.Read` target guard; it
  is not general external-change protection and does not cover the final
  guard-release-to-commit gap.
- This adds no source re-inventory or reread, authoring lock, target
  compare-and-swap, retry, or rollback of competing external changes.
- `WorkbookMaterializer` and Doctor inspection remain wholly inside `VbaDev`;
  they add no dependency on another product or reverse product dependency.
- Other source workflows keep their released behavior during the staged
  rollout; extending admission requires an explicit follow-up change.
