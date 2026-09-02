---
status: accepted
---

# Model conditional declarations as logical families

ADR 0036 supersedes this ADR's document-scoped host-projection authority,
last-known-good host state, and worksheet/workbook intrinsic examples. Current
built-in host evidence comes only from the environment-scoped UserForm Event
catalog. Conditional source families, source/catalog coexistence, completion,
handler association, and compatibility rules otherwise remain accepted.

VbaLanguageServer analyzes conditionally compiled declarations without
selecting the `#If` branches of one concrete Office host. One or more
case-insensitively same-named declarations in the same VBA declaration scope and
namespace form one `ConditionalDeclarationFamily` when every member is guarded
by conditional compilation. A guarded declaration with no same-name peer is a
one-variant family. The model does not create a synthetic absent variant for
configurations in which that declaration is unavailable. An unconditional
declaration is not absorbed into the family.

Distinct conditional branch paths remain physical variants even when they come
from separate `#If...#End If` blocks. Family formation does not require a proof
that the predicates are mutually exclusive and does not assert that the
declarations can coexist in a concrete VBA compilation. `VbaDev` build and test
remain authoritative for the actual host configuration.

Within one immutable project snapshot, `ConditionalFamilyIdentity` is composed
from the project, VBA declaration scope, declaration namespace, and name under
the shared case-insensitive VBA name-equivalence rule. It is not derived from a
representative variant, source range, or conditional-directive offset. Physical
variants remain separately identifiable through their source declarations and
accessor kinds while retaining branch paths as semantic metadata.

Raw family and variant identities are not persistent revision identifiers.
Incremental analysis may reuse them only when it proves unchanged declaration
ownership; otherwise it rebuilds the affected relationships. Rename supplies
explicit correspondence between its immutable pre-edit target and hypothetical
post-edit target rather than equating raw identities across snapshots.

`ConditionalFamilyCanonicalName` is presentation state, not identity. It uses
the source spelling of the first physical variant in stable project declaration
order: source order within one document, and canonical source URI followed by
declaration range for a project-level scope. Completion, a family-level Hover
heading, and Prepare Rename use that spelling. Variant Signature Help and
Definition detail retain each physical declaration's source spelling.
Visibility, the use site, active signature, and conditional branch never choose
a different family name. Forming a family does not edit source; only an explicit
case-only Rename normalizes every declaration and resolved occurrence to the
requested spelling.

This distinction matters for VBA's predefined constants. On a 64-bit
development platform both `Win32` and `Win64` are true, so separate bare
`#If Win32` and `#If Win64` blocks can coexist and fail in the concrete
compiler. The language server still retains their declarations as related
conditional variants. Source that requires exclusivity should use a structural
chain such as `#If Win64...#ElseIf Win32...#End If`.

Every family variant retains its declaration kind, available valid signature,
declared type, visibility, declarator-local `WithEvents` presence where
applicable, `WithEventsTypeEligibility` where applicable, conditional branch
path, and source location. A
`RecoveredEventDeclaration` additionally retains its recovery reason and
missing-signature state. A `RecoveredWithEventsVariableDeclaration` retains its
ordinary variable identity and written modifier but is excluded from Event
binding. Name Resolution binds a use to the logical family rather than an
arbitrary physical variant. Completion projects one logical name when at least
one variant is completion-eligible; a placement-, visibility-, or name-invalid
recovered Event declaration is not. Definition returns every variant location,
References bind to the family, and Rename changes the family atomically only
when its meaning-preservation proof succeeds.

`Public`, `Private`, and `Friend` are retained as variant metadata and are not
components of `ConditionalFamilyIdentity`. Conditional visibility differences
therefore neither split a family nor create a declaration collision. Name and
Call Resolution classify visibility independently for each use and physical
variant. A variant proven invisible at that use cannot contribute an applicable
callable signature or named-argument completion candidate, but it remains in
the family for Definition and family-wide Rename. The language server does not
select a host branch merely to choose one visibility.

A one-variant family uses the same editor projections as a multi-variant family:
eligible Completion, Hover, and callable Signature Help use the generic `[#If]`
marker, Definition returns its one physical declaration, and References and
Rename retain family identity. No editor feature selects or fabricates a
concrete compilation environment merely because only one physical variant
exists.

An unconditional declaration is never a conditional family variant. After the
MS-VBAL declaration-kind collision matrix is applied, a same-name
otherwise-colliding set containing both unconditional and conditional
declarations is a `DeclarationCollision` in the language server's union model
and produces `validation.duplicateDeclaration`; a false condition in one
concrete host does not turn that mixed set into a conditional family.
Complementary Property accessor kinds are linked by Property identity before
this rule is applied and therefore do not collide merely because their
conditional status differs. Same-named unconditional callables are ordinary
duplicates even when their parameter lists differ.

Callable variants form one `ConditionalCallableFamily`. Name Resolution binds
the callable name to this family. Call Resolution applies the same
`CallArgumentMapping` operation independently to every Function, Sub, source
Declare, parameterized Property, or Event signature. A callable-kind or
accessor mismatch with the call's syntactic role makes that variant
inapplicable; it does not filter the variant from the family. A better argument
match never selects a semantic definition, infers an active branch, or supplies
one variant's return type to Type Resolution or downstream member completion.

`CallContextCompatibility` is recorded independently as compatible,
incompatible, or indeterminate for every variant before the family result is
consumed. A statement invocation can admit a Sub or Function and discard a
Function result. A value-producing expression requires a value-producing
callable, and Property read and assignment contexts apply their respective
accessor rules. A syntactically recognized `RaiseEvent` context admits Event
variants and makes non-Event callable kinds incompatible. An Event variant is
incompatible in ordinary statement, value, and Property contexts. Incomplete
syntax that does not yet establish whether the role is `RaiseEvent` is
indeterminate rather than incompatible. A context-incompatible variant remains
in `ConditionalCallCompatibility` with a conclusive inapplicable reason. Its
argument-mapping evidence may still be retained for presentation.

