---
status: accepted
---

# Make Rename meaning-preserving

VbaLanguageServer treats Rename as a semantic refactoring rather than a
project-wide text replacement. Prepare Rename returns the occurrence range
under the request with the `RenameTarget` declaration's canonical name as its
placeholder. An ordinally identical `RenameName` is a successful no-change
result. A case-only difference is intentional and updates the logical target
and every resolved target occurrence.

A `RenameName` must satisfy the shared MS-VBAL `IDENTIFIER` contract recorded
by ADR 0010. Rename does not trim the request or accept a typed-name suffix or
`FOREIGN-NAME`. An Event `RenameTarget` additionally rejects every requested
name containing an ASCII underscore with `invalidName`. This target-specific
validation precedes the ordinary no-change result, so an ordinally unchanged
request for an existing underscore-invalid `RecoveredEventDeclaration` is also
invalid. The recovered declaration and its bound `RaiseEvent` references may be
renamed to a valid underscore-free name, repairing the source. This restriction
does not apply to a `WithEvents` variable or ordinary procedure name.

A `ModuleIdentity` `RenameName` has the MS-VBAL section 4.2 module-name ceiling
of 31 Unicode code points, while other targets retain the shared 255-character
identifier ceiling. A 32-code-point module Rename fails with `invalidName`.

A manifest-backed `ModuleIdentity` Rename also compares the requested name with
the containing source template's actual `VbaProjectName` from `VBProject.Name`.
`ProjectManifest.projectName`, the document name, and the workbook filename are
tooling identities rather than substitutes for this VBA identity. When the
source template cannot supply an authoritative containing-project name, Rename
fails with `analysisIncomplete` instead of claiming that the collision is
absent. An `AdHocVbaProject` has no containing-project-name authority by design,
so it skips only this check; that deliberate absence does not disable all ad-hoc
module Rename. Acquisition of the template identity remains a language-server
internal concern and introduces no new `VbaDev` command or public manifest
field.

The containing `VbaProjectName` authority is bound to the exact source-template
content captured at Rename request start. A cached or last-known-good observation
is authoritative only when its content fingerprint matches that captured
template; a value from older content is not reused after the template changes.
If the current content cannot supply the name, Rename fails with
`analysisIncomplete`. Once the immutable request snapshot has captured the
matching fingerprint and name, Rename does not reread the template or move its
semantic baseline during planning.

An active referenced project or object library contributes its authoritative
`ReferencedVbaProjectName` to the same module-name uniqueness check. For a
TypeLib-backed reference this is the actual library/project name supplied by the
selected TypeLib, not the manifest's human-visible reference name and not every
generated or supplemental qualifier alias. Consequently active names such as
`VBA`, `Excel`, or `Word` reject an equal module Rename, while a derived alias
such as `MicrosoftExcel160ObjectLibrary` does not reject Rename unless it is
itself the authoritative referenced project name. How the language server keeps
that name distinct from ordinary qualifier aliases is an internal concern.

Referenced-name mutation authority comes from an explicit bundled project name
or from a concrete TypeLib identity in a current-schema persisted or generated
catalog committed for the active `ReferenceSelectionFingerprint`. A
stale-persisted catalog can continue to serve ordinary editor metadata but
cannot prove this collision boundary. Background refresh in flight does not
invalidate an otherwise-current committed authority and is not awaited. Rename
captures the catalog lifecycle revision, concrete identity, and project name at
request start and does not move that reference baseline during planning.

For a manifest-backed project, every active reference must contribute that
authoritative name before `ModuleIdentity` Rename can prove the absence of a
project or object-library collision. If a reference catalog is unavailable,
ambiguous, or legacy metadata lacks the name, Rename fails immediately with
`analysisIncomplete` and guidance to retry after reference metadata is ready.
It neither waits for in-flight background discovery nor infers from a manifest
reference name or qualifier alias. The gap does not by itself disable Completion,
Hover, or Rename of a non-module target; those features retain their established
best-committed-catalog behavior.

