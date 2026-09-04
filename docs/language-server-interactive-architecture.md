# Language server interactive architecture

## Scope

This document summarizes the latency-sensitive C# language-server path and the
fallbacks that preserve correctness when an optimized path cannot prove that it
is safe. The detailed decisions remain in ADRs 0003 and 0011 through 0018.

The interactive infrastructure is host-neutral. A project manifest selects
references, and reference catalogs provide types, members, constants, and
explicit global exposure. Excel is the host currently covered by shipped
catalog data. Word, PowerPoint, and other VBA hosts must be added through
manifest/reference selection and catalog providers, not through host-name
conditions in the parser, workspace snapshot cache, Semantic Inventory,
scheduler, or LSP feature handlers.

## Two deep modules

### Workspace snapshot authority

`VbaLanguageWorkspace` and `VbaProjectSnapshotProvider` form the workspace
snapshot authority. Together they own:

- exact open-document revisions and immutable `VbaDocumentAnalysis`;
- short reserve/build/compare-and-commit transitions outside the workspace
  state lock;
- exact `VbaVersionedDocumentSnapshot` capture for document-only operations;
- manifest-backed project identities and watcher-fed source membership;
- affected-project invalidation and committed project snapshot reuse; and
- project-scoped `VbaSemanticInventory` construction.

Feature handlers do not resolve manifests, enumerate source files, read
catalogs, or build an alternate semantic inventory. A warm project-snapshot hit
returns before source enumeration, disk stat/read, or project-wide semantic
construction. Open buffers remain authoritative over equivalent disk sources.
The deep `VbaProjectDiskInventory` Module provides one shared filesystem
Implementation and cache for cold project-snapshot materialization, watched
source reload, and background reconciliation. `VbaProjectReconciler` depends on
it through a one-method observation Seam whose immutable disk-only request
contains the resolved project disk scope, ordered typed manifest probes, typed
barrier overrides, typed observed-barrier document identities, and typed
open-source exclusion identities. Open text, document versions, authority
keys, authority generations, workspace and manifest revisions, and
known-source baselines stay in the reconciler and workspace reconciliation
scope; the inventory does not receive them or decide whether an observation
may commit. Production
and deterministic test Adapters use that same narrow observation Interface.
The filesystem Adapter owns source extension enumeration,
recursive versus top-directory scope, nested-manifest ownership, path/URI
identity, stable reads, decoding, decoded-text reuse, and manifest probes. It
returns syntax-free facts with an opaque `DiskContentIdentity`.
`VbaProjectSourceDocumentCache` parses and projects those facts without
filesystem access. Its watched-source operation validates ownership,
invalidates the prior decoded fact, and stable-reads one source without project
enumeration. Cold capture may reuse decoded text only while metadata and the
explicit invalidation generation remain unchanged; reconciliation always
stable-reads bytes, even when length and timestamp are unchanged. Deterministic
tests count every operation at the inventory Seam and require a warm
interactive capture to add no inventory call, manifest read, source
enumeration, metadata query, source read, or project/semantic rebuild.
Snapshot cache and reconciliation Interfaces receive structural project,
authority, and document identities. Active, tracked, open, revision, and
manifest-barrier documents are projected once to `VbaDocumentIdentity` or
`VbaIdentifiedDocument`; presentation URIs remain adjacent protocol or
filesystem data and are not delimiter-composed cache keys.
Disk changes become visible through accepted watcher events, reconciliation,
or an explicit reload; an unreported raw disk write may remain stale while the
warm snapshot is valid.

Closed `.bas`, `.cls`, and `.frm` bytes use one process-wide
`DiskSourceDecoding` service. Recognized UTF-8, UTF-16 LE, and UTF-16 BE BOMs
select strict decoders; BOM-less bytes try strict UTF-8 first. Only a Windows
process then tries the `GetACP` code page captured once at process start, and
ACP 65001 remains the canonical UTF-8 path. Non-Windows hosts have no implicit
legacy fallback. Invalid bytes become an `invalid-disk-source-encoding`
diagnostic and never become parsed or semantic text. Existing open paths are
still ownership facts, but their authoritative LSP Unicode text bypasses disk
reads and decoding during cold capture and reconciliation.

