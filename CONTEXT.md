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
not define project references for workbook-backed projects.
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
equal rank external matches remain ambiguous.
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
`ProjectManifest` can be found. It provides source definitions and
`LanguageVocabulary`, but it has no `VbaProjectReferenceSelection` and therefore
does not contribute external reference completions.
_Avoid_: workbook-backed project, default Excel project, settings-backed project

**VbaDefinition**:
An identifiable declaration in a `VbaProject` that editor features can refer to. It includes modules, classes, forms, procedures, properties, constants, variables, parameters, enums, user-defined types, and events.
_Avoid_: symbol, item, thing

**VbaProjectReferenceDefinition**:
A definition supplied by an active `VbaProjectReference` rather than by exported
source files in a `VbaProject`. Office object model members, Scripting Runtime
types, RegExp types, DAO/ADO types, and other referenced-library members are all
`VbaProjectReferenceDefinition`s.
_Avoid_: HostDefinition, ReferenceLibrary, built-in, standard library, external symbol

**VbaProjectReferenceSelection**:
The set of `VbaProjectReference`s whose `VbaProjectReferenceDefinition`s are
active for a `VbaProject`. For language-server behavior, it is resolved from
the `ProjectManifest` document definition; source template documents are not
used as a reference-selection input, and VS Code host application settings do
not participate in reference selection.
_Avoid_: HostApplicationSelection, mode, profile, target language

**VbaProjectReferenceCatalog**:
A discoverable, cached, or bundled metadata source that maps active
`VbaProjectReference`s to `VbaProjectReferenceDefinition`s,
`CallableSignature`s, and catalog-owned qualifier aliases. If no catalog is
available for an active reference, the reference remains active but contributes
no external definitions.
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
`Scripting`. It is not stored in `vba-project.json` and is not mechanically derived
from `Reference.Description` alone.
_Avoid_: manifest alias, display name, host name

**VbaProjectReferenceCatalogAvailability**:
The operational state describing whether an active `VbaProjectReference` has a
usable `VbaProjectReferenceCatalog`. Missing catalog availability can be
reported through language-server output, status, or trace and through an
`EnvironmentDiagnostic`, but it does not create source diagnostics by itself.
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
queries only read committed catalog state.
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
editor-facing catalog.
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
or available `VbaProjectReferenceCatalog`s.
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
The fixed VBA words whose casing is defined by the language server rather than by a `VbaDefinition` or `VbaProjectReferenceDefinition`. It includes VBA keywords, intrinsic types, intrinsic constants, and literals.
_Avoid_: host definition, project definition

**CompletionExpectation**:
The syntax-owned description of what may legally follow at an editor position. It is derived from `VbaSyntaxTree`, remains stable across irrelevant trivia, and fails closed when syntax does not establish a valid continuation. A completed grammar marker opens its next slot only after the lexical separator required by VBA, while punctuation operators can open an operand slot immediately. Related position facts may carry canonical contextual statements, syntax words, or module-kind-specific starter words, but contain no `VbaDefinition` or LSP trigger metadata.
_Avoid_: general completion, trigger context

**SyntaxWord**:
A fixed VBA word required by the active grammar transition, such as `Then`, `In`, or a declaration continuation. It is selected by `VbaSyntaxTree` for the exact editor position and is not a general keyword proposal.
_Avoid_: keyword completion, declaration keyword

**CompletionCandidate**:
An editor proposal admitted by a `CompletionExpectation` after semantic resolution. It may originate from a `VbaDefinition`, `VbaProjectReferenceDefinition`, `LanguageVocabulary`, named `CallableParameter`, callable-owned line label, contextual branch statement, or `EndStatementCompletion`. Its insertion and replacement facts are complete before LSP projection, and proposals with the same label remain distinct when their effective insertion text differs.
_Avoid_: completion definition, raw vocabulary

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
The editor-facing declaration summary for a non-callable `VbaDefinition` or `VbaProjectReferenceDefinition`, or the fallback when no richer `CallableSignature` is available. Constants, enums, and user-defined types include `Const`, `Enum`, or `Type`. Variables, parameters, enum members, and user-defined type members use declaration forms such as `Name As Type`; arrays keep `()` after the name. Parameter labels include effective `ByRef` metadata while omitting `ByVal`. `Static` and `WithEvents` are included when they apply, while visibility modifiers and unavailable implicit types are omitted.
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

**NameResolution**:
The case-insensitive process of matching an identifier reference to the closest visible `VbaDefinition` or `VbaProjectReferenceDefinition`. Procedure-local definitions outrank current-module definitions, current-module definitions outrank public project definitions, and project definitions outrank referenced-library definitions, including reference qualifier names; among referenced-library definitions, a `MainVbaProjectReference` match outranks matches from other active `VbaProjectReference`s.
_Avoid_: lookup, binding, search

**ModuleIdentity**:
The name of an exported VBA module, class, or form as defined by `Attribute VB_Name`. The source file name is only a fallback when `Attribute VB_Name` is absent.
_Avoid_: file name, module file, path name

**TypeResolution**:
The process of matching an explicit VBA type annotation to a `VbaDefinition` or `VbaProjectReferenceDefinition` for member completion and member documentation. Source `VbaDefinition`s outrank referenced-library `VbaProjectReferenceDefinition`s unless the annotation is reference-qualified, and assignment-based inference is outside the MVP.
_Avoid_: type inference, runtime type, guessed type

**MemberChainResolution**:
The process of resolving a sequence of member accesses by carrying each resolved member's declared result type to the next member access. It applies to both source `VbaDefinition`s and `VbaProjectReferenceDefinition`s when result type metadata is available; missing or ambiguous result types stop the chain.
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
An identifier reference written with a qualifier, such as `ModuleIdentity.MemberName`, `variable.MemberName`, or `Word.Application`. When the qualifier names a module, class, or form, only public members of that definition are visible from outside that module; when it names an active `VbaProjectReferenceQualifier`, only that reference's `VbaProjectReferenceDefinition`s are visible.
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
Domain Expert: "No. In the MVP, the `VbaProject` is only the active file's folder, so sibling `.bas`, `.cls`, and `.frm` files are indexed."

Dev: "Is the VS Code workspace folder always the `WorkbookBackedProject`?"
Domain Expert: "No. The `ProjectManifest` identifies the `WorkbookBackedProject`; a workspace can contain none, one, or several workbook-backed projects."

Dev: "What happens when I edit a loose `.bas` file outside any `vba-project.json`?"
Domain Expert: "It is an `AdHocVbaProject`: source definitions and `LanguageVocabulary` work, but no external `VbaProjectReferenceDefinition`s are active. Create a `WorkbookBackedProject` when reference-aware completions are needed."

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

Dev: "Should completion wait while TypeLib metadata is being discovered?"
Domain Expert: "No. Completion, hover, and signature help use the best committed `LastKnownGoodReferenceCatalog`. `VbaProjectReferenceCatalogRefresh` runs in the background after project activation or an effective reference-selection change."

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
Domain Expert: "If no source definition named `Word` wins first, it completes root `VbaProjectReferenceDefinition`s from the active Word object library reference."

Dev: "Where does the `Word` qualifier name come from?"
Domain Expert: "From a `VbaProjectReferenceQualifier` supplied by the `VbaProjectReferenceCatalog`. It is not written in the `ProjectManifest` and is not parsed from `Reference.Description` alone."

Dev: "If there is a source module named `Word`, does `Word.Application` still force the Word host?"
Domain Expert: "No. Source `VbaDefinition`s outrank reference qualifier names, so `Word` resolves to the source module first."

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
