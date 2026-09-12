---
status: accepted
---

# Use Semantic Inventory for editor queries

ADR 0036 supersedes only the document-scoped host-projection inventory input
described below. Semantic Inventory now consumes one current
environment-scoped UserForm Event catalog and forms its binding from
authoritative `.frm` source kind; all other immutable inventory and editor-query
decisions remain accepted.

## Context

The original editor-query implementation exposed `VbaSourceIndex` as a broad
coordination object for source definitions, semantic resolution, occurrences,
formatting, and semantic tokens. That made it possible for each feature to
coordinate its own project indexing and cache behavior. Editor queries instead
need one immutable project-scope authority whose lookup structures match their
access patterns.

## Decision

Project snapshots expose a `VbaSemanticInventory` as the editor-query authority.
The inventory owns immutable maps by document URI, normalized name, module,
type, parent type, qualifier, and callable identity. LSP request execution
queries this inventory for completion, hover, signature help, definition,
references, document symbols, workspace symbols, formatting, rename, and
semantic tokens.

Inventory construction establishes `InteractiveSemanticReadiness`; it does not
eagerly construct the project-validation diagnostic index. Project Validation
Diagnostics are a lazy consumer of the same captured inventory and exact
`VbaProjectSnapshot`, not a prerequisite or a second project-scope authority.
Editor-query handlers never request that diagnostic index as a condition of
completion, hover, signature help, symbols, definition, references, rename,
formatting, or semantic-token execution.

The shared name-candidate inventory admits each original source/reference URI
spelling once and retains its typed document identity alongside presentation
data. Name, member, type, call, and diagnostic lookups reuse these admissions.
Raw spelling lookup is ordinal-exact; typed identity equality, unresolved-file
semantics, and the distinction from filesystem ownership remain unchanged.
Invalid admissions retain failure, not a default identity that can compare equal.
An unknown query spelling may be identified for that lookup but never enters
the retained map. The map is constructor-complete, read-only for concurrent
queries, shares the inventory lifecycle, and is explicitly charged to retained
capacity. This is not a process-global URI cache or a second identity policy.

The inventory's name-resolution service owns the immutable, lazily published
`EffectiveDeclaredType` results described in ADR 0045. One result per physical
declaration and parameter ordinal supplies all semantic and presentation
consumers without forcing project validation or building another candidate
inventory. Retained-analysis admission reserves the capacity for these results
alongside occurrence and token caches, including before they are requested.

The project-validation lifecycle may later build the diagnostic index from the
inventory's existing definitions and semantic resolution. Completing that
index cannot mutate the inventory maps, occurrence shards, semantic-token
caches, or revision identity already captured by an editor request. A
cancelled build is not published or retained as a complete index, so a later
applicable project revision can evaluate normally from its own captured
inventory.

The inventory also owns occurrence and semantic-token shards for the committed
snapshot. Resolved identifier occurrences are built through document-local lazy
shards and exposed to references, rename, formatting support, and semantic
tokens only after the shard is complete. Semantic tokens and encoded LSP token
data are cached per document URI on top of those occurrence shards. First use is
atomic through lazy/cache publication; callers never receive a partially built
or mixed-revision shard.

The inventory implements all editor-query behavior directly through its owned
maps, `VbaSemanticResolution`, resolved-occurrence shards, formatter, and token
caches. It neither stores nor delegates to a raw `VbaSourceIndex`.
The former `VbaSourceIndex` compatibility facade is removed. Source projection
is owned by the internal `VbaSourceDocumentProjector`, while semantic-token
protocol metadata is owned by the internal `VbaSemanticTokenLegend`. LSP
request capture, project snapshots, and behavioral tests query only the
inventory.

Reference selection and catalog definitions are immutable data inputs to the
inventory. Host behavior is expressed at that boundary through the manifest's
main reference and catalog metadata such as `MainHostGlobal`; the inventory,
workspace snapshot cache, and interactive scheduler do not branch on Excel,
Word, PowerPoint, or another Office application. The shipped catalog coverage
currently provides the Excel experience. Future Word or PowerPoint support
must add or select host catalog data at the project/reference boundary rather
than add host-specific policy to the generic interactive infrastructure.

The accepted document-wide `HostClassProjectionSnapshot` is also an immutable
inventory input. `current` class entries provide authoritative projected Host
Event evidence, `lastKnownGood` entries are explicitly advisory, and
`indeterminate` entries provide no projected Event candidate. The inventory
never consumes raw CLI results, operational lifecycle messages, class deltas,
or tombstones, and an editor query never initiates or waits for inspection.

## Consequences

There is no compatibility-index fallback inside a committed project snapshot.
Reference catalog projections, source definitions, resolution state, and
semantic caches remain scoped to its inventory. If an inventory cannot be
reused safely, the project snapshot provider rebuilds the affected project
scope instead of switching an interactive caller to a raw index.

Behavioral tests assert explicit source, reference, precedence, ambiguity,
identity, and range results through the inventory Interface. Test setup may use
an internal fixture to parse and project source text, but that fixture does not
serve editor queries or create a second project-scope authority. Structural
tests require the former facade type to remain absent and the inventory's
semantic-resolution implementation to share one candidate inventory. Any reuse
optimization must use private semantic fingerprints or rebuild conservatively.

`VbaLanguageServer.Cli` is an executable deployment project, not a supported
library Interface. If editor-query semantics later need a reusable Interface,
that Interface must be designed deliberately in a separate
library project rather than inferred from public types in the executable.

ADR 0042 permits completed immutable analysis and atomically published editor
shards to outlive active snapshot retirement in a bounded process-local store.
Reopening creates fresh snapshot and validation ownership; it never promotes an
old active snapshot. Reuse requires a private proof of exact current source
content, structural document identity and membership, project identity, reference selection/catalogs,
and intrinsic host-event metadata. Declaration-shape, visibility, type, module
identity, manifest, membership, or reference-catalog changes that fail that proof
create a new inventory and conservatively invalidate occurrence/token shards.
Validation state and publication rights never cross the retention boundary.
Future member-local reuse still requires a private semantic fingerprint that
proves the declaration environment and member identity are stable.
