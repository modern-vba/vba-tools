# VBA Tools

VBA Tools provides Visual Studio Code tooling for exported VBA source files and
workbook-backed VBA projects. This glossary defines the domain terms used when
discussing language-server behavior, VS Code integration, and companion command
tooling for VBA.

## Product

**VbaTools**:
The repository-level product area for the VS Code extension, VBA language
server, Test Explorer integration, and companion CLI used for modern VBA source
workflows.
_Avoid_: VBA-LanguageServer, vba-devtools, VbaDev, xls-common-devtools

**VbaLanguageServer**:
The language-server component that provides editor intelligence for exported VBA
source files in VS Code.
_Avoid_: extension, CLI, test adapter

**VbaDev**:
The C#/.NET companion CLI that performs workbook-backed project operations such
as project creation, CommonModules management, reference management, build,
test, publish, export, and environment diagnostics.
_Avoid_: language server, VS Code command, CommonModules package

**VscodeExtension**:
The VS Code extension package that activates VBA language support, launches the
language server, and invokes `VbaDev` for project-level workflows.
_Avoid_: language server, command-line tool

**ToolingCommand**:
A user-facing or automation-facing `VbaDev` command. It should have explicit
inputs, outputs, side effects, and verification behavior.
_Avoid_: script, helper, task

**VbaDevTerminal**:
A VS Code integrated terminal session opened by `VscodeExtension` for direct
`VbaDev` use. It scopes command availability to that terminal environment
rather than treating `VbaDev` as a machine-level PATH installation.
_Avoid_: global CLI install, project command, automatic project creation

**ConsoleEntryPoint**:
The C# entry point that parses command-line arguments, invokes a
`ToolingCommand`, and returns a meaningful process exit code.
_Avoid_: UI, macro, language-server endpoint

**DotNetProject**:
A .NET project that builds `VbaDev`, its tests, or shared implementation
code in this repository.
_Avoid_: workbook project, npm package

**CommonModulesPackage**:
A versioned release artifact produced by `xls-common-devtools`, normally as
`common_modules_repo.zip`, that provides shared VBA source files and a
machine-readable CommonModules manifest consumed by `VbaDev`.
_Avoid_: vendored source, submodule, built-in library

## CommonModules

**CommonModulesRepository**:
A directory named `common_modules_repo` that provides shared VBA source files
for a `WorkbookBackedProject`.
_Avoid_: common-modules-repo, common modules folder, package cache

**CommonModulesRuntimeBaseline**:
The shared VBA source files required for ordinary runtime use of CommonModules
inside a `DocumentSourceSet`.
_Avoid_: all common modules, test modules

**CommonModulesTestFoundation**:
The shared VBA source files required to author and run VBA unit tests inside a
`DocumentSourceSet`.
_Avoid_: runtime baseline, project-specific tests

**CommonModuleDependency**:
A shared VBA source file that must accompany another CommonModules file for that
file to work inside a `DocumentSourceSet`.
_Avoid_: optional module, copied file list

**CommonModuleName**:
The stable extensionless module base name used to identify one CommonModules
source entry across manifests and tooling.
_Avoid_: file path, file name with extension, display label

**InstalledCommonModule**:
A CommonModules source entry that has been added to one `DocumentSourceSet` and
is tracked as part of that document's desired shared-source set.
_Avoid_: inferred module file, loose copy, reference

**CommonModulesDirectory**:
The `common-modules` organization directory inside a `DocumentSourceSet`. It is
the default placement for CommonModules source files when `VbaDev` needs to copy
an `InstalledCommonModule` source file but no existing same-name source file
already chooses a location. It does not create a separate source set, and
CommonModules installation is still determined by the `ProjectManifest`.
_Avoid_: CommonModulesRepository, source set, installed-module marker

## Workbook Projects

**WorkbookBackedProject**:
A VBA development project that keeps exported source files and one or more
Office macro documents under a project manifest. The initial supported document
kind for workbook-backed automation is an Excel `.xlsm` workbook.
_Avoid_: workspace folder, repository, source folder

**ExplicitWorkbookExport**:
A `VbaDev` export operation scoped by a caller-provided workbook path rather
than by a `ProjectManifest` document definition.
_Avoid_: path-only export, ad hoc export, project export

**ExplicitWorkbookImport**:
A `VbaDev` import operation scoped by a caller-provided source directory and
workbook path rather than by a `ProjectManifest` document definition.
_Avoid_: path-only import, ad hoc import, project import

**ProjectManifest**:
The project-local manifest, stored as `vba-project.json`, that identifies a
`WorkbookBackedProject` and carries default values for VS Code commands and
`VbaDev` operations. It is also the language server's source of truth for the
`VbaProjectReferenceSelection` of each document definition; VS Code settings do
not define project references for workbook-backed projects. A project-local
`project.json` is not a `ProjectManifest` for language-server project-boundary
or reference-selection behavior.
_Avoid_: package file, extension settings, workspace settings

**LanguageServerManifestResolution**:
The lightweight language-server process that reads `vba-project.json` directly and
resolves `ProjectManifest`, `DocumentSourceSet`, and
`VbaProjectReferenceSelection` for editor features. Completion, hover, and
signature help do not synchronously invoke `VbaDev` to resolve project or
reference state.
_Avoid_: CLI-backed completion, command-line manifest resolution, synchronous tooling call

**ExportedVbaSource**:
A `.bas`, `.cls`, or `.frm` text file exported from a VBA project and edited or
analyzed outside the VBE.
_Avoid_: workbook, code blob

**VbaFormSidecar**:
An `.frx` binary sidecar that stores non-text designer data for a `.frm` form.
It belongs to the same form source unit as the matching same-directory `.frm`
file and does not define separate source identity or placement.
_Avoid_: exported source, separate module, generated cache

**PrimaryOfficeDocument**:
The single Office macro document that a `WorkbookBackedProject` treats as the
subject of project lifecycle commands.
_Avoid_: arbitrary workbook, generated output, secondary document

**DocumentSourceSet**:
The exported VBA source files and source template document that belong to one
Office macro document within a `WorkbookBackedProject`. Nested organization
directories under the document source path do not create separate source sets;
exported VBA source identity remains flat, and extension-including source file
names must be unique within the source set.
_Avoid_: source folder, document, test suite

**VbaProjectReference**:
A library reference that one Office macro document's VBA project requires to
compile or run. A `DocumentSourceSet` may require zero or more
`VbaProjectReference`s, named by the human-visible library name shown to VBA
developers. Language features may use available `VbaProjectReference`s as the
source of external VBA definitions.
_Avoid_: Reference, .NET ProjectReference, CommonModuleDependency

**MainVbaProjectReference**:
The `VbaProjectReference` that corresponds to the `PrimaryOfficeDocument` kind
and acts as the precedence winner for unqualified external definition names when
multiple referenced libraries provide the same name. For an Excel document, this
is the Excel object library; for a Word document, this is the Word object
library; and equivalent Office document kinds follow the same rule. It is the
expected main reference for the document kind, but it contributes definitions
only when that reference is present in the `VbaProjectReferenceSelection`. Other
equal rank external matches remain ambiguous. Host-generic root names such as
`Application` and `ActiveWindow` come from whichever catalog is the active main
reference and are not synthesized for projects without that reference.
_Avoid_: active reference, preferred library, MainHostApplication

**ProtectedVbaProjectReference**:
A `VbaProjectReference` that Office or VBIDE keeps as part of the workbook's VBA
project and that tooling should not remove during generated workbook
normalization.
_Avoid_: built-in reference, default reference, undeletable reference

**PublishableVbaSource**:
An exported VBA source file from a `DocumentSourceSet` that should be imported
into the distributed Office macro document.
_Avoid_: test-only source, build-only source

**TestOnlyVbaSource**:
An exported VBA source file used for authoring or running VBA unit tests and
excluded from published Office macro documents by default.
_Avoid_: runtime source, publishable source

**PublishExclusionMarker**:
The `'#ExcludePublish` source comment marker that declares a project-local
exported VBA source file as `TestOnlyVbaSource` or otherwise not publishable.
_Avoid_: filename-only test detection, implicit publish exclusion

## Testing

**TestExplorerNode**:
A VS Code Testing API item representing a runnable or discoverable testing scope
for workbook-backed VBA tests.
_Avoid_: test result row, source symbol, command

**TestProcedure**:
A VBA procedure that the workbook-backed test runner can execute and report as
an individual test after a `DocumentSourceSet` or project test run.
_Avoid_: macro, module, assertion

**TestRunError**:
A project-level or document-level failure that prevents a workbook-backed test
run from completing as a normal set of `TestProcedure` outcomes.
_Avoid_: failed assertion, failed test, diagnostic