Event variants use this same `ConditionalCallableFamily`; there is no separate
`ConditionalEventFamily`. Only syntactically admitted source Event declarations
become callable variants. An Event outside module level in a class-module code
section receives `syntax.eventDeclarationNotAllowedInModule` over the `Event`
keyword. `Private` or `Friend` receives
`syntax.eventVisibilityNotAllowed` over that modifier; explicit `Public` and
omitted visibility are valid and both mean Public. The independent diagnostics
coexist. Invalid placement or visibility retains a
`RecoveredEventDeclaration` for existing syntactically admitted `RaiseEvent`
binding, Definition, References, and Rename, but not for completion, callable
projection, Signature Help, or handler suffix resolution.

An Event identifier containing an ASCII underscore receives one
`syntax.eventNameCannotContainUnderscore` diagnostic over the complete
identifier. It remains an invalid-name `RecoveredEventDeclaration` used by
existing syntactically admitted `RaiseEvent` binding, Definition, References,
and a repairing Rename, but not by completion, callable projection, Signature
Help, or handler suffix resolution. An Event parameter declared with `Optional`
or `ParamArray`
likewise contributes no valid signature. When its name, placement, and
visibility remain valid, it is still a physical `ConditionalDeclarationFamily`
variant used by completion, Definition, References, and Rename, but not by
Signature Help. The parameter-modifier codes are
`syntax.eventOptionalParameterNotAllowed` and
`syntax.eventParamArrayParameterNotAllowed`, each ranged to its modifier token.
Event Rename rejects an underscore-bearing requested name with `invalidName`
and can repair an invalid-name recovered declaration with a valid name.

`RaiseEvent` is admitted only inside a procedure in a class-module code section.
Any other placement receives `syntax.raiseEventStatementNotAllowedHere` over
the keyword and enters neither target resolution nor call mapping. After
placement admission, target resolution considers only source Event declarations
in the enclosing class module, including their conditional family and eligible
`RecoveredEventDeclaration`s. A same-named non-Event declaration, another
class's Event, a TypeLib Event, or an intrinsic host Event is not a fallback.
When no eligible local target exists,
`validation.raiseEventTargetNotDeclaredInEnclosingModule` is emitted over the
Event identifier, suppressing generic unresolved-name and aggregate call
diagnostics. A recovered local target retains Definition, References, and
repairing Rename but contributes only indeterminate call evidence. Completion
in this context likewise projects only completion-eligible source Event names
from the enclosing class module and never TypeLib or intrinsic Events.

After target admission, `RaiseEvent` applies the shared `CallArgumentMapping` to
every valid Event signature and maps valid arguments by source position. Each
named-argument form instead receives
`syntax.raiseEventNamedArgumentNotAllowed` from its argument name through `:=`;
it is not reinterpreted as positional or passed to call mapping. `RaiseEvent`
with no arguments omits parentheses. An empty `()` receives
`syntax.raiseEventEmptyArgumentListNotAllowed` over the delimiters, while a
parenthesized list containing one or more omitted argument slots receives one
`syntax.raiseEventOmittedArgumentNotAllowed` over the complete list. The empty
list is not also classified as an omitted list. Neither malformed list is
passed to call mapping, and both suppress the aggregate
`validation.incompatibleCallArgumentList` diagnostic. An independently invalid
named argument retains its own syntax diagnostic when it coexists with an
omitted slot. `RaiseEvent` exposes no remaining named parameters regardless of
signature metadata and supplies no result type. Signature Help retains every
physical Event signature and uses the same `[#If]` presentation and ranking
rules. Event-specific invocation and handler behavior remains a context policy
or semantic projection over the shared family rather than a second family
identity.

An `EventReference` from `RaiseEvent` or from the resolved event-name suffix of
a `WithEventsHandlerCandidate` retains its Event target associations. Candidate
recognition is syntax-role-based: the occurrence must be the declaration name of
a Sub, Function, or individual Property Get, Let, or Set accessor in the same
class module as its matching module-level variable or conditional variable
family. A procedural-module declaration, declaration in another class, or
ordinary same-spelled occurrence is not such a candidate.

`WithEventsHandlerNameDecomposition` first splits the complete procedure
identifier at its final ASCII underscore before procedure kind is validated. The
variable-name prefix and Event-name suffix must both be nonempty valid identifier
forms. The prefix may contain underscores or non-ASCII identifier characters;
VBA's Event-name restriction means the suffix cannot contain an underscore. The
split is purely syntactic and never enumerates alternative separators or
consults variable declarations, reference catalogs, Event members, conditional
branch paths, parameter signatures, visibility, or `Static`. A name without a
valid decomposition remains an ordinary procedure name.

