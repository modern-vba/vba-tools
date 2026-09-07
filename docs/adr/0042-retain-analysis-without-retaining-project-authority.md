---
status: accepted
---

# Retain analysis without retaining project authority

## Context

Explorer preview replacement can close the previous VBA document before opening
the next one. Retiring the last tracked source correctly ends its active project
and diagnostic ownership, but discarding every parsed projection and semantic
inventory forces unchanged projects to repeat preparation on each preview.
Issue #365 requires a resident CommonModules project to reopen within one second
after initial Interactive Semantic Readiness, including repeated zero-source
transitions. Idle time alone must not discard eligible analysis.

## Decision

Keep a bounded, process-local store of `VbaRetainedProjectAnalysis` separately
from active project snapshots. Only a complete snapshot that passes the existing
current-ownership commit fence can admit reusable data. An entry contains parsed
source projections, exact source text, captured semantic inputs, and a detached
semantic inventory shell. It contains no active snapshot, lifecycle epoch,
reconciliation authority, diagnostic ownership, background task, validation index,
or publication lease.

Retiring the last source still removes active snapshot state, authority seeds,
reconciliation baselines, parsed disk caches, and inactive manifest state. Old
source exclusions belonging to a retired scope expire with that authority, so a
silently recreated file is discoverable in a later lifecycle. An active nested
scope retains its own exclusions. A reopened document obtains a fresh lifecycle,
scope identity, snapshot, reconciliation generation, and diagnostic ownership.
Abandoned builds cannot populate either active or retained state after retirement.

Before inactive reuse, refresh relevant closed manifest ancestors and known
descendant barriers from current content. Open manifest overlays remain
authoritative, and a nearer effective manifest stops ancestor traversal. Resolve
the current project again, enumerate source membership and nested ownership, and
perform stable content reads for every closed source even if metadata matches.
Open buffers supply their current Unicode text. Watcher-first behavior within an
already active project remains unchanged.

Reuse requires a private equality proof over the structural project identity,
ordered references and CommonModules entries, reference-catalog selection
revision, intrinsic host-event revision, exact document identity/membership,
and complete source text. Equivalent URI spelling (including VS Code's escaped
lowercase drive) never changes a document identity. An inventory's definitions
and occurrence shards remain together; they are never mixed with a different
projection merely to change presentation URIs. Missing or undecodable inputs cannot prove
equivalence. Matching individual source projections may avoid reparsing; the
semantic inventory is reusable only when the whole proof succeeds. Any changed
semantic input rebuilds the inventory conservatively.

ADR 0017's occurrence and token shards may cross this retention boundary only
under that proof. A fresh inventory shell shares immutable definitions,
resolution, and atomically published editor caches, while owning a new validation
gate, observer anchor, and lazy validation index. Sharing data gives obsolete
work no right to publish diagnostics or replace current authority.

## Capacity and lifetime

The default limit is four entries and 1 GiB of accounted retained analysis.
The Windows CommonModules measurement with settled registry reference catalogs
accounts for approximately 624 MB; the bundled-catalog-only case accounts for
approximately 372 MB. The total budget therefore accommodates this reference
project with its full semantic inputs. It does not promise four projects of that
size at once.
Admission charges syntax tokens and nodes, source and reference definitions,
catalogs and host-event metadata, retained documents, and reserved capacity for
all lazy occurrence/token shards. Shared structures are charged per entry. The
accounting is a conservative weighted estimate, not a measurement of the CLR
heap; the CommonModules reference measurement records both the estimate and
managed heap size in the performance evidence.

Eviction uses least recent successful admission/reuse with deterministic order.
An entry larger than the total budget, or one whose syntax footprint cannot be
accounted for, is not retained and follows the ordinary cold path. Capacity
misses and changed-input rebuilds are outside the unchanged resident-cache
latency target. In-flight reads may finish using captured immutable data after
eviction, but must still pass their own current-ownership fence before commit.
Retained entries have no idle timer and are released on workspace/server
teardown. There is no persistence, startup prewarming, or user-facing cache setting.

## Verification

Complete workspace close/reopen tests compare token integers, definitions,
references, membership, and diagnostics with independent fresh analysis. They
cover one and zero remaining sources, discarded edits, missed source/manifest
changes, capacity, and stale work. Existing reconciliation, cancellation, and
publication tests retain the active-state retirement contract.

[The Explorer measurement](../preview-analysis-performance.md) records the
actual VS Code document lifecycle, twenty provider latency samples, fresh-token
oracle comparisons, renderer observations, corpus identity, environment, and
separated scheduler phases. Synthetic workspace/LSP timings supplement this
measurement and never substitute for Explorer preview evidence.
