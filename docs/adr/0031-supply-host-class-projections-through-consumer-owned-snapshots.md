---
status: accepted
---

# Supply host class projections through consumer-owned snapshots

`VbaDev` produces a `HostClassProjection` only through the read-only,
document-scoped `vba-dev host-class list` command. It follows ordinary project
discovery, selects the requested `ProjectDocument` or the primary document when
`--document` is omitted, defaults to human-readable text, and exposes
schema-versioned JSON through `--format json`; `capabilities --format json`
advertises `featureVersions["hostClass.list"]` as `1.0` and
`commandSchemaVersions["host-class list"]` as `1.1`. These string-valued CLI
contracts are independent from the extension-to-language-server notification
schema `2`, extension refresh generation, and document-local snapshot revision.
The command owns its
inspection invocation and dedicated Excel/VBIDE process, but does not persist
projection state, choose refresh timing, or write the workbook, source,
manifest, or generated host Event members.

The command never opens the source template in place. It creates a unique
invocation-owned `HostClassInspectionWorkspace` and copies the selected source
template into it. To bind COM to the exact newly Job-owned Excel process, the
process bootstrapper may first open one generated macro-free `.xlsx` containing
no project bytes. That bootstrap is used only for process ownership, is neither
inspected nor saved, and is closed and deleted before the private source copy is
opened. The command then sets and verifies force-disabled automation security
and disabled Excel Events, requires zero open workbooks, opens only the private
source copy read-only, and requires exactly one open workbook. It imports no
project source, changes no references, never saves the copy, rereads or rehashes
no original input after the start-time copy, releases its
`AutomationExcelProcess` before workspace removal, and emits no projection when
the copy cannot be prepared.

Machine-readable projection output is held until owned-process release is
proved. Failure to prove release is a command-level failure and emits no JSON
projection. After release, workspace deletion receives bounded retries; if only
deletion remains unsuccessful, the command reports the retained absolute path
as a housekeeping warning while preserving the projection and successful exit
status. Normal failure and cancellation also release the process before
attempting workspace removal.

After request scope is established and process release succeeds,
`host-class list --format json` emits one schema-valid
`HostClassProjectionResult` even when class-local or enumeration failure makes
the command exit nonzero. Each enumerated class is `resolved` or `unverified`;
the required top-level `classEnumerationComplete` is true exactly when the
complete, unambiguous class-identity set was enumerated. Top-level `complete` is
true only when `classEnumerationComplete` is true and every class projection is
`resolved`. The result also carries required request-context fields `project`,
`document`, and `sourceTemplate`: the canonical absolute project root, the
manifest-resolved document name, and the canonical absolute source-template
path selected at invocation start. The consumer may commit `resolved` entries
independently.
Schema `1.1` also carries `vbaProjectName` and
`sourceTemplateFingerprint` as an all-or-nothing pair when inspection can read
a valid actual `VBProject.Name`. The fingerprint is the uppercase SHA-256 of
the exact private-copy bytes inspected. The pair remains in this unreleased
legacy producer and transport during migration, but Module Rename and
project-name diagnostics ignore it. Their containing-project authority comes
only from a request-scoped static `VbaProjectIdentityRead` of the exact current
source-template package; neither a present nor missing host-projection pair can
substitute. Host Event projection remains valid independently of the pair.
Each entry carries a `HostClassIdentity` scoped by the selected
`ProjectDocument` and composed of the projection-supplied `VBComponent.Name`
plus component kind (`form` or `document`). Name equality is case-insensitive
while projection casing is retained. A consumer associates source only through
an explicit matching `Attribute VB_Name` and a compatible component kind; file
name, display sheet name, component ordinal, COM identity, and temporary path
do not participate. A missing attribute, kind mismatch, or template/source name
mismatch leaves that source unassociated and its host Event evidence
`indeterminate`; it is a document-level `HostClassSourceAssociationFailure`
that preserves the current projection and every correctly associated source but
makes `HostClassProjectionStatus` attention-required. It creates no source
diagnostic, Doctor result, automatic Excel retry, or file-name binding. The
ordinary non-host `ModuleIdentity` file-name fallback is unchanged.