Every conclusive semantic name conflict returns the existing
`sameScopeCollision` reason rather than introducing a project-specific top-level
failure. `error.data.conflicts` is always present and contains the complete
conflict set; the unreleased singular `collisionKind` shape is not retained.
Every entry carries `collisionKind` and authoritative `name`. A
`sourceDeclaration` entry also carries its `uri` and `range`, and a
`referencedProject` entry carries its manifest `referenceName`. Entries are
ordered by stable project declaration order for source conflicts, followed by
the containing project and then active reference-selection order. The
actionable message names every conflict in the same order rather than reporting
only the first and forcing repeated Rename attempts. A single containing-project
or referenced-project conflict uses the stable forms
`Module name '<name>' conflicts with containing VBA project '<project>'.` and
`Module name '<name>' conflicts with referenced project or object library '<project>'.`

An already-existing `validation.moduleIdentityNameConflict` does not remove the
source-owned `ModuleIdentity` as a Rename target. The source definition remains
resolvable so a Rename to a non-conflicting name can repair externally edited
source; an ordinally unchanged request retains the general successful no-change
behavior rather than pretending to repair the diagnostic.

One `RenamePlan` is computed from the immutable `VbaProject` snapshot fixed at
the Rename request start. Then-current unsaved editor contents are authoritative
members of that snapshot and Rename neither saves them nor substitutes disk
contents. If any participating source changes while the plan is being prepared,
the request fails with `resourceOperationConflict` and `sourceChanged`, asking
the user to run Rename again. It does not automatically replan against a newer
snapshot because the original position, target, conditional relationships, or
name bindings may no longer denote the operation the user initiated.
Complementary Property Get, Let, and Set accessors with the same property
identity form one logical target family and rename atomically. A distinct
case-insensitive declaration in the same VBA declaration scope or namespace
rejects the plan.

For a renameable source-owned `ModuleIdentity`, Prepare Rename may start from
any resolved `ModuleIdentityOccurrence`. The unquoted payload of
`Attribute VB_Name` is its declaration occurrence even though the exported
syntax uses quotes; its Prepare range excludes the quotes and its placeholder
is the canonical `ModuleIdentity`. Resolved type occurrences such as
`Implements IFoo`, `As IFoo`, and `New Customer`, standard-module qualifiers
such as `Module1.Run`, predeclared/default-instance qualifiers such as
`UserForm1.Show`, and a conclusive source-interface prefix in an implementation
declaration initiate the same logical Rename. Ordinary string literals remain
non-targets.

The file-name fallback used when `Attribute VB_Name` is absent is analysis
recovery, not mutation authority. Such a source has no explicit declaration
occurrence from which a meaning-preserving identity edit can be derived, and
the behavior of importing attribute-less source is not a documented contract
on which Rename should rely. Prepare Rename therefore fails with
`moduleIdentityNotExplicit`; it does not insert export metadata or rename only
the file. The user must re-export or explicitly repair the source metadata
first. Ordinary Explorer file Rename remains outside semantic Rename.

A procedural module with duplicate `VB_Name` records, or any module with a
misplaced, malformed, or invalid-valued `VB_Name`-like record, forms
`InvalidModuleIdentityMetadata`, not a first-match identity or a filename
fallback. Prepare Rename fails with `moduleIdentityInvalid` and a `duplicate` or
`malformed` condition, while syntax diagnostics direct the user to re-export or
repair the metadata. An otherwise valid quoted identifier exceeding the same
31-code-point module-name ceiling is malformed. By contrast, MS-VBAL permits a
class or form module header to repeat valid `VB_Name` attributes: the last value
is the authoritative `ModuleIdentity`, and earlier
`ShadowedModuleIdentityMetadata` records are neither Prepare Rename targets nor
Rename edits. Rename does not promote the filename while invalid identity
evidence is present.

