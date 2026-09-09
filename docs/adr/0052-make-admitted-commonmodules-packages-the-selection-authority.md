---
status: accepted
---

# Make admitted CommonModules packages the selection authority

## Context

CommonModules package admission validates a complete closed flat repository,
including its canonical manifest, source identities, classifications, dependency
declarations, required references, and permitted form sidecars. A raw manifest
entry list does not establish that authority. The former public positional
`CommonModulesPackage` and raw-list dependency resolver allowed callers to
construct or replace supposedly validated entries and run selection without
complete admission. Read-only collection interfaces also allowed nested mutable
collections to change after validation.

The domain previously used `CommonModulesPackage` for a versioned release ZIP.
That distribution artifact is now called `CommonModulesReleaseArtifact`. The
runtime `CommonModulesPackage` represents admitted repository metadata, not a
release version, source archive, byte snapshot, or project lock file. ADR 0004's
distribution and version-pinning decisions remain unchanged.

## Decision

### Admission and immutable ownership

`CommonModulesPackage` is a sealed authority with a private constructor. Its
controlled factories finish complete live or captured package admission before
issuing a value. No caller-facing constructor or factory accepts arbitrary
entries as proof of admission, and no setter, initializer, or record-copy path
can replace admitted entries. Reading the canonical manifest alone is not
sufficient to obtain a Package or a selection plan.

The Package independently owns its entries, nested Categories, Dependencies,
and RequiredReferences collections, and its private identity indexes and graph
state. Published values and selection plans are deeply immutable. Mutating an
input collection, retaining an entry, or obtaining a collection through another
interface cannot alter the admitted facts. Immutable entry facts may be shared
by plans without reconstructing or weakening their authority.

Live filesystem validation and captured-byte validation retain their separate
implementations in this decision. Both must establish complete admission before
issuing the same trusted value. Consolidating those validators is a separate
change; introducing the Package boundary does not imply that consolidation has
already happened.

### Request and dependency selection

The Package alone resolves requests and derives dependency selections. A
request with an extension matches a complete ModuleFile; an extensionless
request matches a complete CommonModuleName. Both use OrdinalIgnoreCase exact
comparison. The Package preserves spelling and performs no trimming, prefix
matching, or substring matching. Missing or ambiguous requests retain the
CommonModulesManifestException error category. A caller's existing input
normalization policy remains at its own boundary.

Resolve all requested identities against the admitted Package. Visit requested
roots in request order, placing dependency components before their dependents
and including an entry only at its first encounter. A
CommonModuleDependencyComponent is a strongly connected component, not an
invalid cycle. Its members follow manifest declaration order; outgoing edges
follow member declaration order and then each member's declared dependency
order, with first occurrence winning. Existing admission rules still reject
self-dependencies and invalid runtime-to-test dependencies.

`CommonModulesSelectionPlan` represents only an ordinary dependency-closed
selection. The Package derives its RequiredReferences as the OrdinalIgnoreCase
first-seen union over the selected entry order, preserving the spelling of the
first declaration. Callers consume these immutable selection facts instead of
rebuilding indexes, sorting the closure, aggregating references independently,
or invoking a resolver over a raw list. Snapshot, installation, new-project,
command, diagnostic, and test paths use an admitted Package or its plan.

### Reconciliation with installed state

`CommonModulesReconciliation` remains a separate, pure comparison of one
admitted Package with an independently captured installed selection. Package
identity and dependency facts stay authoritative; Reconciliation owns the
installed-state comparison, orphan classifications, diagnostic reachability,
and projection order.

First request the Package closure for repository-backed directly requested
roots in installed order. Then append repository-backed installed entries in
their existing order, skipping identities already present. This append retains
an entry without expanding additional dependencies from that entry. These are
Reconciliation's own ordered facts, not a CommonModulesSelectionPlan. It obtains
the final reference union through `Package.GetRequiredReferences(ordered names)`
over that exact order, without issuing a new dependency-closed plan. Do not pass
every installed name through requested-root resolution.

For example, a directly requested Feature depending on Base and a retained
dependency Extra depending on Another produce Base, Feature, Extra when Extra
is not reachable from the requested closure. Retaining Extra does not newly
install Another. Update preserves existing installed positions and appends newly
installed entries in selection order; it does not rewrite the stored manifest
to match a newly sorted list.

Preserve current, missing-orphan-marker, retained-orphan, and stale-orphan-marker
facts. Reappeared requested orphans participate in Update's refresh closure,
but their stored markers withhold Doctor's current dependency authority until
refresh commits. An unreachable-dependency warning requires every requested
root to have current authority. Missing installed dependencies retain requested
root order and first dependency encounter order. Update and Doctor project the
same reconciliation facts without independently reconstructing these rules.

### Snapshot and mutation boundaries

`CommonModulesPackageSnapshot` owns captured bytes, staging, guarded reads, and
cleanup. It delegates metadata selection to its admitted Package. Capture still
proves a complete stable repository generation; a Package obtained from a live
reader does not, by itself, authorize later copying from a changing directory.
Snapshot reads retain their existing lifetime checks and return independent
byte copies. Immutable metadata or a plan captured before cleanup can remain
usable afterward without retaining authority to read disposed snapshot bytes.

Source copying, destination validation, reference-resolution evidence, project
leases, cancellation, manifest-last commitment, recovery, and cleanup outcomes
remain outside Package selection. Provisional metadata is only early validation
evidence. A repository-backed mutation still derives its final plan from a
fresh stable snapshot after latest-state replanning, under the existing lease
and commitment rules. It does not reuse a stale plan or reread live files to
fill gaps in a captured plan.

Installed-only Add and an Update with no installed targets retain their paths
that require no repository admission. They do not manufacture an empty Package
or an empty plan that masquerades as package authority. An empty selection
requested from an actually admitted Package remains a legitimate empty
selection; it does not make an empty or invalid package admissible.

## Consequences

The public boundary establishes validation once and prevents callers from
forging or changing the facts used for identity and dependency selection.
Reconciliation can retain its installed-state policy without becoming a second
package validator. Snapshot and transaction lifetimes remain explicit and
separate from immutable metadata ownership.

Regression coverage must exercise real admission, mutation attempts against
published collections, exact request matching, error categories, request and
component ordering, cycles, reference union, and reconciliation. It must also
preserve installed-only Add, zero-target Update, retained orphan state, and the
append-without-extra-closure behavior. Invalid packages cannot serve as shortcut
fixtures for trusted selection tests.

This decision changes the internal authority boundary rather than the manifest
grammar, CLI result schemas, release format, or existing mutation guarantees.
It introduces no package version pinning, automatic pruning, live/captured
validator consolidation, or new snapshot cleanup policy.