**TestResultOutput**:
The command-line report emitted by a `ToolingCommand` after running VBA unit
tests for a `PrimaryOfficeDocument`.
_Avoid_: worksheet result sheet, internal test state

**EnvironmentDiagnostic**:
A read/check-oriented `ToolingCommand` that reports whether the local Windows,
Excel, COM, VBIDE, project prerequisites, and reference catalog availability can
support workbook-backed automation and editor intelligence.
_Avoid_: build, test run, repair command

## Language

**VbaProject**:
A set of exported VBA source files that belong to the same logical VBA project. When a source file belongs to a `ProjectManifest` document definition, the project boundary is that document's `DocumentSourceSet`; otherwise the ad-hoc project boundary is the folder containing the active `.bas`, `.cls`, or `.frm` file.
_Avoid_: workspace, repository, package

**AdHocVbaProject**:
A `VbaProject` inferred from exported source files when no containing
`ProjectManifest` can be found. It provides source definitions,
`LanguageVocabulary`, and the always-active `VbaStandardLibraryReference`, but
it has no manifest-controlled `VbaProjectReferenceSelection` and therefore does
not contribute definitions from other external references. Its source boundary
is the active source file's directory, not nested organization directories or
the VS Code workspace root.
_Avoid_: workbook-backed project, default Excel project, settings-backed project

**VbaDefinition**:
An identifiable declaration in a `VbaProject` that editor features can refer to. It includes modules, classes, forms, procedures, properties, constants, variables, parameters, enums, user-defined types, and events.
_Avoid_: symbol, item, thing

**VbaProjectReferenceDefinition**:
A definition supplied by the always-active `VbaStandardLibraryReference` or by
an active manifest-controlled `VbaProjectReference`, rather than by exported
source files in a `VbaProject`. VBA standard-library constants, Office object
model members, Scripting Runtime types, RegExp types, DAO/ADO types, and other
referenced-library members are all `VbaProjectReferenceDefinition`s.
_Avoid_: HostDefinition, ReferenceLibrary, built-in, standard library, external symbol

**VbaStandardLibraryReference**:
The always-active reference-catalog representation of the Visual Basic For
Applications standard library. It is present in every `VbaProject`, including an
`AdHocVbaProject`, independently of `ProjectManifest` reference selection, and
supplies all supported VBA standard-library constants as structured external
definitions. Those definitions retain their declaring owner, definition kind,
declared type, documentation, completion presentation, and semantic-token facts;
their reference origin keeps them outside `RenameTarget`. Its catalog-owned
`VBA` qualifier is available in every project and exposes the standard library's
public root surface, while still following ordinary `NameResolution` so a
higher-rank source definition named `VBA` can shadow it. Its metadata is bundled
with the language server and does not depend on TypeLib discovery, COM registry
state, Office installation state, or `VbaProjectReferenceCatalogLifecycle`.
_Avoid_: language vocabulary, implicit string list, host library

**HostGlobalReferenceDefinition**:
A root-level, read-only property `VbaProjectReferenceDefinition` supplied by an
active `MainVbaProjectReference` and usable without an explicit host object such
as `Application`; its owning main reference determines the host-specific type.
Neither a document kind alone nor an `AdHocVbaProject` activates it when the
owning `VbaProjectReference` is absent. Host-generic names such as
`Application` and `ActiveWindow` are still catalog-derived host globals: Excel,
Word, PowerPoint, and other hosts supply them only when their active main catalog
exposes them. Excel-specific names such as `ActiveCell`, `ActiveSheet`,
`ActiveWorkbook`, and `ThisWorkbook` are available only from the Excel main
reference. Excel `ThisWorkbook` is modeled through this catalog-derived host
global; source files are not promoted to document module instances by matching
the reserved `ThisWorkbook` name, and document module/base-object member merging
is outside this concept. Its hover declaration uses value-reference form such as
`ActiveCell As Range`, not accessor-declaration form such as
`Property ActiveCell As Range`. Host globals whose catalog type is deliberately
unavailable, such as Excel `ActiveSheet`, do not gain guessed member completion
from possible runtime object kinds. Member completion for a typed host global is
derived from the declared catalog type, such as `Workbook` or `Range`; it does
not inspect live host application state, open workbook contents, worksheet names,
or active cell state.
_Avoid_: built-in global, implicit host variable, language intrinsic

**LibraryGlobalReferenceDefinition**:
A root-level `VbaProjectReferenceDefinition` supplied whenever its owning
`VbaProjectReference` is active, independently of which reference is the main
host. VBA standard-library constants such as `vbCrLf` and public
referenced-library enum members such as Excel `xlCenter` are
`LibraryGlobalReferenceDefinition`s; their availability follows the activation
rule of their respective owning references. Enum-member declaration labels use
the catalog-provided declared type and do not infer a contextual enum type from
the use site; for example, Excel `xlCenter` is shown as `xlCenter As Long` when
the Excel catalog records the member type as `Long`.
_Avoid_: language constant, host global, project constant

**ReferenceDefinitionGlobalExposure**:
The catalog-owned classification that determines whether a
`VbaProjectReferenceDefinition` participates in unqualified `NameResolution`.
A library global is exposed whenever its reference is active, a main-host
global only when its reference is the `MainVbaProjectReference`, and an ordinary
member never participates as a root definition. The classification reflects
the referenced library's application-object, library-module, and enum binding
metadata rather than identifier-name or owner-name rules. Generated and bundled
catalogs preserve the same classification. Catalog members that are hidden or
restricted are not ordinary completion roots; they contribute root candidates
only when this classification has explicitly selected them as a library global
or main-host global. A catalog that lacks this classification does not infer
root exposure from owner names, hidden type names, or member names; its ordinary
type and member metadata may remain usable, but root globals are absent until a
refreshed catalog supplies explicit exposure metadata.
_Avoid_: root member, static member, implicit completion

**VbaProjectReferenceSelection**:
The manifest-controlled set of non-baseline `VbaProjectReference`s whose
`VbaProjectReferenceDefinition`s are active for a `VbaProject`, in addition to
the always-active `VbaStandardLibraryReference`. For language-server behavior,
it is resolved from the `ProjectManifest` document definition; source template
documents are not used as a reference-selection input, and VS Code host
application settings do not participate in reference selection.
_Avoid_: HostApplicationSelection, mode, profile, target language

**VbaProjectReferenceCatalog**:
A discoverable, cached, or bundled metadata source that maps active
`VbaProjectReference`s to `VbaProjectReferenceDefinition`s,
`CallableSignature`s, and catalog-owned qualifier aliases. If no catalog is
available for an active reference, the reference remains active but contributes
no external definitions. A legacy catalog may be partially usable when ordinary
type, member, and signature metadata are present while newer exposure metadata
is absent; in that case only `ReferenceDefinitionGlobalExposure` fails closed.
The bundled `VbaStandardLibraryReference` catalog is baseline metadata rather
than refreshable Office TypeLib metadata.
_Avoid_: host catalog, object model snapshot, reference cache

**VbaProjectReferenceCatalogIdentity**:
The machine-readable identity used by a `VbaProjectReferenceCatalog` after a
human-visible `VbaProjectReference` name has been resolved, such as a TypeLib
GUID, major/minor version, LCID, and path. `ProjectManifest` references are not
required to store catalog identities. If one manifest name resolves to multiple
VBA-available catalog identities, the reference is ambiguous until a resolver or
future user choice disambiguates it.
_Avoid_: manifest name, display name, reference description

**VbaProjectReferenceQualifier**:
A catalog-owned qualifier alias that lets a `QualifiedReference` address one
active `VbaProjectReference` explicitly, such as `Excel`, `Word`, or
`Scripting`, and includes always-active standard-library qualifiers such as
`VBA`. It is not stored in `vba-project.json` and is not mechanically derived
from `Reference.Description` alone. It participates in `NameResolution` at
referenced-library rank, so a higher-rank source definition with the same name
shadows the qualifier rather than the qualifier acting as an absolute reference
escape hatch. When used with a trailing dot in completion, it exposes that
reference catalog's public root surface: root-level types, exposed constants,
and explicit `ReferenceDefinitionGlobalExposure` definitions, not hidden owners
or restricted internal members. The exposed surface is still filtered by the
active `CompletionExpectation`, so type, value, and creatable-type contexts see
different role-compatible candidates.
_Avoid_: manifest alias, display name, host name