An object-variable member access such as `foo.Member` does not contain an
occurrence of `IFoo` merely because `foo` has type `IFoo`: `foo` denotes the
variable and `Member` denotes the member. Rename therefore follows those
resolved identities rather than manufacturing a type-name occurrence from the
receiver's static type.

For a project-local source file whose basename equals the old
`ModuleIdentity` under case-insensitive comparison, the same semantic
`RenamePlan` renames the `.bas`, `.cls`, or `.frm` basename in place to the new
identity. A matching `.frx` sidecar follows its `.frm` as the same form source
unit. When the existing basename differs from the old identity, Rename preserves
that deliberate path spelling rather than guessing a file rename. Explorer F2
remains an ordinary filesystem rename and never implies semantic
`ModuleIdentity` Rename. An intentional case-only identity Rename also changes
the final basename casing to the requested spelling on a case-insensitive
filesystem; how the language server safely realizes that transition is an
internal concern rather than a different user-visible operation.

Project-local source includes both an unowned source inside a manifest-backed
`DocumentSourceSet` and source inside an `AdHocVbaProject`. Ad-hoc Rename uses
the same explicit-identity, one-folder semantic-collision, basename-following,
same-directory `.frm`/`.frx`, client-capability, and resource-conflict rules. It
does not infer CommonModules ownership or a host projection merely because no
manifest exists.

File-following `ModuleIdentity` Rename is available only when the LSP client
advertises both ordered `documentChanges` and the `rename` resource operation.
Otherwise the operation fails at Rename entry with `clientCapabilityMissing`;
the server never omits the required file operation and returns text edits alone.
For a capable client, the server preflights source and destination existence,
case-insensitive destination collisions, required form sidecars, and the complete
semantic edit set before it returns one ordered `WorkspaceEdit`. File Rename
does not overwrite an existing destination or ignore a collision.

A preflight source-unit or destination conflict returns
`resourceOperationConflict` and no edit. Its structured
`error.data.condition` is `sourceMissing`, `sourceChanged`,
`destinationExists`, or `sidecarConflict`, and the error identifies the affected
path and repair guidance. This filesystem planning failure is distinct from a
VBA `sameScopeCollision`, from `clientCapabilityMissing`, and from a later
`WorkspaceEditApplicationFailure`; the stable top-level reason does not expand
for every file condition.

Atomicity here is a `RenamePlan` guarantee, not a promise that the client can
roll back arbitrary filesystem failure. The server returns every required text
and resource operation or no plan. A destination, permission, or filesystem
provider may still change after preflight, and a client whose advertised failure
handling is not transactional for resource operations may leave earlier edits
applied. That operational state is `WorkspaceEditApplicationFailure`, distinct
from semantic plan rejection; the observing client owns Undo, retry, and repair
guidance rather than relying on server-side rollback.

A manifest-listed `InstalledCommonModule` is the managed-source exception. Its
`ManagedModuleIdentity` cannot initiate semantic Rename from its attribute,
references, qualifiers, or dependent declaration segments. Rename does not
rewrite the installed entry's `name` or `moduleFile`, mutate the configured
`CommonModulesRepository`, or silently detach the source from dependency and
publish-classification ownership. The identity must instead be renamed in the
canonical CommonModules source or explicitly detached into project-local source
first. This exception does not freeze ordinary member Rename or local content
edits inside the installed copy. Because the semantic occurrence and its owner
are known, Prepare Rename fails actionably with `managedModuleIdentity` rather
than returning `null`.

`HostManagedModuleIdentity` is the other source-identity exception. A form
source currently associated with a current `HostClassProjection` cannot
initiate ordinary source Rename because editing `Attribute VB_Name`, references,
and `.frm`/`.frx` paths would not rename the source-template component that owns
its `HostClassIdentity`. Last-known-good evidence alone cannot prove that the
association has ceased, so a non-no-op identity Rename fails with
`analysisIncomplete`. A form conclusively outside host association, including
an ad-hoc `.frm`, remains an ordinary project-local `ModuleIdentity` and follows
the file rule above. Current host ownership makes Prepare Rename fail with
`hostManagedModuleIdentity`; last-known-good-only ownership makes it fail with
`analysisIncomplete`.

