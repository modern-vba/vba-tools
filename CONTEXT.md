# VBA Language Server

VBA-LanguageServer provides editor intelligence for exported VBA source files.
This glossary defines the domain terms used when discussing language-server behavior for VBA.

## Language

**VbaProject**:
A set of exported VBA source files that belong to the same logical VBA project. For this language server, the MVP project boundary is the folder containing the active `.bas`, `.cls`, or `.frm` file, including sibling `.bas`, `.cls`, and `.frm` files in that folder.
_Avoid_: workspace, repository, package

**VbaDefinition**:
An identifiable declaration in a `VbaProject` that editor features can refer to. It includes modules, classes, forms, procedures, properties, constants, variables, parameters, enums, user-defined types, and events.
_Avoid_: symbol, item, thing

**SourceConstant**:
A `Const` declaration defined inside a `VbaProject`. It is a source `VbaDefinition` named value, distinct from a `HostConstant`, a `HostEnumMember`, and intrinsic constants in `LanguageVocabulary`.
_Avoid_: host constant, enum member, intrinsic constant

**ConditionalCompilationConstant**:
A named value introduced by a `#Const` directive and referenced by conditional compilation directives such as `#If` and `#ElseIf`. It belongs to conditional compilation, not ordinary `NameResolution`, so it is not a `VbaDefinition`.
_Avoid_: source constant, host constant, variable

**HostDefinition**:
A definition supplied by an enabled `HostApplication` rather than by exported source files in a `VbaProject`. Office object model members such as Excel workbook APIs, Word document APIs, PowerPoint presentation APIs, and Access database APIs are `HostDefinition`s, and they retain their originating `HostApplication`.
_Avoid_: built-in, standard library, external symbol

**HostEnum**:
An Office host enumeration supplied by an enabled `HostApplication`. Its members are `HostEnumMember`s, and both the enum and its members retain their originating `HostApplication`.
_Avoid_: VBA enum, project enum, built-in enum

**HostEnumMember**:
A named value that belongs to a `HostEnum`, commonly written in VBA as an Office constant such as `xlUp` or `wdStory`. When enum membership is known, use `HostEnumMember` rather than `HostConstant`.
_Avoid_: host constant, magic value, literal

**HostConstant**:
A named value supplied by an enabled `HostApplication` whose `HostEnum` membership is not known or does not exist. Prefer `HostEnumMember` for Office named values that can be tied to a host enum.
_Avoid_: enum member, language constant, intrinsic constant

**HostApplication**:
An Office application whose object model can supply `HostDefinition`s for a `VbaProject`. Excel, Word, PowerPoint, and Access are separate `HostApplication`s even though they all use VBA.
_Avoid_: language, runtime, library

**ReferenceLibrary**:
An external VBA type library that is not a `HostApplication` object model. DAO and ADO are `ReferenceLibrary` candidates rather than part of Access `HostApplication` support.
_Avoid_: host application, object model

**HostApplicationSelection**:
The configured set of `HostApplication`s whose `HostDefinition`s are active for a `VbaProject`. It is resolved from the active document's configuration, formed from the `MainHostApplication` plus explicitly enabled additional `HostApplication`s, and defaults to Excel only so existing VBA editor behavior remains stable until another host is explicitly enabled.
_Avoid_: mode, profile, target language

**MainHostApplication**:
The configured primary `HostApplication` for a `VbaProject`. It defaults to Excel, is always part of the `HostApplicationSelection`, and represents the Office object model that should feel native for unqualified host references when more than one `HostApplication` is available.
_Avoid_: active application, default library, preferred host

**SyntaxHighlighting**:
Editor coloring for VBA source text. It combines lexical classification for VBA syntax with meaning-aware classification from parsed project information when that information is available.
_Avoid_: color theme, formatting

**SyntaxDiagnostic**:
An editor diagnostic that reports malformed VBA source syntax in a `VbaProject`. A `SyntaxDiagnostic` is about grammar and source structure, not semantic checks such as unresolved `VbaDefinition`s, missing `HostDefinition`s, type mismatch, or ambiguous `NameResolution`.
_Avoid_: compile error, semantic diagnostic, runtime error