The current exported-source collector supplies only `.frm` candidates as
`form`. Exported `.cls` files are ordinary `ClassModule` sources and their
name, `VB_PredeclaredId`, or a matching projection never manufactures
`document` provenance. The `document` association path is reserved for a
future adapter that can supply authoritative document-source provenance.

A source module currently associated with a current projected form class has a
`HostManagedModuleIdentity`. Ordinary source semantic Rename does not change
that identity because editing `Attribute VB_Name`, source references, and form
files cannot rename the source-template `VBComponent` that owns the
`HostClassIdentity`. A last-known-good form association remains advisory for
editor presentation but is insufficient to prove that identity mutation is
safe, so a non-no-op request fails with `analysisIncomplete`. A form
conclusively outside host association remains project-local and may use ordinary
source Rename.

Intrinsic document module identity is always source-template-owned, including
when a future Adapter projects `ThisWorkbook` or sheet code into source. It is
never an ordinary source F2 target. Associated form and document identities
require a separate workbook-backed refactoring that updates the template-owned
component and re-establishes projection evidence rather than silently breaking
source association.

A resolved entry also carries required `intrinsicEventSourceName`, the VBE
Object-box name that qualifies intrinsic handlers in that class. Consumers
derive the complete handler name by joining it, one ASCII underscore, and the
Event name: for example `Worksheet` and `Change` form `Worksheet_Change`,
`Workbook` and `Open` form `Workbook_Open`, and `UserForm` and `Initialize`
form `UserForm_Initialize`. Matching is case-insensitive while projection
casing is retained. This value is projection data rather than
`HostClassIdentity`; the producer establishes it from the VBE-equivalent
object/Event association and never infers it from `VBComponent.Name`, component
kind, source file name, or optional base-type provenance. Failure to establish
it makes the class `unverified` with
`intrinsicEventSourceNameReadFailure` and preserves applicable last-known-good
projection data. Because the command is unreleased, schema `1.1` makes the field
required rather than introducing a compatibility alias or default.

A class identity may occur at most once in one enumeration. Two observations
with the same component kind and case-insensitively equal `VBComponent.Name`
are not coalesced, even when their projected contents match, because the public
identity cannot distinguish their components. The producer omits that identity,
adds top-level `classEnumerationFailure`, sets `complete: false`, and exits
nonzero while continuing to inspect other unique identities. This conflict
alone does not add `inspectionStateUntrusted`; that diagnostic requires
separate evidence that the shared Excel/VBIDE state is untrustworthy. Equal
names with different `form` and `document` kinds remain distinct identities.

A `resolved` projection carries the full Event signatures observed for that
host class as authoritative snapshot data. The language server does not
reconstruct that Event surface from a `VbaProjectReferenceCatalog`. The
projection may additionally carry catalog-resolvable
`HostClassBaseTypeProvenance` for navigation and provenance, but its absence or
failure to resolve against the active catalog neither removes inspected Event
members nor invalidates the resolved projection.

Each Event is serialized as structured `HostEventSignature` data rather than a
VBE-style display label. It carries the Event name and ordered parameters,
including each parameter's name, type reference, passing mechanism, array
shape, available `Optional` or `ParamArray` metadata, and optional
documentation. Parameter names and rendered labels are presentation data rather
than Event-handler compatibility identity, and consumers derive their own
labels. The public projection DTO remains independent of the language server's
internal `VbaCallableSignature`, even when the consumer translates between
their equivalent semantic fields.

Within one `HostClassIdentity`, case-insensitive Event name is the unique Event
identity. Same-name observations are not overloads and never become a
`ConditionalCallableFamily`. The producer may coalesce them only when parameter
count, canonical types, passing mechanisms, array shape, `Optional` or
`ParamArray` shape, and both availability values agree. Differences limited to
Event or parameter casing, parameter names, or optional documentation are
presentation differences and are normalized deterministically. Any callable
contract or availability conflict makes the complete class `unverified` with
`eventEnumerationFailure`; the producer does not expose multiple same-name
signatures.

Coalescing retains one complete observed presentation rather than combining
fields from different observations. It first prefers an observation with
nonempty documentation. Among equally documented candidates, it compares the
tuple of Event name, ordered parameter names, and documentation by
`OrdinalIgnoreCase` and then `Ordinal`, and retains the minimum candidate as a
whole. This choice affects only presentation because callable contract and
availability equality have already been established.

