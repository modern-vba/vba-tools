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
projection into the provider's complete validator. Physical
identity supplied by other owners, source encoding, extension tokens, and schema
property names retain their existing policies. Manifest schema 1, DAP protocol
2.0, transported snapshot schema 2, and CLI snapshot feature 2.0 are unchanged.

### Shared lexical URI admission

Issue #433 revises this decision's former reservation that URI admission keeps
its existing policy. The file/path part of semantic document identity now belongs
to the neutral `VbaTools.SourceIdentity` foundation at `tools/vba-source-identity`.
The extension extends `WindowsPathIdentity`; it does not add another casing table
or parallel URI helper. C# and TypeScript consume shared data-only URI cases using
independent loaders. The DAP depends on this small foundation, not on Semantics.

A source-URI field accepts explicit absolute file URIs. It decodes percent escapes
exactly once with strict UTF-8, rejecting malformed escapes and encoded slash or
backslash. Double-encoded separators remain literal filename text after decoding.
Encoded drive colons, Unicode/escaped spellings, Windows separators, drive and UNC
roots, and lexical dot segments identify the same file where appropriate.
The localhost drive form is local; arbitrary UNC authorities never become drive
aliases. Query and fragment are excluded from equality but remain in the original
URI. No filesystem access, link or short-name resolution, DNS, or drive-map lookup
occurs. Windows equality remains .NET OrdinalIgnoreCase; NFC/NFD and K/Kelvin do
not merge. Separate path fields and native POSIX paths retain their own behavior.

Each admitted source keeps typed identity alongside exact original URI spelling.
Source inventory, active positions, breakpoint lookup, admission indexes, and
runner state use that identity. They never emit a regenerated URI or identity key.
Duplicate source identities and same-identity/same-line breakpoints reject even
when contents/settings agree. Ordering and module-name identity remain separate.
One dirty editor still overlays its disk source with disk-relative layout and
editor URI spelling; two dirty aliases reject instead of overwriting each other.

Known identity failures remain explicit request-scoped source rejections before
generation workspace creation, build, or Excel. Corrected initial launch may retry;
Restart retains a still-usable old session. Relative-path safety, source membership,
byte authority, encoding, generation binding, and physical ownership retain their
independent validation. LSP non-file and unresolved typed identities and revision
fencing remain local policies. Already admitted immutable URI values are reused;
this change does not diagnose or close issue #415's intermittent runtime failure.

This intentionally rejects previously tolerated raw source paths, malformed escapes,
or encoded separators, while accepted lexical URI aliases bind consistently.
Protocol schemas and provider versions do not change. Foundation guards, normal
test/release entry points, and self-contained package probes cover the new owner
and its consumers.

## Consequences

Manifest selection, debug targeting, Test Explorer, and snapshot capture agree
on textual identities and preserve original spelling. Ambiguous editor content
cannot be selected by enumeration order. The generated-data verification command
and behavioral tests cover Unicode identities, strict lexical relationships,
conflict diagnostics, overlays, ordering, and cleanup. ADR 0025's immutable
capture and ADR 0027's byte and process boundaries remain in force; this decision
does not change ordinary Command Palette Build/Test/Publish dirty-source policy.
