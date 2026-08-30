---
status: accepted
---

# Separate syntax and validation diagnostics

VbaLanguageServer will keep `SyntaxDiagnostic`s limited to parser recovery and
malformed VBA source structure, while reporting parsed-source validity rules as
`VbaValidationDiagnostic`s. LSP `textDocument/publishDiagnostics` may publish
both diagnostic kinds together, but collectors remain separate so
document-local validation rules can ship before project-aware diagnostics such
as unresolved identifiers, duplicate declarations, and type mismatch.

Diagnostics publication is latest-only for a source URI. A committed document
analysis may enqueue a `textDocument/publishDiagnostics` notification, but the
publisher rechecks the captured document lifecycle epoch, reservation token,
and client document version before sending. If a later analysis, close
authority transition, tombstone, or close/reopen lifecycle supersedes that
captured revision, the queued publication is skipped. This stale-revision
suppression keeps V1 diagnostics from following and overwriting V2 diagnostics.

Project-aware fan-out adds a project fence to that URI-local check. Each
validation run captures one `ProjectDiagnosticRevision` covering the resolved
project authority, source membership, every member's open- or
disk-authoritative source revision, effective manifest and reference selection,
and semantic reference-catalog revisions. Every URI partition carries that
project revision together with the target document's authority, client or disk
version, lifecycle epoch, and reservation token. Immediately before transport,
both fences must still be current. A stale target-document fence rejects that
URI; a stale project fence rejects every partition from the superseded result,
even when a target URI itself did not change. The server validates a newer
project snapshot rather than comparing output equality to salvage old
partitions.

While a replacement project validation is pending, the URI that changed
publishes its new document-local diagnostics immediately and excludes any
project-aware partition fenced to its former document revision. Unchanged
members retain their last accepted complete diagnostic sets until a fresh
project result becomes available; the server does not clear and repopulate the
whole project on every edit. The fresh result republishes only sets that
changed. Delete and project departure still clear immediately. Repeated project
invalidations are coalesced latest-only by resolved project authority.

Publication is also transport-decoupled from document mutation. `didChange`
commits analysis before diagnostics serialization or transport I/O completes,
and a stalled diagnostics write must not block mutation admission or
latency-sensitive editor queries. When the client document version is known,
`publishDiagnostics` includes that version so a version-aware client can reject
late diagnostics that were already in flight before the newer revision existed.

## Consequences

Diagnostic codes use separate namespaces: `syntax.*` for `SyntaxDiagnostic`s
and `validation.*` for `VbaValidationDiagnostic`s. Document-local validation can
consume only `VbaSyntaxTree`, while future project-aware validation can consume
`VbaProjectSnapshot`, `NameResolution`, `TypeResolution`,
`VbaProjectReferenceSelection`, and available `VbaProjectReferenceCatalog`s
without blocking the initial validation slice.

Project-aware collection validates each affected immutable
`VbaProjectSnapshot` once and partitions the result by source URI across every
member of `SourceDocuments`. Open buffers and closed disk sources participate
equally, for both manifest-backed and ad hoc project scopes. A change does not
revalidate an unrelated project. Each partition is combined with that member's
document-local diagnostics, compared with its previously accepted complete
publishable set, and sent through the existing URI-owned latest-only mailbox
only when the set changed. If a previously published URI's complete set becomes
empty, the server publishes an empty diagnostics tombstone.

Initial diagnostic codes follow the glossary distinction between declaration
`CallableParameter`s and call-site `CallArgument`s:

- `syntax.raiseEventArgumentListRequiresParentheses`
- `syntax.eventDeclarationNotAllowedInModule`
- `syntax.eventVisibilityNotAllowed`
- `syntax.eventNameCannotContainUnderscore`
- `syntax.eventOptionalParameterNotAllowed`
- `syntax.eventParamArrayParameterNotAllowed`
- `syntax.withEventsDeclarationNotAllowedHere`
- `syntax.withEventsArrayNotAllowed`
- `syntax.withEventsNewNotAllowed`
- `syntax.withEventsTypeDeclarationCharacterNotAllowed`
- `syntax.withEventsTypeRequired`
- `syntax.raiseEventStatementNotAllowedHere`
- `syntax.raiseEventNamedArgumentNotAllowed`
- `syntax.raiseEventEmptyArgumentListNotAllowed`
- `syntax.raiseEventOmittedArgumentNotAllowed`
- `syntax.moduleIdentityMetadataMalformed`
- `syntax.moduleIdentityMetadataDuplicate`
- `validation.duplicateCallableParameterName`
- `validation.duplicateNamedCallArgument`
- `validation.positionalCallArgumentAfterNamed`
- `validation.incompatibleCallArgumentList`
- `validation.raiseEventTargetNotDeclaredInEnclosingModule`
- `validation.withEventsTypeCannotBeEnclosingClass`
- `validation.withEventsTypeMustBeClass`
- `validation.withEventsTypeMustBeAccessible`
- `validation.withEventsTypeMustExposeEvents`
- `validation.eventHandlerMustBeSub`
- `validation.incompatibleEventHandlerSignature`
- `validation.interfaceMemberNotImplemented`
- `validation.interfaceMemberKindMismatch`
- `validation.incompatibleInterfaceMemberSignature`
- `validation.interfaceMemberContractNotFullyImplemented`
- `validation.moduleIdentityNameConflict`

`ModuleIdentityMetadata` is source structure, so malformed `VB_Name`-like
records and a procedural-module duplicate are `SyntaxDiagnostic`s rather than
project validation. A procedural module has exactly one correctly placed
`Attribute VB_Name = "<VbaIdentifier>"` record. A class or form module may have
multiple valid class-header records under MS-VBAL; the last one is authoritative
and earlier records are `ShadowedModuleIdentityMetadata`, not duplicate
diagnostics or semantic identity occurrences. A malformed or misplaced record
still invalidates the metadata set for every module kind. The module-name value
is limited to 31 Unicode code points by MS-VBAL section 4.2, independently of
the general 255-character identifier ceiling; an overlength value is malformed
metadata. An invalid metadata set supplies no project-wide module/type identity,
qualifier, or host association, while the remaining module body stays available
for local syntax analysis. Complete absence is the separate analysis-recovery
`FallbackModuleIdentity` state and is not reclassified as malformed metadata.

Event-specific grammar restrictions follow the same boundary. A source `Event`
declaration is admitted only at module level in a class-module code section.
Any other placement, including a standard module or procedure body, receives
the error-severity `syntax.eventDeclarationNotAllowedInModule` over exactly the
`Event` keyword with the stable message
`Event declarations are allowed only at module level in a class module.`
Explicit `Public` and omitted visibility are valid and both mean Public.
`Private` or `Friend` receives the error-severity
`syntax.eventVisibilityNotAllowed` over exactly that modifier with the stable
message `Event declarations can only be Public.` Placement and visibility
diagnostics are independent and both are retained when both restrictions are
violated.

An Event identifier containing an ASCII underscore receives one error-severity
`syntax.eventNameCannotContainUnderscore` diagnostic per declaration. Its range
is the complete Event-name identifier, not one item per underscore, and its
stable message is `Event name cannot contain an underscore.` The declaration
retains an invalid-name `RecoveredEventDeclaration` for existing syntactically
admitted `RaiseEvent` binding, Definition, References, and a repairing Rename,
but is excluded from completion, callable projection, Signature Help, and Event
suffix resolution after `WithEventsHandlerNameDecomposition`.

Invalid placement or visibility likewise retains a
`RecoveredEventDeclaration` for existing syntactically admitted `RaiseEvent`
binding, Definition, References, and Rename, but is excluded from completion,
callable projection, Signature Help, and handler suffix resolution. Its
placement or modifier must be repaired through a source-structure edit rather
than Rename. All such `RecoveredEventDeclaration` forms remain indeterminate
evidence and suppress dependent aggregate call and handler diagnostic cascades.