Projection order is canonical rather than inherited from COM or VBIDE
enumeration. Class entries are ordered by component kind and then
`VBComponent.Name` using `OrdinalIgnoreCase` followed by `Ordinal`; resolved and
unverified entries are not partitioned by status. Each class's Events are
ordered by Event name with the same comparisons, while parameters retain their
inspected ordinal positions. Human-readable and JSON output use the same class
and Event order, and enumeration ordinals are not serialized.

Membership in a resolved class's Event collection establishes structural Event
existence. Each `HostEventSignature` separately carries required
`authoringAvailable` and `existingHandlerRecognizable` behavior values.
Authoring availability controls ordinary completion and retains eligibility
evidence for future `MemberStubGeneration`; existing-handler recognition
controls association, navigation, and signature guidance for an already-written
handler. The values may differ, including a structurally present Event that is
not offered for authoring but remains recognizable. They are the consumer
contract instead of raw TypeLib flags.

Both availability booleans are required for every Event in a `resolved` class.
The producer does not default unknown evidence to either value: false could
silently hide valid behavior, while true could offer or bind behavior the host
does not support. Failure to establish either value makes the complete class
entry `unverified` and preserves its `LastKnownGoodHostClassProjection`. A
successfully inspected `false`/`false` pair is still a valid resolved Event
because structural existence is represented independently by collection
membership.

Each parameter carries a discriminated `HostEventTypeReference`. An intrinsic
reference uses its canonical VBA type name. A TypeLib reference uses its type
name, compared case-insensitively with display casing retained, together with
the library GUID, major and minor version, and LCID. An unresolved reference
retains its display name but cannot establish canonical equality, even against
another unresolved reference with the same text. Human-visible reference names,
VBA qualifiers, and registry paths are not type-identity fields. This portable
identity is independent of the current internal `VbaTypeReference` name and
qualifier representation.

An unresolved type is valid opaque evidence when inspection successfully reads
the Event and its complete parameter structure but cannot express that type as
an intrinsic or TypeLib identity. It does not by itself make the class
`unverified`: the class remains `resolved`, its Event name and other structural
metadata remain authoritative, and only type compatibility involving that
parameter is `indeterminate`. A metadata read failure, incomplete class or
Event enumeration, or loss of inspection trust instead makes the class
`unverified`. Consequently a top-level result may remain `complete: true` when
its resolved entries contain unresolved type references, while only an
`unverified` entry preserves the previous
`LastKnownGoodHostClassProjection`.

An `UnverifiedHostClassEntry` carries only its `HostClassIdentity`,
`unverified` status, a stable `reasonCode`, and a human-readable `message`. It
does not serialize Event signatures observed before failure or any other
partial `HostClassProjection` payload. A machine consumer therefore has no
best-effort Event data to commit and preserves the last known good class
projection, or remains `indeterminate` when none exists. A future
diagnostic-only observation payload requires a new schema version and remains
separate from authoritative projection data.

The stable class-local reason codes are `eventEnumerationFailure`,
`intrinsicEventSourceNameReadFailure`, `signatureReadFailure`,
`availabilityReadFailure`, `inspectionTimeout`, `inspectionAborted`,
`cancelled`, and the fallback `inspectionFailure`. Messages may carry the Event
name, inspection stage, or other human-readable detail but are not parsed by
consumers. Failure to enumerate the complete set of class identities instead
adds the top-level diagnostic
`classEnumerationFailure`, because no reliable class entry can represent an
unknown omitted identity. Source-template preparation failure and
process-release failure continue to invalidate the invocation rather than
becoming class-local reasons.

After request scope is established, cooperative cancellation emits a
schema-valid terminal result only when process release and serialization
succeed. Entries resolved before cancellation remain resolved; the in-progress
class and every known unprocessed class become `unverified` with
`reasonCode: "cancelled"`. If class enumeration had not completed,
undiscovered classes remain omitted and unknown. The result adds top-level
`operationCancelled`, is `complete: false`, and exits nonzero without also
claiming `classEnumerationFailure` or `inspectionAborted`. Failure to prove
process release or serialize the terminal state leaves no usable JSON.