This policy ends when source bytes have become Unicode. `VbaIdentifier`
continues to recognize every MS-VBAL `VbaIdentifierForm` independently of the
file encoding and ACP. VBE import is a separate `vba-dev` boundary with its own
operation-fixed ACP staging and lossless verification; language-server disk
decoding does not define `VBComponents.Import` behavior.

`VbaProjectIdentityModel` is the only owner of source-document equality and
project-authority comparison. `VbaDocumentIdentity` uses a canonical local full
path for file documents and a normalized URI for non-file documents, independently
of source revision, open-buffer authority, and `DiskContentIdentity`.
`VbaProjectAuthorityIdentity` uses canonical manifest plus selected document for
a manifest-backed project and canonical source root for an ad-hoc project. It
excludes references, CommonModules, source content, and cache-forming inputs.
Presentation paths and protocol URI spellings never substitute for either
authority identity.

`VbaProjectSnapshotIdentity` composes that typed authority with the canonical
source root, selected document kind, ordered semantic reference selection,
source-template selection, and order-independent CommonModules module-file
membership. Snapshot cache lookup, batch deduplication, supersession, scope
invalidation, retirement, and reconciliation resolution comparison retain this
opaque type end to end; they do not unwrap it into a caller-composed string.
Equivalent active documents in one manifest document therefore share a warm
snapshot, while a snapshot-forming manifest transition replaces only the
snapshot identity. Source text and `DiskContentIdentity` remain outside it and
continue to advance the existing revision and reconciliation fences.

Reference-catalog work uses three adjacent typed identities rather than the full
snapshot identity. `VbaProjectReferenceCatalogScopeIdentity` combines project
authority and `ReferenceSelectionFingerprint` for cache lookup and persistence;
its public `CreatePersistentKey` method is the opaque cross-process serialization
boundary. `VbaProjectReferenceCatalogRefreshAuthorityIdentity` combines optional
project authority with one reference name while excluding the selection, so a
newer selection supersedes older commits for that catalog. Automatic work uses
`VbaProjectReferenceCatalogAutomaticWorkIdentity`: the selection plus project
authority only when discovery is context-specific.

Background reconciliation keeps that stable authority identity separate from a
manifest's mutable content identity. Authority transitions use one
subject-document-aware relation: `Same`, `RetainPrevious`, `Replace`,
`Unrelated`, or `Indeterminate`, together with explicit ownership and source
boundary facts. Workspace admission, manifest appearance and removal, nested
manifest transfer, watcher retention, and reconciliation consume the same
relation. Missing, malformed, rootless, or unresolved evidence is
`Indeterminate` and fails closed; it cannot silently merge or replace authority.
Source changes are compared by `DiskContentIdentity`, not file metadata.
Filesystem scans run outside the ordered mutation lane with at most two
scopes in flight. The deep `VbaProjectReconciler` Module converts each result
into one ordered `VbaProjectReconciliationScopePlan`; plans commit in stable
authority order through one required scheduler mutation per scope. The
workspace exposes the narrow `CaptureProjectReconciliation` and
`TryCommitProjectReconciliationScope` Seam. A stale fence rejects only that
scope, so fresh peer scopes still commit. Manifest authority replacement and
snapshot authority transfer share one mutation. Remaining source mutations
wait for a fresh follow-up plan when that transfer invalidates their captured
authority.

Accepted commits advance source-by-source disk baselines. Invalid manifest text
advances only the observed-disk baseline, preserves any last-known-good
effective manifest, and publishes one validation diagnostic until the text
changes. If no last-known-good manifest exists, a cold interactive request that
first discovers the invalid disk text records it and returns a manifest error.
Once validation has recorded that invalid disk state, the file stops acting as
an ownership barrier and later resolution falls back without rereading it.
Diagnostics and manifest lifecycle notifications are ephemeral effects. Each
accepted scope dispatches its effects in stable order synchronously inside that
scope's required scheduler mutation, after committing authority state and before
releasing the ordered lane. A failed effect is reported without rolling back
authority state or blocking later effects or peer scopes.