**VbaProjectReferenceCatalogAvailability**:
The operational state describing whether an active `VbaProjectReference` has a
usable `VbaProjectReferenceCatalog`. Missing catalog availability can be
reported through language-server output, status, or trace and through an
`EnvironmentDiagnostic`, but it does not create source diagnostics by itself.
A catalog with usable ordinary metadata but missing root-exposure metadata is
available for the metadata it can prove, while its root-exposure contribution is
stale and eligible for refresh.
_Avoid_: source diagnostic, unresolved reference, compile error

**ManifestReferenceConsistency**:
The condition that a `ProjectManifest` document definition contains the
references expected for its document kind, including the expected
`MainVbaProjectReference`. Missing expected references are reported through
language-server output, status, or trace and through `EnvironmentDiagnostic`;
they do not cause the language server to implicitly activate references that are
absent from the manifest.
_Avoid_: source diagnostic, auto-added reference, implicit default

**VbaProjectReferenceCatalogRefresh**:
The lifecycle-owned background process that preloads persisted metadata and
discovers TypeLib metadata after project activation or an effective
`VbaProjectReferenceSelection` change. Ordinary VBA source edits do not restart
it. Editor requests use the best committed catalog without waiting for preload
or discovery. Per-reference ownership spans preload and discovery so stale
metadata cannot overtake a newer commit, while an explicit refresh may bypass
lifecycle negative caching for references that are currently free.
_Avoid_: completion-time discovery, source-edit refresh, blocking metadata load

**VbaProjectReferenceCatalogLifecycle**:
The project-scoped C# responsibility that reacts to project activation,
effective `ProjectManifest` reference-selection changes, and deactivation. It
schedules persisted-catalog preload and TypeLib discovery independently from
ordinary VBA source edits. Completion, hover, signature help, and other editor
queries only read committed catalog state and do not wait for in-flight preload
or discovery work to finish.
_Avoid_: source-edit refresh, completion-time preload, per-document catalog reload

**ReferenceSelectionFingerprint**:
The case-insensitive deterministic identity of one effective
`VbaProjectReferenceSelection`, including the document kind, main-reference
state, and normalized reference names. Repeated activation with the same
project scope and fingerprint shares one automatic catalog lifecycle revision.
_Avoid_: document version, manifest version, catalog identity, TypeLib identity

**ReferenceCatalogLifecycleRevision**:
One generation of automatic persisted preload and discovery for a project scope
and `ReferenceSelectionFingerprint`. Missing or unreadable persisted results
are negative-cached only for this revision; an explicit retry or changed
selection may start new work.
_Avoid_: source revision, cache format version, LSP document version

**LastKnownGoodReferenceCatalog**:
The latest usable bundled, persisted, stale-persisted, or generated catalog
revision already committed for a reference. A cancelled or failed refresh does
not replace or remove it; only a later successful atomic commit changes the
editor-facing catalog. While new catalog work is in flight, editor requests use
this committed snapshot when it exists; if no committed snapshot exists for a
reference, that reference contributes no editor candidates until a later request
sees a successful commit.
_Avoid_: in-flight catalog, failed discovery result, source diagnostic

**SyntaxHighlighting**:
Editor coloring for VBA source text. It combines lexical classification for VBA syntax with meaning-aware classification from parsed project information when that information is available.
_Avoid_: color theme, formatting

**SyntaxDiagnostic**:
An editor diagnostic that reports malformed VBA source syntax in a `VbaProject`. A `SyntaxDiagnostic` is about grammar and source structure, not semantic checks such as unresolved `VbaDefinition`s, missing `VbaProjectReferenceDefinition`s, type mismatch, or ambiguous `NameResolution`.
_Avoid_: compile error, semantic diagnostic, runtime error

**VbaValidationDiagnostic**:
An editor diagnostic produced after a source file has been parsed into
`VbaSyntaxTree`, when VBA validity rules can be checked without treating the
source as parser recovery. Duplicate callable parameter names, duplicate
call-site named arguments, and positional arguments after named arguments are
`VbaValidationDiagnostic`s, even when they are published as LSP errors. Some
`VbaValidationDiagnostic`s are document-local, while others require project
state such as `NameResolution`, `TypeResolution`, `VbaProjectReferenceSelection`,
or available `VbaProjectReferenceCatalog`s. Reference-catalog availability,
stale exposure metadata, missing host globals, and host-global assignment
validity are not `VbaValidationDiagnostic`s in the current scope.
_Avoid_: SyntaxDiagnostic, parser recovery diagnostic, raw compiler error

**VbaSyntaxTree**:
The parsed VBA source structure needed for `SyntaxHighlighting`,
`SyntaxDiagnostic`s, and completion candidate discovery while preserving the
syntax structure those editor features depend on. It does not include
compile-time type inference or unresolved-name diagnostics in the current
scope.
_Avoid_: regex scan result, semantic model, compiler

**VbaTokenStream**:
The source-range-preserving lexical token sequence produced before
`VbaSyntaxTree` parsing. It classifies VBA keywords, identifiers, literals,
operators, punctuation, comments, whitespace, newlines, line continuations, and
preprocessor directives so lexical `SyntaxHighlighting` and parser recovery can
continue even when the full syntax tree is incomplete.
_Avoid_: text split, regex match list, semantic token

**ReusableVbaParserCore**:
The parser and syntax model layer that can serve `VbaLanguageServer` editor
features and may later be shared with documentation tooling such as DoxyVB6
without depending on LSP, VS Code, workbook automation, or `VbaDev` command
behavior.
_Avoid_: language-server feature code, DoxyVB6 adapter, workbook parser

**SemanticToken**:
A meaning-aware classification of a source range, derived from parsed `VbaProject` information. `SemanticToken`s refine `SyntaxHighlighting` for declarations and references, using standard editor token categories whenever a VBA meaning can be represented by one.
_Avoid_: syntax token, text token

**SourceFormatting**:
Editor-initiated rewriting of VBA source text to match the language server's
source style. It includes casing normalization and indentation formatting,
while preserving source meaning. Source formatting is fail-closed: incomplete
or malformed source may still receive safe lexical or structural formatting, but
formatting does not guess unresolved names, ambiguous names, or malformed block
relationships.
_Avoid_: syntax highlighting, refactoring

**CasingNormalization**:
A `SourceFormatting` operation that rewrites VBA keywords and identifier
references to their canonical casing. For source-defined names, the declaration
spelling is the canonical casing; formatting normalizes references to that
spelling but does not change the declaration name itself. Identifier reference
casing is normalized only when `NameResolution` resolves the reference to one
definition unambiguously; unresolved or ambiguous names keep their original
casing. Procedure-local `VbaDefinition`s such as local variables and
`CallableParameter`s participate in casing normalization within their visible
procedure scope. `VbaProjectReferenceDefinition`s also participate when their
`VbaProjectReference` is active, a usable `VbaProjectReferenceCatalog` supplies
the definition, and `NameResolution` resolves the reference unambiguously. In a
`QualifiedReference` or `MemberChainResolution` expression, each segment is
normalized only while the corresponding definition can be resolved; once a
segment is unresolved or ambiguous, formatting does not guess casing for that
segment or later segments in the chain. String literals, ordinary comments, and
`DocumentationComment` prose are not casing-normalized even when they contain
text that looks like an identifier.
_Avoid_: rename, spelling correction

**LanguageVocabulary**:
The fixed VBA words whose casing is defined by the language server rather than
by a `VbaDefinition` or `VbaProjectReferenceDefinition`. It includes VBA
keywords, intrinsic types, and literals. VBA standard-library constants are
structured `VbaProjectReferenceDefinition`s supplied by the
`VbaStandardLibraryReference`, not completion-only vocabulary strings.
_Avoid_: host definition, project definition

**CompletionExpectation**:
The syntax-owned description of what may legally follow at an editor position. It is derived from `VbaSyntaxTree`, remains stable across irrelevant trivia, and fails closed when syntax does not establish a valid continuation. A completed grammar marker opens its next slot only after the lexical separator required by VBA, while punctuation operators can open an operand slot immediately. Related position facts may carry canonical contextual statements, syntax words, or module-kind-specific starter words, but contain no `VbaDefinition` or LSP trigger metadata.
_Avoid_: general completion, trigger context

**SyntaxWord**:
A fixed VBA word required by the active grammar transition, such as `Then`, `In`, or a declaration continuation. It is selected by `VbaSyntaxTree` for the exact editor position and is not a general keyword proposal.
_Avoid_: keyword completion, declaration keyword