If shared `HostClassInspectionState` becomes untrustworthy during one class,
the invocation does not start replacement Excel and continue. The causal class
uses its most specific reason, such as `inspectionTimeout` or
`inspectionFailure`; every known later class that was not attempted uses
`inspectionAborted`. Earlier finalized `resolved` entries remain usable only
when their isolation from the failure is established. The result adds top-level
`inspectionStateUntrusted`, is `complete: false`, and exits nonzero. Failure to
prove process release, or inability to prove that earlier results are
unaffected, invalidates the complete JSON result.

`HostClassList` reuses the ordinary 30-second Excel-process-start deadline,
the 300-second workbook-open deadline with its manifest override, and the
5-second cooperative-cleanup grace period. Complete host-class identity
enumeration has one 60-second deadline, and each class receives a fresh
60-second deadline for its complete Event, signature, and availability
inspection. There is no command-wide or per-Event deadline. A per-class
deadline marks the causal class `inspectionTimeout` and every known later
unprocessed class `inspectionAborted`. An identity-enumeration deadline instead
adds top-level `classEnumerationFailure` and `inspectionStateUntrusted`; omitted
classes remain unknown.

`unverified` entries preserve the
`LastKnownGoodHostClassProjection` keyed by the same `HostClassIdentity`, or
leave that class `indeterminate` when none exists. When
`classEnumerationComplete` is true, the consumer removes every previously
committed identity absent from the result, even if a listed class-local failure
makes `complete` false. When it is false, unreported classes remain unknown and
retain any last-known-good projection. Duplicate identity, enumeration timeout,
and cancellation before enumeration completes make it false; a class-local
Event inspection failure does not. Absence carries this deletion meaning
without tombstones, and consumers do not infer enumeration authority from
diagnostic codes. Malformed JSON, schema or request-context mismatch, or
process-release failure invalidates the complete invocation.

`VscodeExtension` owns the background `HostClassProjectionLifecycle` and
supplies committed immutable snapshots to the language server. It binds each
invocation to a monotonically increasing, consumer-local refresh generation for
the selected `ProjectDocument`. A result commits only when that generation
remains current and its `project`, `document`, and `sourceTemplate` context
matches the current selection. A superseded or mismatched result changes
neither resolved projections nor identity deletion state. The generation is
not passed to or serialized by `VbaDev`, and schema `1.1` carries no
consumer-specific request ID, mtime, or inspection timestamp. Its optional
legacy source-template fingerprint identifies the bytes behind the transported
project-name observation but is neither a refresh generation nor Rename or
diagnostic authority. Synchronous editor requests never invoke or wait for inspection; an
unavailable projection leaves host Event evidence `indeterminate`, and an
`AdHocVbaProject` receives no projection rather than inferring one from source
file or module names.

The lifecycle schedules inspection when a project document first becomes
active, when manifest reconciliation adds or removes a document or changes its
effective source-template identity, when the selected source-template file is
created, changed, or deleted, and when the consumer explicitly requests a
refresh. Removing the document or changing to a different source-template
identity advances the generation, cancels in-flight work, and removes the old
projection before any new refresh. A content change at the same template path
advances the generation but preserves last-known-good state if refresh fails;
temporary absence at that same path reports unavailable state and also
preserves last-known-good. Exported `.bas`, `.cls`, `.frm`, or `.frx` edits,
reference-selection or catalog changes alone, active-editor changes,
build/test/publish completion, and bin or publish output changes do not trigger
host-class inspection unless they also change the selected source template.

Relevant exported source and manifest changes separately run
`HostClassSourceAssociationReevaluation` against a context-compatible current
snapshot. This source-only operation neither starts Excel nor advances the
projection generation. It recomputes all present form and document source
associations, updates the document's association failures, and clears their
attention state immediately when every association succeeds. If the same
manifest change also changes document or source-template identity, the ordinary
projection lifecycle invalidation and refresh rules take precedence.