An accepted manifest mutation requests an immediate reconciliation follow-up
so newly exposed ancestors and descendants converge without waiting for the
next cadence. Rejected mutations retry only while their structural
`VbaProjectReconciliationRejectedProgressIdentity` makes new progress. That
typed identity contains project authority, rejection and mutation kinds,
captured revision fences, and ordered typed document/revision facts rather than
a delimiter-based path or URI fingerprint. One trigger stops after 32 passes to
bound filesystem churn; every pass remains cancellation-aware.

Effective disk and unsaved manifest overlays are project ownership barriers. An
outer recursive inventory or reconciliation scan excludes sources below a
valid descendant manifest. Background scans retain revision-fenced probes for
known descendant barriers, so a missed invalid-to-valid rewrite or deletion
converges without adding filesystem work to an interactive request. If a nearer
manifest appears, ownership transfers to the new authority without publishing
a false deletion, while tracked peers that remain in the outer project keep
their outer authority. Authority-transfer commits also reactivate the selected
catalog for affected open sources, even when the fallback manifest text was
already warm and did not itself need reloading.

Manifest revisions are path-local. A change in one project cannot invalidate a
warm snapshot for an unrelated project; ad-hoc scopes watch only manifest
candidates in the active source's ancestor chain. Reconciliation captures an
authority incarnation as well as manifest and source revisions, so a scan from
a retired scope cannot commit into a later scope that happens to reuse the same
key. When the last tracked source retires, project caches, disk baselines, and
inactive manifest history are pruned. Open manifest overlays and manifest state
needed by a still-active ancestor or descendant boundary remain retained.

Source revision journals retain entries only while an overlapping snapshot or
reconciliation capture can still need them. Cancellation may dispose a capture
while its filesystem scan is in flight. Once scanning completes, its watermark
lease remains alive through ordered scope commits so a mid-scope stop cannot
prune newer source-revision fences. Completed captures release their leases and
prune acknowledged history. Shutdown cancels reconciliation, waits for a bounded
grace period, rejects late commits, and observes a non-cooperative scan if it
finishes after detachment.

`VbaDocumentAnalysis` owns text, coordinates, syntax, projected source
definitions, and document diagnostics for one accepted revision. Its build
consumes `SyntaxChangeSet` only while projecting the next source document; the
committed analysis retains neither parser route nor update metadata.
Exact-version features, including guarded Enter, capture that committed
analysis. They do not resolve a project, inspect a catalog, or recompute
diagnostics.

`VbaSemanticInventory` is the only project-scope editor-query authority. It
owns one immutable definition-candidate inventory, semantic resolution, lazy
occurrence shards, formatting, and semantic-token caches. The internal
`VbaSourceDocumentProjector` maps parsed syntax to immutable source definitions
and owns safe member-local definition reuse. The internal
`VbaSemanticTokenLegend` owns protocol token metadata. Neither Module creates
an alternate project-scope query authority.

### Interactive Semantic Readiness and project diagnostics

`InteractiveSemanticReadiness` means that one exact immutable
`VbaProjectSnapshot` has completed source capture, projection, reference
selection, and `VbaSemanticInventory` construction. The snapshot is then ready
for completion, hover, signature help, symbols, definition, references, rename,
formatting, and semantic-token capture. Building the inventory does not eagerly
construct its Project Validation Diagnostics. In particular,
`textDocument/semanticTokens/full` can capture and return exact token data while
complete-call validation for that same snapshot is still running.

Document-local syntax and validation diagnostics remain in the accepted
`VbaDocumentAnalysis` and can enter URI publication immediately. Project-wide
collection has a separate two-stage lifecycle:

1. The ordered lane captures the exact snapshot, source membership and
   revisions, manifest and reference selection, reference-catalog revision,
   and a cheap source-template path/existence/metadata fence into one
   `ProjectDiagnosticRevision`. It does not read, hash, or parse the workbook
   package.