A written `WithEvents` modifier belongs to its individual variable declarator,
not to the complete comma-separated declaration line. In
`Private WithEvents publisher As Publisher, other As Publisher, WithEvents app As Excel.Application`,
only `publisher` and `app` carry the modifier. `WithEvents` is syntactically
admitted only at module level in a class-module code section, whether the
containing declaration is introduced by `Public`, `Private`, or `Dim`. Class
modules include `.cls` and `.frm` source and document-module source exported as
`.cls`. An additional `Static` modifier does not admit the declaration: the
ordinary variable is recovered and the written `WithEvents` receives the same
placement diagnostic. A written `WithEvents` in a standard module or procedure body receives
one error-severity `syntax.withEventsDeclarationNotAllowedHere` diagnostic per
offending declarator. Its range is exactly that declarator's `WithEvents`
keyword and its stable message is
`WithEvents variables are allowed only at module level in a class module.`

Invalid placement retains a `RecoveredWithEventsVariableDeclaration` and its
normal variable definition, written modifier, Definition, References, Hover,
and ordinary Rename. It is excluded entirely from
`WithEventsEventBindingSet`, handler-prefix binding, handler diagnostics, and
establishing dependent Rename of its own rather than contributing
`notWithEvents` or `indeterminate` evidence.

The complete admitted shape is `WithEvents IDENTIFIER As class-type-name`.
Each independent violation produces its own declarator-local error-severity
`SyntaxDiagnostic`:

- `syntax.withEventsArrayNotAllowed` selects the complete array designator,
  including its parentheses and any bounds, with
  `WithEvents variables cannot be arrays.`
- `syntax.withEventsNewNotAllowed` selects exactly `New`, with
  `New cannot be used with WithEvents.`
- `syntax.withEventsTypeDeclarationCharacterNotAllowed` selects exactly the
  offending `%`, `&`, `^`, `!`, `#`, `@`, or `$` suffix, with
  `Type-declaration characters cannot be used with WithEvents.`
- `syntax.withEventsTypeRequired` selects the variable identifier when `As` is
  absent or exactly `As` when its type is absent, with
  `WithEvents variables require an explicit class type in an As clause.`

Every present violation is retained, including a type-declaration character
together with a missing explicit type, and the independent placement diagnostic
remains when applicable. No restriction, recovery, type, or `WithEvents` state
propagates across comma-separated declarators. When the identifier remains
recoverable, the syntax-invalid declarator is a
`RecoveredWithEventsVariableDeclaration`: ordinary Definition, References,
Hover, Type Resolution from surviving metadata, and Rename remain available,
but the declarator contributes no `WithEventsEventBindingSet` entry, handler
diagnostic, or dependent Rename relationship of its own. When it belongs to a
`ConditionalDeclarationFamily`, a sibling whose later
`WithEventsTypeEligibility` is `eligible` may still establish family-wide
dependent edits.

The syntactically admitted declarator remains a
`WithEventsVariableDeclaration` and receives a separate project-aware
`WithEventsTypeEligibility` after ordinary VBA Type Resolution:

- `eligible` requires a specific VBA-accessible class other than the enclosing
  class and an authoritative, complete structural Event surface containing at
  least one valid Event. An external TypeLib member marked
  `FUNCFLAG_FHIDDEN` or `FUNCFLAG_FRESTRICTED` still counts structurally.
- `invalidEnclosingClass` means the canonical type identity is the enclosing
  class itself.
- `invalidNotClass` means the type conclusively resolves to something other
  than a specific class.
- `invalidInaccessibleType` means a specific class is conclusively inaccessible
  to VBA, including a `TKIND_COCLASS` marked `TYPEFLAG_FRESTRICTED`.
  `TYPEFLAG_FHIDDEN` does not make an explicitly resolved coclass inaccessible.
- `invalidNoEvents` means a specific accessible non-enclosing class has an
  authoritative, complete structural Event surface containing no valid Event.
- `indeterminate` retains unresolved or ambiguous type resolution, a missing,
  stale, or incomplete catalog or `HostClassEventSurface`, incomplete
  conditional-compilation ownership of the `WithEvents` declaration, and a
  source Event surface containing any recovered, unnamed malformed, or
  conditionally unowned Event evidence even when another named Event remains
  available for positive navigation.

Creatability is neither required nor disqualifying. Assignment compatibility
and `Implements` compatibility do not establish Event-source eligibility. The
conclusive-invalid states are mutually exclusive and use the precedence
`invalidEnclosingClass`, `invalidNotClass`, `invalidInaccessibleType`, then
`invalidNoEvents`. Each produces one error-severity `VbaValidationDiagnostic`
over the complete declared type reference, including any qualifier:

- `invalidEnclosingClass` produces
  `validation.withEventsTypeCannotBeEnclosingClass`, with
  `A WithEvents variable cannot use its enclosing class as its declared type.`
- `invalidNotClass` produces `validation.withEventsTypeMustBeClass`, with
  `WithEvents variables must use a specific class type.`
- `invalidInaccessibleType` produces
  `validation.withEventsTypeMustBeAccessible`, with
  `The declared WithEvents class must be accessible to VBA.`
- `invalidNoEvents` produces `validation.withEventsTypeMustExposeEvents`, with
  `The declared WithEvents class must expose at least one Event.`

An `indeterminate` result produces no `WithEvents` type diagnostic. A
conclusive-invalid declaration remains available for ordinary variable
Definition, References, Hover, Type Resolution, and Rename but contributes no
`WithEventsEventBindingSet` entry, handler diagnostic, or dependent Rename
relationship of its own. It is not a
`RecoveredWithEventsVariableDeclaration`, because its source syntax is admitted.
An `indeterminate` declaration is likewise not recovered; it normally
contributes one `indeterminate` binding entry before Event-suffix lookup. A
partial compatibility TypeLib catalog is the narrow exception: an exact,
individually complete member retained from one uniquely identified default
source may contribute a resolved `externalTypeLibAdvisory` association, while
an unknown suffix remains `indeterminate` and type eligibility remains
`indeterminate`. This evidence still suppresses
aggregate handler diagnostics and prevents `HandlerEventRenameConvergence`; an
indeterminate-only handler candidate also makes upstream variable Rename fail
with `analysisIncomplete`, while mixed resolved and indeterminate evidence
retains its existing resolved navigation and safe dependent-edit projections. A
sibling whose type eligibility is `eligible` may still establish family-wide
dependent edits independently of a recovered or conclusive-invalid variant.
`TypeLibEventSurface` and `HostClassEventSurface` selection separately determine
when Event evidence is authoritative.

For an external declared type, the authoritative `TypeLibEventSurface` requires
the declared type itself to be `TKIND_COCLASS`. A direct `TKIND_INTERFACE` or
`TKIND_DISPATCH` declaration is conclusively `invalidNotClass`; its members are
not reclassified as Events merely because a coclass references that interface.
Exactly one coclass implemented interface whose flags contain both
`IMPLTYPEFLAG_FDEFAULT` and `IMPLTYPEFLAG_FSOURCE` supplies the callable members
projected as Events. A non-default `FSOURCE` interface is not merged or used as
a fallback even when it is the only source interface. `FDEFAULTVTABLE` alone is
not a substitute; when the same interface also carries `FDEFAULT | FSOURCE`, it
is projected once. Before default-source selection, every retained association
must have a nonempty identity and raw `TKIND_INTERFACE` or `TKIND_DISPATCH`
category. Missing or different raw kind is indeterminate and is not forwarded
as a coclass Event source. The complete aggregate preserves coclass and interface
`TYPEFLAGS`, member `FUNCFLAGS`, identity, signatures, and completeness, then
derives three distinct projections:

- `TypeLibStructuralEventSurface` retains every callable member of the unique
  default source, including `FUNCFLAG_FHIDDEN` and
  `FUNCFLAG_FRESTRICTED`, for `WithEventsTypeEligibility`.
- `TypeLibEventAuthoringSurface` excludes hidden and restricted members from
  ordinary Event completion and retains eligibility evidence for the separately
  deferred `MemberStubGeneration` feature.
- `TypeLibExistingHandlerRecognitionSurface` retains those member names for
  suffix resolution of an already-written `WithEventsHandlerCandidate`, matching
  the VBE code-window association without claiming that VBE compile validates
  the external handler signature.

