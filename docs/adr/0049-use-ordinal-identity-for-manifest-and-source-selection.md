---
status: accepted
---

# Use ordinal identity for manifest and source selection

## Context

Some extension projections used JavaScript lowercase for names and source paths,
while provider admission and Command Palette selection used .NET OrdinalIgnoreCase.
This made sigma/final-sigma identities disagree and incorrectly equated K with
the Kelvin sign. Dirty-editor maps could silently overwrite another editor, and
path-relative calculations could reject a source accepted by the provider.

## Decision

Reuse the existing .NET 10-generated ordinalIgnoreCaseKey for document, reference,
and CommonModule names and textual Windows source-path identity. Names retain
their stored spelling. Sigma/final sigma, micro sign/Greek mu, and supplementary
case pairs compare consistently; K/Kelvin and NFC/NFD spellings remain distinct.
No Unicode normalization, full case folding, or replacement casing table is added.

One small extension-owned WindowsPathIdentity Module normalizes lexical Windows
paths without choosing a new resolution base. Callers retain their existing
project, source-root, or current-directory resolution rules. The module compares
drive or UNC roots and complete normalized segments using the same ordinal rule.
Its strict-descendant operation returns the candidate's original-spelled relative
suffix from that same proof. Root equality, prefixed siblings, other roots, and
normalized escapes do not qualify. Neither path.relative nor equality-key length
establishes containment. This is textual identity, not physical alias resolution
or filesystem ownership evidence.

Every known duplicate identity fails admission with the conflicting entries.
Two dirty editors for one source always abort capture, even if text and encoding
match. One dirty editor and its matching disk entry form one valid captured
source: disk-relative layout and the editor's URI spelling remain visible.
Inventory and transported source paths, relative paths, and source URIs retain
their independent uniqueness contracts. Materialization failure still uses the
existing caller-owned temporary-directory cleanup and retained-path reporting.

Equality does not choose ordering. Transported sources keep portable raw UTF-16
ordinal order. Breakpoints keep their existing deterministic order and use an
independent identity set to detect duplicates that need not be adjacent in it.
Equality keys never become emitted names, paths, or URIs.

Manifest projections keep their separate purposes and required fields. A narrow
identity-conflict diagnostic path exposes conflicting entries without turning a
projection into the provider's complete validator. URI admission, physical
identity supplied by other owners, source encoding, extension tokens, and schema
property names retain their existing policies. Manifest schema 1, DAP protocol
2.0, transported snapshot schema 2, and CLI snapshot feature 2.0 are unchanged.

## Consequences

Manifest selection, debug targeting, Test Explorer, and snapshot capture agree
on textual identities and preserve original spelling. Ambiguous editor content
cannot be selected by enumeration order. The generated-data verification command
and behavioral tests cover Unicode identities, strict lexical relationships,
conflict diagnostics, overlays, ordering, and cleanup. ADR 0025's immutable
capture and ADR 0027's byte and process boundaries remain in force; this decision
does not change ordinary Command Palette Build/Test/Publish dirty-source policy.