An intrinsic document module such as `ThisWorkbook` or a sheet code module is
always host-managed even if a future source Adapter exposes it to the language
server. Its component identity and CodeName remain source-template-owned and do
not become a source F2 target. Renaming either an associated form or an intrinsic
document module requires a separate workbook-backed refactoring that updates
the template-owned component and then re-establishes host projection evidence.
Prepare Rename therefore fails with `hostManagedModuleIdentity` and explains
that ownership boundary.

All variants of a `ConditionalDeclarationFamily` also form one Rename target
and change atomically, including variants declared in separate
`#If...#End If` blocks. Rename preserves each variant's conditional branch path
and possible-definition membership. A better-matching
`ConditionalCallableFamily` signature at one call site never narrows the Rename
target below the complete family. Property-accessor and conditional-family
membership are orthogonal; the plan follows both relationships while retaining
every accessor's branch path. It fails closed when the pre- and post-Rename
target, conditional-call compatibility, or possible-definition sets cannot be
compared completely.

Variant visibility also does not narrow the Rename target. `Public`, `Private`,
and `Friend` alternatives remain one family and Rename changes every physical
variant atomically, even when the initiating use can access only some of them.
Visibility continues to constrain Name and Call Resolution at each use; it does
not split refactoring identity.

Prepare Rename on a call occurrence bound to a
`ConditionalCallableFamily` returns that occurrence's identifier range and the
family's `ConditionalFamilyCanonicalName`. That presentation spelling comes
from the first physical variant in stable project declaration order, independent
of visibility, the initiating use, or the displayed active signature. F2
therefore targets every physical family variant and every resolved family
occurrence, even when that call's arguments rank or fit only some signatures. It
never renames only the displayed `activeSignature`; the conditional directives
and each variant's branch membership remain unchanged. A
`ConditionalDeclarationFamily` composed of `WithEventsHandlerCandidate`
definitions classified `resolvedHandler` or `nonSubProcedureAssociation` is the
exception when every physical variant has one of those classifications:
`ConditionalDependentRenameCoverage` is `completeDependent`, the family is a
`DependentRenameTarget`, and ordinary complete-name occurrences do not initiate
Rename.

The family is never split by candidate role. A physical
`ordinaryProcedure` or conclusive noncandidate declaration makes coverage
`conclusiveMixed`; Definition and References still retain every family variant,
but a non-no-op upstream Event or `WithEvents` variable Rename fails with
`resolutionChanged`. It neither renames the unrelated variant nor derives edits
for only the candidate subset. When there is no conclusive mixed variant but at
least one `indeterminateCandidate` or otherwise incompletely classified
recovered variant, coverage is `indeterminateCoverage` and Rename fails with
`analysisIncomplete`. A conclusive mixed variant takes precedence when both
kinds of evidence exist. Prepare Rename may still identify a proven variable
prefix or convergent Event suffix; the requested Rename performs this
family-wide proof before emitting edits.

