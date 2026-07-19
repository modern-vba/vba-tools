---
status: accepted
---

# Use Semantic Inventory for editor queries

## Context

`VbaSourceIndex` remains the broad compatibility implementation for source
definitions, semantic resolution, occurrences, formatting, and semantic tokens.
As language-server features grow, editor queries need stable project-scope
lookup structures that match their access patterns instead of each caller
coordinating a raw index.

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

During migration, the inventory may delegate complex semantic behavior to the
compatibility `VbaSourceIndex`, but callers outside the project snapshot no
longer receive a raw index. Straightforward document and workspace-symbol
queries are served from inventory-owned maps immediately. More specialized
indexes can replace delegated paths incrementally while preserving the existing
NameResolution precedence and ambiguity behavior.

## Consequences

`VbaSourceIndex` remains available inside the inventory as a conservative
fallback until each query path has a dedicated immutable structure. Reference
catalog projections, source definitions, and semantic caches remain scoped to a
committed project snapshot. Ordinary source edits can later reuse unaffected
inventory maps when declaration shape and project boundaries are unchanged.

Differential tests must compare inventory results with the compatibility index
while the migration is incomplete. Public `VbaDefinitionIdentity` equality and
range behavior remain unchanged; any reuse optimization must use private
semantic fingerprints or rebuild conservatively.

Because inventory shards are scoped to one committed project snapshot, any
declaration-shape, visibility, type, module identity, manifest,
source-membership, or reference-catalog change creates a new inventory and
conservatively invalidates occurrence and token shards. Future member-local
reuse may carry forward unchanged shards only when a private semantic
fingerprint proves the declaration environment and member identity are stable.