**SemanticToken**:
A meaning-aware classification of a source range, derived from parsed `VbaProject` information. `SemanticToken`s refine `SyntaxHighlighting` for declarations, references, and conditional-compilation names, using standard editor token categories and modifiers whenever a VBA meaning can be represented by them.
_Avoid_: syntax token, text token

**SourceFormatting**:
Editor-initiated rewriting of VBA source text to match the language server's source style. It includes casing normalization and indentation formatting, while preserving source meaning.
_Avoid_: syntax highlighting, refactoring

**CasingNormalization**:
A `SourceFormatting` operation that rewrites VBA keywords and resolvable identifiers to their canonical casing.
_Avoid_: rename, spelling correction

**LanguageVocabulary**:
The fixed VBA words whose casing is defined by the language server rather than by a `VbaDefinition` or `HostDefinition`. It includes VBA keywords, intrinsic types, intrinsic constants, and literals.
_Avoid_: host definition, project definition

**IndentationFormatting**:
A `SourceFormatting` operation that rewrites leading whitespace according to VBA block structure.
_Avoid_: alignment, line wrapping

**EndStatementCompletion**:
An editor completion that inserts the matching VBA block closer for a block opener, such as `End Sub`, `End Function`, or `End If`.
_Avoid_: automatic typing, on-type edit

**DocumentationComment**:
A structured Doxygen-style VBA comment block attached to a `VbaDefinition` and shown by editor features such as hover and signature help regardless of public or private visibility. Plain apostrophe comments are not `DocumentationComment`s; when an implementation member has no `DocumentationComment`, it may inherit one from the interface member named by its `Implements` relationship.
_Avoid_: comment, note, description

**CallableSignature**:
The structured call shape for a callable `VbaDefinition` or `HostDefinition`. It includes the displayed signature label, ordered parameters, optional parameter metadata, parameter passing metadata, parameter type names, default values, return type names, and parameter documentation when that documentation is available from source comments or host catalog metadata.
_Avoid_: parameter list, call text, method shape

**HostTypeLibraryDiscovery**:
The process of collecting host catalog metadata from an available Office type library for an enabled `HostApplication`. It may provide `HostEnum`, `HostEnumMember`, `HostConstant`, `CallableSignature`, type, value, and documentation metadata.
_Avoid_: COM refresh, object member scan, signature-only discovery

**HostSignatureDiscovery**:
The part of `HostTypeLibraryDiscovery` that collects `CallableSignature` and callable type metadata for `HostDefinition`s. It enriches host metadata so editor features can show accurate signature help without guessing signatures from member names alone.
_Avoid_: COM refresh, member scan, full type-library discovery

**RenameTarget**:
A source-defined `VbaDefinition` that can be renamed inside its `VbaProject`. `HostDefinition`s, string literals, and `DocumentationComment`s are not `RenameTarget`s.
_Avoid_: renameable symbol, edit target

**NameResolution**:
The case-insensitive process of matching an identifier reference to the closest visible `VbaDefinition` or `HostDefinition`. Procedure-local definitions outrank current-module definitions, current-module definitions outrank public project definitions, and project definitions outrank host definitions, including host qualifier names; among host definitions, a `MainHostApplication` match outranks matches from other enabled `HostApplication`s.
_Avoid_: lookup, binding, search

**ModuleIdentity**:
The name of an exported VBA module, class, or form as defined by `Attribute VB_Name`. The source file name is only a fallback when `Attribute VB_Name` is absent.
_Avoid_: file name, module file, path name

**TypeResolution**:
The process of matching an explicit VBA type annotation to a `VbaDefinition` or `HostDefinition` for member completion and member documentation. Source `VbaDefinition`s outrank host `HostDefinition`s unless the annotation is host-qualified, and assignment-based inference is outside the MVP.
_Avoid_: type inference, runtime type, guessed type

**MemberChainResolution**:
The process of resolving a sequence of member accesses by carrying each resolved member's declared result type to the next member access. It applies to both source `VbaDefinition`s and host `HostDefinition`s when result type metadata is available; missing or ambiguous result types stop the chain.
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
An identifier reference written with a qualifier, such as `ModuleIdentity.MemberName`, `variable.MemberName`, or `Word.Application`. When the qualifier names a module, class, or form, only public members of that definition are visible from outside that module; when it names an enabled `HostApplication`, only that host's `HostDefinition`s are visible.
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