The extension uses one `HostClassProjectionRefreshScheduler` across all project
documents and permits at most one running `host-class list` invocation.
Automatic template and manifest activity uses a one-second trailing-edge
debounce; initial activation and explicit refresh bypass it. A selected-template
event advances its document generation and requests cancellation immediately,
then delays replacement inspection. A raw manifest observation instead fences
the matching in-flight result while manifest parsing and effective request-context
resolution are delayed; an unchanged context releases that result without
creating an inspection trigger, while a resolved document or template identity
change advances the generation and schedules replacement. This prevents transient
invalid editor text from clearing state and prevents an old-context result from
committing during manifest reconciliation. Each canonical `ProjectDocument`
retains only its latest pending generation. A new resolved trigger replaces an
older queued request for the same document and requests cooperative cancellation
when that document is already running.
Replacement never starts before the superseded CLI has exited and its owned
Excel process cleanup has completed. Any schema-valid partial result from the
superseded generation is discarded whole. Work for other documents is not
cancelled and remains FIFO; replacing one document's request does not overtake
another document already waiting. Queue time has no deadline. Extension
shutdown cancels the running invocation and drops every pending request.

The lifecycle performs no timer-based automatic retry after a schema-valid
partial or `unverified` result, source-template preparation or Excel automation
failure, timeout, schema mismatch, or process-release failure. Cancellation and
supersession likewise do not create retry work; supersession already has the
newest generation queued. Recovery begins only from a later lifecycle trigger
or explicit consumer refresh, which creates a new generation. Explicit refresh
bypasses debounce but still obeys single-flight scheduling and cleanup
ordering. Host-class schema `1.1` therefore adds no retryability or backoff
fields, and the extension surfaces the failed state and explicit recovery
action instead of repeatedly starting Excel in the background.

The extension contributes `VBA Tools: Refresh Host Events` with command ID
`vbaTools.hostClasses.refresh`. It uses the ordinary project and document
chooser, creates a new generation for one selected document, bypasses debounce,
and enters the same single-flight queue. Its progress notification is
cancellable. Success produces no toast. An explicitly requested failure shows
one error notification with `Show Output`; a background failure never produces
a popup. When explicitly requested inspection succeeds but one or more
`HostClassSourceAssociationFailure`s remain after reassociation, the command and
inspection remain successful but the extension shows one warning:
`Host Events refreshed, but <N> source module(s) could not be associated.` Its
only action is `Show Output`. Background refresh or source-only reassociation
does not show this warning, cancellation remains silent, and an inspection
failure shows only the existing error notification.

Cancelling that explicit progress removes only the same document's queued
request or requests cooperative cancellation of only its running invocation.
It does not cancel another document's running or queued work. If process release
and serialization succeed and the cancelled invocation still owns the current
generation, its schema-valid terminal partial result follows the ordinary
commit contract: resolved entries may commit, cancelled or unverified entries
preserve last-known-good state, and absence removes an old identity only when
`classEnumerationComplete` is true. Invalid JSON, context mismatch, or
process-release failure preserves all prior state. Supersession remains
different because its obsolete generation discards the complete result. User
cancellation ends progress as cancelled and creates neither an error
notification nor automatic retry; remaining degraded state appears through
status and Output.

`HostClassProjectionStatus` uses a status-bar item only while work is queued or
running or attention is required. A completely current state hides it. Queued
or running state shows a synchronization icon and the affected document;
last-known-good use, template unavailability, partial or unverified result, and
invocation failure or `HostClassSourceAssociationFailure` show a warning icon.
A current projection with any source-association failure is therefore not a
completely current consumer state. Hover identifies project, document,
state, whether last-known-good data is active, and the latest reason and
message. Clicking opens VBA Tools Output and never retries implicitly. Output
records generation, project and document, trigger, queue/start/cancel/commit or
discard transitions, resolved and unverified counts, identity deletions, reason
codes, and last-known-good retention. This state does not create source
diagnostics, a dedicated Project Health view, or a `vba-dev doctor` result.

For source-association attention, the status-bar item adds only the total
failure count to its VBA Host Events warning and never embeds a source path.
Hover shows project, document, total count, and counts grouped by association
reason. Output lists every failure without truncation, including source URI,
source kind, the present or missing `Attribute VB_Name`, any corresponding
projection identity, the exact mismatch, and guidance to re-export or repair
metadata. A successful `HostClassSourceAssociationReevaluation` clears this
attention state without running host-class inspection.