2. A typed `VbaProjectAuthorityIdentity` latest-only mailbox reads the fenced
   source-template bytes and derives exact project-identity evidence, then runs
   the bounded `workspace/diagnostic` validation against the snapshot's
   existing `VbaSemanticInventory`. Both phases observe cancellation. Only
   after the complete current result exists does it partition by URI and post
   complete current sets to the separate `textDocument/diagnostic` publication
   mailboxes. Content equality is not a transport-suppression contract.

A newer source, manifest, catalog, close, or retirement state replaces pending
work and cancels obsolete active validation for that authority. Collectors
observe cancellation through project, declaration-pair, handler, and
argument-list traversal. Cancellation or failure publishes no partial batch,
releases revision ownership, and leaves both the ready inventory and the last
accepted diagnostics for unchanged members intact. A later input can run
normally. Every partition still passes both the exact project fence and its
target document fence immediately before transport.

Each successful selected reference-catalog commit immediately invalidates and
cancels every affected current project validation. It schedules no per-commit
replacement. The shared catalog batch instead records dirty authorities and,
when it settles, requests exactly one new diagnostic capture for each
still-current dirty authority. A failed or no-op batch with no commit requests
none. Each capture reads the best committed catalog revision, including a
retained last-known-good revision after refresh failure. Restored tabs and
sequential catalog revisions therefore replace obsolete validation work rather
than multiplying full-project runs that can no longer publish.

Invalidation retains active-URI and project-member routing for that final
refresh. Retirement removes both routes and discards the authority's mailbox
work. A late catalog settle cannot reactivate a retired authority; only a new
document/project lifecycle can establish fresh routing.

### Interactive work scheduler

`VbaInteractiveWorkScheduler` owns mutation ordering, immutable request
capture, bounded execution, cancellation ownership, priority, coalescing, and
shutdown. LSP feature code supplies a request kind and captured operation; it
does not select freshness, project scope, queue priority, catalog waits, or
cache invalidation policy.

The ordered lane admits mutations and captures reads. A read admitted after a
mutation cannot capture before that mutation commits. After capture, the read
runs against its pinned immutable snapshot on the bounded executor, so a later
mutation can commit without changing the earlier result. Request identifiers
retain response ownership even when responses finish out of order, and
`LspMessageTransport` serializes complete output frames.

Latency-critical reads reserve capacity ahead of normal, bulk, and background
work. Deterministic aging prevents starvation.
`VbaLatestOnlyBackgroundMailbox` owns pending replacement, active authority,
ready FIFO, capacity retry, and stop behavior for project validation,
diagnostics publication, and catalog refresh-start work. It takes the latest
delegate at execution start
without allowing a full queue to block the mutation lane. Diagnostics serialize
revision reservation and mailbox posting in producer order, while revision
freshness remains in the producer Module. Concurrent producers therefore
cannot restore an older pending revision.

### Intrinsic UserForm Event catalog mutations

The extension owns one environment-scoped catalog acquisition lifecycle,
operational status, and current-state replay. The language server receives only
the closed `vba/intrinsicHostEventCatalog` schema-`1.0` notification: a positive
monotonic revision and either one immutable complete catalog or `null` to clear
unavailable state. This transport version is independent from the `host-event
list --format json` CLI schema and from source, workspace, project, or document
revisions.

Admission parses the complete nested schema before mutation scheduling. Because
the catalog is environment-wide, pending notifications are ranked by their
single revision and the ordered lane retains only the greatest queued revision.
At execution, the workspace accepts only a revision newer than retained state,
atomically replaces or clears the catalog, and invalidates every affected
manifest-backed and ad-hoc project snapshot and diagnostic inventory. The
payload carries no project, document, source-template, component,
`VbaProjectName`, fingerprint, or source-association context.

After language-client start or restart, the extension replays the latest
current in-session catalog without running discovery again. It persists no
catalog across extension activations. Every authoritative `.frm` `FormModule`
binds the current catalog by source kind; no manifest or template association
is required.