## Example Dialogue

Dev: "Should completion include a procedure from another folder?"
Domain Expert: "No. In the MVP, the `VbaProject` is only the active file's folder, so sibling `.bas`, `.cls`, and `.frm` files are indexed."

Dev: "Should a form module participate in rename and go to definition?"
Domain Expert: "Yes. A `.frm` file in the same folder is part of the same `VbaProject`."

Dev: "Is a `Public Enum` a definition?"
Domain Expert: "Yes. `Enum` and user-defined `Type` declarations are `VbaDefinition`s and should participate in completion, hover, rename, and go to definition."

Dev: "Is an `Event` only a declaration, or can it be referenced?"
Domain Expert: "An `Event` is a `VbaDefinition`. Event handler procedure names and `RaiseEvent` statements can both refer to it."

Dev: "Where do Office object model completions come from?"
Domain Expert: "They are `HostDefinition`s supplied by enabled `HostApplication`s, even when the language server stores or discovers their metadata locally."

Dev: "Should enum and constant discovery be called `HostSignatureDiscovery`?"
Domain Expert: "No. Use `HostTypeLibraryDiscovery` for Office type-library metadata as a whole. `HostSignatureDiscovery` is only the callable-signature part of that discovery."

Dev: "When `HostTypeLibraryDiscovery` reads `xlUp`, should it store that value as a root `HostDefinition`?"
Domain Expert: "No. If the type library exposes its enum membership, store it as a `HostEnumMember` under the parent `HostEnum`; unqualified `NameResolution` may project it as a named value without duplicating the catalog shape."

Dev: "Is `xlUp` a `HostConstant`?"
Domain Expert: "Not when the catalog knows it belongs to an Excel enum. Then it is a `HostEnumMember`; `HostConstant` is reserved for host-supplied named values without known `HostEnum` membership."

Dev: "Should a source `Const` and a `HostConstant` be highlighted like enum members?"
Domain Expert: "No. A `HostEnumMember` uses enum-member classification. A `SourceConstant` and a `HostConstant` use readonly variable classification so source and host constants align without implying enum membership."

Dev: "Is `#Const DEBUG = True` a `SourceConstant`?"
Domain Expert: "No. `DEBUG` is a `ConditionalCompilationConstant`. It should be classified as conditional-compilation metadata, not resolved as an ordinary `VbaDefinition`."

Dev: "Do callers need to write a host enum qualifier to use `xlUp`?"
Domain Expert: "No. A `HostEnumMember` belongs to a `HostEnum` in catalog metadata, but VBA source commonly references it as an unqualified named value when `NameResolution` can select one candidate."

Dev: "Is `XlDirection` itself a host definition?"
Domain Expert: "Yes. A `HostEnum` is a root `HostDefinition` that can participate in completion, hover, semantic tokens, casing normalization, and type annotations when its `HostApplication` is enabled."

Dev: "Does `XlDirection.xlUp` mean the same thing as `xlUp`?"
Domain Expert: "Not in the baseline model. `HostEnum` membership is catalog metadata for a `HostEnumMember`; enum-qualified member access is a separate reference form from ordinary unqualified or `HostApplication`-qualified host references."

Dev: "If two enabled hosts define the same enum member name, does the member behave differently from other host definitions?"
Domain Expert: "No. Unqualified `HostEnumMember` and `HostConstant` references follow normal `NameResolution`: the `MainHostApplication` can break a host tie, and equal-rank non-main host matches remain ambiguous."

Dev: "If one host defines the same enum member name in two different host enums, should the main host break the tie?"
Domain Expert: "No. The `MainHostApplication` can break ties between host applications, not between multiple same-name `HostEnumMember`s inside the same host. That unqualified reference remains ambiguous unless a future enum-qualified reference form selects the enum."

Dev: "Does enabling Access also enable DAO and ADO completions?"
Domain Expert: "No. Access contributes its `HostApplication` object model; DAO and ADO are separate `ReferenceLibrary` candidates."