**CompletionCandidate**:
An editor proposal admitted by a `CompletionExpectation` after semantic resolution. It may originate from a `VbaDefinition`, `VbaProjectReferenceDefinition`, `LanguageVocabulary`, named `CallableParameter`, callable-owned line label, contextual branch statement, or `EndStatementCompletion`. Its insertion and replacement facts are complete before LSP projection, and proposals with the same label remain distinct when their effective insertion text differs.
Candidate discovery reads only the already-admitted `VbaProject` source,
language vocabulary, and committed reference-catalog definitions; it does not
perform TypeLib discovery or reference-catalog refresh during an editor
completion request, and it does not wait for in-flight catalog work. Hidden or
restricted catalog definitions are omitted from ordinary completion unless they
are admitted as exposed root definitions. Reference-qualified completion also
filters exposed root definitions by the active role, so type positions, value
positions, and creatable-type positions do not receive one mixed catalog list.
When same-label candidates differ by semantic role or effective insertion text,
they remain separate and rely on completion kind, detail, and icon metadata to
identify the role. Only candidates with the same label, same effective insertion
text, and equal resolution rank are eligible for the existing ambiguity or
coalescing rules. Editor-facing ordering follows visible-source proximity before
referenced-library candidates: procedure-local, current-module, public project,
then standard or external reference catalog candidates. After an explicit
`VbaProjectReferenceQualifier`, ordering is scoped to that qualifier's admitted
surface and uses existing kind and label metadata without triggering additional
catalog discovery.
_Avoid_: completion definition, raw vocabulary

**QualifierCompletionCandidate**:
A `CompletionCandidate` that helps the user start a `QualifiedReference`, such
as `ModuleIdentity.` or `VbaProjectReferenceQualifier.`. It is not a value,
callable, or type definition by itself; after the qualifier is formed, member
completion owns the next candidate set. Source module qualifiers and active
reference qualifiers use the same qualifier-completion behavior. The displayed
label is the qualifier name without the dot; the inserted text includes the
trailing dot so member completion can continue from the qualified position. It
is admitted only at positions whose `CompletionExpectation` can start a
qualified reference, not after a completed expression or other closed grammar
slot. It remains distinct from a same-name value, callable, type, or constant
candidate because its effective insertion text forms a qualifier.
_Avoid_: module value, namespace object, callable candidate

**PropertyAccess**:
The semantic capability retained when complementary `Property Get`, `Property Let`, or `Property Set` declarations are coalesced into one logical property. Source accessor identity distinguishes a legal Get/Let/Set family from duplicate accessors, while `Readable` and `Writable` capabilities are derived from source accessor kinds or TypeLib invoke metadata. `Unknown` remains loadable for legacy catalogs but admits no context-specific `CompletionCandidate` until refreshed.
_Avoid_: getter flag, setter flag, inferred property mode

**IndentationFormatting**:
A `SourceFormatting` operation that rewrites leading whitespace according to
VBA block structure. It depends on source ranges, tokens, and syntax block
structure rather than `NameResolution`; identifier meaning does not affect
indent depth. Each emitted indentation level follows the resolved editor style:
`indentSize` spaces when spaces are requested, or one tab when tabs are
requested. A formatting client that does not provide `indentSize` uses
`tabSize` as a compatibility fallback. When block structure is incomplete or
malformed, indentation uses only recognized structure and does not infer
repairs for missing block boundaries.
_Avoid_: alignment, line wrapping

**EndStatementCompletion**:
An explicit editor completion candidate that inserts the matching VBA block
closer for a block opener, such as `End Sub`, `End Function`, or `End If`.
_Avoid_: `BlockSkeletonInsertion`, automatic typing, on-type edit

**BlockHeader**:
A complete logical VBA statement that opens a body-owning block with a canonical
matching terminator. It may span multiple physical lines through VBA line
continuations, but only its final physical line completes the header.
_Avoid_: definition line, opener line, declaration row

**BlockDeclarationHeader**:
A `BlockHeader` that declares a body-owning module member or module-level type.
Participating forms are non-external `Sub` and `Function`, `Property Get`,
`Property Let`, `Property Set`, `Enum`, and `Type`.
_Avoid_: block header, declaration line

**BlockSkeletonInsertion**:
An editor action that expands a complete `BlockHeader` after an Enter keypress
at the end of its final physical line into an indented empty body and its
matching block terminator. It does not activate on an intermediate line that
continues the logical header. Every participating form receives the same single
empty body line; the action does not add form-specific placeholders such as an
initial `Case` clause. The action inserts that body line and the terminator at
the caret without consuming or reusing pre-existing following blank lines; such
lines remain unchanged after the new terminator.
It is separate from explicit `EndStatementCompletion` and from
`SourceFormatting`. It participates in `BlockDeclarationHeader`s and in
block-form `If...Then`, `For`, `For Each`, `Select Case`, and `With` statement
headers. A single-line `If`, `While...Wend`, and every pre-condition or
post-condition `Do...Loop` form are outside its scope. Unconditional
`Do...Loop` is also outside its scope. An `Event` declaration does not
participate because VBA events have neither a body nor an `End Event`
terminator. External `Declare Sub` and `Declare Function` declarations also do
not participate because their implementation bodies exist outside VBA. A
matching terminator already owned by a participating header
suppresses the action, but post-header block pairing alone does not establish
ownership when nested blocks use the same terminator. Prefix ancestry and
leading indentation distinguish a candidate-owned closer or branch from an
ancestor boundary: candidate indentation suppresses insertion, ancestor
indentation permits further validation, and ambiguous indentation fails closed.
The action does not move or rewrite an existing body. It scaffolds a fresh
empty block rather than repairing an existing unterminated body. Existing code
or comments that could belong to the body make the header ineligible;
intervening blank lines do not.
A following end of file, same-level block declaration, conditional-compilation
boundary, or known ancestor branch or terminator may establish a safe non-body
boundary. The prospective terminator is speculatively inserted and reparsed; it
is eligible only when it closes the candidate, restores the ancestor boundary,
removes only directly caused missing or mismatched diagnostics, and introduces
no new error. Eligibility is otherwise local and fail-closed: the participating
header must parse completely, be permitted by the current `VbaModuleKind`, and
have no overlapping error-severity `SyntaxDiagnostic` or
`VbaValidationDiagnostic` apart from the directly caused missing or mismatched
diagnostics eliminated by the validated insertion. Warnings and informational
diagnostics do not suppress the action, and unrelated diagnostics elsewhere in
the document do not suppress the action. Trailing whitespace and an apostrophe
comment are header trivia and remain unchanged; they do not make the header
ineligible when Enter is pressed at the actual physical line end. Invalid
trivia, such as a comment after a line-continuation marker, remains a syntax
error and suppresses the action.
Branch headers such as `Else`, `ElseIf`, `Case`, and `Case Else` do not own a
new block and never trigger the action, even when the shared enclosing
terminator is missing.
For both `For` and `For Each`, the inserted canonical terminator is the bare
`Next` statement without a repeated counter or element name.
Any top-level colon in the `BlockHeader`, including a trailing colon, makes the
header ineligible because it expresses same-physical-line statement structure.
Colons inside string literals or comments remain trivia and do not affect
eligibility.
Conditional-compilation directives such as `#If...Then` are not `BlockHeader`s
for this action. An ordinary participating header inside a conditional branch
remains eligible only when its block relationship can be established entirely
within that branch; the action never infers a relationship across `#Else`,
`#ElseIf`, or `#End If`.
The terminator copies the exact leading whitespace of the `BlockHeader`'s first
physical line. The empty body line adds one resolved editor indentation unit to
that prefix: `indentSize` spaces when spaces are requested, or one tab when tabs
are requested. A continued header's final line does not become the indentation
base. Existing header whitespace remains unchanged, and inserted text preserves
the document's line-ending convention. The terminator uses canonical
`LanguageVocabulary` casing independently of the header's spelling; the action
does not recase the existing header.
_Avoid_: `EndStatementCompletion`, source formatting, automatic completion

**DocumentationComment**:
A structured Doxygen-style VBA comment block attached to a `VbaDefinition` regardless of public or private visibility. Hover shows the complete rendered comment. Signature Help presents only the active `CallableParameter`'s `@param` documentation; its protocol metadata retains documentation per parameter so the client can select the active one, but callable summary, details, and return documentation are not projected. Plain apostrophe comments are not `DocumentationComment`s; when an implementation member has no `DocumentationComment`, it may inherit one from the interface member named by its `Implements` relationship.
_Avoid_: comment, note, description

