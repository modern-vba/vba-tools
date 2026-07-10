# Full VbaSyntaxTree Parser Core

## Problem

VbaLanguageServer currently relies on regex-oriented source scanning for many
parser-adjacent responsibilities. That approach has already become too narrow
for VBA source files that use static procedures, external declarations, multiple
declarations per statement, implicit `Variant` declarations, `As New`
annotations, preprocessor blocks, line continuations, `.frm` designer sections,
and expression-shaped completion contexts.

The next parser step needs to preserve enough VBA syntax structure for syntax
highlighting, syntax error detection, and completion candidate discovery without
expanding into compile-time type inference or unresolved-name diagnostics.

## Solution

Introduce a reusable hand-written C# parser core in a new
`VbaLanguageServer.Syntax` project. The parser core will produce a
source-range-preserving `VbaTokenStream` and `VbaSyntaxTree` and will replace
the current `VbaModuleParser` behavior used by the language server.

The parser core is a `ReusableVbaParserCore`: it must not depend on LSP,
VS Code, workbook automation, or `VbaDev` command behavior. VbaLanguageServer
will consume it for editor features, and DoxyVB6 may later consume the same
syntax model through a separate adapter.

Rubberduck's public parser grammar and declaration-resolution source are used
as a compatibility reference for coverage review only. Rubberduck is not copied,
generated from, bundled, or used as a dependency. Excel Live Server is not used
as a parser design source because no public source code or developer-oriented
detailed design documentation suitable for parser comparison was available.

## User Stories

- As a VBA developer, I want syntax highlighting to remain useful even when the
  current source file contains incomplete or malformed code.
- As a VBA developer, I want syntax diagnostics to identify malformed source
  structure such as broken declarations, unterminated blocks, and invalid line
  continuations without reporting semantic compiler checks as syntax errors.
- As a VBA developer, I want completion to understand statement, expression,
  member-access, argument-list, and `With` contexts more reliably than a regex
  scanner can.
- As a tooling maintainer, I want parser behavior to be testable independently
  from LSP transport and workbook automation.
- As a future DoxyVB6 integrator, I want module structure, attributes,
  documentation comments, and source ranges to remain available without pulling
  in VS Code or language-server dependencies.

## Scope

The parser must model the syntax structure needed for VBA syntax highlighting,
parser recovery diagnostics, and completion candidate discovery. It does not
include compile-time type inference or unresolved-name diagnostics in the
current scope.

The initial parser scope includes:

- `VbaLanguageServer.Syntax` project and public syntax model skeleton;
- `VbaTokenStream` lexer with source ranges;
- module attributes, module options, and module identity;
- `.bas`, `.cls`, and `.frm` files;
- `.frm` `FormDesignerBlock` raw text and boundaries;
- top-level `ModuleMember`s;
- procedures, properties, events, enums, user-defined types, constants,
  variables, and external `Declare` statements;
- statement and block syntax;
- expression syntax sufficient for member access, argument lists, and
  completion context;
- preprocessor directive syntax nodes for `#Const`, `#If`, `#ElseIf`, `#Else`,
  and `#End If`;
- leading comments, trailing comments, blank lines, attributes,
  `DocumentationComment`s, line continuation markers, and recoverable skipped
  tokens or malformed nodes;
- derived extraction of existing `VbaDefinition`s and `CallableSignature`s from
  `VbaSyntaxTree`;
- language-server integration that routes the current `VbaModuleParser`
  behavior through the new parser core.

The initial parser scope excludes:

- byte-for-byte source reconstruction from the syntax tree;
- preprocessor branch evaluation and inactive-branch semantic filtering;
- form designer control metadata as `VbaDefinition`s;
- expression-level incremental parsing;
- unresolved identifier diagnostics;
- duplicate declaration diagnostics;
- type mismatch diagnostics;
- invalid assignment target diagnostics;
- invalid procedure accessibility diagnostics by module kind;
- broader VBA compiler-compatibility diagnostics;
- DoxyVB6 repository changes, NuGet packaging, or direct DoxyVB6 parser
  replacement.

Deferred semantic and compiler-compatibility diagnostics are tracked separately
in issue #120.

## Acceptance Criteria

- `VbaLanguageServer.Syntax` builds as a separate C# project and has no
  dependency on LSP, VS Code, workbook automation, or `VbaDev` command behavior.
- The lexer emits a source-range-preserving `VbaTokenStream` for keywords,
  identifiers, literals, operators, punctuation, comments, whitespace, newlines,
  line continuations, and preprocessor directives.
- Lexical syntax highlighting can use `VbaTokenStream` even when full parsing
  recovers from malformed or incomplete code.
- The parser emits a `VbaSyntaxTree` with module, module attribute, option,
  `ModuleMember`, declaration, statement, expression, argument-list,
  member-access, preprocessor, comment/trivia, unknown, and malformed node
  coverage.
- The parser keeps `.frm` designer content out of definitions and references
  while preserving `FormDesignerBlock` raw text and source boundaries.
- `DocumentationComment`s, attributes, leading/trailing comments, blank lines,
  line continuations, and recoverable skipped tokens remain available with
  source ranges.
- Parser recovery diagnostics cover unterminated strings, invalid line
  continuations, missing block terminators, malformed declaration headers,
  malformed preprocessor nesting, unexpected statement-boundary tokens, and
  recoverable `.frm` code-section boundary failures.
- `VbaDefinition` and `CallableSignature` extraction is a derived layer over
  `VbaSyntaxTree`, not a responsibility of syntax nodes themselves.
- Existing language-server behavior for document symbols, definition,
  references, hover, signature help, semantic tokens, completion, formatting
  inputs, and diagnostics is preserved or improved through the new parser.
- The current regex scanner no longer owns the main language-server parsing
  path when the PRD is complete.
- Incremental parsing keeps the ADR 0003 `ModuleMember` replacement boundary:
  safe body edits may replace one affected `ModuleMember`, and parser recovery
  or boundary changes fall back to full module parsing.
- Tests include representative Rubberduck-compatible VBA syntax cases without
  copying Rubberduck grammar or implementation.
- Documentation explains how a future DoxyVB6 adapter can consume
  `VbaLanguageServer.Syntax` without making DoxyVB6 integration part of this
  PRD.

## Implementation Plan

1. Add `VbaLanguageServer.Syntax` and the public syntax model skeleton.
2. Implement the `VbaTokenStream` lexer.
3. Parse modules, module attributes, options, module identity, and `.frm`
   designer/code boundaries.
4. Parse declarations and top-level `ModuleMember`s.
5. Parse statements and blocks with recovery diagnostics.
6. Parse expressions, member access, and argument lists for completion context.
7. Add preprocessor directive syntax nodes and nesting recovery.
8. Derive `VbaDefinition` and `CallableSignature` models from `VbaSyntaxTree`.
9. Replace the `VbaModuleParser` language-server path with the new parser core.
10. Add regression coverage for semantic tokens, completion, diagnostics,
    document symbols, definition, references, hover, signature help, and
    formatting inputs.
11. Document DoxyVB6 reuse readiness and the parser-core dependency boundary.

## Relationships

- ADR 0009 keeps editor-intelligence behavior in the C# VbaLanguageServer.
- ADR 0003 keeps incremental parsing at the `ModuleMember` replacement boundary.
- ADR 0010 records the hand-written reusable C# parser-core decision.
- Issue #120 tracks semantic and compiler-compatibility diagnostics that are
  intentionally outside this PRD.