Tentative prefix resolution identifies the complete variable target.
`WithEventsEventBindingSet` then resolves the suffix independently through every
physical module-variable variant except a
`RecoveredWithEventsVariableDeclaration` or a variant with conclusive-invalid
`WithEventsTypeEligibility`, after at least one syntactically admitted
`WithEvents` variant has `eligible` or `indeterminate` type eligibility and
admits the target for the procedure-kind-independent candidate. `WithEvents`
presence is recorded on each individual comma-separated declarator and never
propagates to a sibling. An ordinary variant without `WithEvents` becomes
`notWithEvents` before type or Event lookup. A type-eligible
`WithEventsVariableDeclaration` retains `resolved`, `notEvent`, or
`indeterminate` and its provenance according to suffix resolution, without
choosing a host branch. A type-indeterminate declaration contributes one
`indeterminate` entry before suffix lookup. A syntax-invalid
`RecoveredWithEventsVariableDeclaration` and a conclusive-invalid type variant
remain in their ordinary variable family for Definition, References, Hover, and
Rename but are excluded from this binding set; neither supplies
`notWithEvents`, `notEvent`, or `indeterminate` evidence. A nonconditional
variable or complete family enters binding only when it has at least one
`WithEvents` variant whose type eligibility is `eligible` or `indeterminate`; a
target with none produces no binding set. Different type-eligible `WithEvents`
variable variants may resolve to different Event identities. An external type
resolves an Event only through the coclass's unique default
`FDEFAULT | FSOURCE` `TypeLibEventSurface`; non-default source interfaces are
not unioned or selected as fallback. For an already-written candidate, suffix
resolution uses `TypeLibExistingHandlerRecognitionSurface`, so a structurally
known hidden or restricted member can retain its VBE code-window association
without entering `TypeLibEventAuthoringSurface` completion. The current work
performs no `MemberStubGeneration`. An intrinsic form or document target instead
resolves through `HostClassEventSurface`. An unguarded valid source Event
shadows a same-name
projected host Event for the external `WithEvents` binding, but a guarded source
Event family and the host Event remain separate configuration-dependent
associations. The server does not select a compilation branch or prove branch
coverage, so the host association remains possible even when source text has a
same-name Event in every apparent `#If` / `#Else` branch. The host Event never
becomes a variant of the source `ConditionalCallableFamily`. An excluded
variant establishes no dependent relationship of its own, but ordinary variable
Rename still covers its complete family and a sibling whose type eligibility is
`eligible` may establish family-wide dependent edits. For either
`resolvedHandler` or
`nonSubProcedureAssociation`, Definition from the suffix returns the location
union of every resolved Event target and every conditional Event declaration variant,
including a `RecoveredEventDeclaration`; References retain each target
association. The complete identifier retains its original procedure or Property
definition. Signature-dependent projections use only valid callable variants,
while `RecoveredEventDeclaration`s remain indeterminate evidence. Rename
remains subject to ADR 0029's complete target proof.

External Event-name completion deduplicates a configuration-dependent source
family and same-name host Event to one `Click`-style item. Its detail is
`Event [#If]`, and its insertion text is only the Event name. Signature Help
retains every valid source-family signature and the distinct host signature,
marking each with the same `[#If]` presentation without source/host provenance
or conditional-expression text. `RaiseEvent` completion and Signature Help
continue to use only the enclosing source Event family. For an already-written
external handler, a host Event admitted only by
`existingHandlerRecognizable` may still contribute signature guidance even
when `authoringAvailable` excludes it from ordinary completion.

For a candidate classified `resolvedHandler` or `nonSubProcedureAssociation`, the
multiple associations remain distinct for Rename.
`HandlerEventRenameConvergence` exposes its suffix as one Event `RenameTarget`
only when at least one entry is resolved, every resolved association identifies
the same source-owned logical target, and no entry is indeterminate. A
configuration-dependent source/host pair cannot converge because the projected
host Event is not the source family's `RenameTarget`. An initiating source
Event Rename that would need to change such a shared dependent handler fails
atomically with `analysisIncomplete`; it neither skips the handler nor renames
the intrinsic host Event. A
`notWithEvents` entry is neutral because that variable variant cannot receive
Events; a `notEvent` entry is neutral because its type-eligible class
conclusively lacks that suffix Event and has no competing Event binding. In both
cases, the dependent procedure, Property, or conditional-family Rename
preserves ordinary references. Distinct Event
identities and resolved non-renameable external Events do not converge. Rename
initiated from an Event target applies the same proof to every dependent
candidate suffix and fails closed when another Event identity or indeterminate
entry shares that token. Definition and References retain their complete
association unions regardless of Rename convergence.

`WithEventsHandlerRecognition` aggregates the binding entries independently for
each physical `WithEventsHandlerCandidate` without selecting a branch. A Sub
with at least one `resolved` entry produces `resolvedHandler` and becomes a
`WithEventsHandlerDeclaration`. A Function or Property accessor with at least
one `resolved` entry produces `nonSubProcedureAssociation`. This classification
records an Event association and non-Sub procedure kind without itself asserting
invalidity; all resolved entries still contribute prefix and suffix navigation
projections, but the declaration is not a handler. Each resolved target retains
an `EventHandlerValidationAuthority`:
source Events are `sourceDeclared`, current authoritative host projections are
`currentHostProjected`, TypeLib Events are `externalTypeLibAdvisory`, and
retained stale host evidence is `lastKnownGoodHostAdvisory`. The first two
authorities permit compile-style validation; the advisory authorities preserve
editor guidance without authorizing a compile-style diagnostic. A set
containing only conclusive `notWithEvents` or
`notEvent` entries produces `ordinaryProcedure`, with no handler-specific prefix binding,
Event reference, compatibility analysis, diagnostic, or dependent Rename. A set
with no resolved entry and at least one `indeterminate` entry produces
`indeterminateCandidate`, regardless of procedure kind or other conclusive
non-handler entries; its complete identifier retains its original definition
and its prefix retains the variable binding, while suffix Event reference,
procedure-kind validation, signature comparison, and handler diagnostics are
deferred. Dependent Rename is deferred as well. A `WithEvents` variable Rename
fails with `analysisIncomplete` if any candidate whose prefix binds that
variable target remains an `indeterminateCandidate`; it does not classify the
candidate as an ordinary procedure or guess a dependent Rename. Later
`resolvedHandler` or `nonSubProcedureAssociation` classification admits dependent
Rename, while later `ordinaryProcedure` classification leaves that procedure
unchanged. A mixed
resolved and non-resolved binding set retains its resolved navigation
projections but cannot establish either aggregate handler diagnostic.
A fully resolved set containing any `externalTypeLibAdvisory` or
`lastKnownGoodHostAdvisory` target likewise suppresses both diagnostics without
removing its associations.

Public, Private, Friend, or omitted visibility and initial or trailing `Static`
do not participate in `WithEventsHandlerRecognition`,
`EventHandlerCompatibility`, or conditional-family identity. They remain
metadata on the physical declaration and are preserved by source edits. Each
Property Get, Let, or Set accessor is a distinct physical candidate even when
the complementary accessors share one Property identity.

