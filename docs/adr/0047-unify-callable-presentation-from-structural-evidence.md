---
status: accepted
---

# Unify callable presentation from structural evidence

## Context

Source, reference, intrinsic host, and Implements adapters assembled callable
labels independently. Effective-type enrichment and setter invocation then
modified completed labels. Ordinary Hover displayed documentation before the
declaration, while conditional Hover and completion details reversed that order.

## Decision

One inventory-owned internal presentation Module has two responsibilities:
assemble callable and parameter labels from structural evidence, and compose a
documentation group with its declaration. It owns display rules, not binding,
type resolution, contract compatibility, or completion edits. ADRs 0017, 0032,
0036, and 0045 retain their existing authorities.

Origin adapters supply names, callable kinds, effective types, parameters, and
meaningful declaration roles. Consumers do not recover these facts by slicing
completed labels. In particular, setter invocation selects its argument slots
semantically before rendering. Ordinary Property labels can remain collapsed,
while required accessor contracts retain Get, Let, or Set. AssignedValue retains
its effective ByVal semantics even though that keyword is omitted in display.

Every known effective ByRef is displayed, including implicit source ByRef;
ByVal is omitted. Optional parameters use brackets and omit default expressions.
ParamArray, array shape, available type evidence, and the current short Declare
form remain visible. Unknown external facts remain unknown; presentation does
not manufacture Variant, passing mechanisms, or callable kinds. Intrinsic host
evidence comes from ADR 0036's environment catalog.

The in-process presentation shape remains separate from the persisted reference
catalog DTOs. Schema 1 and generator typelib-catalog-v12 remain compatible; no
new required catalog fields or display-only migration are introduced. Existing
stale and fail-closed metadata behavior remains in force. Presentation introduces
no additional retained cache, refresh, discovery, or type authority.

Ordinary and conditional Hover and completion details use documentation,
one horizontal rule, then a fenced VBA declaration. The rule is present only
when both parts exist. A coalesced presentation keeps distinct nonempty
documentation variants in stable numbered order, with one rule between the
whole group and its declaration. Existing conditional markers, physical
alternatives, and separators between distinct presentation groups are preserved.

Signature Help continues to provide a plain signature label and separate
parameter documentation. VS Code owns its native signature/documentation
separator. No Markdown rule, duplicate callable documentation, or custom client
renderer is added there. Active-parameter selection, nulls, retrigger behavior,
and completion insertion edits retain their existing contracts.

## Consequences

One display policy covers source, reference, host, and contract presentations
without coupling their semantic authorities. Behavioral verification exercises
real inventory and LSP paths, persistence reuse, conditional and accessor cases,
and completion edits. Native Signature Help is checked in a dark VS Code theme
with documented, undocumented, and unmapped active parameters.
