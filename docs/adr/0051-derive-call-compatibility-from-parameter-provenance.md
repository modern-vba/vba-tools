---
status: accepted
---

# Derive call compatibility from parameter provenance

## Context

A TypeLib input parameter can have a pointer-shaped ABI and a displayed `ByRef`
label without requiring the exact writable-storage contract of an ordinary
source VBA parameter. Applying that contract to every pointer incorrectly
rejects valid Automation input calls with declared String arguments, including
calls equivalent to Dictionary Add, Exists, and Remove. Source VBA also has
Variant, Object, and specific-class exceptions to ordinary exact ByRef typing.

This decision refines `CallArgumentMapping` and ADR 0011's diagnostic contract.
ADR 0045 remains the canonical declared-type authority, and ADR 0047 remains the
independent callable-presentation policy.

## Decision

Select compatibility from the callable's provenance and structural parameter
evidence. Preserve applicable, inapplicable, and indeterminate outcomes for each
mapped callable variant. An unavailable rule or unresolved type is not proof of
incompatibility; independently conclusive mapping failures remain visible.

### Source declarations

Source direct-storage ByRef checks retain exact canonical types and array shape
for ordinary parameters. Integer storage cannot satisfy a Long ByRef parameter
through numeric widening. A non-array Variant formal accepts the modeled
Variant-compatible types, including whole typed arrays; a Variant-array formal
retains exact element-type and shape checks.

Object and specific-class formals use proven assignment compatibility. Canonical
equality, class-to-Object assignment, and an established source Implements
relationship provide positive evidence. An unproved class relationship remains
indeterminate. Direct Variant storage is incompatible with an Object or
specific-class ByRef formal. Value temporaries, including individually
parenthesized arguments, retain ordinary value compatibility.

These distinctions follow the separate static and runtime argument rules in
[MS-VBAL procedure invocation argument processing](https://learn.microsoft.com/en-us/openspecs/microsoft_general_purpose_programming_languages/ms-vbal/1fb9af32-fc48-4c4f-998a-ed8047048ca5).
In particular, differing object declarations can involve Set assignment and
normal-return copy-back; ordinary scalar reference equality cannot represent
that behavior. Compatibility does not predict the runtime value or guarantee
successful execution.

Source `Declare` procedures remain source declarations for this analysis.
[MS-VBAL external procedure declarations](https://learn.microsoft.com/en-us/openspecs/microsoft_general_purpose_programming_languages/ms-vbal/75679e90-7e14-420d-af11-f83ffaf60418)
applies VBA procedure rules to their declared parameters. This does not infer or
validate an unmodeled native DLL ABI.

### TypeLib calling evidence

Retain `VbaTypeLibParameterPassing` independently of presentation: explicit
`Input`, `Output`, `InputOutput`, or `Unknown` direction plus the number of
enclosing `VT_PTR` descriptors, or unknown depth. FIN alone establishes input,
FOUT alone output, and both input/output. When neither flag is set, direction
remains unknown.
The direction flags describe information flow, not the source expression's
storage classification. See [MS-OAUT PARAMFLAGS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-oaut/4ca6f07b-f89f-469b-ba9e-81fda041c8ac).

`VbaCallablePassingConvention.AutomationDispatch` identifies positively known
Automation-compatible dispatch or vtable semantics. The reader accepts a
`FUNC_DISPATCH` descriptor, or a virtual/pure-virtual descriptor on a dispatch
type or an interface carrying FOLEAUTOMATION/FDUAL. FDISPATCHABLE alone is not
equivalent evidence. This classification uses the existing descriptors without
special-casing a library or member name. The supporting distinction is documented
by [TYPEFLAGS](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/ne-oaidl-typeflags)
and [FUNCKIND](https://learn.microsoft.com/en-us/windows/win32/api/oaidl/ne-oaidl-funckind).

For this known convention, an explicit input-only parameter with pointer depth
zero or one uses ordinary value compatibility for both direct storage and value
temporaries. A String argument can therefore satisfy a modeled Variant input
even when the parameter is pointer-shaped. Zero or one is the implemented input
support boundary, not a universal ABI restriction: Automation supports object
and interface pointer types. Unsupported shapes remain indeterminate. See
[Automation-compatible types](https://learn.microsoft.com/en-us/windows/win32/midl/oleautomation).

For an explicit Automation Output or InputOutput parameter with exactly one
enclosing `VT_PTR`, the supported writable profile is limited to a scalar Long
parameter and proven direct writable scalar Long storage. That exact case is
applicable. A differing type such as Integer storage, any array, a value
temporary, a complex type, or missing evidence remains indeterminate. None of
those cases produces a conclusive external ByRef type-mismatch error.

This is a deliberately bounded positive profile. The
[MS-VBAL imported-library qualification](https://learn.microsoft.com/en-us/openspecs/microsoft_general_purpose_programming_languages/ms-vbal/1fb9af32-fc48-4c4f-998a-ed8047048ca5)
permits parameter-passing differences for imported library procedures.
Automation coercion does not establish whether VBA supplies a temporary or how
it copies a result back to the caller. Extending the writable profile requires
additional evidence for that actual passing contract, not reuse of a source
ByRef mismatch or a general assumption from pointer shape.

Other external conventions, missing direction or pointer evidence, and unmodeled
external contracts remain indeterminate. They neither inherit source ByRef
rules from the displayed label nor acquire the source Variant/Object exceptions
merely because their ABI contains a pointer. This preserves the distinction
between a proved mismatch and incomplete compatibility evidence.

### Persistence and presentation

The `typelib-catalog-v13` generator persists passing convention and parameter
direction/ABI evidence in both raw TypeLib and projected signatures. Save/load
round trips preserve those values. Earlier generator entries are stale under
the existing refresh policy. Missing fields retain unknown semantics, including
when an older catalog remains available as last-known-good data.

Callable labels remain governed by ADR 0047. Hover and Signature Help may display
`ByRef` while compatibility uses a known Automation input contract. Analysis does
not parse labels, rewrite them to change semantics, or manufacture missing
metadata from their wording.

## Consequences

The same mapping result continues to drive diagnostics, Signature Help selection,
and named-argument compatibility. Positive source/reference fixtures must prove
the target and parameter types were resolved so an absent diagnostic cannot pass
because analysis was unavailable. Regression coverage retains ordinary exact
ByRef failures, array mismatches, source exceptions, accepted Automation input
metadata, missing/unsupported evidence, and persisted-catalog round trips.

This change adds no runtime dispatch, source edit, diagnostic suppression by
library name, or general native ABI validator. Unknown relationships and
unsupported contracts remain explicit analysis limitations.