A deliberately partial compatibility catalog is not an authoritative
`TypeLibEventSurface` and cannot prove eligibility, ineligibility, or a negative
Event lookup. It retains nothing unless complete type identity and flags plus a
complete implemented-interface association set conclusively identify exactly
one default source. Only an incomplete callable surface beyond that boundary
may retain an individually complete member in the positive existing-handler
recognition path. The retained contract is advisory, an absent suffix stays
indeterminate, and no type or handler diagnostic follows from the partial
catalog. A callable is complete only with complete raw member metadata, a
non-null signature, and an ordered parameter collection whose elements and
present type identities are structurally readable. An incomplete member is
excluded from that positive partial projection without discarding a complete
sibling. Duplicate case-insensitive member names coalesce only
when their raw member identity, flags, callable kind, result type, and complete
ordered parameter contract agree. A conflict is indeterminate; signature
labels, parameter names, display labels, and documentation do not participate
in semantic contract identity.

In an empty or partially typed `Sub` declaration-name slot, the same-class
`WithEvents` Event authoring surface admits a name-only
`ContractPrefixCompletion` for each case-insensitively prefix-matching exact
variable name followed by one underscore, but only when at least one downstream
Event member survives admission and collision filtering. Selection replaces
only the partial declaration-name fragment. No complete
`variable_Event` name appears beside that prefix. Selecting the prefix carries
editor-neutral retrigger intent and enters the same second-stage
`ContractMemberNameCompletion` as typing the complete prefix manually. The
second stage replaces only the Event-name suffix and takes precedence as soon
as the fragment exactly equals the complete variable prefix and underscore and
that exact prefix has at least one surviving Event member; longer prefix matches
are then omitted. If an exact textual prefix has no surviving Event, it remains
ineligible and viable longer prefix matches stay in the first-stage list rather
than opening an empty second stage. Neither stage offers an Event
after `Function` or a Property accessor, creates a procedure body, or expands
`MemberStubGeneration`. The globally registered `_` and space triggers return
these candidates only in their respective semantic declaration contexts;
explicit completion retains its ordinary behavior.
A prospective unconditional declaration or any unconditional same-scope
Function, Property accessor, or incompatible Sub with the complete handler name
suppresses a second name candidate. When the prospective declaration and every
peer are conditionally guarded, the advisory candidate remains without
comparing conditions, branches, or nesting; diagnostics own any actual
duplicate. An existing conflicting association and handler diagnostic remain
the repair path rather than completion creating another declaration.
The prefix item uses the generic `[#If]` detail marker only when every
participating `WithEvents` prefix origin is conditionally guarded; downstream
Event conditionality is not projected into that first stage. The prefix row
represents a relationship origin rather than a concrete Event contract, so it
is outside the shared concrete-contract provenance projection. A second-stage
member item uses the marker when its `WithEvents` relationship, source Event
declaration, or retained configuration-dependent host-shadow alternative makes
the Event contract conditional, but neither stage marks an item merely because the
completion location is guarded. Equivalent declarations across apparently
exhaustive branches retain the applicable marker because conditions and branch
coverage are not evaluated; condition expressions are never displayed.
Event name completion requires a complete handler name, the `Sub` kind, and an
authoring Event from current or committed last-known-good evidence. Missing
signature or documentation metadata degrades detail without removing the
name-only item. Missing Event identity or authoring availability contributes no
guessed candidate. Indeterminate collision evidence retains a known advisory
item, while a conclusively occupied unconditional name suppresses it even when
signature compatibility remains indeterminate.

A complete coclass with no default source interface or a structurally empty
default source has an authoritative empty Event surface and is
`invalidNoEvents`. A coclass marked `TYPEFLAG_FRESTRICTED` is instead
`invalidInaccessibleType`; `TYPEFLAG_FHIDDEN` affects discoverability but not
explicit eligibility. More than one default source interface is malformed and
therefore `indeterminate`, as is any missing, unreadable, stale, or incomplete
raw type kind, type flag, implemented-interface association-set identity, flag,
association-target raw kind, or completeness metadata. Those failures retain no
callable. After complete
association metadata identifies exactly one default source, incomplete callable
enumeration or callable metadata still leaves the structural surface
`indeterminate` but may retain individually complete callables for positive
existing-handler recognition. Catalogs preserve those raw facts rather than
deriving Event-source eligibility from a flattened editor-facing `Class` kind,
browser visibility, or a union of every source interface.

For an intrinsic form or document class, the authoritative
`HostClassEventSurface` combines valid source Event declarations owned by that
class with the corresponding built-in Event members from a complete, current
`HostClassProjection`. The same aggregate supplies structural eligibility,
ordinary Event authoring, and existing-handler recognition without selecting a
host branch. A missing, stale, or incomplete projection makes the surface
`indeterminate` rather than authoritatively empty, so it cannot establish
`invalidNoEvents`. Source file extensions, reserved document-module names, and
ordinary module names never substitute for the projection. An intrinsic handler
inside the form or document module remains a separate relationship and does not
create a `WithEventsEventBindingSet` entry or
`WithEventsHandlerDeclaration`. ADR 0031 defines the projection producer and
consumer-owned lifecycle.

That separate relationship is an `IntrinsicHostHandlerCandidate`: in the source
module associated with a current or last-known-good projection, a physical Sub,
Function, or Property accessor is admitted when its complete name
case-insensitively equals `IntrinsicEventSourceName`, one ASCII underscore, and
an `existingHandlerRecognizable` projected Event name. A Sub becomes an
`IntrinsicHostHandlerDeclaration`; a Function or Property accessor becomes a
`nonSubProcedureAssociation`. It retains the host Event association but creates
no `WithEvents` variable-prefix reference or binding set, and a same-name source
Event does not replace the projected target. Its singleton projected signature
uses the same `EventHandlerCompatibility` operation as an external handler.
`currentHostProjected` evidence permits the two handler diagnostics;
`lastKnownGoodHostAdvisory` preserves editor guidance but suppresses them.

The complete intrinsic candidate name remains the procedure or Property
definition. Only its Event-name suffix is an `EventReference` to the projected
`HostEventIdentity`; the `IntrinsicEventSourceName` prefix and underscore have
no independent semantic target. Hover and declaration-parameter Signature Help
use the projected Event data, rendering the complete handler spelling for
Signature Help. Definition uses navigable `HostClassBaseTypeProvenance` when
available and otherwise returns no location. References for the host identity
include intrinsic and external handler suffixes actually bound to it, excluding
an external association shadowed to a source Event. Ordinary complete-name
occurrences remain procedure-family references.

Current projection evidence also makes that complete intrinsic handler name a
fixed host contract rather than a Rename target. Prepare Rename returns no
target from any declaration segment, conditional variant, non-Sub association,
or ordinary complete-name occurrence, and a direct non-no-op Rename fails with
`notRenameTarget`, including a case-only change. A last-known-good-only
association makes the same mutation fail with `analysisIncomplete`; without
either association, ordinary procedure Rename rules apply.

Same-named, same-scope, all-conditional intrinsic candidates use the existing
`ConditionalDeclarationFamily`; there is no
`ConditionalIntrinsicHostHandlerFamily`. Recognition, procedure-kind
classification, and compatibility run independently for every physical
candidate against the same singleton projected host Event. Under current
authority, a compatible sibling does not suppress a conclusively invalid
variant's handler diagnostic. Last-known-good authority suppresses diagnostics
for every variant. Definition and References retain the complete procedure
family without evaluating conditions or selecting an active branch.

A source `Event` parameter declared with `Optional` or `ParamArray` is likewise
a `SyntaxDiagnostic`, and the malformed declaration does not contribute a valid
Event signature to semantic analysis. Each `Optional` token receives
`syntax.eventOptionalParameterNotAllowed`, and each `ParamArray` token receives
`syntax.eventParamArrayParameterNotAllowed`; the range is exactly the offending
keyword token, and both diagnostics are retained if both modifiers occur. When
the Event name, placement, and visibility are valid and the declaration identity
remains recoverable, parameter-only recovery keeps its `VbaDefinition` identity
for completion, Definition, References, and Rename, but contributes no valid
`CallableSignature` or Signature Help item. Its presence supplies indeterminate
recovery evidence so dependent aggregate call and handler-signature diagnostics
are suppressed.