**CallableSignature**:
The structured call shape for a callable `VbaDefinition` or `VbaProjectReferenceDefinition`. It includes the displayed signature label, ordered parameters, optional parameter metadata, parameter passing metadata, parameter type names, default values, return type names, callable kind, named-argument support, and parameter documentation when that documentation is available from source comments or reference catalog metadata. When shown by Signature Help or as a callable hover declaration, the primary label carries the callable kind (`Sub`, `Function`, `Property`, `Event`, or source `Declare` form), available return type, available parameter type metadata, and effective `ByRef` metadata, including implicit VBA `ByRef`, while `ByVal` is omitted even when explicit. Property accessors are collapsed to `Property`, `ParamArray` is shown when available, array parameters keep their `()` marker, optional parameters are represented with brackets rather than the `Optional` keyword, and visibility modifiers and default values are omitted. Reference catalog signatures follow the same rules but show only metadata supplied by the catalog; missing passing, type, callable-kind, or named-argument support metadata is not inferred. Current TypeLib catalogs establish named-argument support from their parameter metadata, while legacy persisted catalogs remain fail-closed and stale so they can be refreshed. TypeLib discovery maps COM invoke kinds, `[retval]` presence, and return-value semantics to explicit callable kinds, and normalizes source-interface members forwarded through a coclass to `Event`.
_Avoid_: parameter list, call text, method shape

**Hover**:
An editor feature that explains the `VbaDefinition` or `VbaProjectReferenceDefinition` under the cursor. It renders the attached `DocumentationComment`, followed by a horizontal separator and a fenced `vba` declaration block. Callable definitions use their rich `CallableSignature`; other definitions use their `DeclarationLabel`. Hover does not expand per-parameter documentation or track an active `CallableParameter`.
_Avoid_: SignatureHelp, tooltip, parameter hover

**SignatureHelp**:
An editor feature that shows the rich `CallableSignature` for a resolved call site and tracks the active `CallableParameter`. It omits callable-level documentation and retains per-parameter documentation. Each LSP parameter label is the complete displayed parameter segment, including brackets, passing metadata, array markers, and type metadata when present.
_Avoid_: hover, tooltip, parameter hover

**DeclarationLabel**:
The editor-facing declaration summary for a non-callable `VbaDefinition` or `VbaProjectReferenceDefinition`, or the fallback when no richer `CallableSignature` is available. Constants, enums, and user-defined types include `Const`, `Enum`, or `Type`. Variables, parameters, enum members, root value properties such as `HostGlobalReferenceDefinition`s, and user-defined type members use declaration forms such as `Name As Type`; arrays keep `()` after the name. External enum members use the catalog-provided declared type rather than a contextual enum type inferred from the call or assignment site. Parameter labels include effective `ByRef` metadata while omitting `ByVal`. `Static` and `WithEvents` are included when they apply, while visibility modifiers and unavailable implicit types are omitted.
_Avoid_: signature, display name, hover text, owner-qualified name

**CallableParameter**:
A declared input slot on a callable definition, such as `Arg1` in `Sub Example(ByVal Arg1 As String)`. It is matched by name or position from a `CallArgument`.
_Avoid_: argument, call argument, local variable

**CallArgument**:
A value slot supplied at a call site, such as `"x"` or `Arg1:="x"` in `Example("x")` or `Example Arg1:="x"`. `CallArgument`s are distinct from `CallableParameter`s and may be positional, named, or omitted.
_Avoid_: parameter, callable parameter, argument text

**StatementFormCall**:
A VBA call form that invokes a callable at statement level without the `Call` keyword and without wrapping the argument list in parentheses, such as `ExampleSub Arg1:=1` or `ModuleName.ExampleSub "x"`. It is distinct from a parenthesized call and from expression uses of a callable name.
_Avoid_: bare call, implicit call, call expression

**NamedCallArgument**:
A `CallArgument` that explicitly names the target `CallableParameter`, such as `Arg1:="x"`.
_Avoid_: named parameter, named callable parameter

**PositionalCallArgument**:
A `CallArgument` matched to a `CallableParameter` by ordinal position rather than by name.
_Avoid_: unnamed parameter, indexed parameter

**OmittedCallArgument**:
An empty positional `CallArgument` slot in VBA call syntax, such as the first slot in `Example(, Arg2:="x")`.
It is still positional for named-argument ordering: `Example(Arg2:="x", )` has an omitted positional slot after a named argument.
_Avoid_: missing parameter, blank parameter

**ReferenceSignatureDiscovery**:
The process of collecting `CallableSignature` and type metadata for `VbaProjectReferenceDefinition`s from an available referenced-library catalog source. It enriches reference metadata so editor features can show accurate signature help without guessing signatures from member names alone.
_Avoid_: HostSignatureDiscovery, COM refresh, member scan, metadata scrape

**RenameTarget**:
A source-defined `VbaDefinition` that can be renamed inside its `VbaProject`. `VbaProjectReferenceDefinition`s, string literals, and `DocumentationComment`s are not `RenameTarget`s.
_Avoid_: renameable symbol, edit target

**ExternalDefinitionNavigation**:
The go-to-definition behavior for `VbaProjectReferenceDefinition`s supplied by
reference catalogs. Until the VS Code extension exposes a read-only virtual
catalog document provider with stable definition identities, external
definitions do not return navigable locations. Hover, completion, rename
rejection, and find-references behavior can still use the structured catalog
definition.
_Avoid_: vba-reference file, generated source file, decompiled definition

**NameResolution**:
The case-insensitive process of matching an identifier reference to the closest visible `VbaDefinition` or `VbaProjectReferenceDefinition`. Procedure-local definitions outrank current-module definitions, current-module definitions outrank public project definitions, and project definitions outrank referenced-library definitions. `HostGlobalReferenceDefinition`s, `LibraryGlobalReferenceDefinition`s, standard-library constants, and reference qualifier names all use referenced-library rank rather than shadowing source definitions. Among referenced-library definitions, a `MainVbaProjectReference` match outranks matches from other active `VbaProjectReference`s.
_Avoid_: lookup, binding, search

**ModuleIdentity**:
The name of an exported VBA module, class, or form as defined by `Attribute VB_Name`. The source file name is only a fallback when `Attribute VB_Name` is absent.
_Avoid_: file name, module file, path name

**TypeResolution**:
The process of matching an explicit VBA type annotation to a `VbaDefinition` or `VbaProjectReferenceDefinition` for member completion and member documentation. Source `VbaDefinition`s outrank referenced-library `VbaProjectReferenceDefinition`s unless the annotation is reference-qualified, and assignment-based inference is outside the MVP.
_Avoid_: type inference, runtime type, guessed type

**MemberChainResolution**:
The process of resolving a sequence of member accesses by carrying each resolved member's declared result type to the next member access. It applies to both source `VbaDefinition`s and `VbaProjectReferenceDefinition`s when result type metadata is available; missing or ambiguous result types stop the chain. Host-object member chains use declared type metadata from the active source and reference catalogs only; they do not inspect a live Office application or workbook state.
_Avoid_: host chain resolution, dotted lookup, chained lookup

**ContinuedMemberChain**:
A `MemberChainResolution` expression written across multiple physical VBA lines using code line-continuation markers. It is one logical member chain for resolution, while each segment keeps its original physical source range for editor features; a leading dot on a continued physical line belongs to this explicit chain rather than to a `WithReceiver`, and comment continuations are not part of it.
_Avoid_: logical line, multiline chain, wrapped chain

**ContinuedArgumentList**:
A parenthesized call argument list that spans multiple physical VBA lines using code line-continuation markers. It keeps signature help active and counts the active parameter across those physical lines, but it does not change `MemberChainResolution` or `ContinuedMemberChain`.
_Avoid_: multiline call, wrapped call, logical call

**WithReceiver**:
The nearest active `With ... End With` expression that supplies the implicit receiver for a leading-dot member chain that is not part of a `ContinuedMemberChain`. Its receiver expression may itself be a `ContinuedMemberChain`; nested `With` blocks use the innermost active `WithReceiver`, and missing or ambiguous receiver types do not produce guessed member results.
_Avoid_: with context, current object, implicit type

**QualifiedReference**:
An identifier reference written with a qualifier, such as `ModuleIdentity.MemberName`, `variable.MemberName`, or `Word.Application`. The qualifier itself follows `NameResolution`; a source definition named `Excel` or `Word` may therefore shadow a same-name `VbaProjectReferenceQualifier`. When the qualifier names a module, class, or form, only public members of that definition are visible from outside that module; when it names an active `VbaProjectReferenceQualifier`, only that reference's public root-surface `VbaProjectReferenceDefinition`s are visible.
_Avoid_: dotted lookup, member access, qualified symbol

**EventReference**:
A reference to an event definition from either a `RaiseEvent` statement or a `WithEvents` handler name. The MVP resolves `RaiseEvent EventName` within the current module and resolves `WithEventsVariable_EventName` handlers through an explicit `WithEvents` variable declaration.
_Avoid_: callback, event procedure, handler lookup

