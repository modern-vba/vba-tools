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

Registry-only discovery uses the same neutral TypeLib registry catalog contract
as `vba-dev`: scan the merged, shared `HKEY_CLASSES_ROOT\TypeLib` view once,
interpret version and LCID keys as hexadecimal, group versions of one GUID as a
descending lineage, and retain `win32` and `win64` paths as metadata without
using the language-server process bitness as Office bitness. It does not union
`Registry32` and `Registry64` views or start Excel/VBIDE. A catalog-level read
failure is incomplete and fails closed; malformed individual registrations
cannot manufacture an identity.

When TypeLib registry discovery cannot identify one concrete reference identity,
the background lifecycle may invoke
`vba-dev reference list --project <path> --document <name> --format json`. The
command resolves every manifest-defined reference for that document without
changing the manifest. `vba-dev` owns any required Excel/VBIDE probe and receives
no VS Code editor state. The language server consumes the returned identity only
inside background refresh; synchronous editor requests never invoke or wait for
the CLI. A schema-valid, complete response whose scope is `project` and whose
project, document, and mode match the request is processed per reference even
when the command exits nonzero: each `resolved` entry may commit a new catalog,
while an `ambiguous` or `unavailable` entry preserves only that reference's
`LastKnownGoodReferenceCatalog`, or remains unavailable if none exists. An
`unverified` entry necessarily makes the response incomplete. Malformed JSON,
a schema or request-context mismatch, `complete: false`, or a nonzero exit
without a valid response makes the entire invocation untrusted; no entry from
that invocation commits, and every affected reference preserves its
last-known-good state. Validation ignores unknown additive properties and
unknown warning or diagnostic codes, but rejects unknown schema, scope, mode,
status, or status-specific reason discriminators, missing required properties,
wrong JSON types, known status-inconsistent properties, noncanonical identity
values, duplicate candidates, and noncanonical candidate order.

Generated and persisted TypeLib catalogs retain raw `TYPEKIND`, coclass and
interface `TYPEFLAGS`, implemented-interface identity and `IMPLTYPEFLAGS`,
callable-member `FUNCFLAGS`, member identity and signatures, and Event-surface
completeness so semantic analysis can derive the VBE-equivalent
`TypeLibEventSurface`. A coclass projects Events only from its unique
`FDEFAULT | FSOURCE` interface; non-default source interfaces are not unioned.
The retained flags support separate structural, authoring, and
existing-handler-recognition projections: hidden and restricted members count
structurally, are excluded from ordinary handler authoring, and remain
recognizable on already-written handler-shaped declarations. A
`TYPEFLAG_FHIDDEN` coclass remains explicitly usable, while a
`TYPEFLAG_FRESTRICTED` coclass is conclusively inaccessible to VBA. A legacy
catalog without these facts remains usable for ordinary metadata it proves, but
its TypeLib Event surface fails closed as stale and remains eligible for
refresh.

Generated and persisted catalogs also retain callable Property accessor
identity and its accessor-specific signature. `INVOKE_PROPERTYGET` maps to
`Property Get`, `INVOKE_PROPERTYPUT` to `Property Let`, and
`INVOKE_PROPERTYPUTREF` to `Property Set`; value-put and reference-put are never
collapsed merely to `Writable` for interface implementation semantics. Ordinary
property resolution may still coalesce those physical definitions into one
logical readable or writable member. Missing accessor identity fails closed for
Let or Set implementation completion and validation rather than being inferred
from a value type. The unreleased persistent and bundled catalog schemas are
regenerated so legacy entries cannot silently supply that missing distinction.

Generated, bundled, and persisted catalogs also distinguish the authoritative
`ReferencedVbaProjectName` supplied by the selected project or TypeLib from
human-visible manifest reference names and ordinary qualifier aliases. For
mutation authority, the name must come either from an explicit bundled contract
or from a concrete TypeLib identity in a current-schema persisted or generated
catalog committed for the active `ReferenceSelectionFingerprint`. An in-flight
refresh does not invalidate that committed authority. A stale-persisted or
legacy catalog may remain usable for definitions, Completion, Hover, and other
metadata it proves, but it cannot prove the referenced-name uniqueness required
by `ModuleIdentity` Rename and remains eligible for refresh.