For an Event family, the same target includes every Event declaration,
`RaiseEvent` identifier reference, and event-name identifier range within a
resolved `WithEventsHandlerCandidate` name whose
`HandlerEventRenameConvergence` succeeds. Candidate recognition is
syntax-role-based. A Sub, Function, or individual Property accessor declaration
in the same class module as its matching module-level variable or conditional
variable family can first form a procedure-kind-independent
`WithEventsHandlerCandidate`.
`WithEventsHandlerNameDecomposition` first splits the complete declaration
identifier at its final ASCII underscore. Both parts must be nonempty valid
identifier forms. The variable prefix may contain underscores or non-ASCII
identifier characters, while the Event suffix cannot contain an underscore.
The split never depends on declared variables, Event catalogs, conditional
branches, or signature metadata. The prefix then binds the complete variable
target. That target is candidate-admitted only when at least one syntactically
admitted `WithEvents` variant has `eligible` or `indeterminate`
`WithEventsTypeEligibility`. Its `WithEventsEventBindingSet` retains every
included variable-variant-to-Event-target association without merging distinct
Event identities. A `RecoveredWithEventsVariableDeclaration` and a variant with
conclusive-invalid type eligibility contribute no entry; a type-indeterminate
variant contributes one `indeterminate` entry before suffix lookup. A resolved
external Event association can originate only from the declared coclass's
unique default `FDEFAULT | FSOURCE` `TypeLibEventSurface`, and an
already-written candidate resolves its suffix through
`TypeLibExistingHandlerRecognitionSurface`, including a structurally known
hidden or restricted member that is absent from ordinary handler authoring.
Non-default source interfaces do not create competing associations. A resolved
Function or Property candidate contributes the same prefix and suffix
Definition, References, and dependent-Rename projections as a valid handler,
although it remains `nonSubProcedureAssociation` rather than becoming a
`WithEventsHandlerDeclaration`. A procedural-module declaration, declaration in
another class, invalidly decomposed name, target without a binding-admitting
variant, or ordinary same-spelled occurrence contributes no candidate
projections.
The `WithEvents` variable-name prefix is a separate semantic occurrence and is
not changed by Event Rename. A candidate's parameter list and procedure kind
never narrow the target to one Event variant.

Definition and References may retain several distinct Event identities for one
candidate suffix, but Rename does not turn them into a synthetic target.
`HandlerEventRenameConvergence` succeeds only when at least one binding entry is
`resolved`, every resolved association identifies the same source-owned logical
Event `RenameTarget`, and no entry is `indeterminate`. Physical Event variants
within one `ConditionalDeclarationFamily` identify that one family target. A
configuration-dependent `HostEventShadowing` pair does not converge because its
source family and projected host Event remain distinct identities. A source
Event Rename whose shared dependent handler has that pair fails atomically with
`analysisIncomplete`; it neither omits the handler nor changes the intrinsic
host Event. A
`notWithEvents` entry is neutral because that variable variant cannot receive
Events. A `notEvent` entry is also neutral because its type-eligible class
conclusively lacks that suffix Event and has no competing Event binding. In both
cases, the dependent procedure, Property, or conditional-family Rename
preserves ordinary references. Distinct
Event identities, a resolved external Event that is not a `RenameTarget`, or
incomplete resolution fails convergence. Prepare Rename on the suffix exposes
the Event target only after this proof; it never chooses one resolved Event or
proposes renaming several unrelated Events together.

Replacing the event-name suffix also changes the candidate's complete procedure
or Property name. Same-named, same-scope, all-conditional candidate declarations
form the existing `ConditionalDeclarationFamily`, not a separate handler-family
kind. Complementary Property Get, Let, and Set accessors retain their existing
Property identity across that conditional relationship. The `RenamePlan`
therefore adds a dependent procedure-, Property-, or conditional-family Rename
for every candidate classified `resolvedHandler` or
`nonSubProcedureAssociation`: it derives the new complete name from the
unchanged `WithEvents` prefix and
requested Event `RenameName`, edits every physical declaration in the dependent
logical target, and edits every ordinary occurrence bound to that target. This
dependency does not make the procedure, Property, or conditional family a
variant or member of the Event family.

The proof is symmetric for Rename initiated from an Event declaration or
another occurrence of that Event target. Every dependent candidate suffix to be
edited must converge specifically on the initiating Event target. If the same
suffix also associates with another resolved Event identity, the plan rejects
the dependent edit rather than breaking that other binding. An
`indeterminate` entry likewise rejects the complete plan; `notWithEvents` and
`notEvent` entries do not. Definition and References remain unchanged by this
Rename-only proof.