An Event `RenameTarget` applies the same name restriction. A requested name
containing an ASCII underscore fails with `invalidName`, including an
ordinally unchanged request for an existing underscore-invalid recovered
declaration. A valid underscore-free name may repair that declaration and its
existing bound `RaiseEvent` references.

A `RaiseEvent` statement is syntactically admitted only inside a procedure in a
class-module code section. Any other placement receives one error-severity
`syntax.raiseEventStatementNotAllowedHere` diagnostic over exactly the
`RaiseEvent` keyword, with the stable message
`RaiseEvent statements are allowed only inside a procedure in a class module.`
The diagnostic covers both procedure-external placement and a procedure in a
procedural module. Independent argument-shape syntax diagnostics remain, but
the placement-invalid statement does not enter Event target resolution or
`CallArgumentMapping`.

After placement admission, target resolution is limited to a source Event or
conditional Event family declared in the enclosing class module. It never falls
back to a same-named non-Event declaration, another class's Event, a TypeLib
Event, or an intrinsic host Event. When no eligible local target exists, the
complete Event-name identifier receives the error-severity project-aware
`validation.raiseEventTargetNotDeclaredInEnclosingModule` with the stable
message
`RaiseEvent target must be an Event declared in the enclosing class module.`
This diagnostic takes precedence over a generic unresolved-name or
`validation.incompatibleCallArgumentList` diagnostic at the occurrence. An
eligible local `RecoveredEventDeclaration` remains bound for Definition,
References, and repairing Rename; because it contributes no valid signature,
its call compatibility remains indeterminate and the aggregate call diagnostic
is suppressed.

A named-argument form in `RaiseEvent` receives
`syntax.raiseEventNamedArgumentNotAllowed` from its argument-name identifier
through `:=`, excluding the value expression. One diagnostic is emitted for
each named argument. The form is not converted to a positional argument for
call mapping.

A zero-argument `RaiseEvent` written with empty parentheses receives
`syntax.raiseEventEmptyArgumentListNotAllowed` over the complete `()` range,
with the stable message
`RaiseEvent must omit parentheses when no arguments are supplied.` It does not
also receive the omitted-argument diagnostic. A parenthesized `RaiseEvent`
argument list containing one or more omitted slots receives one
`syntax.raiseEventOmittedArgumentNotAllowed` over the complete list, including
both parentheses, with the stable message
`RaiseEvent arguments cannot be omitted.` The diagnostic is emitted once per
list rather than once per omitted slot. Neither malformed list is passed to
`CallArgumentMapping`. A coexisting named-argument form independently retains
its named-argument syntax diagnostic.

By contrast, `Optional` and `ParamArray` remain valid parameter syntax for an
ordinary handler `Sub`, so a handler using them is diagnosed, when appropriate,
through the project-aware `validation.incompatibleEventHandlerSignature` rule
rather than as malformed handler syntax.

The first project-aware declaration diagnostic is
`validation.duplicateDeclaration` at error severity. It uses one explicit
MS-VBAL declaration-kind and namespace collision matrix rather than treating
every same-spelled visible definition as a duplicate. The matrix covers
procedure parameters, local variables and constants; module variables,
constants, procedures, properties, and Enum members; members within one Enum or
UDT; and project-level public Enum, public UDT, and `ModuleIdentity`
collisions. Cross-module public procedures are not declaration collisions.

Property identity and its Get, Let, and Set accessor kinds are established
before conditional-family or collision analysis. Complementary accessor kinds
form one legal family even when some are unconditional and others are
conditional. Within each accessor kind, an unconditional declaration and a
same-named conditional declaration collide, while all-conditional declarations
may form one `ConditionalDeclarationFamily`. A repeated accessor kind that can
be active together is a duplicate declaration. Parameter-list and declared-type
compatibility within a legal Property family belongs to a separate validation
rule.

Same-named unconditional Functions, Subs, and source Declare callables remain
duplicate declarations even when their parameter lists differ; VBA does not
gain ordinary source overloads. Same-named declarations in the same scope and
namespace, when every otherwise-colliding declaration is conditionally
compiled, form one
`ConditionalDeclarationFamily` even when they occur in separate
`#If...#End If` blocks. Family formation models author intent for editor
features; it does not prove mutual exclusivity and neither creates nor
suppresses a `DeclarationCollision`. Repeated declarations in one branch path
remain duplicates, and an unconditional declaration is not absorbed into the
family. For Property declarations, this analysis occurs independently within
each accessor kind after the complementary kinds are linked. The family and its
editor projections are specified by ADR 0030.

A guarded declaration also forms a one-variant
`ConditionalDeclarationFamily` when it has no same-name peer. This preserves
conditional editor identity but does not make that declaration a collision
candidate by itself. The model does not add a synthetic absent variant for
other compilation configurations.

After the accessor-kind matrix is applied, a same-name set containing any
unconditional declaration is checked as a collision in the language server's
union-of-configurations model, including a set that also contains conditional
declarations. Different parameter lists do not exempt unconditional callables.
When every otherwise-colliding declaration is conditional, the initial
`validation.duplicateDeclaration` collector retains declarations as candidates
and requires an authoritative conditional-compilation environment before
reporting a collision. Callable variants form the
`ConditionalCallableFamily` specified by ADR 0030; call context determines
which signatures admit argument mapping, and compatibility does not turn them
into semantic overloads.

Every physical declaration with at least one directly proven collision peer
receives one `validation.duplicateDeclaration` on its declaration identifier.
The message is `Declaration '<name>' conflicts with another declaration in this
scope.` and related information points only to its direct collision peers in
stable project declaration order. Source order never designates one declaration
as the original, and the same collision does not create repeated diagnostics at
one identifier range. Thus two guarded declarations that each collide with one
unconditional declaration do not identify each other as peers unless their own
simultaneous activity is independently proven.

A project or object-library name conflict is instead
`validation.moduleIdentityNameConflict` at error severity. It selects the
authoritative unquoted `ModuleIdentityMetadata` payload when that module name
conclusively equals the containing `VbaProjectName` or an active
`ReferencedVbaProjectName`. The conflicting source module remains available to
NameResolution, Definition, References, and repairing Rename; the diagnostic
does not reclassify valid identity metadata as malformed. A conflict with
another source module remains `validation.duplicateDeclaration`. If containing
project or active-reference name authority is incomplete, the collector emits
no speculative source diagnostic; the established environment and catalog
availability reporting remains responsible for that evidence gap.

One authoritative module payload receives at most one such diagnostic even when
it conflicts with multiple identities. The message lists the complete conflict
set, ordered with the containing project first and active references in
`VbaProjectReferenceSelection` order. `diagnostic.data.conflicts` preserves the
same ordered entries; every entry contains `collisionKind` and authoritative
`name`, and a `referencedProject` entry also contains its manifest
`referenceName`. The collector neither truncates this set nor creates a
synthetic related-information location for a containing binary project or an
external library identity.

`validation.incompatibleCallArgumentList` is emitted at error severity only for
a syntactically complete call whose resolved target has no indeterminate
`CallArgumentMapping` and whose every possible signature is conclusively
inapplicable. This includes an ordinary callable as the single-signature case.
A conditional family with any applicable or indeterminate variant produces no
call-compatibility diagnostic. The language server therefore does not warn
merely because a call and its matching declaration may occupy corresponding
conditional-compilation branches that it has not selected.
After syntax admission, `RaiseEvent` uses the same rule against every Event
signature in its enclosing-module `ConditionalCallableFamily`; each valid
argument maps by source position and no named-argument completion candidates are
produced. A placement-invalid or targetless statement never enters this
mapping. A named argument is malformed syntax and is not reinterpreted as
positional. It does not also produce
`validation.duplicateNamedCallArgument`,
`validation.positionalCallArgumentAfterNamed`, or the aggregate
`validation.incompatibleCallArgumentList` diagnostic. A parenthesis-free
malformed form likewise remains
`syntax.raiseEventArgumentListRequiresParentheses` and does not also produce
the aggregate call diagnostic. Empty-parentheses and omitted-argument forms
remain their respective `SyntaxDiagnostic`s, do not enter
`CallArgumentMapping`, and do not also produce the aggregate call diagnostic.
Independent named-argument syntax diagnostics are retained when they coexist
with an omitted argument. Placement and target diagnostics likewise suppress
the aggregate call diagnostic; the target diagnostic also replaces a generic
unresolved-name diagnostic at that identifier.

