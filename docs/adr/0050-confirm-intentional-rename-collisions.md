---
status: accepted
---

# Confirm intentional Rename collisions

## Context

Consolidation can require temporarily giving an existing declaration another
declaration's name. Rejecting every collision prevents this intermediate step.
The original declaration and its complete references must remain the edit unit;
merging implementations or adopting references from the colliding target is a
different operation.

## Decision

Keep ordinary F2 Rename and strict behavior for clients without explicit
confirmation integration. The VS Code middleware advertises experimental
vbaRenameConfirmation protocol version 1. A fully planned operation with
recognized collision impacts returns a structured confirmation challenge rather
than an applicable WorkspaceEdit. The native, theme-aware warning identifies
the original and requested names, conflicts and available locations, binding
or ambiguity concerns, and any retained filenames. It offers Cancel and
Continue once. Dismissal and cancellation produce no edit; there is no setting
or remembered permission to ignore future collisions.

Planning first establishes the pre-edit source-owned target, Property or
conditional family, complete occurrence set, and WithEvents/Implements dependent
closure. Independent failures still reject the entire operation. Structured
evidence distinguishes permitted collision consequences from invalid names,
unavailable source or catalog authority, incomplete dependent coverage, and
unrelated effective-type changes. Top-level failure codes and message parsing
are not impact classification. ADR 0045 remains the effective-type authority.

The independent proof uses a collision-free control Rename with the same
original edit closure. Its leading character matches the requested name so
DefType changes remain subject to the ordinary effective-type proof. Physical
declarations and established associations are mapped between the original,
control and requested results. Only consequences supported by those mappings
enter the collision review; the control name never enters the returned edit.

Confirmation may permit collision-caused target and non-target binding changes,
ambiguous or unresolved classifications, logical grouping and type-name
resolution changes. It never acquires extra edits from a post-collision merged
identity. The colliding declaration and its source text stay untouched. Rename
does not insert qualifiers or type clauses, redirect references, remove
declarations, generate members, or merge bodies. Existing diagnostics remain
available; the user manually consolidates any temporarily invalid source.

Before presenting a warning, compute the complete path decision. If an otherwise
file-following module has an existing destination, retain its original path.
If either participating UserForm destination conflicts, retain both .frm/.frx
paths and resource filename references. Metadata, the outer designer identity
and established semantic occurrences still change together. Offsets, binary
bytes and nested component names remain unchanged. Source-unit, template,
ownership and currentness evidence remains mandatory even when the final plan
has no resource operation. Client resource capabilities are required only for
operations actually present. Case-only references to the same file are not
distinct destination conflicts.

The server owns one pending captured plan and its evidence. A new Rename
invalidates the previous pending operation; shutdown releases it. The
vba/confirmRename exchange consumes the opaque confirmation identifier once,
for either continue or cancel. It cannot change the approved target, name,
source snapshots, closure, impacts or path decision. Before returning the stored
complete edit, revalidate source revisions, source-template content, catalog
authority, source units and captured destination evidence. Any change requires
a fresh user action; confirmation never silently replans or retries.

## Consequences

This narrowly revises the strict collision and meaning-preservation guarantees
in ADR 0029 and the basename-following rule in ADR 0036. Ordinary safe Rename,
no-change and case-only operations, existing namespace distinctions, and
Explorer file Rename retain their behavior. Managed and external identities
remain outside source-owned Rename. Native preview annotations alone do not
authorize an operation. Normal WorkspaceEdit conversion/application and
case-only file tracking remain the client boundary; no automatic save, Excel
launch, workbook mutation, collision repair or background retry is introduced.