A Rename of a module-level `WithEvents` variable applies the symmetric rule to
every `resolvedHandler` or `nonSubProcedureAssociation` candidate owned by that
variable. The plan changes the variable declaration and its ordinary references,
replaces the candidate declaration-name prefix in every physical dependent
variant with the requested variable `RenameName`, preserves each Event suffix,
derives each new complete procedure or Property name, and changes every ordinary
occurrence bound to each dependent logical target. It does not rename any Event.
A syntax-invalid `RecoveredWithEventsVariableDeclaration`, whether invalid by
placement or declarator shape, retains ordinary variable Rename participation
but supplies no `WithEventsEventBindingSet` entry or dependent relationship of
its own. A syntactically admitted declaration with conclusive-invalid
`WithEventsTypeEligibility` follows the same Rename rule without becoming a
recovered declaration. If either belongs to a `ConditionalDeclarationFamily`,
Rename still targets the complete family and includes any dependent edits
established by a sibling whose type eligibility is `eligible`. A standalone
recovered or conclusive-invalid declaration, or a family without a type-eligible
sibling, produces no handler-prefix or other dependent candidate edit from that
variant. A type-indeterminate declaration instead contributes an
`indeterminate` binding entry. Any such entry prevents
`HandlerEventRenameConvergence`; when no entry resolves and the candidate is
therefore `indeterminateCandidate`, an upstream variable Rename fails with
`analysisIncomplete`. Mixed resolved and indeterminate entries retain the
existing resolved projections and dependent-edit rules without treating the
indeterminate Event as resolved.

An ordinary occurrence of the complete candidate name binds the original
procedure, Property identity, or complete `ConditionalDeclarationFamily`,
rather than its Event suffix. Definition of a conditional candidate family
returns every physical declaration, and References retain every family-bound
complete-name occurrence without selecting a branch. A declaration-name
occurrence alone carries the three simultaneous projections: complete original
definition, `WithEvents` variable reference in the prefix, and Event reference
in the suffix.

The original procedure, Property identity, or complete conditional candidate
family is a dependent-only Rename target. Prepare Rename on any physical
declaration prefix selects the `WithEvents` variable, and Prepare Rename on its
suffix selects the Event only when `HandlerEventRenameConvergence` succeeds. The
separating underscore and every ordinary occurrence bound to the complete
candidate target return no Prepare Rename target. A direct Rename request for
the complete target fails with `notRenameTarget`; a nonconvergent suffix fails
closed without target selection. The server does not reverse-infer a variable or
Event Rename from a requested complete candidate name. Every physical
declaration and ordinary target reference remains editable only as a derived
change in an initiating Event or `WithEvents` variable `RenamePlan`.
Deliberately detaching a `nonSubProcedureAssociation` Function or Property from
the Event relationship requires a manual edit or separate repairing Code Action.

A source procedure or Property logical target that is conclusively associated
with a member contract of an applicable `Implements` relationship is also a
`DependentRenameTarget`. Its complete `IFoo_Bar`-style name remains the ordinary
definition and reference identity for navigation and binding, but it cannot
initiate an independent Rename because changing that name alone would alter or
sever interface fulfillment.

When the source interface type is a `RenameTarget`, its `RenamePlan` changes the
type declaration and resolved occurrences, the applicable `Implements` type
occurrence, and the prefix of every conclusively associated implementation
target. When the source interface member is a `RenameTarget`, its plan changes
that member and resolved occurrences and the suffix of every associated
implementation target. For an `InterfaceVariableAccessorContract`, the owning
Public-variable logical target supplies that member Rename and drives every
derived Get, Let, and Set implementation suffix. Each dependent edit expands
complementary Property accessors, conditional declaration variants, and
ordinary complete-name references atomically under the same hypothetical-project
proof. A direct Rename request for the complete implementation target fails with
`notRenameTarget`; deliberate detachment requires a manual edit or a future Code
Action.