Same-named, same-scope, all-conditional declarations form the existing
`ConditionalDeclarationFamily`; there is no separate conditional handler-family
kind or candidate-role family split. Complete-name Definition returns every
physical declaration, References bind complete-name occurrences to the family,
and neither operation selects a host branch. This applies equally to
`WithEventsHandlerCandidate` and `IntrinsicHostHandlerCandidate` variants under
ADR 0031. Each physical Sub handler retains its own declaration signature and
compatibility result, while each Function or Property accessor retains its
non-Sub association and any authority-permitted diagnostic evidence. A current
intrinsic host projection compares every physical Sub independently with the
same singleton host signature and permits a diagnostic on each conclusively
invalid physical variant; a compatible sibling does not suppress it.
Last-known-good host authority suppresses diagnostics for every variant. No
condition is evaluated and no active branch is selected.

The shared `ContractMemberNameCompletion` collision rule first excludes the
physical declaration being edited and applies the declaration-kind, namespace,
and Property-accessor collision matrix. With no same-scope otherwise-colliding
peer having the complete contract name, the candidate remains regardless of
whether the prospective declaration is guarded. When at least one such peer
exists, any unconditional prospective declaration or peer suppresses the
candidate; only a guarded prospective declaration whose every peer is also
guarded retains the advisory item. Complementary Property Get, Let, and Set
kinds are not peers under this rule merely because they share a Property name.
The rule is otherwise identical for Event handlers and interface
implementations. Their evidence differs only when establishing the shared
minimum admission facts: complete contract identity, compatible declaration
kind, and conclusive authoring admission.

Kind-first contract completion is staged for intrinsic host Events, same-class
`WithEvents`, and `Implements` authoring. `ContractPrefixCompletion` follows
`Sub`, `Function`, or a complete Property Get, Let, or Set keyword sequence and
is admitted in either an empty or partially typed declaration-name slot. It
inserts only a semantic prefix plus underscore, such as `Worksheet_`,
`publisher_`, or `IFoo_`. It contributes a prefix only when at least one
same-kind downstream member survives the shared admission and collision rules,
and it never mixes complete member names into that first-stage list. The
partial name filters prefixes by case-insensitive leading text, and acceptance
replaces only that partial declaration-name fragment. Once the text exactly
equals a complete semantic prefix including its underscore and that exact
prefix has at least one surviving downstream member, the second-stage member
list takes precedence and suppresses longer prefix matches that share the same
leading text. An exact textual match with no surviving member remains
ineligible, so viable longer prefix matches stay in the first-stage list rather
than opening an empty second stage; member suffixes are never shown before a
viable exact prefix exists. The
resulting prefix enters the same `ContractMemberNameCompletion` used after
manual prefix entry; that second stage presents complete canonical names while
replacing only the suffix. Neither stage creates a parameter list, body,
terminator, or member stub. Looking ahead to the surviving downstream set is
only an existential admission check. The first-stage `[#If]` marker reflects
only guarded prefix provenance—a same-class `WithEvents` declaration or an
applicable `Implements` relationship—and never aggregates conditionality from
the downstream Event or interface member. Intrinsic prefixes are unmarked, and
the completion location alone affects neither stage's marker. The second-stage
member item retains the complete contract-provenance marker rule. ADR 0031
supplies the intrinsic host evidence and presentation specialization.

Prefix acceptance carries an editor-neutral `retriggerCompletion` continuation
intent. The language server does not embed a VS Code-specific command in the
domain candidate. After the prefix edit has been applied, the first-party VS
Code client maps that intent to reopening suggestions. Clients that do not
implement the continuation still insert the prefix correctly and obtain the
same second-stage candidates through an explicit completion request; the server
retains no prefix-selection session state.

Within one completion request, case-insensitively identical inserted prefixes
coalesce into one `ContractPrefixCompletion` even when they arise from several
conditional relationships or from different contract domains. After the edit,
the second stage re-resolves every matching contract origin from the current
document and unions their admissible member candidates. It never narrows that
set according to which prefix item was accepted, so completion insertion and
manual prefix entry remain semantically identical.

The coalesced item preserves one contributor-supplied spelling rather than
synthesizing casing. Prefix spellings group with `OrdinalIgnoreCase`; an exact
spelling conflict uses the ordinal-minimum spelling for both label and insertion
text, independent of source and enumeration order. Its compact detail is `Host
Events`, `WithEvents`, or `Interface` when the contributors share one contract
domain, and `Multiple Contracts` when domains differ. No signature or
individual-member detail is projected at the prefix stage.

For a coalesced prefix, marker aggregation considers only origins that provide
at least one surviving downstream member. It shows `[#If]` exactly when every
such origin has guarded prefix provenance. Any participating unconditional
origin, including an intrinsic host origin, removes the marker; an origin with
no remaining member is absent from the aggregate. Conditionality belonging only
to a downstream member still does not affect the prefix marker. A prefix row
presents a relationship origin rather than a concrete Event or interface
contract alternative, so this rule is outside the shared concrete-contract
provenance projection.

The second stage likewise coalesces case-insensitively identical complete names
within one required procedure or Property-accessor kind. Multiple contract
origins or signature variants therefore produce one name-only
`ContractMemberNameCompletion`, not duplicate rows. Accepting it binds neither
an origin nor a signature; all contributors remain available to subsequent
Signature Help, Definition, and validation.

The coalesced member row uses `Event`, `Interface Member`, or `Multiple
Contracts` according to its contributing domains. It appends `[#If]` when any
contributing contract has conditional provenance, even if another contributor
is unconditional, because this marker describes the retained member variant
set rather than prefix availability. The completion location alone adds no
marker. A contract's provenance is conditional when its applicable `WithEvents`
or `Implements` relationship, source Event or interface member, Public variable
owning a derived accessor, or retained configuration-dependent host-shadow
alternative is conditional. Signature Help, Hover, and diagnostic detail
project the same state; the handler or implementation location alone does not
change it. Casing conflicts use the prefix-stage contributor-spelling and
ordinal-minimum rule, but the edit replaces only the member suffix and does not
recase the existing prefix.

The completion detail pane projects every distinct signature presentation in
the same stable order as Signature Help. Presentation identity includes the
rendered signature label and its `[#If]` state, so repeated identical variants
collapse while an otherwise-identical unconditional and conditional pair stays
distinct. Completion selects no active signature or parameter and reveals no
contract-origin name or conditional expression.