Dev: "If I install support for Word and PowerPoint, do their object models appear automatically?"
Domain Expert: "No. They appear only when the `HostApplicationSelection` enables those `HostApplication`s; the stable default is Excel only."

Dev: "Which Office object model should unqualified host references feel native to?"
Domain Expert: "Use the configured `MainHostApplication`; it defaults to Excel."

Dev: "If Excel and Word both define `Application`, which one does `Application` mean?"
Domain Expert: "Source `VbaDefinition`s still win first. Among host definitions, the `MainHostApplication` definition wins; if only non-main hosts tie, `NameResolution` stays ambiguous."

Dev: "Should unqualified completion show both Excel and Word `Application`?"
Domain Expert: "No. Unqualified host completion follows `NameResolution`; use `Word.` for Word-specific qualified completion."

Dev: "Should syntax highlighting only color keywords and comments?"
Domain Expert: "No. `SyntaxHighlighting` includes lexical VBA coloring and `SemanticToken`s for parsed project meaning."

Dev: "Is an unresolved identifier a `SyntaxDiagnostic`?"
Domain Expert: "No. `SyntaxDiagnostic`s report malformed VBA grammar and source structure. Unknown names and ambiguous `NameResolution` are semantic concerns."

Dev: "Is source formatting only about casing?"
Domain Expert: "No. `SourceFormatting` includes `CasingNormalization` and `IndentationFormatting`, but it is not a semantic refactor."

Dev: "Is `String` a host definition when formatting casing?"
Domain Expert: "No. Intrinsic words such as `String`, `True`, and `Nothing` belong to `LanguageVocabulary`."

Dev: "Should typing Enter after `Sub` automatically insert `End Sub`?"
Domain Expert: "No. `EndStatementCompletion` is an explicit completion item, not an on-type edit."

Dev: "Can a normal apostrophe comment appear in hover?"
Domain Expert: "No. Hover and signature help use `DocumentationComment`s only, with interface documentation inherited through `Implements` when the implementation has none."

Dev: "Should a private helper with a `DocumentationComment` appear in hover?"
Domain Expert: "Yes. Visibility does not hide an attached `DocumentationComment`."

Dev: "Can `Range` be renamed?"
Domain Expert: "No. Excel object model members are `HostDefinition`s, not `RenameTarget`s."

Dev: "What happens when two public modules expose the same name?"
Domain Expert: "`NameResolution` treats equal-rank matches as ambiguous, so hover and go to definition should stay silent for that reference."

Dev: "If `Customer.cls` says `Attribute VB_Name = \"CustomerRecord\"`, what is the class name?"
Domain Expert: "The `ModuleIdentity` is `CustomerRecord`; the file name is only a fallback."

Dev: "Should `Set ws = Worksheets(1)` make `ws.` show worksheet members?"
Domain Expert: "Not in the MVP. `TypeResolution` uses explicit declarations such as `Dim ws As Worksheet`."

Dev: "If both a source class and Excel define `Range`, what should `Dim r As Range` mean?"
Domain Expert: "The source `VbaDefinition` wins. Use a host-qualified annotation such as `Dim r As Excel.Range` to force a `HostDefinition`."

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
Domain Expert: "No. `Word.Application` is a `QualifiedReference` through the enabled Word `HostApplication`; unqualified `Application` follows `MainHostApplication` precedence."

Dev: "What should `Word.` complete?"
Domain Expert: "If no source definition named `Word` wins first, it completes root `HostDefinition`s from the enabled Word `HostApplication`."

Dev: "If there is a source module named `Word`, does `Word.Application` still force the Word host?"
Domain Expert: "No. Source `VbaDefinition`s outrank host qualifier names, so `Word` resolves to the source module first."

Dev: "Does `Button_Click` resolve without reading form designer metadata?"
Domain Expert: "Only when `Button` is explicitly declared as a `WithEvents` variable. That handler name is an `EventReference` to the `Click` event on the declared type."

Dev: "Do form designer properties create completion candidates?"
Domain Expert: "No. A `FormDesignerBlock` is not parsed into `VbaDefinition`s in the MVP."

Dev: "How much source does an incremental parse replace?"
Domain Expert: "It replaces the affected `ModuleMember`, not individual expression nodes."