The extension does not forward a raw `HostClassProjectionResult` or class
deltas to the language server. It folds current results, last-known-good state,
indeterminate classes, and authoritative identity deletion into one immutable
document-wide `HostClassProjectionSnapshot`. A custom
`vba/hostClassProjectionSnapshot` LSP notification carries an independent
transport schema version `2`, a monotonically increasing document-local
`revision`, canonical project, document, and source-template context, and
`state: "present"` or `state: "cleared"`. A present payload carries
`classEnumerationComplete` and the complete class entry set. It may carry
`vbaProjectName` and `sourceTemplateFingerprint` only together; a cleared
payload carries neither. The language server rejects schema `1`, a half pair,
malformed authority, and unknown payload shapes. Each entry is
`current`, `lastKnownGood`, or `indeterminate`; only current and last-known-good
entries carry a complete `HostClassProjection`. Operational reason messages and
Output history remain extension-owned. During this migration the language
server continues to validate and retain the optional pair for schema
compatibility, but Module Rename and `validation.moduleIdentityNameConflict`
ignore it. The class entries, revision fence, Event semantics, and current form
ownership boundary remain unchanged until the environment-catalog cutover.

The language server atomically replaces the complete document snapshot or
clears it; it processes no class delta or class deletion tombstone. It discards
an older revision or context that does not match its currently resolved project
document. The notification is admitted as a workspace mutation and pending
notifications for the same document coalesce to the greatest revision. An
accepted replacement invalidates the affected project's semantic inventory and
project-aware diagnostics, but synchronous editor requests never invoke or wait
for inspection or notification completion. After language-client start or
restart, the extension first enqueues manifest synchronization and then replays
the latest desired snapshot for every active document.

Snapshot evidence state and semantic authority are deliberately different.
`current` entries are authoritative for Event existence, availability,
signature compatibility, compile-style validation, and meaning-preserving
mutation. A `lastKnownGood` entry may supply advisory completion, hover,
Signature Help, existing-handler association, and navigation from its retained
names and signatures, but the effective `HostClassEventSurface` remains
`indeterminate` for semantic conclusions. Stale evidence does not establish
`invalidNoEvents`, handler incompatibility, result type, or type compatibility,
and a Rename or other mutation that requires current host evidence fails with
`analysisIncomplete`. An `indeterminate` entry supplies no projected Event
candidate. Status reports last-known-good use globally; individual editor items
need no stale annotation.

For external `WithEvents` handler recognition, this maps directly onto
`EventHandlerValidationAuthority`: a current host Event is
`currentHostProjected`, while retained stale evidence is
`lastKnownGoodHostAdvisory`. `sourceDeclared` and `currentHostProjected` are the
only diagnostic-authoritative values; `externalTypeLibAdvisory` and
`lastKnownGoodHostAdvisory` are guidance-only. Either
`validation.eventHandlerMustBeSub` or
`validation.incompatibleEventHandlerSignature` requires every binding entry to
be resolved and every target to be diagnostic-authoritative. A non-resolved
entry or advisory target suppresses both aggregate diagnostics. The incompatible
signature diagnostic additionally requires every retained signature to be
conclusively incompatible; any compatible, indeterminate, or recovered
signature evidence suppresses it.

For an associated intrinsic class, an unguarded current valid source Event
shadows a case-insensitively same-name projected host Event in source Event
resolution and the external `WithEvents` authoring, suffix-resolution, and
signature surfaces. This is source-over-host precedence, not a duplicate
declaration, overload, or coalesced Event: the signatures need not match and no
duplicate diagnostic is produced. The projected Event remains separately
available only as evidence for recognizing the intrinsic host handler, so a
Rename of the source Event changes its declaration, `RaiseEvent` references,
and external handlers but never renames that intrinsic handler. An invalid
`RecoveredEventDeclaration` does not shadow. Last-known-good host evidence
remains advisory and cannot displace the current source declaration. This
separation follows the VBA behavior in which a form-module Event declaration
shadows the form's built-in Event for `RaiseEvent` while the built-in Event
continues to exist independently.

Intrinsic host-handler recognition uses only the projection's
`intrinsicEventSourceName`, one ASCII underscore, and a projected Event whose
`existingHandlerRecognizable` value is true. It never substitutes the component
name, source-file name, component kind, or base-type provenance. Current
projection evidence establishes the association authoritatively; retained
last-known-good evidence preserves it only as advisory guidance.

