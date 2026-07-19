---
status: accepted
---

# Use a serial interactive work scheduler

## Context

The C# VbaLanguageServer originally read one stdio JSON-RPC message and awaited
its complete request or notification handling before reading the next message.
A long-running request therefore prevented the server from accepting
`$/cancelRequest`, later document updates, shutdown, or EOF. Request execution
also used one cancellation token for both computation and response writing, so
a cancelled request could lose its required `RequestCancelled` response.

ADR 0009 keeps VBA parsing, workspace state, and editor-intelligence behavior
authoritative in C#. ADR 0012 requires exact-version guarded Enter requests and
a bounded client fallback, but it does not move scheduling authority into the
TypeScript adapter.

## Decision

The runtime uses `VbaInteractiveWorkScheduler` to separate input admission from
execution. The stdio reader continues admitting requests, relevant workspace
mutations, and ordered non-mutating barriers. One FIFO execution lane remains
the default compatibility mode. Concurrent reads are deferred; this decision
does not enable them.

Every admitted input receives an `InputSequence`. A relevant
`didOpen`, `didChange`, `didClose`, or watched-file mutation advances the
`ReadFence`; a read records the latest preceding mutation sequence. FIFO
execution means that the read observes every relevant mutation through that
fence. Unknown or non-mutating notifications remain ordered barriers but do not
advance it.

Each active numeric or string request id has generation-specific
`RequestCancellationOwnership`. `$/cancelRequest` bypasses the lane only to
signal that request-scoped token. A later `didChange` never cancels an earlier
read merely because newer source arrived. `VbaLspRequestExecution` alone chooses
and writes one terminal response. It converts observed explicit cancellation
to LSP error `-32800`, releases cancellation ownership after choosing the
terminal outcome, and writes with the separate transport-lifetime token.

The existing `LspMessageTransport` serialized writer remains the single output
path for responses and notifications. Its lock spans the complete header,
payload, and flush operation, preventing frame interleaving.

A valid `shutdown` request is ordinary sequenced work. A following `exit` is an
ordered terminal barrier, so it observes the completed shutdown and exits with
code 0. An `exit` without an admitted valid shutdown aborts immediately with
code 1. EOF, host cancellation, or a terminal work/transport failure closes
admission, cancels in-flight ownership, skips queued work that never started,
observes owned tasks, and stops without attempting cancellation responses on a
dead transport.

Scheduler instrumentation measures three monotonic intervals separately:

- admission: scheduler entry through successful channel admission;
- queue: successful admission through execution start;
- execution: execution start through terminal work completion, including any
  response write.

The no-op sink is the production default. Deterministic process tests may
enable file-backed timing and request gates through test environment variables.
A Release benchmark records all three p95 values and requires mutation
admission p95 at or below 2 milliseconds on the reference fixture.

The server continues advertising LSP full-text document synchronization. This
decision changes neither that contract nor the C# authority established by ADR
0009.

The scheduler may mark full-text `didChange` mutations as coalescible by source
URI. Coalescing is an execution-lane optimization, not a new observable
document-version contract: admission sequence and read fences are still
recorded for every input. When multiple queued `didChange` mutations for the
same URI are adjacent and no request, barrier, lifecycle transition, watched
file mutation, manifest mutation, or other source URI separates them, the lane
may skip the superseded mutation body and execute only the newest queued body.
The optimization stops at the first non-matching queued item, so a read admitted
between V2 and V3 still observes V2 before V3 can execute. Coalescing is
controlled by `VbaInteractiveWorkSchedulerOptions` and can be disabled without
removing exact-version snapshots or latest-only diagnostics.

## Considered options

- Keeping the blocking read/execute loop preserved incidental serial behavior,
  but made explicit request cancellation and prompt lifecycle control
  impossible.
- Running requests concurrently would improve throughput for some reads, but
  requires snapshot isolation and a broader read-fence policy. It is deferred
  to issue #218.
- Cancelling every older read on `didChange` could reduce obsolete work, but
  changes observable semantics and conflates document versions with explicit
  request cancellation.
- Writing cancellation responses with the request token loses the response
  once that token is cancelled.

## Consequences

The adapter remains thin and the C# server remains authoritative. Arrival
causality is explicit instead of depending on a blocking read loop. The first
slice still has one execution lane, so CPU-heavy work can delay later work even
though input and cancellation remain responsive.

Request ids must distinguish numeric and string values. Overlapping duplicate
ids receive a deterministic invalid-request response without replacing the
existing owner; reuse is allowed after terminal ownership release. Shutdown,
exit, EOF, cancellation-token disposal, output serialization, completion versus
cancellation races, and timing thresholds require gate-driven regression tests
without sleep-based ordering assumptions.

Coalesced work records a normal terminal completion with zero execution time
for the superseded queued mutation. That keeps admission/completion accounting
balanced while making accepted input versions distinct from optional
intermediate analysis work.
