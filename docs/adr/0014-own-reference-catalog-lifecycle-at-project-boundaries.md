---
status: accepted
---

# Own reference catalog lifecycle at project boundaries

## Context

The C# language server previously resolved `ProjectManifest` state, loaded
persisted reference catalogs, and planned TypeLib discovery from ordinary VBA
`didChange` notifications. The refresh service then attempted persisted preload
again before discovery. Missing or unreadable cache entries therefore caused
repeated filesystem work on the hottest editing path.

ADR 0013 serializes interactive language-server work for compatibility. Any
preload awaited by a source notification consequently delays later completion,
hover, and signature-help requests even though those requests need only the
best catalog already committed in memory. A single global catalog version also
caused a successful refresh to rebuild project snapshots that did not select
the changed reference.

ADR 0009 keeps project resolution, reference selection, and editor intelligence
authoritative in the C# language server.

## Decision

`IReferenceCatalogLifecycle` owns automatic reference-catalog work at project
boundaries. It reacts to project activation, effective manifest
reference-selection changes, and manifest deactivation. Ordinary VBA source
edits and source watcher reloads update source analysis and diagnostics only.

Each manifest-document scope records a deterministic
`ReferenceSelectionFingerprint` and `ReferenceCatalogLifecycleRevision`.
Repeated activation with the same scope and fingerprint reuses the active
revision. Equal fingerprints admitted together share one persisted preload and
discovery pass. A changed fingerprint starts a new revision. Work for different
fingerprints remains concurrent when their reference sets are disjoint; when
they overlap, the later work asynchronously joins the in-flight owner before
re-evaluating the shared reference. Cancellation therefore cannot leave a
later lifecycle revision permanently skipping another revision's reservation.

Persisted preload and TypeLib discovery run in coordinator-owned background
tasks. The coordinator owns their lifetime token, observes failures, and
cancels them during server shutdown. Shutdown waits for cooperative work for a
bounded interval; a non-cooperative synchronous TypeLib COM call remains
observed but cannot hold the language-server process open indefinitely.
Completion, hover, signature help, semantic tokens, and other editor queries
never await lifecycle work; they read the best catalog state already committed.

A cache-owned per-reference lease spans persisted preload and discovery.
Automatic lifecycle work asynchronously waits for an existing owner, while an
explicit refresh immediately claims only references that are currently free.
Disjoint reference sets remain concurrent. This prevents a delayed stale
preload from overwriting a newer generated catalog and preserves the existing
non-overlapping explicit-refresh contract.

A missing or unreadable persisted entry is negative-cached by the active
lifecycle revision because that revision is not scheduled again. An explicit
refresh bypasses the lifecycle ledger and may retry immediately. Moving to a
different fingerprint also permits a new attempt.

Cancelled, failed, and ambiguous refreshes preserve the
`LastKnownGoodReferenceCatalog`. A successful preload or discovery replaces the
catalog, its source, and its reference-specific last-change revision under one
cache lock. A discovery result is successful only when it has no error and
exactly one identity; malformed results cannot commit or persist a catalog.
Project snapshots compare only the greatest last-change revision among
references in their effective selection, so unrelated project scopes remain
cached without allocating a revision string on every interactive query.

Catalog availability, cache-read warnings, and discovery failures remain
language-server log, status, trace, or environment information. They do not
become VBA source diagnostics.

## Considered options

- Retaining lifecycle work in `didChange` repeats manifest and filesystem work
  on the interactive editing path.
- Loading catalogs from completion, hover, or signature help directly violates
  the non-blocking editor-query contract.
- Permanently caching missing or corrupt entries would suppress explicit retry
  and later selection changes.
- Retaining one global catalog revision would continue rebuilding unrelated
  project scopes.

## Consequences

The first query after project activation may temporarily observe bundled or
previously committed metadata until background preload completes. Later
queries observe the new atomic commit automatically.

Lifecycle operation counts are deterministic by scope, fingerprint, and
revision. Source changes do not restart automatic work. Release tests must keep
the p95 query-latency increase at or below five milliseconds while preload or
discovery is delayed. Shutdown tests must prove both that cooperative blocked
work is cancelled without an external release and that non-cooperative work
cannot make shutdown unbounded.
