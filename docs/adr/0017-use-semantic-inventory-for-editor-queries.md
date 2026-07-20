---
status: accepted
---

# Use Semantic Inventory for editor queries

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
`VbaSourceIndex` query instances remain as a compatibility facade for existing
library and test callers; the facade constructs and delegates to
`VbaSemanticInventory`, not the reverse. Static protocol metadata and source
projection helpers do not make that class a project-scope authority. LSP
request capture and project snapshots expose only the inventory.

Reference selection and catalog definitions are immutable data inputs to the
inventory. Host behavior is expressed at that boundary through the manifest's
main reference and catalog metadata such as `MainHostGlobal`; the inventory,
workspace snapshot cache, and interactive scheduler do not branch on Excel,
Word, PowerPoint, or another Office application. The shipped catalog coverage
currently provides the Excel experience. Future Word or PowerPoint support
must add or select host catalog data at the project/reference boundary rather
than add host-specific policy to the generic interactive infrastructure.

## Consequences

There is no compatibility-index fallback inside a committed project snapshot.
Reference catalog projections, source definitions, resolution state, and
semantic caches remain scoped to its inventory. If an inventory cannot be
reused safely, the project snapshot provider rebuilds the affected project
scope instead of switching an interactive caller to a raw index.

Behavioral tests assert explicit source, reference, precedence, ambiguity,
identity, and range results without using the compatibility facade as an
oracle. Structural tests keep the inventory free of a `VbaSourceIndex` field
and require its semantic-resolution services to share one candidate inventory.
Any reuse optimization must use private semantic fingerprints or rebuild
conservatively.

Because inventory shards are scoped to one committed project snapshot, any
declaration-shape, visibility, type, module identity, manifest,
source-membership, or reference-catalog change creates a new inventory and
conservatively invalidates occurrence and token shards. Future member-local
reuse may carry forward unchanged shards only when a private semantic
fingerprint proves the declaration environment and member identity are stable.
