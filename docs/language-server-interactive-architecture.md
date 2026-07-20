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
catalogs, or build an alternate source index. A warm project-snapshot hit
returns before source enumeration, disk stat/read, or project-wide semantic
construction. Open buffers remain authoritative over equivalent disk sources.
Project-snapshot construction and background reconciliation route their
manifest and exported-source I/O through one host-neutral project filesystem
boundary. Deterministic tests count every operation at that boundary and
require a warm interactive capture to add no manifest read, source enumeration,
metadata query, source read, or project/semantic rebuild.
Disk changes become visible through accepted watcher events, reconciliation,
or an explicit reload; an unreported raw disk write may remain stale while the
warm snapshot is valid.

Background reconciliation keeps a stable authority identity separate from a
manifest's mutable content identity. A manifest-backed authority is keyed by
manifest path and document name; an ad-hoc authority is keyed by its inferred
root. Filesystem scans run outside the ordered mutation lane with at most two
scopes in flight, and their results commit in stable authority order only after
source, manifest-overlay, and workspace revision fences still match. Accepted
commits advance source-by-source disk baselines. Invalid manifest text advances
only the observed-disk baseline, preserves any last-known-good effective
manifest, and publishes one validation diagnostic until the text changes. If
no last-known-good manifest exists, a cold interactive request that first
discovers the invalid disk text records it and returns a manifest error. Once
validation has recorded that invalid disk state, the file stops acting as an
ownership barrier and later resolution falls back without rereading it.

An accepted manifest mutation requests an immediate reconciliation follow-up
so newly exposed ancestors and descendants converge without waiting for the
next cadence. Rejected mutations retry only while their captured fingerprint
makes new progress, and one trigger stops after 32 passes to bound filesystem
churn; every pass remains cancellation-aware.

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
reconciliation capture can still need them. Completed captures release their
watermark leases and prune acknowledged history. Shutdown cancels
reconciliation, waits for a bounded grace period, rejects late commits, and
observes a non-cooperative scan if it finishes after detachment.

`VbaDocumentAnalysis` owns text, coordinates, syntax, projected source
definitions, document diagnostics, and incremental parse metadata for one
accepted revision. Exact-version features, including guarded Enter, capture
that committed analysis. They do not resolve a project, inspect a catalog, or
recompute diagnostics.

`VbaSemanticInventory` is the only project-scope editor-query authority. It
owns one immutable definition-candidate inventory, semantic resolution, lazy
occurrence shards, formatting, and semantic-token caches. `VbaSourceIndex`
query instances form a compatibility facade that delegates to an inventory; no
raw index is stored in a project snapshot or used as an interactive
coordination path.

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
work. Deterministic aging prevents starvation. Authority-keyed mailboxes
coalesce advisory diagnostics and catalog work to the latest pending state
without allowing a full queue to block the mutation lane. Diagnostics revision
reservation, pending-mailbox replacement, and worker ownership commit under
one gate, so concurrent producers cannot restore an older pending revision.

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
7. Diagnostics capture the committed analysis and are admitted to their
   latest-only background mailbox. The mutation never awaits diagnostics
   transport.

The member path does not create a full-length masked source and does not clone
every shifted suffix collection. It remains an optimization: boundary edits,
parser recovery, unsafe projections, or ambiguous membership take a
conservative fallback.

Reference-catalog preload and discovery are project-lifecycle background work,
not source-edit work. Interactive requests read the best committed catalog and
never await discovery. A catalog commit re-enters the ordered mutation lane and
invalidates only scopes whose selected reference state changed.

## Safety fallback matrix

| Optimized path | Unsafe or unavailable condition | Required fallback |
| --- | --- | --- |
| Direct `ModuleMember` source-window parse | Recovery, boundary/header/terminator change, invalid window, cross-member conditional compilation, or shape mismatch | Full-module parse |
| Watcher-fed scoped source invalidation | Source relationship is unknown or a watcher reports a structural change | Rebuild the affected project scope |
| Bounded concurrent immutable reads | `VBA_TOOLS_INTERACTIVE_SERIAL_WORKER=1` rollback mode | Execute captured reads serially with the same visible results |
| Background catalog preload/discovery | Cache miss, cancellation, ambiguity, or refresh failure | Continue with bundled or last-known-good committed catalogs |
| Exact-version guarded Enter | Requested revision is stale, pending, closed, or mixed | Return no insertion plan and let native Enter behavior continue |
| Latest-only diagnostics publication | Queue pressure or a superseding revision/close tombstone | Retain or retry only the latest queued authority state. A superseded queued publication is skipped; a versioned publication already in transport may finish and is rejected by version-aware clients if a newer revision has arrived. |
| Cached project snapshot | Manifest, selected catalog revision, source membership, or affected source revision changed | Construct a new immutable snapshot for that project scope |

Fallbacks preserve correctness and availability; they are not alternate
feature-owned coordination paths.

## Performance verification

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
last-known-good-catalog behavior. Performance results are accepted only from
Release runs with the full syntax, language-server, process, packaging,
extension, Extension Host, and guarded Enter regression suites.