At a physical implementation declaration whose applicable source contract and
upstream targets are conclusive, Prepare Rename projects two semantic segments
from the one written identifier. A request within the interface prefix returns
only that prefix range and the source interface type family's canonical name as
its placeholder. A request within the member suffix returns only that suffix
range and the source interface member family's canonical name; a derived
`InterfaceVariableAccessorContract` instead uses the owning Public-variable
family's canonical name. The semantic separator underscore between those
segments returns no target. This boundary comes from the resolved `Implements`
contract rather than a final-underscore text split, so underscores that belong
to either upstream identifier remain inside that segment. An external,
unresolved, ambiguous, or otherwise non-source-owned upstream identity returns
no Prepare Rename target rather than choosing or manufacturing one.

An ordinary occurrence bound to the complete implementation procedure,
Property identity, or `ConditionalDeclarationFamily` carries no interface-type
prefix or interface-member suffix occurrence. Prepare Rename therefore returns
no target at every character in that complete name; Definition and References
continue to use the complete implementation identity. Only an initiating source
interface type or member `RenamePlan` changes the ordinary occurrence as an
atomic derived edit. A bypassed non-no-op Rename request for the dependent
complete target fails with `notRenameTarget`, while an ordinally unchanged
request follows the general successful no-change rule.

An `IntrinsicHostHandlerCandidate` is different from that external dependent
target: it has no renameable upstream source Event or `WithEvents` variable, so
it is a fixed host-contract name and never becomes a `DependentRenameTarget`.
With current `HostClassProjection` evidence, Prepare Rename returns no target
from its prefix, underscore, Event suffix, complete declaration, conditional
family variant, Function or Property association, or ordinary complete-name
reference. A direct non-no-op Rename of the complete target fails with
`notRenameTarget`, including a case-only change; the general ordinally unchanged
request still returns successful `null`. With last-known-good evidence only,
the association remains advisory and the same non-no-op mutation fails with
`analysisIncomplete`. When neither current nor last-known-good evidence forms
the candidate, the procedure remains ordinary and follows normal Rename rules.
Intentional detachment requires a manual edit or a future Code Action rather
than meaning-preserving Rename.

The hypothetical-project proof covers either initiating Event or `WithEvents`
variable Rename and every dependent candidate-target Rename as one atomic edit.
Every containing conditional family must first have `completeDependent`
coverage. `conclusiveMixed` fails with `resolutionChanged`; only absent that
conclusive evidence can `indeterminateCoverage` fail with
`analysisIncomplete`. A derived complete name that collides in its declaration
scope fails with `sameScopeCollision`; a changed binding fails with
`resolutionChanged`; and incomplete candidate ownership or reference analysis
fails with `analysisIncomplete`. In particular, every
`WithEventsHandlerCandidate` whose
prefix binds an initiating `WithEvents` variable target is examined. If any such
declaration remains an `indeterminateCandidate` after conclusive mixed coverage
has been ruled out, the complete variable Rename fails with
`analysisIncomplete`; the plan neither
leaves a potentially latent Event relationship unchanged nor guesses a
dependent Rename for what may be an ordinary procedure. After later evidence
classifies the declaration as `resolvedHandler` or
`nonSubProcedureAssociation`, its dependent logical-target Rename is included;
after classification as `ordinaryProcedure`, the procedure remains unchanged. A
`nonSubProcedureAssociation` Function or Property retains
`validation.eventHandlerMustBeSub` after the derived Rename only when every
resolved Event target has `sourceDeclared` or `currentHostProjected`
`EventHandlerValidationAuthority`; changing the upstream name does not repair
procedure kind. An external TypeLib or last-known-good host association remains
diagnostic-free before and after the edit.
Textually similar procedures and occurrences that are not semantically related
remain unchanged.

Family formation does not normalize existing casing. An ordinal match with the
family canonical name remains a successful no-change result. Any
case-insensitive but ordinally different request is an explicit case-only Rename
and rewrites every family declaration and resolved occurrence to the requested
spelling, including a request that matches a noncanonical variant's prior
spelling.

The plan then evaluates the hypothetical renamed project through the same
`NameResolution` rules used by editor features. Every edited target occurrence
must continue to resolve to the renamed logical target. Every pre-existing
non-target semantic occurrence must retain its former binding or its former
unresolved or ambiguous classification. Unrelated pre-existing invalid or
ambiguous source does not reject a Rename by itself.