**FormDesignerBlock**:
The non-code designer section of an exported `.frm` file, such as form and control property declarations. The MVP keeps it out of AST definitions and references even though the file itself belongs to the `VbaProject`.
_Avoid_: form code, form module, generated code

**ModuleMember**:
A top-level parsed member inside a VBA module, such as a procedure, property, enum, user-defined type, event, constant, variable, or declaration block. Incremental AST updates use `ModuleMember` ranges as their replacement unit.
_Avoid_: function block, top-level node, parse chunk

**VbaInteractiveWorkScheduler**:
The C# language-server module that continuously admits parsed LSP input while
committing workspace mutations and capturing immutable read state through one
ordered FIFO mutation-and-capture lane. Captured reads execute on a bounded
concurrent executor. The scheduler owns request cancellation, internal latency
and fairness policy, and deterministic stop behavior; it does not make the
TypeScript adapter authoritative for VBA semantics.
_Avoid_: unbounded parallel request executor, extension-host scheduler, request thread

**InputSequence**:
The monotonic sequence assigned when `VbaInteractiveWorkScheduler` admits a
request, mutation, barrier, or explicit cancellation control. It records input
causality independently from execution start and response order.
_Avoid_: document version, request id, execution index

**ReadFence**:
The latest relevant workspace-mutation `InputSequence` that precedes an
admitted read. The ordered mutation-and-capture lane commits every earlier
mutation before the read captures one immutable revision. Later mutations may
commit while the captured read executes, but cannot alter that pinned revision;
non-mutating barriers and explicit cancellation controls do not advance the
fence.
_Avoid_: cancellation version, response sequence, source revision

**RequestCancellationOwnership**:
The generation-specific association between one active numeric or string LSP
request id and its request-scoped cancellation token. `$/cancelRequest` signals
that owner outside the ordered mutation-and-capture lane, while the request
executor remains the single owner of its normal or `RequestCancelled` response.
Ownership is released after choosing the terminal response and before writing
it, so a completed id can be reused without an older generation removing the
new owner.
_Avoid_: document-change cancellation, shared server token, response ownership

## Workspace Context

**ModernVbaWorkspace**:
The local multi-repository workspace that may contain `vba-tools`, archived
`vba-devtools`, `DoxyVB6`, and Excel macro repositories for integration work.
_Avoid_: monorepo, single repo

## Example Dialogue

Dev: "Should completion include a procedure from another folder?"
Domain Expert: "Only when that folder belongs to the same `DocumentSourceSet` through `vba-project.json`. Without a `ProjectManifest`, an `AdHocVbaProject` indexes only the active source file's directory, so nested `common-modules` procedures are outside completion."

Dev: "Should a standard module name appear at statement level so I can write `Lib_Common.New_Foo`?"
Domain Expert: "Yes, but as a `QualifierCompletionCandidate`, not as a value or callable definition. Once `Lib_Common.` exists, member completion should show the public members owned by that `ModuleIdentity`."

Dev: "Should `Excel.` and `Scripting.` behave differently from `Lib_Common.` in completion?"
Domain Expert: "No. They are also `QualifierCompletionCandidate`s when their owning `VbaProjectReferenceQualifier`s are active; after the qualifier is formed, member completion should show that reference qualifier's exposed definitions."

Dev: "Should the completion label include the dot, like `Lib_Common.`?"
Domain Expert: "No. The label is `Lib_Common`, while the insertion text is `Lib_Common.`. The label stays searchable as the qualifier name, and the inserted dot moves the editor into member completion."

Dev: "What if a module qualifier and a function have the same label, like `Foo`?"
Domain Expert: "They remain distinct when their effective insertion text or semantic role differs. A `Foo` qualifier inserts `Foo.` and carries qualifier detail, while a `Foo` function inserts `Foo` and carries callable detail. If the UI metadata is not enough to tell them apart, that is a presentation issue, not a name-resolution merge."

Dev: "Should catalog candidates sort above source candidates because there may be many Office members?"
Domain Expert: "No. Completion ordering follows source proximity before referenced-library candidates: procedure-local, current-module, public project, then standard or external catalog definitions. Explicit reference-qualified completion, such as `Excel.`, is already scoped to that reference and orders within the admitted surface."

Dev: "Should `Lib_Common` appear after a completed call like `ExampleFunc() |`?"
Domain Expert: "No. `QualifierCompletionCandidate`s follow the active `CompletionExpectation`; they appear where a qualified reference can start, not where the grammar has already closed the expression."

Dev: "Is the VS Code workspace folder always the `WorkbookBackedProject`?"
Domain Expert: "No. The `ProjectManifest` identifies the `WorkbookBackedProject`; a workspace can contain none, one, or several workbook-backed projects."

Dev: "What happens when I edit a loose `.bas` file outside any `vba-project.json`?"
Domain Expert: "It is an `AdHocVbaProject`: source definitions, `LanguageVocabulary`, and definitions from the always-active `VbaStandardLibraryReference` work, but no manifest-controlled external references are active. Create a `WorkbookBackedProject` when other reference-aware completions are needed."

Dev: "Should completion call `vba-dev` to resolve project references?"
Domain Expert: "No. `LanguageServerManifestResolution` reads the `ProjectManifest` directly for editor features. `VbaDev` owns project creation, reference changes, doctor/repair, build, test, publish, and export; background catalog refresh may use tooling, but synchronous editor requests do not invoke the CLI."

Dev: "Should Test Explorer show every `TestProcedure` before the first run?"
Domain Expert: "No. It should show runnable `WorkbookBackedProject` and `DocumentSourceSet` nodes first, then add procedure-level nodes after test output identifies them."

Dev: "Is a workbook lock a failed `TestProcedure`?"
Domain Expert: "No. It is a `TestRunError` on the project or document scope because the test run could not reach individual test execution."

Dev: "Should a form module participate in rename and go to definition?"
Domain Expert: "Yes. A `.frm` file in the same folder is part of the same `VbaProject`."

Dev: "Is a `Public Enum` a definition?"
Domain Expert: "Yes. `Enum` and user-defined `Type` declarations are `VbaDefinition`s and should participate in completion, hover, rename, and go to definition."

Dev: "Is an `Event` only a declaration, or can it be referenced?"
Domain Expert: "An `Event` is a `VbaDefinition`. Event handler procedure names and `RaiseEvent` statements can both refer to it."

Dev: "Where do Office object model completions come from?"
Domain Expert: "They are `VbaProjectReferenceDefinition`s supplied by active `VbaProjectReference`s, even when the language server stores or discovers their metadata locally."

Dev: "Does enabling Access also enable DAO and ADO completions?"
Domain Expert: "No. The Access object library, DAO, and ADO are separate `VbaProjectReference`s, so each must be active before its definitions appear."

Dev: "If I install support for Word and PowerPoint, do their object models appear automatically?"
Domain Expert: "No. They appear only when the `VbaProjectReferenceSelection` includes those object libraries."

Dev: "Which Office object library should unqualified external references feel native to?"
Domain Expert: "Use the `MainVbaProjectReference` derived from the active document kind."

Dev: "If an Excel document's manifest omits the Excel object library, should Excel completions still appear?"
Domain Expert: "No. `MainVbaProjectReference` identifies the expected precedence winner, but absent references do not contribute definitions. Report `ManifestReferenceConsistency` through language-server output, status, or trace and through `EnvironmentDiagnostic`."

Dev: "If Excel and Word both define `Application`, which one does `Application` mean?"
Domain Expert: "Source `VbaDefinition`s still win first. Among referenced-library definitions, the `MainVbaProjectReference` definition wins; if only non-main references tie, `NameResolution` stays ambiguous."

Dev: "If a procedure declares `Dim ActiveCell As String`, does `ActiveCell` still mean Excel's active range?"
Domain Expert: "No. Procedure-local source definitions outrank current-module definitions, public project definitions, and every referenced-library definition. The local `ActiveCell` is a `RenameTarget`; Excel's catalog `ActiveCell` is used only when no higher-rank source definition wins."

Dev: "Should unqualified completion show both Excel and Word `Application`?"
Domain Expert: "No. Unqualified external completion follows `NameResolution`; use `Word.` for Word-specific qualified completion."

Dev: "Should syntax highlighting only color keywords and comments?"
Domain Expert: "No. `SyntaxHighlighting` includes lexical VBA coloring and `SemanticToken`s for parsed project meaning."

Dev: "Is an unresolved identifier a `SyntaxDiagnostic`?"
Domain Expert: "No. `SyntaxDiagnostic`s report malformed VBA grammar and source structure. Unknown names and ambiguous `NameResolution` are semantic concerns."