Handler recognition first admits the complete declaration-name occurrence of a
Sub, Function, or individual Property Get, Let, or Set accessor. The
syntax-only `WithEventsHandlerNameDecomposition` is procedure-kind-independent.
Tentative prefix resolution then requires a module-level variable target in the
same class module that is admitted to `WithEventsEventBindingSet` by at least one
`eligible` or `indeterminate` `WithEventsTypeEligibility`; a declaration in a
procedural module or another class and an ordinary same-spelled occurrence do
not enter handler validation. Public, Private, Friend, or omitted visibility and
initial or trailing `Static` remain declaration metadata and do not affect
candidate identity, binding, compatibility, or conditional-family membership.

The resulting `WithEventsHandlerCandidate` has one
`WithEventsEventBindingSet` entry per physical module-variable variant except a
`RecoveredWithEventsVariableDeclaration` or a variant with conclusive-invalid
`WithEventsTypeEligibility`, after at least one syntactically admitted
`WithEvents` variant has `eligible` or `indeterminate` type eligibility. An
ordinary variant without `WithEvents` becomes `notWithEvents` before type or
Event-member lookup. A type-eligible `WithEventsVariableDeclaration` becomes
`resolved`, `notEvent`, or `indeterminate` according to suffix resolution. A
TypeLib suffix lookup uses `TypeLibExistingHandlerRecognitionSurface`, not the
narrower `TypeLibEventAuthoringSurface`, so a hidden or restricted member can
retain the VBE-style association for an already-written candidate without
becoming an ordinary completion candidate. The current work performs no
`MemberStubGeneration`. A
type-indeterminate declaration contributes one `indeterminate` entry before
suffix lookup. Syntax-invalid recovered and conclusive-invalid type variants are
excluded entirely instead of receiving any binding status. The `resolved`
entries retain their Event references, while every other included entry keeps
its distinct status and provenance. A family containing no `WithEvents` variant
whose type eligibility is `eligible` or `indeterminate` does not enter handler
binding. Distinct type-eligible `WithEvents` variable variants may contribute
distinct Event identities without creating a synthetic Event family. Each
Property accessor is a separate physical candidate even when complementary
accessors share one Property identity.

`WithEventsHandlerRecognition` aggregates those entries independently for every
physical candidate. A Sub with at least one `resolved` entry is
`resolvedHandler` and becomes a `WithEventsHandlerDeclaration`. A Function or
Property accessor with at least one `resolved` entry is
`nonSubProcedureAssociation`. This classification records an Event association
and non-Sub procedure kind without itself asserting invalidity: its prefix
variable binding and every resolved suffix Event reference remain available for
navigation and upstream-initiated dependent Rename under ADR 0029, but it is not
a handler and does not enter `EventHandlerCompatibility`. Every resolved target
retains an
`EventHandlerValidationAuthority`: a source Event is `sourceDeclared`, a current
authoritative host projection is `currentHostProjected`, a TypeLib Event is
`externalTypeLibAdvisory`, and retained stale host evidence is
`lastKnownGoodHostAdvisory`. The first two authorities permit compile-style
validation. The advisory authorities preserve association, Hover, Definition,
and signature guidance without authorizing a compile-style handler diagnostic.
A set containing only conclusive
`notWithEvents` or `notEvent` entries produces `ordinaryProcedure` and supplies
no handler-specific projections or diagnostics. A set with no resolved entry
and at least one `indeterminate` entry produces `indeterminateCandidate`,
regardless of procedure kind or other conclusive non-handler entries; it retains
the prefix variable binding but defers suffix Event references, procedure-kind
validation, signature comparison, and handler diagnostics. Mixed resolved and
non-resolved sets expose their resolved navigation projections but cannot
establish either aggregate handler diagnostic. A fully resolved set containing
any `externalTypeLibAdvisory` or `lastKnownGoodHostAdvisory` target likewise
suppresses both diagnostics.
Same-named, same-scope,
all-conditional declarations form the existing `ConditionalDeclarationFamily`;
they do not create a separate handler-family kind.

`validation.eventHandlerMustBeSub` is a project-aware, error-severity diagnostic
for one physical `nonSubProcedureAssociation` candidate. For a
`WithEventsHandlerCandidate`, every entry in its `WithEventsEventBindingSet`
must be `resolved` and every resolved target must have `sourceDeclared` or
`currentHostProjected` `EventHandlerValidationAuthority`. For an
`IntrinsicHostHandlerCandidate`, its one projected target must be
`currentHostProjected`. Its stable message is
`Event handlers must be declared as Sub procedures.` A Function selects exactly
its `Function` keyword. A Property accessor selects the complete source span
from `Property` through `Get`, `Let`, or `Set`. A `notWithEvents`, `notEvent`, or
`indeterminate` entry suppresses the diagnostic so no conclusion is drawn from
only some compilation configurations. Any `externalTypeLibAdvisory` or
`lastKnownGoodHostAdvisory` association also suppresses it; external TypeLib
behavior is advisory, and stale host evidence cannot establish current compile
behavior. Incomplete conditional-compilation ownership of the candidate
declaration likewise suppresses the diagnostic while retaining safe positive
navigation associations. Each Property accessor is diagnosed
independently. Visibility and `Static` do not participate. The
`nonSubProcedureAssociation` candidate never also receives
`validation.incompatibleEventHandlerSignature`.

`validation.incompatibleEventHandlerSignature` is a project-aware,
error-severity diagnostic for a syntactically complete
`WithEventsHandlerDeclaration` or `IntrinsicHostHandlerDeclaration` only when
its applicable target evidence and Event-signature compatibility are conclusive
as specified below. Its own conditional-compilation ownership must also be
complete. A
nonconditional source Event, resolved TypeLib Event, or projected host Event
contributes one signature, while a conditional Event family contributes every
physical signature. The
`resolved` external binding entries project a `ResolvedEventSignatureSet`; a
binding set with no resolved entry produces no signature comparison. An
intrinsic declaration projects its singleton host signature. A hidden or
restricted TypeLib Event resolved for an already-written candidate contributes
its retained signature even though the authoring projection omits that Event.

`EventHandlerCompatibility` compares the handler declaration independently with
every projected signature rather than using
`CallArgumentMapping` or selecting a branch. Any compatible or indeterminate
signature suppresses the diagnostic, and the compatibility result never
narrows Definition, References, Rename, or the handler's Event-target binding.
TypeLib and last-known-good host comparison results remain available for Hover,
Signature Help, and other advisory guidance, but their advisory
`EventHandlerValidationAuthority` prevents them from causing either handler
diagnostic. A current authoritative host projection instead carries
`currentHostProjected` and may participate in the same diagnostics as
`sourceDeclared`.
Parameter names are excluded from compatibility; ordered parameter count,
canonical types, array shape, effective passing mechanism, and required,
Optional, or `ParamArray` role participate, followed by Optional default
presence or its evaluated constant value. Parameter-type comparison requires
the same canonical type identity after spelling normalization and Type Resolution. A
type-declaration character and an `As` type, or qualified and unqualified names,
may match only when they resolve to that same identity. Call-site Let coercion
and assignment compatibility are not used: `Object` and a concrete class, a
class and an implemented interface, `Variant` and a concrete type, and distinct
numeric types remain different. Missing, unresolved, ambiguous,
catalog-dependent, or host-dependent evidence makes that comparison
indeterminate when it cannot establish a canonical identity; it is not guessed
compatible or incompatible. Array shape, effective parameter mechanism, and
Optional or `ParamArray` shape remain separate comparison dimensions. Missing
catalog or signature metadata likewise remains indeterminate for singleton and
conditional targets alike. A
`RecoveredEventDeclaration` is not placed in the
`ResolvedEventSignatureSet`, but its presence also suppresses this aggregate
diagnostic. A target containing only recovered Event declarations therefore
does not become an unresolved-name or incompatible-handler cascade.
For a conditional handler family, this analysis runs separately for each
physical handler declaration against the complete projected Event-signature
set. For an external handler, the diagnostic is emitted on one physical variant
only when every `WithEventsEventBindingSet` entry is `resolved`, every projected
signature is conclusively incompatible, no signature is indeterminate, no
resolved target contains a `RecoveredEventDeclaration`, and every resolved
target carries `sourceDeclared` or `currentHostProjected`
`EventHandlerValidationAuthority`. For an intrinsic handler, its singleton
signature must be conclusively incompatible and its target must be
`currentHostProjected`. A `notWithEvents`, `notEvent`, or `indeterminate`
binding entry, one compatible or indeterminate Event signature, recovered
declaration, `externalTypeLibAdvisory`, or `lastKnownGoodHostAdvisory` evidence
suppresses the diagnostic for that physical handler. Another handler variant's
result does not change it. A match with any possible Event signature suppresses
the diagnostic without asserting that their conditional branch paths
correspond.