Documentation groups under its displayed signature. Empty values are omitted
and identical nonempty values coalesce. One distinct value renders directly;
multiple values render in stable contributor order as numbered `Documentation
variants`. The projection neither chooses, merges, nor summarizes them, adds no
origin or condition label, and imposes no count limit that hides information.

At one physical intrinsic declaration, Signature Help projects the singleton
host Event using the complete intrinsic handler spelling and does not add
`[#If]` merely because that declaration is guarded. The complete declaration
identifier and ordinary complete-name occurrences retain their procedure-family
projection, while only the Event-name suffix is an `EventReference` to the
projected host identity. Definition and References for that suffix do not
narrow or select the conditional procedure family.

For an external handler family only, `ConditionalDependentRenameCoverage` is
`completeDependent` when every physical family variant is a
`WithEventsHandlerCandidate` classified `resolvedHandler` or
`nonSubProcedureAssociation`. That complete family is a
`DependentRenameTarget`: a source Event or module-level `WithEvents` variable
Rename changes every physical declaration and all ordinary family references
atomically, while the family cannot initiate Rename independently. A conclusive
`ordinaryProcedure` or noncandidate variant makes coverage `conclusiveMixed`;
the family remains unsplit for Definition and References, but the upstream
Rename performs no edit and fails with `resolutionChanged`. With no conclusive
mixed variant, an `indeterminateCandidate` or otherwise incompletely classified
recovered variant makes coverage `indeterminateCoverage` and fails with
`analysisIncomplete`. `conclusiveMixed` takes precedence if both evidence kinds
exist. Proven prefix or convergent suffix target selection remains available
before the requested Rename performs this coverage proof. An intrinsic handler
family has no such upstream target and never enters this coverage analysis.
Under current host projection authority, every physical intrinsic variant and
the complete family retain one fixed host-contract name: Prepare Rename returns
no target and a direct non-no-op Rename fails with `notRenameTarget`, including
a case-only change. Last-known-good-only association makes that mutation fail
with `analysisIncomplete`; without current or last-known-good association, the
ordinary conditional-family Rename contract applies.

A candidate declaration-name occurrence has three projections when it has a
resolved binding: the complete identifier defines its original procedure or
Property, its prefix refers to the `WithEvents` variable target, and its suffix
carries every resolved `EventReference` from the binding set. Every ordinary
occurrence of the complete name binds the original definition or complete
conditional family instead of being reinterpreted by its spelling. For a
`resolvedHandler` or `nonSubProcedureAssociation`, Rename of the variable projection
changes every physical candidate prefix owned by that variable and triggers a
dependent atomic Rename only after every containing conditional family has
`completeDependent` coverage. It then changes the complete procedure, Property
identity, or conditional-family names and ordinary references while preserving
every Event suffix. Complementary Property accessors participate atomically
through their existing Property identity. ADR 0029 owns the coverage, collision,
resolution, and complete candidate-ownership proof for that transaction. The
dependent procedure, Property, or complete conditional family cannot initiate
Rename: a
physical declaration prefix selects the variable, its suffix selects the Event
only when `HandlerEventRenameConvergence` and ADR 0029's complete target proof
succeed, and its underscore and ordinary complete-name references have no
Prepare Rename target. A derived Rename preserves
`validation.eventHandlerMustBeSub` on each `nonSubProcedureAssociation` Function
or Property accessor only when the complete target authority is
diagnostic-authoritative—`sourceDeclared` or `currentHostProjected`; an external
TypeLib or last-known-good host association remains diagnostic-free. It does not
repair procedure kind or detach the Event relationship.

The error-severity `validation.eventHandlerMustBeSub` diagnostic is emitted for
one physical `nonSubProcedureAssociation` candidate only when every entry in its
`WithEventsEventBindingSet` is `resolved` and every resolved target has
`sourceDeclared` or `currentHostProjected`
`EventHandlerValidationAuthority`. Its stable message is
`Event handlers must be declared as Sub procedures.` A Function selects exactly
its `Function` keyword. A Property accessor selects the complete source span
from `Property` through `Get`, `Let`, or `Set`, and each accessor is diagnosed
independently. Any `notWithEvents`, `notEvent`, or `indeterminate` entry
suppresses the diagnostic rather than assuming an active compilation
configuration. Any `externalTypeLibAdvisory` or
`lastKnownGoodHostAdvisory` association also suppresses it; external TypeLib
behavior is advisory, and stale host evidence cannot establish current compile
behavior. Visibility and `Static` do not participate. The candidate does
not enter `EventHandlerCompatibility` and never also receives
`validation.incompatibleEventHandlerSignature`; its prefix and resolved suffix
navigation projections and upstream-initiated dependent Rename remain
available under ADR 0029.

Handler parameter validation is a separate, family-aware
`EventHandlerCompatibility` analysis. The handler name first binds its Event
target or complete Event-bearing family independently of its parameter
declaration. The analysis then compares each syntactically complete physical
handler Sub declaration independently with every physical Event signature and retains a compatible,
incompatible, or indeterminate result for each handler-to-Event pair. It never
selects a signature or compilation branch, and one handler variant's result does
not affect another. Compatibility does not change Definition, References,
Rename, or family binding.
TypeLib compatibility remains available for Hover, Signature Help, and other
advisory guidance, but `EventHandlerValidationAuthority` prevents it from
causing either handler diagnostic.
The same is true of last-known-good host evidence. A current authoritative host
projection instead carries `currentHostProjected` and may participate in the
same diagnostics as `sourceDeclared`.