Dev: "Is `RaiseEvent Completed ""ok""` a `VbaValidationDiagnostic`?"
Domain Expert: "No. Parenthesis-free `RaiseEvent` arguments are malformed statement syntax, so that is a `SyntaxDiagnostic`. Duplicate parameter names and invalid named-argument ordering are `VbaValidationDiagnostic`s."

Dev: "If an active reference has no usable catalog, should the editor mark source lines?"
Domain Expert: "No. The reference stays active but contributes no external definitions. Report `VbaProjectReferenceCatalogAvailability` through language-server output, status, or trace and through `EnvironmentDiagnostic`, not through source diagnostics."

Dev: "Should missing root-exposure metadata or unavailable host globals create source diagnostics?"
Domain Expert: "No. They affect editor intelligence availability, not source validity. Report catalog, reference, and refresh state through output, trace, status, or `EnvironmentDiagnostic`."

Dev: "Should completion wait while TypeLib metadata is being discovered?"
Domain Expert: "No. Completion, hover, and signature help use the best committed `LastKnownGoodReferenceCatalog`. `VbaProjectReferenceCatalogRefresh` runs in the background after project activation or an effective reference-selection change."

Dev: "Should root completion scan TypeLib metadata when many globals like `xlCenter` may be available?"
Domain Expert: "No. `CompletionCandidate` discovery reads only already-admitted source, vocabulary, and committed reference-catalog definitions. Prefix filtering can remain editor-owned until measurement shows a need for server-side incomplete completion."

Dev: "Should completion wait if the Excel reference catalog is currently refreshing?"
Domain Expert: "No. Editor requests use the current `LastKnownGoodReferenceCatalog` when one is committed. If no committed snapshot exists yet, that reference simply contributes no candidates until a later request sees a successful commit."

Dev: "Should every VBA `didChange` resolve the manifest and retry reference catalog work?"
Domain Expert: "No. It updates source analysis and diagnostics only. `VbaProjectReferenceCatalogLifecycle` belongs to project activation and effective reference-selection changes."

Dev: "What happens when two source files from the same project open with the same references?"
Domain Expert: "They share the same `ReferenceSelectionFingerprint` and automatic lifecycle revision, so persisted preload and discovery are scheduled at most once."

Dev: "What happens when a persisted catalog is missing or corrupt?"
Domain Expert: "That result is negative-cached for the current `ReferenceCatalogLifecycleRevision`. It does not create a source diagnostic and does not prevent an explicit retry or a changed selection from trying again."

Dev: "Does refreshing an Excel catalog invalidate a project that selects only Word?"
Domain Expert: "No. Project snapshots track revisions for their selected references, so only affected project scopes are rebuilt."

Dev: "Should `vba-project.json` store TypeLib GUIDs for references?"
Domain Expert: "No. The `ProjectManifest` stores the human-visible `VbaProjectReference` name from `Reference.Description`. After discovery resolves that name, catalogs and caches may use `VbaProjectReferenceCatalogIdentity` keys such as GUID, version, LCID, and path."

Dev: "What if one manifest reference name matches several TypeLib candidates?"
Domain Expert: "First narrow candidates to libraries VBA can actually reference. If multiple usable catalog identities remain, treat the reference as ambiguous rather than guessing."

Dev: "Is source formatting only about casing?"
Domain Expert: "No. `SourceFormatting` includes `CasingNormalization` and `IndentationFormatting`, but it is not a semantic refactor."

Dev: "Is `String` a host definition when formatting casing?"
Domain Expert: "No. Intrinsic words such as `String`, `True`, and `Nothing` belong to `LanguageVocabulary`."

Dev: "Is automatic body and terminator insertion after Enter the same as `EndStatementCompletion`?"
Domain Expert: "No. `EndStatementCompletion` remains an explicit completion candidate. The automatic editor action is `BlockSkeletonInsertion`."

Dev: "Is each physical line of a continued `Function` declaration a separate header?"
Domain Expert: "No. The continued logical declaration is one `BlockDeclarationHeader`, and only its final physical line can trigger `BlockSkeletonInsertion`."

Dev: "Is a trailing `Loop While condition` the terminator of a `While` block?"
Domain Expert: "No. It terminates a post-condition `Do...Loop`; `While...Wend` is a separate form. Both forms are outside `BlockSkeletonInsertion`."

Dev: "Should a space after a completed expression keep general completion open?"
Domain Expert: "No. A completed expression has no `CompletionExpectation`, so an LSP trigger cannot manufacture `CompletionCandidate`s for it."

Dev: "Should `+` and `+ ` produce different completion lists?"
Domain Expert: "No. Irrelevant trivia does not change the `CompletionExpectation`, so semantic resolution must admit the same `CompletionCandidate`s at both positions."

Dev: "Can a normal apostrophe comment appear in hover?"
Domain Expert: "No. Hover uses the complete `DocumentationComment`; Signature Help projects only its `@param` documentation. Interface documentation may be inherited through `Implements` when the implementation has none."

Dev: "Should a private helper with a `DocumentationComment` appear in hover?"
Domain Expert: "Yes. Visibility does not hide an attached `DocumentationComment`."

Dev: "Can `Range` be renamed?"
Domain Expert: "No. Excel object model members are `VbaProjectReferenceDefinition`s, not `RenameTarget`s."

Dev: "Should F12 on `Range` or `xlCenter` open the synthetic `vba-reference://` URI?"
Domain Expert: "No. `ExternalDefinitionNavigation` stays disabled until a read-only virtual catalog document provider exists. Returning an URI that the editor cannot open is not a useful go-to-definition result."

Dev: "Does an Excel document kind activate `ActiveWorkbook` when the Excel object library is absent?"
Domain Expert: "No. `ActiveWorkbook` is a `HostGlobalReferenceDefinition` supplied only when the Excel object library is the active `MainVbaProjectReference`; document kind alone does not synthesize the missing reference."

Dev: "Are `Application` and `ActiveWindow` Excel-only globals?"
Domain Expert: "No. They are host-generic `HostGlobalReferenceDefinition`s supplied by the active `MainVbaProjectReference` catalog when that host exposes them. Excel supplies Excel-typed values, Word supplies Word-typed values, and an ad-hoc project supplies neither."

Dev: "Are `ActiveCell`, `ActiveSheet`, `ActiveWorkbook`, and `ThisWorkbook` also host-generic?"
Domain Expert: "No. They are Excel-specific host globals and appear only when the Excel object library is the active `MainVbaProjectReference` and its catalog exposes them."

Dev: "Should `ThisWorkbook.cls` be merged with the Excel `ThisWorkbook` host global?"
Domain Expert: "No. Real Excel projects reserve the workbook document module name, and the language server does not infer document-module identity from that spelling. `ThisWorkbook` is handled as the Excel catalog's read-only host global; source document-module modeling is a separate concern."

Dev: "Should `ActiveCell` be modeled as a global variable so that it can appear in completion?"
Domain Expert: "No. It is a read-only property `HostGlobalReferenceDefinition`; its project-reference origin makes it available as a value while keeping it outside `RenameTarget`."

Dev: "Should assigning to a read-only host global create a new source diagnostic in this work?"
Domain Expert: "No. `HostGlobalReferenceDefinition` records that the value is read-only, but assignment diagnostics are outside this scope."

Dev: "Should `ActiveCell` hover as `Property ActiveCell As Range`?"
Domain Expert: "No. A root host global is presented as a value reference, so its `DeclarationLabel` is `ActiveCell As Range`; callable or indexed properties use `CallableSignature` when that richer shape is available."

Dev: "Should `ActiveSheet.` show both worksheet and chart members?"
Domain Expert: "No. `ActiveSheet` is a read-only `HostGlobalReferenceDefinition`, but its type is intentionally unavailable because the runtime object kind varies. Member completion after `ActiveSheet.` stays empty rather than guessing a union."

Dev: "Should `ThisWorkbook.` or `ActiveCell.` inspect the currently open Excel workbook before showing members?"
Domain Expert: "No. Typed host globals participate in `MemberChainResolution` through their declared catalog types, such as `Workbook` or `Range`. Completion does not depend on live Excel state, workbook contents, sheet names, or the active cell."

Dev: "Does `ActiveWindow` exist in an ad-hoc project because several Office hosts expose that name?"
Domain Expert: "No. Each host supplies its own typed `ActiveWindow` through its active `MainVbaProjectReference`; an ad-hoc project or a project missing that main reference has no such definition."

Dev: "Should every member found under an Excel `Window` or `Workbook` be considered an unqualified host global?"
Domain Expert: "No. `ReferenceDefinitionGlobalExposure` distinguishes ordinary type members from library globals and globals supplied only by the active main host."