Same-named public members in different modules are therefore not a
project-wide collision by existence alone. A plan may proceed when
qualification keeps every actual occurrence stable, and it fails when an
unqualified occurrence becomes shadowed, ambiguous, unresolved, or bound to a
different definition.

A well-formed `textDocument/rename` request that cannot satisfy this contract
returns LSP `RequestFailed` (`-32803`) with an actionable message and a stable
`error.data.reason`. Initial reasons are `invalidName`, `notRenameTarget`,
`sameScopeCollision`, `resolutionChanged`, `analysisIncomplete`,
`moduleIdentityNotExplicit`, `moduleIdentityInvalid`, `managedModuleIdentity`,
`hostManagedModuleIdentity`, `clientCapabilityMissing`, and
`resourceOperationConflict`.
`InvalidParams` (`-32602`) is reserved for malformed protocol fields or field
types. Prepare Rename returns `null` when the occurrence has no semantic target;
when the semantic occurrence is known but identity ownership or incomplete
authority prevents mutation, it returns the corresponding actionable failure.
Rename returns successful `null` only when the requested name is ordinally
identical to the target's canonical name.

## Considered Options

- Check only for a duplicate declaration in the target's immediate scope. This
  misses changes to non-target occurrences caused by shadowing or precedence.
- Reject any same-named declaration anywhere in the project or referenced
  libraries. This prevents safe qualified Rename operations and does not model
  VBA namespaces accurately.
- Return edits first and rely on a later compile or user review. Rename is
  expected to be a safe editor refactoring, and compilation alone cannot prove
  that an existing valid occurrence retained its meaning.
- Reverse-infer an Event or `WithEvents` variable Rename from a requested
  complete candidate procedure or Property name. The same string can imply a
  narrow definition edit, a variable Rename affecting every candidate, an
  Event-family Rename, or both upstream targets, so this would make Rename scope
  surprising and ambiguous.
- Treat a `nonSubProcedureAssociation` Function or Property as an independently
  renameable ordinary definition. VBE associates its name with the Event in the
  code-window dropdown even when an external TypeLib Event does not receive a
  procedure-kind compile error; an independent Rename would silently detach
  that relationship rather than preserve meaning. Manual
  editing or a separate repairing Code Action can make that intentional change.
- Treat a conclusively recognized `Implements` implementation name as an
  independently renameable ordinary definition. This would silently detach it
  from the interface contract while leaving the upstream type or member name
  unchanged, and would make refactoring scope depend on the cursor location.
- Split a mixed-role `ConditionalDeclarationFamily` into candidate and
  noncandidate targets for Rename. Family identity, Definition, References, and
  possible-definition sets would then change only for the refactoring operation.
- Rename every physical member of a mixed-role family to preserve its family
  identity. That would change an ordinary variable, procedure, or other
  declaration solely because a different conditional variant is Event-related.

## Consequences

Rename planning depends on stable logical definition identities and a semantic
comparison before and after the hypothetical edit. The comparison may be
implemented with a bounded affected-name analysis, but that optimization must
produce the same decision as the complete semantic contract.

`ConditionalFamilyIdentity` is semantic only within one immutable project
snapshot and does not use source ranges or conditional-directive offsets as its
family key. Incremental analysis can reuse identity only when unchanged
ownership is proven. The plan records explicit correspondence between the
pre-edit target and the hypothetical post-edit target rather than depending on
raw identity equality across snapshots.

Property accessor families cannot be edited independently, including when their
Property identity is a dependent target of an Event or `WithEvents` variable
Rename. Existing unresolved or ambiguous occurrences remain allowed only when
Rename leaves their classification unchanged. The presence of a same-spelled
external or cross-module definition is evidence to inspect, not an automatic
rejection.
Clients can present a useful failure message without parsing it, while tests
and first-party integrations can branch on the stable Rename failure reason.