The VS Code extension starts and initializes the language client without
waiting for companion executable resolution. Once that client is operational in
a trusted window, the extension uses its one session resolver to select and
validate the configured-to-bundled `vba-dev` fallback. The same pinned
`CompanionExecutableResolution` serves commands, Doctor, UserForm Event
discovery, and language-server reference discovery. The extension publishes the
selection through one closed `vba/companionExecutable` schema-`1.0`
notification containing the absolute executable path and the already validated
`reference list` output schema version.

The language server pins the first valid notification for its session and moves
one way from registry-only discovery to CLI-backed context discovery. It rejects
a malformed notification, an incompatible notification or reference-list
schema, a non-absolute path, and any attempt to replace the pinned path. A
successful notification is applied as non-coalescing background scheduler work,
so it creates no ordered barrier behind an active read and does not fence a
later higher-priority editor request. Its late pin schedules one latest-only
background refresh for each still-active project authority. It requires neither
a window reload nor a language-server restart, and completion, hover, signature
help, semantic tokens, and diagnostics do not wait for the pin or refresh.

A standalone language server may receive one absolute `--vba-dev` path together
with its stdio selection. It starts the protocol loop before asynchronously
running `vba-dev capabilities --format json` and requiring `reference list` JSON
schema `1.0`. Until validation succeeds, and after a missing, changed,
incompatible, cancelled, or failed candidate, discovery remains registry-only
and fail-closed. The language server records the failure without stopping
language assistance. Neither startup form searches `PATH`, reads VS Code
settings, infers a sibling executable, or substitutes another candidate.

Capability inspection and CLI-backed `reference list` discovery share one
language-server-local `VbaDevProcessInvocation` Deep Module. The Module pins the
resolved absolute executable, accepts an immutable ordered argument list, uses
no shell, and begins concurrent stdout and stderr drain immediately after
process start. A nonzero exit remains an ordinary complete process result for
the command-specific contract to interpret. Each caller retains its own JSON
schema, request-context validation, warnings, and catalog policy.

When cancellation wins after process start while terminal exit or either
stream drain remains pending, the Module requests process-tree termination
once, waits for terminal exit without the cancelled token, and drains both
streams within a bounded cleanup deadline before preserving the original
cancellation outcome. Reference-catalog shutdown extends its producer wait by
that declared cleanup budget, and each batch awaits the shared invocation task
through cancellation rather than detaching a per-reference waiter. Cooperative
process cleanup therefore remains observed instead of escaping the
coordinator's shorter registry-only grace. A benign
already-exited race is absorbed; a missed deadline or other inability to prove
termination is a lifecycle failure.
This local-substitutable Seam does not absorb the debug
adapter's Windows Job ownership or an interactive stdin cancellation protocol.
It adds a language-server dependency on the public `vba-dev` process contract
without adding a reverse dependency to `VbaDev`. The pre-existing parser
reference from `VbaDev` to a language-server-owned project remains a separate
migration tracked by issue 361.

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

Cancelled, failed, and ambiguous per-reference results preserve that
reference's `LastKnownGoodReferenceCatalog`. A successful preload or resolved
CLI entry replaces the catalog, its source, and its reference-specific
last-change revision under one cache lock. A discovery result is successful
only when it has exactly one identity and belongs to a trusted complete
invocation; malformed or incomplete results cannot commit or persist a catalog.
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
- Resolving or validating `vba-dev` before language-client startup makes safe
  semantic highlighting depend on an optional managed process.
- Restarting the language server after companion resolution discards usable
  editor state merely to enable background metadata enrichment.
- Permanently caching missing or corrupt entries would suppress explicit retry
  and later selection changes.
- Retaining one global catalog revision would continue rebuilding unrelated
  project scopes.

## Consequences

The first query after project activation may temporarily observe bundled or
previously committed metadata until background preload completes. Later
queries observe the new atomic commit automatically.

`InteractiveSemanticReadiness` is independent from companion executable and
UserForm Event catalog readiness. A blocked capability probe cannot hold
activation, initialization, `didOpen`, or the first semantic-token response.
Restricted Mode starts no managed companion process and retains registry-only,
safe language assistance.

Lifecycle operation counts are deterministic by scope, fingerprint, and
revision. Source changes do not restart automatic work. Release tests must keep
the p95 query-latency increase at or below 10 milliseconds while preload or
discovery is delayed. Shutdown tests must prove both that cooperative blocked
work is cancelled without an external release and that non-cooperative work
cannot make shutdown unbounded.