The same analysis is not conditional-family-specific. Its input is a
`ResolvedEventSignatureSet`. An external handler projects every `resolved`
entry in one `WithEventsEventBindingSet`; an
`IntrinsicHostHandlerDeclaration` projects its singleton host Event under ADR
0031. A nonconditional source Event or a resolved TypeLib Event projected
through `TypeLibEventSurface` contributes one signature; a resolved host Event
projected through `HostClassEventSurface` likewise contributes one. A
conditional Event family contributes all physical Event signatures.
An already-written candidate that resolves a hidden or restricted TypeLib Event
through `TypeLibExistingHandlerRecognitionSurface` contributes that retained
signature even though the authoring projection omits it. Different variable
variants may contribute
different Event identities; the projection retains that provenance rather than
forming a new Event family. Each projected external Event contract combines
the applicable `WithEvents` relationship and Event target provenance for its
shared Completion, Signature Help, Hover, and diagnostic marker; an intrinsic
host contract has only target provenance, and a retained host-shadow alternative
is conditional in its own right. A binding set with no resolved entry produces no
signature set. Missing catalog or signature metadata can make a comparison
indeterminate. A `RecoveredEventDeclaration` contributes no placeholder
signature, but its separately retained presence remains indeterminate evidence.
The same indeterminate recovery rule applies to call compatibility.

`EventHandlerCompatibility` is not `CallArgumentMapping`. A handler parameter
list is a declaration to compare with another declaration; it has no call-site
expressions, named arguments, omitted arguments, or active parameter. The two
analyses share lower-level ordered-parameter-count, canonical-type, array,
effective parameter-mechanism, and Optional or `ParamArray` shape comparison
primitives where their rules coincide. Parameter names are deliberately
excluded: Event and handler parameters correspond by ordinal position. Their
types match only when spelling normalization and Type Resolution establish the
same canonical type identity. Type-declaration characters and qualified or
unqualified names may match when they resolve to that identity, but call-site
Let coercion and assignment compatibility do not participate. `Object` and a
concrete class, a class and an implemented interface, `Variant` and a concrete
type, and distinct numeric types are therefore different. Missing, unresolved,
ambiguous, catalog-dependent, or host-dependent type evidence remains
indeterminate when it cannot establish a canonical identity rather than
becoming a guessed match or mismatch. Array shape, effective parameter
mechanism, and Optional or `ParamArray` shape are compared independently.

The error-severity
`validation.incompatibleEventHandlerSignature` diagnostic is emitted only for a
syntactically complete physical resolved handler when every entry in its
`WithEventsEventBindingSet` is `resolved`, every signature in its
`ResolvedEventSignatureSet` is conclusively incompatible with that physical
handler, no signature is indeterminate, and no resolved target contains a
`RecoveredEventDeclaration`, with every resolved Event target carrying
`sourceDeclared` or `currentHostProjected`
`EventHandlerValidationAuthority`. Any `notWithEvents`,
`notEvent`, or
`indeterminate` binding entry, compatible or indeterminate signature, or
recovered declaration suppresses the diagnostic for that handler variant. Any
`externalTypeLibAdvisory` or `lastKnownGoodHostAdvisory` association also
suppresses it.
Another physical handler's result does not affect it. A match with any possible
Event signature suppresses the diagnostic without claiming that their
conditional branch paths correspond.
For example, conditional `LongPtr` and `Long` handler variants receive no
diagnostic when the possible Event signatures contain both shapes, while a
physical `String` handler variant that matches neither is diagnosed at that
variant.

The diagnostic selects the complete parenthesized handler parameter list, or
the handler identifier when the list is omitted, and uses the stable message
`Event handler signature does not match any available Event signature.` When
supported, related information contains one item for each conclusively
incompatible source signature with a declaration location. It shows the
signature label and all independently conclusive parameter-count,
ordinal-position, canonical-type, array-shape, effective-passing-mechanism, and
Optional or `ParamArray` shape reasons. Parameter names are excluded.
Each Event detail uses `[#If]` when its projected contract provenance is
conditional because of the applicable `WithEvents` relationship, source Event
declaration, or retained host-shadow alternative. The handler declaration's
own guardedness alone does not add the marker, and no condition expression or
branch path is exposed.

`CallArgumentMapping` is shared rather than reimplemented by each editor
feature. It retains argument-to-parameter mapping, active-parameter,
remaining-named-parameter, and compatibility evidence for one signature. A
proven context, structural, or modeled MS-VBAL type violation is inapplicable
even while the call is being edited. A complete call is applicable only when
context, mapping, required-parameter coverage, and type compatibility are all
proven valid. Incomplete source, missing expression type or classification, an
unmodeled Let-coercion, incomplete ByRef or ByVal metadata, and
library-specific behavior remain indeterminate rather than guessed.

Type compatibility follows the modeled MS-VBAL static rules rather than an
exact-type-only shortcut. ByVal uses Let-coercion validity. ByRef retains the
declared-type and expression-classification distinction, including the
value-temporary semantics of an explicitly parenthesized argument. A rule the
language server has not implemented cannot by itself prove incompatibility.

A signature containing `ParamArray` cannot be invoked with any named argument,
including one that names a fixed parameter before the `ParamArray`. Its
per-variant named-argument completion set is therefore empty. Positional
arguments beyond the fixed parameters map to `ParamArray` elements, and an
omitted positional slot in that portion is a valid placeholder. An omitted slot
mapped to a required fixed parameter remains incompatible. Every call consumer
uses this shared mapping contract rather than retaining divergent
`ParamArray` rules.

Each variant argument mapping is applicable, inapplicable, or indeterminate.
`ConditionalCallCompatibility` retains every variant-keyed status and
argument-to-parameter mapping as its primary information. It does not collapse
them into one selected signature or one exclusive four-state value. Consumers
derive facts such as every variant being applicable, at least one variant being
applicable, at least one being inapplicable, no variant being applicable, and
any variant remaining indeterminate directly from the retained results.

A call with both applicable and inapplicable variants is potentially valid in
only some compilation configurations, even if other variants remain
indeterminate. It is not resolved as an overload, and the unresolved variants
are not discarded merely to produce a simpler summary.

The initial call diagnostic consumes these facts only when the call syntax is
complete, every variant is conclusively inapplicable, and no variant is
indeterminate. It then emits the error-severity
`validation.incompatibleCallArgumentList`. Any applicable or indeterminate
variant suppresses this diagnostic. In particular, the language server does not
warn when a call may be valid in a corresponding conditional-compilation branch
that it has not selected.