Recognition remains distinct from external `WithEvents` binding. In the source
module associated with that `HostClassProjection`, a physical Sub, Function, or
Property accessor whose complete declaration name case-insensitively equals
`intrinsicEventSourceName`, one ASCII underscore, and an
`existingHandlerRecognizable` Event name becomes an
`IntrinsicHostHandlerCandidate`. A Sub becomes an
`IntrinsicHostHandlerDeclaration`; a Function or Property accessor becomes a
`nonSubProcedureAssociation`. The candidate has a projected host Event
association but no `WithEvents` prefix reference or
`WithEventsEventBindingSet`, and a same-name source Event never shadows this
intrinsic association.

The complete intrinsic candidate name remains its procedure or Property
definition. Within the declaration-name occurrence, only the Event-name suffix
is an `EventReference` to the projected `HostEventIdentity`; the
`intrinsicEventSourceName` prefix and underscore have no independent reference
or definition target. Hover on the suffix uses the projected signature and
documentation. Definition follows `HostClassBaseTypeProvenance` to a navigable
external Event definition when available and otherwise returns no location
rather than redirecting to the handler. Find References for that host identity
includes every intrinsic or external handler suffix actually bound to the same
projected Event, while `HostEventShadowing` excludes an external suffix bound to
a source Event. Ordinary occurrences of the complete handler name continue to
reference the procedure or complete `ConditionalDeclarationFamily`.

Signature Help inside an intrinsic handler declaration's parameter list
renders one complete handler spelling by joining `intrinsicEventSourceName`,
one underscore, and the projected Event signature. A conditional declaration
does not add `[#If]` when the intrinsic host contract has unconditional
provenance;
ordinary calls to a conditional procedure family retain their existing
procedure-signature presentation. Current and last-known-good evidence provide
the same Hover, Signature Help, Definition, and References projections, with
stale state reported only through the existing lifecycle status.

An `IntrinsicHostHandlerDeclaration` contributes its one projected
`HostEventSignature` to `ResolvedEventSignatureSet` and uses the same
declaration-to-declaration `EventHandlerCompatibility` as an external handler.
A current snapshot assigns `currentHostProjected` authority and may produce
`validation.eventHandlerMustBeSub` for a non-Sub association or
`validation.incompatibleEventHandlerSignature` for a conclusively incompatible
Sub. When that incompatible `HostEventSignature` has no navigable base Event
definition, the primary diagnostic appends its `Expected signature` and
`Mismatches` lines under ADR 0011 rather than hiding the contract, inventing a
related-information location, or creating a virtual definition document. A
last-known-good snapshot assigns `lastKnownGoodHostAdvisory`; it preserves
association, Hover, Signature Help, and navigation but authorizes neither
diagnostic. An intrinsic candidate does not participate in the upstream
dependent-Rename relationship owned by a `WithEvents` variable or source Event.
Instead, current projection authority makes its complete procedure or Property
name a fixed host contract. Prepare Rename returns no target from any declaration
segment, conditional variant, non-Sub association, or ordinary complete-name
occurrence, and a direct non-no-op Rename fails with `notRenameTarget`, including
a case-only change. An ordinally unchanged request retains the general
successful-null result. Last-known-good-only association cannot authorize the
mutation and fails it with `analysisIncomplete`; with no current or retained
association, ordinary procedure Rename rules apply. Deliberate detachment is a
manual edit or a future Code Action.

Same-named, same-scope, all-conditional intrinsic candidates form the existing
`ConditionalDeclarationFamily`; no host-handler-specific family kind is added.
Every physical candidate keeps its own procedure-kind classification and
`EventHandlerCompatibility` result against the same singleton projected host
Event. Current authority permits a diagnostic on each conclusively invalid
physical variant even when a sibling is compatible, while last-known-good
authority suppresses all such diagnostics. Definition and References retain
the complete family without evaluating the `#If` conditions or choosing an
active branch. Because intrinsic handlers have no renameable upstream Event or
`WithEvents` variable, this family never enters
`ConditionalDependentRenameCoverage`.

When the valid same-name source Event is guarded by conditional compilation,
the language server retains its source `ConditionalCallableFamily` and the
projected host Event as distinct configuration-dependent alternatives. It does
not choose an active compilation branch, prove that source declarations cover
every branch, or merge the host Event into the source family; consequently the
host alternative remains possible even for an apparently exhaustive `#If` /
`#Else`. `RaiseEvent` still resolves only the source family and never falls back
to the host Event, while intrinsic host-handler recognition still uses only the
projected Event. An Event Rename whose dependent external handler has this
source/host ambiguity fails atomically with `analysisIncomplete` rather than
renaming only the source-visible subset.

