---
status: accepted
---

# Derive interface variable accessor contracts without synthetic declarations

A source interface class can expose a Public variable even though an
implementing class must supply Property procedures. We derive
`InterfaceVariableAccessorContract`s rather than manufacturing Property source
definitions, so the variable remains the single declaration and navigation
target while completion and fulfillment retain physical accessor identity.

A valid Variant variable requires Get, Let, and Set; Object or a named class
requires Get and Set; every other valid declared type requires Get and Let. Each
derived accessor uses the implemented interface prefix and variable name, is
evaluated independently for completion and fulfillment, and inherits
conditional provenance from the Public variable or applicable `Implements`
relationship. Invalid Public arrays and fixed-length Strings contribute no
contract.

The matrix uses each Public variable declarator's effective declared type. An
explicit `As` type or type-declaration character applies to that declarator;
otherwise its interface module's applicable `DefType` supplies the type, and
Variant is the fallback only when none applies. The implementing class's
`DefType` never changes the interface contract, and an `As` clause on one item
in a comma-separated declaration does not type its siblings.

Each derived Let or Set has one final, required value parameter with the stable
presentation name `AssignedValue`, the Public variable's exact canonical
effective type, and effective ByVal semantics. The presentation name is used by
Signature Help and future stub generation but is ignored by fulfillment. For
this final value slot only, an omitted mechanism and written `ByRef` or `ByVal`
are equivalent because VBA gives every Property value parameter ByVal runtime
semantics; mechanisms on indexed Property parameters remain distinct. The value
slot cannot be Optional, ParamArray, or an array, and type fulfillment requires
canonical identity rather than Let coercion or assignment compatibility.

Conditional Public-variable variants are projected independently and grouped
by implemented name and accessor kind into an
`InterfaceVariableAccessorContractSet`. The set is not a synthetic Property
definition or `ConditionalCallableFamily` and never participates in Name or
Call Resolution. For each represented accessor kind, the interface prefix is a
first-stage candidate in an empty or partially typed declaration-name slot only
when at least one downstream implemented member remains. Partial text filters
prefixes case-insensitively, and selection replaces only that partial name.
Selecting a prefix enters the same second-stage completion as a manually typed
prefix; once the text exactly equals a complete interface prefix and underscore
and that exact prefix has at least one surviving downstream member, that member
stage takes precedence over longer prefix matches. An exact textual prefix with
no surviving member remains ineligible, so viable longer prefix matches stay in
the first-stage list rather than opening an empty second stage. That second
stage emits one member item for the accessor kind and uses `[#If]` when its
`ConditionalContractProvenance`, inherited from the Public variable or
applicable `Implements` relationship, is conditional. Signature Help retains
every contract variant contributing that kind, and Definition returns the
complete owning variable declaration family.
The first-stage prefix still depends on at least one surviving downstream
member, but its own `[#If]` marker reflects only a guarded applicable
`Implements` relationship. Conditional provenance belonging only to a Public
variable or other interface member first appears on the second-stage member
item; the completion location alone contributes no marker. A prefix row
presents the relationship origin rather than one concrete accessor contract, so
it is outside the shared concrete-contract provenance projection.
An implementation Property joins the ordinary physical Property and
conditional-family model only after it exists in source.
Once it is conclusively associated with a derived accessor contract, its
logical Property target is a `DependentRenameTarget` under ADR 0029 rather than
an independent Rename origin. A source interface type Rename drives its
implemented-name prefix, while Rename of the owning Public-variable logical
target drives every derived accessor suffix; complementary accessors,
conditional declaration variants, and ordinary complete-name references expand
as one atomic dependent edit. Deliberate detachment remains a manual edit or a
future Code Action. At a conclusively associated implementation declaration,
Prepare Rename within the accessor suffix selects exactly that suffix range and
the owning Public-variable family's canonical name; the interface prefix and
semantic separator follow ADR 0029's interface-type and no-target rules. An
ordinary reference to the complete implementation Property carries no accessor
suffix projection and cannot initiate Rename.

Fulfillment compares the Cartesian product of contract and implementation
variants independently within each accessor kind. A contract variant is covered
when any implementation variant is compatible, and an implementation variant
is compatible when any contract variant is compatible. Conclusive mismatch and
indeterminate type evidence remain per pair. The model never evaluates or pairs
conditional expressions, branch order, or nesting, so matching complete
signature sets can appear fulfilled even when their source branches are swapped;
conditional alignment remains the author's responsibility.

After applying the implemented-name cascade rule below, failed fulfillment is
classified per represented accessor kind. An absent same-kind candidate is
`validation.interfaceMemberNotImplemented`; a same-name declaration under a
disallowed procedure or accessor kind, including an extra accessor, is
separately `validation.interfaceMemberKindMismatch`; and a same-kind
implementation that conclusively matches no contract variant is
`validation.incompatibleInterfaceMemberSignature`. A wrong-kind declaration
does not enter signature comparison, while an indeterminate comparison
suppresses a conclusive diagnostic.

Partial coverage is a fourth state. When at least one contract variant is
covered but another is conclusively uncovered,
`validation.interfaceMemberContractNotFullyImplemented` reports the incomplete
contract set. A contract variant remains indeterminate rather than uncovered
when any same-kind implementation comparison could still cover it. This state
does not apply when no same-kind candidate exists or when every implementation
variant is conclusively incompatible with every contract variant, and it never
infers correspondence between conditional branches.