The primary diagnostic range is the complete handler parameter-list source
range of that physical variant, including its parentheses when present. If the
declaration omits a parameter list, the range is that handler identifier. The
stable primary message is
`Event handler signature does not match any available Event signature.` It is
self-contained when the client does not support diagnostic related information.

When related information is supported, each conclusively incompatible source
Event signature with a navigable declaration contributes one item. Each item
selects the physical source Event identifier and uses
`Required contract: <Event-signature> [#If]. Mismatches: <reasons>.` The marker
and its preceding space are omitted when that projected Event contract has
unconditional contract provenance, and the
signature includes its `Event` kind. It contains every independently conclusive
mismatch reason in this stable order:
parameter count; then, for each parameter in ordinal order, canonical type;
array shape; effective `ByVal` or `ByRef`; required, Optional, or `ParamArray`
role; and default. Parameter position labels the slot and is not a separate
mismatch reason. Parameter names are excluded. Conditional Event contract
provenance uses only the generic `[#If]` marker and never exposes a condition
expression or branch path.
Navigable items use
stable project declaration order and are never ranked by mismatch count,
mismatch category, conditionality, or current edit state. Identically rendered
physical Event variants remain separate because their locations are distinct.

`EventHandlerCompatibility` and interface contract fulfillment obtain these
ordered facts from the same `VbaCallableContractComparison`. The comparison
returns structured mismatch and indeterminate facts rather than diagnostic
strings. One shared `VbaCallableContractComparisonFormatter` applies the grammar
below. Event and interface consumers retain their own authority, contract-set
aggregation, diagnostic ranges, related-information and fallback decisions.
`CallArgumentMapping` remains a separate call-site operation and does not share
this declaration-comparison result.

Event-handler and interface-member signature diagnostics share one exact
mismatch-reason grammar. `expected` always describes the Event or interface
contract, and `found` describes the written handler or implementation. The
available templates are:

- `parameter count: expected <integer>, found <integer>`
- `parameter <ordinal> type: expected <type>, found <type>`
- `parameter <ordinal> array shape: expected <scalar-or-array>, found <scalar-or-array>`
- `parameter <ordinal> passing: expected <ByVal-or-ByRef>, found <ByVal-or-ByRef>`
- `parameter <ordinal> role: expected <parameter-role>, found <parameter-role>`
- `parameter <ordinal> default: expected <default>, found <default>`
- `value parameter presence: expected <present-or-absent>, found <present-or-absent>`
- `value parameter type: expected <type>, found <type>`
- `value parameter array shape: expected <scalar-or-array>, found <scalar-or-array>`
- `value parameter passing: expected <ByVal-or-ByRef>, found <ByVal-or-ByRef>`
- `value parameter role: expected <parameter-role>, found <parameter-role>`
- `value parameter default: expected <default>, found <default>`
- `return contract presence: expected <present-or-absent>, found <present-or-absent>`
- `return type: expected <type>, found <type>`
- `return array shape: expected <scalar-or-array>, found <scalar-or-array>`

The exact role labels are `required`, `Optional`, and `ParamArray`; shape labels
are `scalar` and `array`; an absent default is `no default`; unavailable or
unevaluable default evidence remains indeterminate and produces no mismatch
reason; and passing always uses the effective `ByVal` or `ByRef` rather than
saying that the mechanism was omitted. Multiple reasons are joined by `; ` and
receive one final period. A conditional marker follows the complete signature
label with one preceding space and before the signature sentence's period, as in
`Required contract: Function IFoo_Parse(...) As String [#If]. Mismatches: parameter 1 passing: expected ByRef, found ByVal; return type: expected String, found Variant.`

An authoritative, conclusively incompatible contract variant without a
navigable definition contributes no synthetic related-information location.
Instead, its diagnostic appends these two LF-separated lines after the stable
primary message:

`Expected signature: <signature> [#If].`

`Mismatches: <reasons>.`

The marker and its preceding space are omitted when that projected Event or
interface contract has unconditional contract provenance.
When the client supports related information, only unlocated contracts use this
primary-message fallback; a navigable contract remains represented solely by
related information and is not duplicated. Exactly identical unlocated
signature-and-reason presentations
coalesce because no location or exposed condition distinguishes them, retaining
the first position. A host projection or external catalog uses its
authoritative contract set's stable signature order. Every distinct
presentation remains visible without a count limit or truncation. Navigable
source items and primary-message fallback lines keep their own orders and are
not interleaved into one synthetic sequence. Neither surface reorders by
compatibility ranking, mismatch count, mismatch category, conditionality, or
current edit state. The collector neither points the fallback back to the
primary error range nor creates a virtual definition document solely for this
diagnostic detail. Advisory evidence that cannot authorize the diagnostic does
not enter the fallback.

Call, Event, and interface contract diagnostics project their details according
to the client's related-information capability. A supporting client receives
every navigable contract detail as related information and only unlocated fallback
lines in the primary message. A client without that capability instead receives
both navigable and unlocated details after the stable primary message in one
deterministic contract sequence, with no contract duplicated. Canonical kind
order is outermost when the diagnostic spans multiple kinds; within each kind,
source-backed contracts use stable project declaration order and precede
unlocated host or catalog contracts in their authoritative set's stable
signature order. This source-first rule is fixed presentation order rather than
compatibility ranking. For an Event or
interface signature mismatch, each projected contract uses the two LF-separated
`Expected signature` and `Mismatches` lines. A projected call candidate uses
`Candidate signature` and `Mismatches`. Interface missing, kind-mismatch,
and partial-coverage diagnostics use one `Required contract` line per projected
contract. Within this primary-message fallback only, exactly identical complete
presentations coalesce at their first position without a multiplicity label.
Signature, generic `[#If]` marker, and every mismatch reason all participate in
that equality; any difference preserves separate details. Physical contracts
remain distinct in analysis, and a supporting client's location-bearing related
items remain distinct. This capability fallback does not apply to evidence that
cannot authorize the diagnostic.

Every Event and interface contract detail uses the same conditional provenance
as contract-facing Completion, Signature Help, and Hover. The marker appears
when an applicable `WithEvents` or `Implements` relationship, source Event or
interface member, Public variable owning a derived accessor, or retained
configuration-dependent host-shadow alternative makes that contract
conditional. A guarded handler, implementation, completion, or other use
location alone never adds the marker.