The aggregate diagnostic is also suppressed while the same call has
`validation.duplicateNamedCallArgument` or
`validation.positionalCallArgumentAfterNamed`, or while a `RaiseEvent` has
`syntax.raiseEventStatementNotAllowedHere`,
`syntax.raiseEventArgumentListRequiresParentheses`,
`syntax.raiseEventNamedArgumentNotAllowed`,
`syntax.raiseEventEmptyArgumentListNotAllowed`,
`syntax.raiseEventOmittedArgumentNotAllowed`, or its identifier has
`validation.raiseEventTargetNotDeclaredInEnclosingModule`. The specific
diagnostic takes presentation precedence and avoids a cascading second error.
Every per-variant `CallArgumentMapping` and incompatibility reason remains
available internally for admitted validation cases, while a placement-invalid,
targetless, empty, or omitted `RaiseEvent` argument list is not mapped at all.
Removing the specific error causes the aggregate rule to be evaluated again
against the updated call.

When the call supplies one or more arguments, the aggregate diagnostic range is
the complete argument-list source range, including delimiters for parenthesized
syntax and the supplied argument sequence for statement-form syntax. A call
with no supplied arguments instead uses the callee identifier so the diagnostic
does not collapse to an empty or delimiter-only range. This range does not
select one failed variant or alter per-signature `activeParameter` values.

The diagnostic's stable primary message is
`No available callable signature accepts this argument list.` If the client
supports LSP diagnostic related information, every conclusively inapplicable
physical signature contributes one related item located at its declaration
identifier. Its exact message is
`Candidate signature: <callable-signature> [#If]. Mismatches: <reasons>.`
An unlocated candidate, or every candidate on a client without
related-information support, instead contributes the same content as two
LF-separated `Candidate signature` and `Mismatches` lines after the primary
message under `ContractDiagnosticDetailProjection`. Conditional labels use the
generic `[#If]` marker; actual condition expressions and branch paths are not
exposed. `Candidate` implies neither active-branch selection nor overload
binding.

Each related item includes every independently conclusive reason rather than
only the first failed check. Reasons that are merely cascading consequences of
an earlier structural mapping failure are omitted. A reason labels the whole
call `call context`, a supplied positional argument
`argument <source-ordinal>`, a supplied named argument
`argument <source-ordinal> ('<written-name>')`, and a missing required input
`parameter '<declared-name>'`, falling back to its one-based declaration ordinal
only when name metadata is absent. Every ordinal is one-based, and the subject
of a supplied source argument is unchanged when different candidates map it to
different parameters. The deterministic category
order is call context, named or positional mapping, required-argument or arity
failure, `ByRef` compatibility, and proven type compatibility. Source argument
order and then declaration parameter order break ties within a category.
Indeterminate type evidence and unmodeled coercion are not presented as
failures. A conclusive context reason uses the exact fragment
`call context: expected <allowed-kind-list>, found <candidate-kind>`. The fixed
expected lists are `Sub or Function` for statement invocation,
`Function or Property Get` for a value-producing read, `Property Let` for value
assignment, `Property Set` for object assignment, and `Event` for `RaiseEvent`.
The found kind preserves the physical declaration label as `Sub`, `Function`,
`Declare Sub`, `Declare Function`, `Property Get`, `Property Let`,
`Property Set`, or `Event`. Mapping and required-input reasons use the exact fragments
`<argument-subject> mapping: named arguments are not accepted`,
`<named-argument-subject> mapping: no parameter named '<written-name>'`,
`<named-argument-subject> mapping: parameter '<declared-name>' is already supplied`,
`<positional-argument-subject> mapping: no parameter accepts this argument`, and
`<parameter-subject>: required argument is missing`. A supplied argument receives
only its first applicable mapping reason in that order. Unknown
named-argument-support metadata leaves the mapping indeterminate. Omitted
`Optional` parameters and unused `ParamArray` portions are valid, and a
missing-required reason caused only by an earlier mapping failure is omitted as
a cascade. Textual duplicate named arguments and positional arguments after a
named argument remain the dedicated diagnostics that suppress the aggregate
diagnostic rather than becoming these reason fragments.

After unique argument mapping, a direct-storage argument conclusively rejected
by a modeled `ByRef` exact-storage rule uses
`<argument-subject> for <parameter-subject> ByRef type: expected <parameter-type>, found <argument-type>`
and, independently,
`<argument-subject> for <parameter-subject> ByRef array shape: expected <scalar-or-array>, found <scalar-or-array>`.
The parameter subject uses its declared name or falls back to its one-based
declaration ordinal. A literal, expression result, callable result, or argument
made into a value temporary by explicit outer parentheses instead uses ordinary
value compatibility. The call diagnostic never reports
`expected ByRef, found ByVal`. Unknown storage-versus-temporary evidence is
indeterminate. When both direct-storage type and array shape are conclusively
incompatible, the type reason precedes the shape reason.

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
incompatible, type precedes shape.

Each incompatibility-reason fragment omits terminal punctuation. Exact duplicate
fragments are removed at their first stable position, then every retained
fragment is joined with `; ` in the established category, source-argument, and
declaration-parameter order. The enclosing `Mismatches:` sentence alone adds
one final period. No retained reason is truncated, summarized by count, or
reduced to the first failure. Related information and primary-message fallback
reuse the same ordered reason sequence.

Type Resolution produces a `ConditionalCallResultType` only when the complete
compatibility result establishes that every variant is applicable and every
variant has the same known canonical resolved value-result type. If any variant
is inapplicable or indeterminate, or has no value, an unknown result type, or a
different canonical result type, the call has no resolved result type and
downstream member completion stops. The implementation must not infer a type
from only the apparently applicable variants. Event invocation always has no
resolved result type.

Signature Help uses the per-variant mappings only to order signatures and
choose the displayed `activeSignature`. Ranking prefers:

1. call-context state in compatible, indeterminate, then incompatible order;
2. membership of every supplied named argument;
3. positional arity compatible with required, Optional, and `ParamArray`
   parameters;
4. exact statically known type matches and proven class or interface assignment
   compatibility;
5. a previously selected viable signature on retrigger, then stable source
   order.

Numeric and string coercions and conversions mediated by `Variant` do not
establish preference. An exact `Variant`-to-`Variant` match is still exact.
Unknown types, incomplete arguments, and conversion rules the language server
has not modeled remain neutral rather than guessed. Every variant remains in
Signature Help regardless of ranking.

The first-party client advertises LSP `contextSupport` and returns its current
`activeSignatureHelp` on a retrigger. The server matches the selected
`SignaturePresentationIdentity`, formed from the signature label and ordered
parameter presentation metadata but not its changing active parameter, against
the current family. A unique match is retained only when it is still tied after
the current context, named-argument, arity, and type ranking. A missing,
non-unique, or no-longer-tied match falls back to stable source order. Clients
without context support also use stable source order, and the server keeps no
hidden cursor-specific selection state.

Each displayed signature derives its own optional `activeParameter` from its
`CallArgumentMapping`. A uniquely mapped positional or named argument selects
that parameter, and additional positional arguments mapped into `ParamArray`
select the `ParamArray` parameter. An unknown named argument, duplicate mapping,
or excess positional argument without `ParamArray` has no active parameter for
that signature rather than clamping to an unrelated parameter. A mapping that
is known remains available for parameter guidance even when a separate type or
call-context rule makes the variant inapplicable.

The editor-neutral result retains nullable active-parameter values regardless
of LSP client capability. The first-party VS Code client uses
`activeParameterSupport` and `noActiveParameterSupport`, allowing projection to
send each signature's parameter index or explicit null. A client with
per-signature support but without no-active support receives only representable
indexes, and a client without per-signature support receives the active
signature's top-level index when one exists. When an older client cannot
represent null, the server preserves all signatures and omits the
unrepresentable value, accepting the protocol's parameter-zero display fallback
without rewriting its internal mapping to zero.

Property Get, Let, and Set declarations retain one Property identity for
Definition, References, and Rename. Property identity and complementary
accessor kinds are established before conditional-family or collision analysis.
Conditional alternatives are grouped independently within each accessor kind:
all-conditional declarations of one kind may form a family, an unconditional
and conditional declaration of that same kind collide, and different
complementary kinds remain legal regardless of their conditional status.
Parameter-list and declared-type compatibility across the resulting legal
Property family belongs to a separate Property validation rule. Read and
assignment call contexts derive their relevant invocation signatures
separately, while every physical variant remains in the family and a
context-incompatible accessor is inapplicable. The setter value parameter is
not an indexed argument when mapping a parameterized Property call.

Named-argument completion returns the case-insensitively deduplicated union of
names remaining across callable variants whose call context is compatible or
indeterminate. A parameter present in every such variant is an ordinary
candidate; a parameter present in only some uses the generic `[#If]` completion
detail. Only proven-incompatible variants are excluded, so incomplete context
does not erase completion; context alone makes the result empty only when every
variant is proven incompatible. Context-incompatible variants remain visible
in Signature Help and remain part of Definition, References, Rename,
diagnostics, and result-type analysis. A `RaiseEvent` context always contributes
no named-argument candidates because its arguments are positional.

The UI never reproduces, normalizes, or summarizes a variant's `#If`,
`#ElseIf`, nesting path, or effective condition. Every conditional family and
variant uses the same presentation-only `[#If]` marker, which is never included
in insertion text. Signature Help can therefore present:

```text
Foo(handle As LongPtr, flags As Long) [#If]
Foo(handle As Long) [#If]
```

Hover preserves the available variant declarations, Definition returns all
variant locations, and References and Rename always retain the complete family.
The presentation-selected active signature does not alter any of those
features.

## Considered Options

- Create a separate `ConditionalEventFamily`. Event declarations already carry
  `CallableSignature`s and require the same conditional identity, variant
  mapping, presentation, diagnostics, Definition, References, and Rename rules.
  Their differences belong to the `RaiseEvent` and `WithEvents` projections, so
  a second family would duplicate shared semantics and risk divergence.
- Resolve a uniquely applicable signature as a semantic overload and propagate
  its return type. Arguments do not select a VBA conditional-compilation branch,
  so this can bind to a declaration excluded by the concrete host and produce
  incorrect downstream member results.
- Combine only declarations whose branch predicates can be proven mutually
  exclusive. This leaves common alternatives in separate conditional blocks
  ambiguous unless the language server implements project-specific constant
  evaluation and Boolean satisfiability.
- Flatten conditional declarations into ordinary definitions and suppress only
  duplicate diagnostics. Completion, Hover, Definition, References, and Rename
  would still see unrelated ambiguous definitions.
- Select one variant from the process host or editor environment. That guesses
  a target compilation and makes otherwise identical source behave differently
  across machines.

## Consequences

Syntax and semantic inventories must retain stable family identities without
discarding physical declarations or their conditional branch paths. Call-site
resolution must accept multiple signatures and expose one reusable
argument-mapping result per variant to Signature Help, named-argument
completion, and `validation.incompatibleCallArgumentList`.

Snapshot-local semantic identities, physical source locations, and
incremental-cache reuse identities must remain separate. A range shift cannot
change family membership inside one analysis, and an unproven reuse cannot
carry stale family membership into a later snapshot.

Current single-definition and single-signature call-site result types must
become family-aware. This is not semantic overload support: Type Resolution,
Definition, References, and Rename consume the family contract, while only
Signature Help consumes presentation ranking. Type Resolution also needs the
fail-closed `ConditionalCallResultType` rather than a selected-signature result.
Signature Help projection must cover both exact LSP 3.18 no-active support and
the legacy parameter-zero display fallback without weakening the editor-neutral
result. The generic `[#If]` marker discloses conditional origin, and `VbaDev`
build and test remain final authority for the active compilation.