The partial-coverage diagnostic is aggregated per `Implements` relationship,
implemented name, and accessor kind at the relationship's complete interface
type reference. Its stable message is
`Interface member '<implemented-name>' does not implement every required <accessor-kind> contract.`
Each conclusively uncovered physical accessor contract contributes one related
item at its source Public variable name in stable project declaration order,
using `Required contract: <kind-specific-signature> [#If].` and omitting the
marker when `ConditionalContractProvenance` is unconditional. No closest
implementation is selected and no pairwise mismatch reasons are shown.

An authoritative uncovered accessor contract without a navigable definition is
appended to the primary message as one LF-separated
`Required contract: <kind-specific-signature> [#If].` line. When the client
supports related information, navigable contracts remain only there. Exactly
repeated unlocated presentations
coalesce at their first position; every distinct presentation remains visible
without truncation in stable contract order. This fallback does not use
`Expected signature` or `Mismatches` because no implementation counterpart is
selected.

An incomplete accessor contract set and an orphaned physical implementation
Property are diagnosed independently. If some accessor contracts are covered
and others conclusively uncovered, retain the aggregate partial-coverage
diagnostic while each same-kind implementation compatible with no accessor
contract receives its physical incompatible-signature diagnostic. If no
accessor contract is covered and every relevant comparison is conclusively
incompatible, emit only the physical incompatible-signature diagnostics; that
state is total incompatibility, not partial coverage.

For a missing derived accessor, one
`validation.interfaceMemberNotImplemented` represents the whole missing
accessor contract set rather than each conditional Public-variable variant. Its
primary range is the complete interface type reference in the applicable
implementing class's `Implements` directive. Related information points to the
name of every contributing Public-variable variant and presents the required
kind-specific signature, using only the generic `[#If]` marker for conditional
`ConditionalContractProvenance` inherited from the Public variable or
applicable `Implements` relationship. The implementation or completion location alone does not add the
marker, and Signature Help, Hover, and diagnostic detail project the same
provenance.

If all same-named declarations use kinds outside the derived accessor contract
sets, the kind mismatch suppresses all missing diagnostics for the represented
accessor contract sets under that implemented name and identifies every expected
accessor kind through related information. Once any allowed accessor kind has an
implementation candidate, absent sibling accessor kinds are reported as missing
normally and wrong-kind extras remain kind mismatches.

Each conclusive physical wrong-kind declaration is diagnosed independently,
including conditional variants. A sibling's result does not suppress that
source repair location, while the implemented-name cascade rule still suppresses
missing accessor diagnostics when no allowed-kind candidate exists.

For a derived accessor kind mismatch, the primary range is the exact `Sub` or
`Function` keyword or the complete `Property Get`, `Property Let`, or
`Property Set` keyword span. The stable message lists the union of represented
accessor kinds in Get, Let, Set order and then the actual kind, for example
`Interface member 'IFoo_Value' requires Property Get, Property Let, or Property Set, not Sub.`
The primary presentation never includes conditional provenance.

Its related information contains one item per contributing physical accessor
contract variant, ordered by Get, Let, Set and then deterministic source order.
Every item selects the source Public variable name and uses
`Required contract: <kind-specific-signature>.` One Public variable may
therefore contribute several items at the same location. Variants remain
distinct and append only `[#If]` when their contract provenance is conditional,
without their conditions or branch paths.

For `validation.incompatibleInterfaceMemberSignature`, the primary range runs
from the implemented Property identifier through its parameter list and any
written return type, including a return type-declaration character. It excludes
visibility, `Static`, the already-correct Property accessor keyword, and the
body. The stable message is
`Interface member '<implemented-name>' signature does not match any required <accessor-kind> contract.`
Including a written return type is necessary for Property Get mismatches.

When related information is supported, each conclusively incompatible physical
accessor contract variant contributes one item at its source Public variable
name, in stable project declaration order. The message is
`Required contract: <kind-specific-signature> [#If]. Mismatches: <reasons>.`
The marker and its preceding space are omitted when the derived accessor
contract has unconditional `ConditionalContractProvenance`.
Reasons use ADR 0011's shared exact
`<subject> <dimension>: expected <contract-value>, found <source-value>` grammar
and join with `; ` before one final period. Every independently conclusive
reason is retained in stable order: parameter-list structure first; then each
parameter by ordinal with type, array shape, passing, role, and default; then
the final Property value parameter; and finally the result. Dependent
differences that cannot be mapped after a structural mismatch are omitted.
Parameter names never participate.
Optional defaults compare presence and evaluated constant value rather than
source spelling, and an unevaluable value remains indeterminate. The final Let
or Set value slot is labeled `value parameter`; written `ByVal`, `ByRef`, and an
omitted mechanism are equivalent there because each has effective `ByVal`
semantics, while indexed Property parameters retain their ordinary effective
mechanisms.

An unresolved named-type identity contributes only the invariant Get contract.
A plain name might later resolve to a value type requiring Let or to a class
requiring Set, so neither setter is guessed. Although `As New` constrains valid
VBA to a named class, its unresolved name deliberately follows the same
user-visible rule: it also contributes no Set contract until identity resolution
succeeds. This uniform unknown-type behavior is easier to predict than exposing
a setter from syntax-only category evidence; a name that remains unresolved is
invalid VBA source.

This projection is distinct from an external TypeLib callable Property. TypeLib
Get, value-put, and reference-put accessors retain their physical invoke-kind
identity under ADR 0014 instead of being inferred from a declared value type.