The aggregate diagnostic is suppressed while that same call has
`validation.duplicateNamedCallArgument`,
`validation.positionalCallArgumentAfterNamed`,
`syntax.raiseEventStatementNotAllowedHere`,
`syntax.raiseEventArgumentListRequiresParentheses`,
`syntax.raiseEventNamedArgumentNotAllowed`,
`syntax.raiseEventEmptyArgumentListNotAllowed`, or
`syntax.raiseEventOmittedArgumentNotAllowed`, or while the same occurrence has
`validation.raiseEventTargetNotDeclaredInEnclosingModule`. These specific
diagnostics already explain why the call is invalid, so also publishing
`validation.incompatibleCallArgumentList` would be a cascade. The underlying
`CallArgumentMapping` still retains all per-signature incompatibility reasons
for admitted validation cases and later diagnostic passes; a placement-invalid,
targetless, empty, or omitted `RaiseEvent` argument list is not mapped. After
the specific diagnostic is removed, the aggregate rule is evaluated again and
may report an independent arity, type, required-argument, or call-context
incompatibility.

When one or more arguments are supplied, the aggregate diagnostic uses the
complete argument-list source range. Parenthesized call syntax includes its
delimiters; statement-form syntax covers the supplied argument sequence. A call
with no supplied arguments uses the callee identifier as its range rather than
an empty or delimiter-only range. This presentation rule is shared by ordinary
callables and conditional callable families and is independent of Signature
Help `activeParameter` projection.

The stable primary message is
`No available callable signature accepts this argument list.` When the client
advertises LSP diagnostic related-information support, the server attaches one
related item for each conclusively inapplicable physical signature. Each item
uses the declaration identifier as its location and contains the signature
label plus concise incompatibility reasons. A conditional signature uses the
generic `[#If]` marker and never exposes its source condition expression or
branch path. Under the shared capability-aware diagnostic-detail projection, a
client without related-information support receives the same conclusive
signature details after the primary message instead. No candidate is duplicated
across both surfaces, and an ordinary callable follows the same projection as
the single-signature case.

A navigable related item uses the exact message
`Candidate signature: <callable-signature> [#If]. Mismatches: <reasons>.`
An authoritative unlocated candidate even on a supporting client, and every
candidate on a client without related-information support, instead uses these
two LF-separated lines after the stable primary message:

`Candidate signature: <callable-signature> [#If].`

`Mismatches: <reasons>.`

The marker and its preceding space are omitted for an unconditional signature.
The `CallableSignature` includes its callable kind, parameters, and return type;
the word `Candidate` implies neither active-branch selection nor overload
binding. Shared capability-projection ordering and primary-message coalescing
rules apply.

Related information does not stop after the first failure. It reports every
independently conclusive incompatibility reason while omitting secondary errors
that exist only because an earlier structural mapping failed. Reasons use this
caller-centric subject grammar: `call context` for the whole call;
`argument <source-ordinal>` for a supplied positional argument;
`argument <source-ordinal> ('<written-name>')` for a supplied named argument;
and `parameter '<declared-name>'` for an absent required parameter, falling back
to `parameter <declaration-ordinal>` only when parameter-name metadata is
missing. All ordinals are one-based. A candidate-specific argument-to-parameter
mapping never changes the subject assigned to a supplied source argument.
Reasons then use this
stable category order: call context; named or positional mapping; missing
required arguments or arity; `ByRef` compatibility; and proven type
compatibility. Source argument order and then declaration parameter order break
ties within a category. Indeterminate type evidence, unresolved classifications,
and unmodeled coercion never become displayed incompatibility reasons. A
conclusive context reason uses the exact fragment
`call context: expected <allowed-kind-list>, found <candidate-kind>`. Expected
kind lists are fixed by syntactic role: `Sub or Function` for statement
invocation, `Function or Property Get` for a value-producing read,
`Property Let` for value assignment, `Property Set` for object assignment, and
`Event` for `RaiseEvent`. The found kind preserves the physical declaration
label as `Sub`, `Function`, `Declare Sub`, `Declare Function`, `Property Get`,
`Property Let`, `Property Set`, or `Event`. Mapping
and required-input reasons use these exact fragments:
`<argument-subject> mapping: named arguments are not accepted`,
`<named-argument-subject> mapping: no parameter named '<written-name>'`,
`<named-argument-subject> mapping: parameter '<declared-name>' is already supplied`,
`<positional-argument-subject> mapping: no parameter accepts this argument`, and
`<parameter-subject>: required argument is missing`. A supplied argument receives
only its first applicable mapping reason in that order. Unknown
named-argument-support metadata makes the mapping indeterminate. Omitting an
`Optional` parameter or an unused `ParamArray` portion is valid. A
missing-required reason that exists only because an earlier structural mapping
failed is a cascade and is omitted. Textual duplicate named arguments and
positional arguments after a named argument retain their dedicated diagnostics,
which suppress the aggregate diagnostic rather than contributing one of these
fragments.

After unique argument mapping, a direct-storage argument conclusively rejected
by a modeled `ByRef` exact-storage rule uses
`<argument-subject> for <parameter-subject> ByRef type: expected <parameter-type>, found <argument-type>`
and, independently,
`<argument-subject> for <parameter-subject> ByRef array shape: expected <scalar-or-array>, found <scalar-or-array>`.
The parameter subject uses its declared name or falls back to its one-based
declaration ordinal. A literal, expression result, callable result, or argument
made into a value temporary by explicit outer parentheses instead uses ordinary
value compatibility. The call diagnostic never reports
`expected ByRef, found ByVal`, because the call-site argument has no declared
passing mechanism. Unknown storage-versus-temporary evidence is indeterminate.
When both direct-storage type and array shape are conclusively incompatible,
the type reason precedes the shape reason.

A uniquely mapped ByVal argument or `ByRef` value temporary rejected by modeled
value compatibility uses
`<argument-subject> for <parameter-subject> type: expected <parameter-type>, found <argument-type>`
and, independently,
`<argument-subject> for <parameter-subject> array shape: expected <scalar-or-array>, found <scalar-or-array>`.
The parameter subject follows the same declared-name and one-based-ordinal
fallback. A type reason requires a modeled Let or Set rule to prove conversion
failure; unknown static types, expression classifications, and unmodeled
coercions remain indeterminate. Type labels use resolved canonical presentation
rather than expression text or raw source spelling. Type-declaration characters
expand to canonical intrinsic names, intrinsic casing is normalized, and an
external type is reference-qualified whenever otherwise-distinct canonical
identities would render alike. Array shape uses only `scalar` or `array`; rank
and bounds are not presented. When both value type and shape are conclusively
incompatible, the type reason precedes the shape reason.

Each incompatibility-reason fragment omits terminal punctuation. Exact duplicate
fragments are removed at their first stable position, then every retained
fragment is joined with `; ` in the established category, source-argument, and
declaration-parameter order. The enclosing `Mismatches:` sentence alone adds
one final period. No retained reason is truncated, summarized by count, or
reduced to the first failure. Related information and primary-message fallback
reuse the same ordered reason sequence.

Interface implementation validation uses four project-aware, error-severity
diagnostics rather than reproducing VBE's generic mismatch message.
After applying the implemented-name cascade rule below, evaluate each allowed
contract kind independently. `validation.interfaceMemberNotImplemented` reports
an allowed contract kind with no same-kind implementation candidate.
`validation.interfaceMemberKindMismatch` separately reports a same-named
declaration under a disallowed procedure or Property accessor kind, including an
extra accessor not represented by the contract. A wrong-kind declaration does
not enter signature validation. `validation.incompatibleInterfaceMemberSignature`
reports a same-kind physical implementation variant only when every contract
variant is conclusively incompatible. Any relevant indeterminate contract or
implementation evidence suppresses the corresponding conclusive diagnostic
rather than guessing.

`validation.interfaceMemberContractNotFullyImplemented` reports the separate
partial-coverage state in which at least one same-kind contract variant is
covered by a compatible implementation variant and at least one other contract
variant is conclusively uncovered. A contract variant is not conclusively
uncovered when any same-kind implementation comparison that could cover it is
indeterminate. No same-kind candidate remains
`validation.interfaceMemberNotImplemented`, while an implementation variant
that conclusively matches no contract remains
`validation.incompatibleInterfaceMemberSignature`; the partial-coverage code
does not replace either case or attempt to align conditional branch paths.