A current catalog provides authoritative built-in UserForm Event evidence. An
unavailable catalog supplies no advisory replacement and is indeterminate, not
an authoritative empty surface. A request admitted before a later replacement
keeps its captured immutable inventory; a later request sees the accepted
replacement. No completion, hover, signature-help, diagnostic, or other editor
request invokes `vba-dev`, launches Excel, or waits for discovery or
notification completion.

### Semantic module-identity Rename

Prepare Rename treats only the unquoted payload of a correctly placed,
authoritative `Attribute VB_Name` record as the declaration occurrence. The
same source-owned identity also resolves through type uses, module and
predeclared-instance qualifiers, and conclusive interface prefixes. Missing
metadata remains filename-only fallback; malformed, misplaced, duplicate, or
overlength metadata is invalid. Neither state authorizes mutation.

A basename that case-insensitively equals the old identity follows a semantic
Rename for `.bas`, `.cls`, and `.frm`; a matching `.frx` follows its form. A
deliberately different basename remains unchanged.

For a manifest-backed module identity, Rename statically captures the exact
selected source-template package bytes into one request-scoped
`VbaProjectIdentityRead`. The reader validates the OPC package and unique
`vbaProject.bin`, opens its CFB `VBA` storage, decompresses the MS-OVBA
directory stream, reads `PROJECTCODEPAGE`, and decodes `PROJECTNAME` from the
same bytes. It starts neither Excel nor VBIDE, waits for no discovery, persists
no cache, and accepts no manifest, document, filename, generated-workbook,
reference-alias, or Event-catalog substitute. Missing, unreadable, malformed,
encrypted, subject to unsupported protection, or otherwise unsupported evidence
fails with `analysisIncomplete`. Before returning any complete module Rename
`WorkspaceEdit`, an unconditional whole-package content
fence rejects a template change even when the plan has no file operation; the
request never rebases itself onto newer content.

For a source-owned form, `VbaFormDesignerBlock` supplies the candidate
outermost root, ordered resource-reference ranges, and evidence problems without
creating designer `VbaDefinition`s. `VbaSemanticInventory` converts only
complete evidence into one `FormSourceUnitRename`, adding the root edit and
every matching `.frx` filename edit independently of the designer property
name. Nested controls, unrelated text, offsets, and binary sidecar content do
not become edits.

`VbaLanguageWorkspace` fences the `.frm`, optional `.frx`, their paths, and
every participating request-start source snapshot as one `FormSourceUnit`, even
when a deliberate basename produces no file operation. It verifies exact
current bytes and rejects malformed, unsafe, missing, displaced, conflicting,
multiply identified, or changed evidence before returning a plan. When paths
follow the identity, the client must advertise ordered `documentChanges` and
the `rename` resource operation. `resourceOperationConflict` carries the
specific condition, path, and repair guidance.

Containing and referenced project names participate only when current
authoritative evidence is complete. Containing authority comes only from the
request's `VbaProjectIdentityRead`; referenced authority retains its current
catalog contract. Manifest labels and generated aliases do not substitute.
This static read changes no ownership classification: installed CommonModules
retain their managed ownership, Worksheet and `ThisWorkbook` code-behind remain
unsupported, and a project-local, source-owned UserForm remains source-renamable
independently of environment-catalog binding. A later provider or filesystem
failure is a client-observed `WorkspaceEditApplicationFailure`; the client owns
Undo, repair, and a fresh request rather than relying on server rollback.

### Contract declaration-name completion

Contract-backed declaration names use one syntax-gated, kind-first flow across
intrinsic Host Events, external `WithEvents` Events, and `Implements` members,
including Property accessors derived from interface Public variables. The
syntax layer admits only an empty or partial name slot after `Sub`, `Function`,
or a complete `Property Get`, `Property Let`, or `Property Set` keyword
sequence. It owns the fragment and replacement range; semantic code does not
infer declaration shape from text.

