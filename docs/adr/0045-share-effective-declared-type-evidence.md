---
status: accepted
---

# Share effective declared type evidence across semantics and presentation

## Context

Ordinary callable parameters defaulted to Variant during source projection,
while interface variables independently applied DefType directives. Event
contracts, return/member resolution, and editor labels could therefore disagree
about the same declaration. Erasing an omitted type early also prevented later
consumers from distinguishing a known fallback from incomplete evidence.

## Decision

One inventory-owned `VbaEffectiveDeclaredTypes` service resolves and atomically
publishes results keyed by physical declaration identity and parameter ordinal.
Source projection retains written type evidence and preserves omission. The
service recovers the captured physical declaration before resolving it, so a
presentation copy cannot become another type authority. Recovery of a malformed
source signature may retain a complete parameter type from its syntax without
pretending that the callable as a whole is valid.

Written As types and type-declaration characters override defaults. Omitted
source types use only the declaring module's preceding DefType directives and
then Variant when no applicable directive is known. Module/local variables,
parameters, and Function/Property Get returns share this rule. Sub, Event,
Property Let, and Property Set return `NoReturnType` independently of their
parameters. Comma-separated declarators, array shape, passing mechanism,
Optional, ParamArray, and default-expression evidence stay independent.

Results distinguish `Known`, `ExplicitlyUnresolved`, `InsufficientEvidence`, and
`NoReturnType`. MissingType remains written but unresolved; an unfinished As
clause is insufficient evidence. External metadata receives no source defaults.
Known named results retain the canonical resolution target and identity, while
display and reference-qualified names are separate presentation values.

Each physical conditional declaration considers directive source order and
ancestor branch paths. A mutually exclusive directive cannot apply. A preceding
possibly coexisting directive outside the declaration's ancestor path makes its
effect uncertain; a later applicable ancestor directive can establish it again.
Unknown ownership is never the unconditional root. The service neither evaluates
condition expressions nor manufactures speculative declaration alternatives.
Existing physical families and return-type convergence continue to decide which
members can be safely exposed; insufficient type evidence creates no members.

Ordinary Call Resolution, Event parameter contracts, Implements contracts,
type-member navigation, Hover, and Signature Help consume this service. Editor
labels show known omitted types without an origin annotation, retain written
unresolved names, and never invent As Variant for unknown omissions. Consumer
policies remain separate: ordinary conversions and ByRef checks, Event/Implements
signature comparison and TypeLib identity, and variable accessor derivation
continue to use their own existing rules over the common evidence.

## Consequences

DefLng R gives omitted rhs parameters and Result returns Long consistently,
regardless of the caller or implementing module's defaults. Uncertain source
and incomplete external declarations are treated conservatively instead of
silently changing their meaning to Variant. Lazy results belong to the immutable
inventory and do not require the validation index; retained-analysis accounting
reserves their declaration and parameter capacity before queries populate it.

Behavioral coverage includes process-level signatures, Hover, ordinary ByRef
calls and Events, plus physical conditional variants, precedence, incomplete
clauses, parameter ordinals, no-return declarations, external metadata, arrays,
and comma-separated declarators. Existing interface, TypeLib, type-member and
conditional-family suites remain the consumer-specific regression boundary.
