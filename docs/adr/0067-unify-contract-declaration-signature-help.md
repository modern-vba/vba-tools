---
status: accepted
---

# Unify contract-declaration Signature Help while typing

## Context

Declaration guidance was assembled by a combined contract path, an accessor
fallback, and an event-handler fallback. Their physical-parameter rules differed
and clamped editing positions to parameters already written, making an empty
list or a trailing comma misleading.

## Decision

One inventory-owned declaration-help Module gathers evidence from the existing
Implements, WithEvents, and intrinsic host event authorities. It owns declaration
position, presentation coalescing, and signature selection, not contract
discovery or ordinary call-site argument mapping. Its closed result distinguishes
help, a declaration with no help, and a position outside a declaration. Only the
last result falls through to call-site resolution.

Syntax-delimited slots describe what the user is entering: an empty list starts
at zero, and a top-level comma advances even when the next parameter is missing.
Each contract variant omits its active parameter beyond its own arity. Nested
parentheses, array commas, strings, and comments do not count as delimiters.
Guidance stops after the declaration's closing parenthesis and does not capture
a subsequent same-line call. Incomplete lists require a proven declaration
context; a cursor in a string or comment receives no declaration help.

An event association already proven by semantic analysis may guide repair of
an incorrectly written Function or Property. This is not valid handler
admission and leaves diagnostics intact. ClassLifecycle remains completion-only.

Presentation identity, conditional markers, stable origin order, and
first-variant documentation remain unchanged. Coalescing precedes retrigger
selection. Capability projection preserves the internal absence of an active
parameter even when older clients display their protocol fallback.

This decision refines ADR-0047's retained active-parameter behavior specifically
for declaration editing. ADR-0032's accessor contracts and ADR-0045's effective
declared types remain the semantic authorities.

## Consequences

Accessor and event presentation fallbacks and their position calculations are
removed. Inventory and process tests exercise empty, trailing, excess, nested,
continued, incomplete, invalid-kind, conditional, and capability-aware cases
through production guidance paths.