| Stage | Input | Result | Edit |
| --- | --- | --- | --- |
| `ContractPrefixCompletion` | Empty or partial declaration name | A viable semantic prefix ending in one ASCII `_` | Replaces only the name fragment |
| `ContractMemberNameCompletion` | Exact viable prefix plus an optional suffix fragment | Canonical complete contract names | Replaces only the suffix and preserves written prefix spelling |

The captured Semantic Inventory enumerates all applicable origins for the
required callable or Property-accessor kind. Domain admission first applies
Event-authoring eligibility, current UserForm catalog evidence with
`authoringAvailable`, interface callable kinds, and derived accessor
kinds. The shared MS-VBAL declaration relationship policy then excludes the
physical declaration being edited and applies declaration-kind, namespace,
Property-accessor, and conditional-family collisions. A candidate with no
colliding peer remains available. Any unconditional prospective declaration or
peer suppresses a collision; an all-guarded prospective set remains available.
Complementary Property Get, Let, and Set accessors do not collide. A prefix
survives only when at least one downstream member survives both steps.

Case-insensitively identical prefixes coalesce within a request. Canonical
spelling is the whole contributor spelling selected by `OrdinalIgnoreCase` and
then `Ordinal` order. Prefix detail is `Host Events`, `WithEvents`, `Interface`,
or `Multiple Contracts`; its `[#If]` marker appears only when every surviving
relationship origin is guarded. Member detail is `Event`, `Interface Member`,
or `Multiple Contracts`; its `[#If]` marker appears when any concrete contract
provenance is conditional. Member-stage conditionality is computed separately
from the concrete relationship, Event or interface member, derived Public
variable, and any catalog Event retained as a configuration-dependent
alternative by `IntrinsicHostEventCoexistence`.
Member rows coalesce only within the required physical declaration kind and
retain every contributing origin. Their distinct signature presentations use
the same stable origin order as Signature Help; identical presentations and
documentation coalesce, empty documentation is omitted, and distinct values
remain numbered without exposing branch expressions.

A prefix completion carries the editor-neutral
`data.retriggerCompletion: true` intent and no command. The VS Code middleware
maps the literal intent to `editor.action.triggerSuggest` after applying the
edit, without overwriting a command already supplied by another participant.
Clients without continuation support apply the same prefix and explicitly
request completion. The server stores no prefix-selection session and always
re-resolves the current immutable snapshot, so either client path obtains the
same second-stage candidates.

The server advertises space and `_` completion triggers. Space specializes the
prefix result only at a valid empty declaration-name slot and otherwise
preserves ordinary completion. Underscore-triggered results are limited to a
proven contract prefix. Explicit and retrigger requests retain ordinary server
behavior, and the client duplicates none of the Host Event, Event-source, or
interface rules.

Both stages are name-only. They insert no parentheses, parameters, snippets,
procedure bodies, terminators, or multi-line stubs. Those edits belong to the
separate future `MemberStubGeneration` boundary.

## Hot-path stages

An ordinary open-document edit follows these stages:

1. The scheduler admits the mutation in input order.
2. The workspace reserves the accepted document revision under a short lock.
3. `VbaDocumentAnalysis` is built outside the lock.
4. A safe callable-body edit uses the ADR 0003 `ModuleMember` source-window
   parser. Prefix storage is retained, the changed member is replaced, and
   unchanged suffix coordinates are projected lazily through segmented syntax
   lists.
5. The workspace commits through compare-and-commit only if version, lifecycle
   epoch, and reservation token still identify the accepted head.
6. Only project scopes containing that source are invalidated.
7. Document-local diagnostics can enter URI publication immediately. Project
   diagnostics capture only their exact snapshot and revision ownership on the
   ordered lane; they do not construct the complete project diagnostic index.
8. The project-authority mailbox runs complete validation later as bounded
   `workspace/diagnostic` work against the ready inventory. A superseding
   revision replaces pending work and cancels obsolete active work.
9. A complete current result is partitioned by URI and admitted to the separate
   latest-only `textDocument/diagnostic` publication mailboxes. The mutation
   never awaits project validation or diagnostics transport.