One partial-coverage diagnostic is emitted per `Implements` relationship,
implemented member name, and required callable or accessor kind. Its primary
range is the complete interface type reference in the applicable implementing
class's `Implements` directive, and its stable message is
`Interface member '<implemented-name>' does not implement every required <kind> contract.`
Each conclusively uncovered physical source contract contributes related
information at its member name, or at the Public variable name for a derived
accessor, in stable project declaration order. The exact related message is
`Required contract: <kind-specific-signature> [#If].`; the marker and its
preceding space are omitted when the projected interface contract has
unconditional contract provenance. The diagnostic
does not select a closest implementation or show `Mismatches:` because no
single implementation variant is the semantic counterpart of an uncovered
contract.

An authoritative, conclusively uncovered contract without a navigable
definition contributes no synthetic related-information location. Instead, the
partial-coverage diagnostic appends one LF-separated line after its stable
primary message:

`Required contract: <kind-specific-signature> [#If].`

The marker and its preceding space are omitted when the projected interface
contract has unconditional contract provenance.
When the client supports related information, only unlocated contracts appear
in this fallback; navigable contracts remain only in related information.
Exactly repeated unlocated presentations coalesce
at their first position, while every distinct presentation remains visible in
the authoritative contract set's stable signature order without truncation.
This one-line form deliberately differs from the two-line `Expected signature`
and `Mismatches` fallback for an incompatible signature because partial
coverage selects no single found implementation signature.

Partial coverage and an orphaned physical implementation are independent
diagnostic facts. When a contract set contains both covered and conclusively
uncovered variants, retain its aggregate
`validation.interfaceMemberContractNotFullyImplemented` even if another
physical same-kind implementation matches no contract; that physical
declaration independently receives
`validation.incompatibleInterfaceMemberSignature`. The two diagnostics expose
the contract-side gap and implementation-side repair location respectively.
When no contract variant is covered and every relevant comparison is
conclusively incompatible, emit only the physical incompatible-signature
diagnostics because the fulfillment state is total incompatibility rather than
partial coverage.

A missing diagnostic is emitted once per missing contract set keyed by the
`Implements` relationship, implemented member name, and required callable or
accessor kind, not once per physical conditional contract variant. Its primary
range is the complete interface type reference in the applicable implementing
class's `Implements` directive. Its stable message is
`Interface member '<implemented-name>' requires a <required-kind> implementation.`
Related information contains one item for each contributing source contract
variant, located at the source member name—or at the Public variable name for a
derived accessor—with `Required contract: <kind-specific-signature>.`
Conditional contract provenance uses only the generic `[#If]` marker; condition
expressions and branch paths are never shown. The primary message remains
self-contained for clients that do not display related information.

When every same-named implementation declaration has a disallowed kind and no
allowed-kind candidate exists, the kind-mismatch diagnostic suppresses all
missing-contract diagnostics for that implemented name. Its related information
shows every expected kind so the first repair remains actionable without a
cascade. After an allowed-kind candidate exists, missing diagnostics resume for
each absent sibling contract kind, while any wrong-kind extra remains a separate
kind mismatch.

Every conclusive physical wrong-kind declaration receives its own
`validation.interfaceMemberKindMismatch`, including each member of a conditional
declaration family. Another physical declaration's result never suppresses that
repair location. This physical multiplicity does not reintroduce the suppressed
missing-contract diagnostics while the implemented name has no allowed-kind
candidate.

The primary range selects only the repairable declared kind: the exact `Sub` or
`Function` keyword, or the complete source span from `Property` through `Get`,
`Let`, or `Set`. It excludes visibility, `Static`, the member name, parameters,
and the rest of the declaration header. The stable self-contained message is
`Interface member '<implemented-name>' requires <expected-kind-list>, not <actual-kind>.`
Canonical kind labels and expected-list order are `Sub`, `Function`,
`Property Get`, `Property Let`, and `Property Set`; two alternatives join with
`or`, while three or more use commas and a final `or`. The expected list is the
union of represented contract kinds. The primary message contains neither
`[#If]` nor condition or branch text because each physical conditional variant
already has its own range.

Related information contains one item for every contributing physical expected
contract variant. Items are grouped by canonical expected-kind order and then
by deterministic source order. Each item selects the source interface member
name—or the Public variable name for a derived accessor—and uses
`Required contract: <kind-specific-signature>.` Multiple kind-specific items may
therefore share one source variable location. Variants remain separate and
append only the generic `[#If]` marker when their contract provenance is
conditional; condition expressions and branch paths are never exposed.

For both a missing implementation and a kind mismatch, an authoritative
required contract without a navigable definition contributes no synthetic
related-information location. Instead, append one LF-separated line after the
stable primary message:

`Required contract: <kind-specific-signature> [#If].`

The marker and its preceding space are omitted when the projected interface
contract has unconditional contract provenance.
When the client supports related information, navigable contracts remain only
there. Exactly repeated unlocated presentations coalesce at their first
position, while every distinct
presentation remains visible without truncation. A missing diagnostic uses its
required-kind contract order; a kind mismatch uses canonical expected-kind
order and then each authoritative contract set's stable signature order. Neither
fallback adds `Mismatches:` because these diagnostics do not compare one
same-kind implementation signature with a contract.

For `validation.incompatibleInterfaceMemberSignature`, the primary range is the
complete signature source span from the implemented member identifier through
its parameter list and any written return type, including a return
type-declaration character. It excludes visibility, `Static`, the already-correct
Sub, Function, or Property accessor keyword, and the procedure body. The stable
self-contained message is
`Interface member '<implemented-name>' signature does not match any required <kind> contract.`
This range deliberately differs from the Event-handler parameter-list range
because a Function or Property Get return type can be the incompatible
component.

When related information is supported, every conclusively incompatible
physical contract variant with a navigable declaration contributes one item at
the source interface member name, or at the Public variable name for a derived
accessor, in stable project declaration order. Its message is
`Required contract: <kind-specific-signature> [#If]. Mismatches: <reasons>.`
The marker and its preceding space are omitted when the projected interface
contract has unconditional contract provenance. Conditional contracts never
expose their condition expression or branch path.
Each item reports every
independently conclusive reason rather than stopping after the first.

Reasons first report parameter-list count. They then proceed by ordinary
parameter ordinal, reporting canonical type, array shape, effective passing
mechanism, role, and default in that order. The final Property Let or Set value
slot follows the indexed parameters and first compares presence, then the same
dimensions under its effective-ByVal normalization when both slots are present.
Function or Property Get result presence, type, and array shape follow the
parameter and Property-value categories. Parameter
position labels a slot rather than constituting a separate mismatch reason.
Parameter names and source spelling of equivalent defaults are not mismatch
reasons. Written `ByVal`, `ByRef`, and an
omitted mechanism are equivalent only for that final value parameter because
all three have effective `ByVal` semantics there; other parameters use their
ordinary effective mechanism. A structural difference that prevents a sound
slot mapping does not cause speculative secondary reasons. Each real unmapped
slot nevertheless retains its own unavailable type, shape, passing, role, or
default dimension as an indeterminate fact. Unavailable or unevaluable default
evidence or an unresolved type remains indeterminate rather than becoming a
displayed mismatch.

Validation collectors should consume structured syntax nodes instead of
re-parsing source text. Duplicate named call arguments and positional call
arguments after named call arguments therefore require `VbaArgumentSyntax` to
model positional, named, and omitted `CallArgument`s before those validation
rules are implemented.

Document-local validation collectors take `VbaSyntaxTree` and the document URI
as their initial inputs. They should not take the raw document text unless a
future syntax-tree source-slice API proves insufficient. Project-aware
validation remains a separate collector that can consume project snapshots and
semantic resolution state.

`didClose` ends the open-buffer lifecycle and invalidates any queued
buffer-authoritative diagnostics. If the URI remains a member of a tracked
project and still has a disk source, the server captures that source under disk
authority, recomputes the affected project, and republishes the current
diagnostics instead of clearing them. Delete, project departure, or loss of any
tracked disk source publishes an empty diagnostics tombstone through the same
latest-only publisher path. A later reopen starts a new open-buffer lifecycle,
so diagnostics captured from either the former open lifecycle or a superseded
disk revision cannot enter the reopened document.
