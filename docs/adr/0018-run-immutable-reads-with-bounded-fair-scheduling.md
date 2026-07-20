---
status: accepted
---

# Run immutable reads with bounded fair scheduling

## Context

ADR 0013 separated stdio input admission from one serial execution lane. That
made cancellation and lifecycle control responsive, but a CPU-heavy reference,
rename, formatting, or project query could still delay unrelated completion,
hover, signature help, and guarded Enter work.

ADRs 0014 through 0017 subsequently established project-owned reference
catalog lifecycle, exact document analysis revisions, watcher-fed immutable
project snapshots, and project-scoped Semantic Inventory. Concurrent query
execution is safe only if it preserves those ownership boundaries, never reads
live mutable workspace state after dispatch, and does not make lower-priority
maintenance work starve.

## Decision

`VbaInteractiveWorkScheduler` retains one FIFO mutation-and-capture lane.
Document, watched-file, manifest, and visible reference-catalog commits enter
that lane. A read admitted after a mutation cannot capture until that mutation
commits. Its capture step then pins exactly one immutable
`VbaVersionedDocumentSnapshot`, `VbaSemanticInventory`, or immutable set of
project inventories. Once capture returns, the query may execute on a bounded
read executor while later mutations continue. Capture does not wait for an
executor slot: a captured background read that is waiting for capacity cannot
hold the ordered lane or delay a later mutation. A later `didChange` does not
cancel or alter the pinned read.

The scheduler owns the consistency and priority mapping. Feature code supplies
an LSP query kind and cannot choose a priority, freshness rule, project scope,
catalog wait, or arbitrary live-workspace callback. The internal classes are:

- latency-critical: guarded block skeleton, completion, hover, and signature
  help;
- normal: definition, document symbols, workspace symbols, prepare rename, and
  semantic tokens;
- bulk: references, rename, and formatting;
- background: latest-only diagnostics, reconciliation, reference-catalog
  refresh admission, and reference-catalog publication.

The queue has both a physical bounded channel and a logical owned-work limit.
Read concurrency and bulk concurrency have independent limits. Production
defaults allow four reads, at most three non-latency-critical reads, and only
one bulk read. One executor slot therefore remains available to later
latency-critical work even when normal, bulk, or background work is sustained.
The single-read serial and deterministic-test configurations retain their one
usable non-latency slot.

Hard queue capacity is not converted into an exception on the ordered mutation
lane by background producers. Diagnostics and reference-catalog refresh own
authority-keyed, single-flight latest mailboxes. A typed `Try` admission may
load-shed a new advisory operation at the hard bound; the mailbox retains its
latest state and a lightweight capacity observer retries one deferred
authority after capacity returns. The capacity notification is level-triggered
and rotates its starting producer, so a producer that rearms during a callback
cannot lose the wake-up or monopolize every released slot. Every admitted or
superseded item still receives one terminal completion and releases its queue
and cancellation ownership before that completion becomes observable.

Pending reads use deterministic aging. Latency-critical, normal, bulk, and
background work start with decreasing base scores. Each higher-class dispatch
ages waiting lower classes until they tie and win by earlier input sequence.
Sustained interactive traffic therefore cannot indefinitely postpone admitted
diagnostics, reconciliation, catalog refresh, or publication work.

Diagnostics capture their immutable analysis and client version before
background admission. Their URI mailbox retains at most one pending revision
and one queued or in-flight scheduler admission. Source and manifest mutations
never await transport writes. The runtime attaches the diagnostics publisher
to the scheduler, so publication is bounded, latest-checked, serialized by
`LspMessageTransport`, and owned by shutdown.

Reference-catalog discovery remains on its dedicated low-impact worker because
TypeLib COM calls may not cooperate with cancellation. The scheduler fairly
admits the short, latest-only refresh-start ticket. Persisted or discovered
catalog data does not become visible directly from that external task: its
cache commit is admitted back through the ordered mutation lane. A
correctness-bearing commit waits cooperatively for capacity and reserves the
next released opportunity ahead of advisory retries; cancellation removes the
reservation and cannot commit late. Trace and result publication use typed
best-effort background admission. Shutdown dispatches producer cancellation
with `CancelAsync` and observes both callbacks and external tasks inside the
same bounded timeout. It then drains or aborts scheduler-owned work, so a late
catalog task cannot admit work after the scheduler closes.

Explicit request cancellation remains request-id-owned and cooperative.
Cancellation is checked during references, rename, formatting, semantic-token,
and occurrence traversal. A cancelled lazy shard build is never published;
another request can retry it. The response decision uses the request token,
then the serialized response write uses the independent transport lifetime.
Each request still produces at most one terminal response.

`VBA_TOOLS_INTERACTIVE_SERIAL_WORKER=1` selects the complete rollback mode.
That mode uses the same mutation/capture boundary and visible query results but
waits for each captured read on the ordered lane. It does not reintroduce a
legacy workspace or request-execution path.

## Considered options

- A free-running task per request improves throughput but loses bounded
  ownership, ordering, and deterministic shutdown.
- Cancelling every pinned read after `didChange` saves some CPU but changes
  useful historical request semantics and conflates supersession with explicit
  cancellation.
- Running TypeLib discovery itself on a scheduler read slot would let a
  non-cooperative COM call hold graceful shutdown and interactive capacity.
- Static strict priority minimizes median completion latency but can starve
  diagnostics and maintenance indefinitely.
- Keeping only the logical owned-work count with an unbounded channel leaves an
  unnecessary allocation and retention escape hatch.

## Consequences

Response order may differ from input order, but request ids retain ownership
and output frames never interleave. Reads admitted before a later mutation may
return an older, internally consistent snapshot; reads admitted after that
mutation capture the new revision. This is intentional and observable only
through ordinary request causality.

Gate-driven tests must cover mutation/capture ordering, old-snapshot pinning,
explicit cancellation races, latest-only coalescing, bulk reservation,
non-latency slot reservation, capacity return, priority aging, catalog commit
re-entry, producer overflow mailboxes, production background wiring,
serialized output, bounded producer cancellation, graceful drain, abort, and
the serial rollback.

Release performance tests run simultaneous bulk, diagnostics, catalog, and
reconciliation load. Mutation admission p95 must remain at or below 5
milliseconds. Warm completion, hover, and signature help must remain at or
below 50 milliseconds p95 and 100 milliseconds p99. Guarded Enter must remain
at or below 100 milliseconds p95 and 150 milliseconds p99.

This decision extends and partially supersedes ADR 0013's serial-default
execution decision. ADR 0013's input sequence, read fence, cancellation
ownership, output serialization, shutdown, and adjacent mutation-coalescing
invariants remain in force.