The member path does not create a full-length masked source and does not clone
every shifted suffix collection. It remains an optimization: boundary edits,
parser recovery, unsafe projections, or ambiguous membership take a
conservative fallback.

Reference-catalog preload and discovery are project-lifecycle background work,
not source-edit work. Interactive requests read the best committed catalog and
never await discovery. A catalog commit re-enters the ordered mutation lane and
invalidates only scopes whose selected reference state changed.
Refresh-start plans fence each selected `VbaProjectAuthorityIdentity`,
independently of the typed manifest-document identity used as the mailbox
authority. Manifest replacement, document removal, and deactivation invalidate
the affected scope revisions. Execution skips stale selections without
discarding fresh peer scopes from the same plan.

## Safety fallback matrix

| Optimized path | Unsafe or unavailable condition | Required fallback |
| --- | --- | --- |
| Direct `ModuleMember` source-window parse | Recovery, boundary/header/terminator change, invalid window, cross-member conditional compilation, or shape mismatch | Full-module parse |
| Watcher-fed scoped source invalidation | Source relationship is unknown or a watcher reports a structural change | Rebuild the affected project scope |
| Bounded concurrent immutable reads | `VBA_TOOLS_INTERACTIVE_SERIAL_WORKER=1` rollback mode | Execute captured reads serially with the same visible results |
| Background catalog preload/discovery | Cache miss, cancellation, ambiguity, or refresh failure | Continue with bundled or last-known-good committed catalogs |
| Exact-version guarded Enter | Requested revision is stale, pending, closed, or mixed | Return no insertion plan and let native Enter behavior continue |
| Background Project Validation Diagnostics | A run is superseded, cancelled, closed, retired, or fails before a complete result exists | Publish no partition, release the run, retain the ready Semantic Inventory and last accepted unchanged-member diagnostics, and allow later current input to run |
| Latest-only diagnostics publication | Queue pressure or a superseding revision/close tombstone | Retain or retry only the latest queued authority state. A superseded queued publication is skipped; a versioned publication already in transport may finish and is rejected by version-aware clients if a newer revision has arrived. |
| Cached project snapshot | Manifest, selected catalog revision, source membership, or affected source revision changed | Construct a new immutable snapshot for that project scope |

Fallbacks preserve correctness and availability; they are not alternate
feature-owned coordination paths.

## Performance verification

### Cold manifest-backed semantic readiness

The primary cold benchmark uses a fresh Release workspace with no in-memory
project snapshot. Fixture and workspace setup complete before the timed
interval. The interval starts immediately before the baseline-compatible
`CreateProjectSnapshot(activeUri)` call and stops when that call returns the
exact `VbaProjectSnapshot` with a readable `VbaSemanticInventory`. The primary
run does not start Project Validation Diagnostics. Semantic-token projection is
timed separately from the returned inventory, and a separate validation run
checks the eventual complete diagnostic contract.

The snapshot observer reports `capture` as `scopeCapture` plus
`snapshotAdmission`, followed by `diskInventory`, `semanticInventory`, and
`storeReturn`; their outer interval is `interactiveSemanticReadiness`.
`openDocument` is reported but remains outside that baseline-compatible
interval, and `semanticTokenProjection` is reported after it. The run must also
report a zero Project Validation build count for the primary interval.

The recorded CommonModules diagnosis supplies this comparison baseline:

| Evidence | Recorded value |
| --- | ---: |
| Manifest document definitions | 1 |
| `VbaProjectSnapshot.SourceDocuments` | 94 |
| Parsed argument lists | 49,097 |
| Cold project snapshot | 49.907 s |
| Complete-call validation phase | 43.262 s |
| Diagnostic-bypass project snapshot | 6.191 s |

The 94 count is the recursive exported-source membership of the one manifest
document source set, not 94 top-level entries in `vba-project.json`. A passing
Windows Release run records a cold semantic-readiness interval no greater than
10 seconds and at least 80 percent faster than 49.907 seconds. Because the
percentage requirement is stricter, the effective ceiling is 9.9814 seconds.
The implementation may not exclude source files, suppress eventual
diagnostics, or weaken revision fences, and the benchmark may not include a
prebuilt snapshot.

