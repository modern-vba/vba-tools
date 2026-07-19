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