For external authoring, configuration-dependent same-name source and host
Events produce one name-only completion item with `Event [#If]` detail.
Signature Help retains every valid source-family signature and the host
signature as separate entries, applying the same `[#If]` marker to each and
showing neither source/host provenance nor conditional-expression text. Hover
and any Event-contract diagnostic detail project that same conditional state;
the external handler or completion location's own guardedness does not change
it.
`RaiseEvent` remains source-family-only. An already-written external handler
may retain a host signature through `existingHandlerRecognizable` even when
`authoringAvailable` excludes that Event from ordinary completion.

Intrinsic handler declaration-name completion remains name-only. In a source
module associated with the projection, an empty or partially typed `Sub`
declaration-name slot admits `ContractPrefixCompletion` for each
case-insensitively prefix-matching exact `intrinsicEventSourceName` plus one
underscore, such as `Worksheet_`. Selection replaces only the partial name
fragment. The prefix appears only when at least one
downstream `authoringAvailable` Event remains after collision filtering, and no
complete handler name appears beside it at this first stage. Selecting the
prefix enters the same intrinsic `ContractMemberNameCompletion` as typing it
manually. Once the fragment exactly equals the complete host prefix and
underscore, that member stage takes precedence over any longer prefix match.
It replaces only the typed Event suffix without
recasing or replacing the prefix, and every matching `authoringAvailable` Event
contributes an item. Function and Property declaration names, ordinary
expressions, and calls admit no intrinsic Event prefix or member candidate.
Current and last-known-good projection evidence supplies the same advisory
items without an item-level stale marker; an Event that is only
`existingHandlerRecognizable` remains absent from authoring completion. The
intrinsic prefix is unmarked: its downstream existence check does not propagate
Event-level conditionality into the prefix, and a guarded completion location
alone adds no `[#If]` marker.

Candidate filtering excludes the physical declaration currently being edited
from collision lookup, then omits an Event only when completing that name would
conclusively collide in the same declaration scope. A same-name set containing
any unconditional declaration is such a collision, including an unconditional
and conditional pair. All-conditional peers remain eligible as one
`ConditionalDeclarationFamily` without branch evaluation. A declaration in
another scope or indeterminate collision evidence does not suppress this
advisory candidate; later validation owns any problem that becomes conclusive.

Each item presents the complete canonical handler name as its label and `Event`
as its kind detail. Its effective filter text and ordering key are the projected
Event suffix, so a typed suffix such as `Ch` naturally matches and orders
`Change`. The detail pane carries the projected Event signature and available
documentation; the list row stays name-only. A conditional procedure location
does not add `[#If]` while the projected intrinsic host contract has
unconditional provenance. Selection inserts neither parentheses nor parameters. Typing `(`
afterward enters the same complete-handler Signature Help used by an existing
intrinsic declaration.

The language server advertises `_` and space as global completion triggers. An
underscore-triggered request returns intrinsic items only when syntax identifies
a callable declaration-name slot and semantic resolution proves the associated
`intrinsicEventSourceName` prefix. The shared prefix-first contract also admits
a same-class `WithEvents` variable or an interface named by `Implements`; all
other underscore-triggered requests return no candidates. A space-triggered
request returns contract prefixes only in a valid empty callable
declaration-name slot; an intrinsic prefix further requires an empty `Sub` slot
and a surviving `authoringAvailable` Event.
the same trigger independently remains available to Signature Help in call
contexts. Explicit completion retains ordinary behavior, and the client does
not duplicate host, Event-source, or interface semantics merely to predict the
server result.

This work adds no `Add Event Handler` command, Code Action, completion snippet,
or other multi-line member insertion. Automatic procedure creation is deferred
to a separate `MemberStubGeneration` backlog feature that must cover
`WithEvents` handlers, intrinsic host handlers, and `Implements` members under
one mutation contract. The projection retains `authoringAvailable` so that the
future feature can distinguish authorable members without expanding the
current completion, Signature Help, and existing-handler-recognition scope.