Dev: "Should hidden or restricted TypeLib members appear in normal completion?"
Domain Expert: "No. Completion should show names users normally write. Hidden or restricted catalog members are suppressed unless `ReferenceDefinitionGlobalExposure` has explicitly selected them as exposed root definitions, such as a main-host global supplied through application-object binding. A hidden owner such as `_Global` is not exposed wholesale."

Dev: "Should an older persisted catalog expose root globals by guessing from `_Global` or `Application` owner names?"
Domain Expert: "No. Missing `ReferenceDefinitionGlobalExposure` metadata fails closed for root globals. The catalog can still supply ordinary type and member metadata that it proves, but root exposure waits for a refreshed catalog."

Dev: "Can the language server recognize host globals by looking for an owner named `Application`?"
Domain Expert: "No. `ReferenceDefinitionGlobalExposure` preserves the referenced library's application-object and library-global binding semantics; owner spelling does not establish global visibility."

Dev: "Are `vbCrLf` and `xlCenter` the same kind of completion?"
Domain Expert: "They are both structured `LibraryGlobalReferenceDefinition`s, but their owning references have different activation rules: `vbCrLf` comes from the always-active `VbaStandardLibraryReference`, while `xlCenter` is available only while its Excel `VbaProjectReference` is active."

Dev: "Should `VBA.` work in an ad-hoc project?"
Domain Expert: "Yes. `VbaStandardLibraryReference` is always active, so its `VBA` qualifier is available in every `VbaProject`, including an `AdHocVbaProject`. It still follows `NameResolution`, so a higher-rank source definition named `VBA` can shadow the qualifier."

Dev: "Should `vbCrLf` require TypeLib discovery or an installed Office application?"
Domain Expert: "No. `VbaStandardLibraryReference` is bundled baseline metadata. It is available immediately and independently of COM registry state, Office installation state, and `VbaProjectReferenceCatalogLifecycle`."

Dev: "Is `vbCrLf` only a completion label because it is available in every project?"
Domain Expert: "No. It is an external constant definition owned by `VBA.Constants`, with its declared `String` type, constant completion presentation, hover declaration, canonical casing, and semantic-token facts. Like every `VbaProjectReferenceDefinition`, it is not a `RenameTarget`, and a same-named source `VbaDefinition` still wins through `NameResolution`."

Dev: "Should `xlCenter` hover as `XlHAlign` when I use it in an alignment property?"
Domain Expert: "No. `DeclarationLabel` uses the catalog-provided type for the enum member itself and does not infer a contextual enum type from the use site. `vbCrLf` hovers as `Const vbCrLf As String`; Excel `xlCenter` hovers as `xlCenter As Long` when the Excel catalog records that member as `Long`."

Dev: "Is `Application` ambiguous because the host exposes both a global value and a class with that name?"
Domain Expert: "No. A value `CompletionExpectation` selects the read-only `HostGlobalReferenceDefinition`, while a type or creatable-type expectation selects the class `VbaProjectReferenceDefinition`; completion does not show both in one context."

Dev: "What happens when two public modules expose the same name?"
Domain Expert: "`NameResolution` treats equal-rank matches as ambiguous, so hover and go to definition should stay silent for that reference."

Dev: "If `Customer.cls` says `Attribute VB_Name = \"CustomerRecord\"`, what is the class name?"
Domain Expert: "The `ModuleIdentity` is `CustomerRecord`; the file name is only a fallback."

Dev: "Should `Set ws = Worksheets(1)` make `ws.` show worksheet members?"
Domain Expert: "Not in the MVP. `TypeResolution` uses explicit declarations such as `Dim ws As Worksheet`."

Dev: "If both a source class and Excel define `Range`, what should `Dim r As Range` mean?"
Domain Expert: "The source `VbaDefinition` wins. Use a reference-qualified annotation such as `Dim r As Excel.Range` to force a `VbaProjectReferenceDefinition`."

Dev: "Should `Application.ActiveWorkbook.Worksheets(1).Range(\"A1\").Find(` be treated as several unrelated qualified references?"
Domain Expert: "No. That is `MemberChainResolution`: each resolved member's declared result type supplies the receiver type for the next member access."

Dev: "Can `Me.CreateCustomer().DisplayName` participate in `MemberChainResolution`?"
Domain Expert: "Yes, inside class and form modules. `Me` is the current instance root, and private members remain visible within that same module."

Dev: "Is `Application.ActiveWorkbook _` followed by `.Worksheets(1)` on the next line a different kind of lookup?"
Domain Expert: "No. It is a `ContinuedMemberChain`: one `MemberChainResolution` expression split across physical lines, with each member still tied to its original source range."

Dev: "Is `Find( _` followed by arguments on later lines a `ContinuedMemberChain`?"
Domain Expert: "No. It is a `ContinuedArgumentList`: the receiver chain has already selected the callable, and the continued lines keep signature help active while identifying the active parameter."

Dev: "Inside `With Application.ActiveWorkbook.Worksheets(1).Range(\"A1\")`, what does `.Find` mean?"
Domain Expert: "The `WithReceiver` is the resolved range expression, so `.Find` is resolved as a member chain on that receiver. If the `WithReceiver` type is missing or ambiguous, no guessed member result is produced."

Dev: "Can the `WithReceiver` expression itself be split across physical lines?"
Domain Expert: "Yes. The receiver expression can be a `ContinuedMemberChain`; once that receiver resolves, leading-dot members inside the block still use the `WithReceiver`."

Dev: "Should `Constructor.New_Foo` resolve across modules?"
Domain Expert: "Yes. It is a `QualifiedReference`; after `Constructor` resolves to a `ModuleIdentity`, `New_Foo` resolves to a public member in that module."

Dev: "Does `Word.Application` mean the same thing as unqualified `Application`?"
Domain Expert: "No. `Word.Application` is a `QualifiedReference` through the active Word `VbaProjectReferenceQualifier`; unqualified `Application` follows `MainVbaProjectReference` precedence."

Dev: "What should `Word.` complete?"
Domain Expert: "If no source definition named `Word` wins first, it completes the active Word reference catalog's public root surface: root types, exposed constants, and explicit root exposure definitions."

Dev: "Should `Excel.` show the same list in `Dim r As Excel.` and `x = Excel.`?"
Domain Expert: "No. The reference qualifier exposes the catalog's public root surface, but `CompletionExpectation` still filters by role. Type contexts see type-compatible candidates, value contexts see value-compatible candidates, and creatable-type contexts see creatable class candidates."

Dev: "Should `Excel._Global` or other hidden owner names become a way to browse internal catalog members?"
Domain Expert: "No. A `VbaProjectReferenceQualifier` exposes the public root surface of the reference catalog. Hidden owners and restricted internal members are not completion entry points."

Dev: "Where does the `Word` qualifier name come from?"
Domain Expert: "From a `VbaProjectReferenceQualifier` supplied by the `VbaProjectReferenceCatalog`. It is not written in the `ProjectManifest` and is not parsed from `Reference.Description` alone."

Dev: "If there is a source module named `Word`, does `Word.Application` still force the Word host?"
Domain Expert: "No. Source `VbaDefinition`s outrank reference qualifier names, so `Word` resolves to the source module first. The reference qualifier is not an absolute escape hatch; rename the source definition or remove the collision if the external reference qualifier is needed."

Dev: "Does `Button_Click` resolve without reading form designer metadata?"
Domain Expert: "Only when `Button` is explicitly declared as a `WithEvents` variable. That handler name is an `EventReference` to the `Click` event on the declared type."

Dev: "Do form designer properties create completion candidates?"
Domain Expert: "No. A `FormDesignerBlock` is not parsed into `VbaDefinition`s in the MVP."

Dev: "How much source does an incremental parse replace?"
Domain Expert: "It replaces the affected `ModuleMember`, not individual expression nodes."

Dev: "Does a later `didChange` cancel an earlier completion or hover request?"
Domain Expert: "No. Once the earlier read captures its immutable revision, the later `didChange` receives a later `InputSequence` and may commit through the ordered lane while that read continues on the pinned revision. Only explicit `$/cancelRequest`, host abort, EOF, or terminal runtime failure signals `RequestCancellationOwnership`."

Dev: "Does explicit cancellation have to wait behind the request it cancels?"
Domain Expert: "No. The input reader signals the matching owner immediately outside the ordered mutation-and-capture lane, but `VbaLspRequestExecution` still writes exactly one normal or `RequestCancelled` response through the serialized transport."

Dev: "Did continuous admission change VBA document synchronization?"
Domain Expert: "No. The server still advertises full-text synchronization. `VbaInteractiveWorkScheduler` changes admission and cancellation ownership, not the document text contract or the C# language-server authority."