An accompanying end-to-end process run is supplemental evidence and records
initialization, `didOpen`, and `textDocument/semanticTokens/full` response time. Set
`VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY` to an empty temporary directory to
capture scheduler phase files. Each `.admitted` file records `inputSequence`,
`readFence`, `kind`, `method`, `requestId`, and `admissionMilliseconds`; each
`.completed` file records the same identity plus `queueMilliseconds`,
`executionMilliseconds`, `cancelled`, and `faulted`. Preserve and report the
records for `textDocument/didOpen`, `textDocument/semanticTokens/full`,
`workspace/diagnostic`, and `textDocument/diagnostic` separately. Project
validation completion is evidence of eventual diagnostics, not part of the
semantic-readiness interval.

Every accepted benchmark report fills these fields. The test output may use
`not measured` rather than silently omitting an observation it cannot discover,
but that output is provisional: the verification note must supply every field
required for acceptance or explicitly explain why an unavailable observation
does not affect the two performance thresholds.

| Field | Required record |
| --- | --- |
| Source revision | Commit SHA and whether the worktree was clean |
| Build | Release command, target framework, runtime and SDK versions, architecture |
| Environment | Windows version, CPU, logical-core count, RAM, power mode, and competing-load notes |
| Corpus | Repository/path, corpus revision, manifest document count, `SourceDocuments` count, line count, and argument-list count |
| Cold-cache definition | Fresh process and empty in-memory workspace; state explicitly whether filesystem/OS caches were controlled |
| Samples | Warm-up policy, measured-run count, individual values, aggregation, and outlier policy |
| Timing source | Stopwatch boundary, process observation, and scheduler timing-directory path |
| Snapshot phases | `capture`, `scopeCapture`, `snapshotAdmission`, `diskInventory`, `semanticInventory`, `storeReturn`, and `interactiveSemanticReadiness` |
| Separate projections | Semantic-token projection from the returned inventory and eventual Project Validation Diagnostics |
| Supplemental LSP phases | Initialize; `didOpen` admission/queue/execution; semantic-token admission/queue/execution/response; `workspace/diagnostic`; `textDocument/diagnostic` publication |
| Correctness | Semantic-token revision/content assertion and final project-diagnostic equivalence result |

### Warm and mixed-load budgets

Release benchmarks exercise latency-critical requests while bulk references,
diagnostics, catalog work, and reconciliation are present. They report
percentiles rather than a single best-case duration. Request measurements
include scheduler queue/capture time plus execution time and enforce these
budgets:

The mixed-load scheduler fixture first fills its queues behind a synthetic
barrier. Its latency window begins when that barrier releases, so it includes
all queue selection, capture, and execution after the work becomes eligible
but excludes time deliberately spent constructing the fixture.

- mutation admission: p95 at or below 5 ms;
- an ordinary edit in an 8,000-line module: p95 at or below 50 ms;
- warm completion, hover, and signature help: p95 at or below 50 ms and p99 at
  or below 100 ms;
- guarded block-skeleton planning: p95 at or below 100 ms and p99 at or below
  150 ms; and
- delayed catalog refresh: interactive p95 increase at or below 10 ms.

Deterministic structural and process tests complement timing benchmarks. They
pin exact-version behavior, cancellation races, output framing, latest-only
diagnostics, watcher-first freshness, affected-project invalidation,
scope retirement and stale-scan rejection, nested manifest ownership,
segmented-suffix behavior, serial rollback equivalence, and
last-known-good-catalog behavior. The repository-owned manifest-backed large
project fixture contains at least 90 source documents and 40,000 argument lists
without using the external CommonModules checkout. Controllable validation
barriers verify semantic-token readiness at that scale. Across the deterministic
workspace, scheduler, and process suites, barriers and counters rather than
wall-clock timing prove coalescing, cancellation, failure recovery, and clean
shutdown. Performance results are accepted only from
Release runs with the full syntax, language-server, process, packaging,
extension, Extension Host, and guarded Enter regression suites.
