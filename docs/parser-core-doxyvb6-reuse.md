# Parser Core DoxyVB6 Reuse Boundary

## Purpose

The Full VbaSyntaxTree Parser Core PRD treats `VbaLanguageServer.Syntax` as a
`ReusableVbaParserCore`. VbaLanguageServer owns the first consumer, but the
syntax model should remain usable by a future DoxyVB6 adapter that needs VBA
module structure and source ranges for documentation generation.

This document records that reuse boundary. It complements ADR 0010, which
selects a hand-written reusable C# parser core and keeps direct DoxyVB6
integration outside the initial parser replacement work.

## Dependency Boundary

`VbaLanguageServer.Syntax` is the dependency boundary for reusable parsing. It
must not depend on:

- LSP request or response types;
- VS Code extension APIs;
- workbook automation or Office COM automation;
- `VbaDev` command behavior;
- VbaLanguageServer feature services such as completion, hover, signature help,
  semantic tokens, formatting, or reference catalog resolution.

Consumers may reference `VbaLanguageServer.Syntax` for lexical and syntactic VBA
structure. VbaLanguageServer-specific layers derive `VbaDefinition`,
`CallableSignature`, diagnostics projection, semantic tokens, and LSP responses
from that syntax model outside the parser core.

## DoxyVB6 Adapter Inputs

A future DoxyVB6 adapter may consume the following syntax information from
`VbaSyntaxTree` and related parser-core types:

- `VbaModuleSyntax` for module kind, `ModuleIdentity`, top-level
  `ModuleMember`s, and code ranges;
- `VbaModuleAttributeSyntax` and `VbaModuleOptionSyntax` for exported module
  metadata such as `Attribute VB_Name`;
- `VbaDeclarationSyntax` and `VbaCallableDeclarationSyntax` for procedures,
  properties, events, enums, user-defined types, constants, variables,
  parameters, external `Declare` statements, type annotations, visibility, and
  source ranges;
- `DocumentationComment`-derived text preserved on declarations and callable
  signatures;
- `VbaSyntaxRange` and `VbaSyntaxPosition` values for linking generated
  documentation back to source locations;
- `.frm` `FormDesignerBlock` raw text and boundaries when documentation needs
  to acknowledge form modules without treating designer properties as ordinary
  code declarations;
- `VbaTokenStream` when documentation tooling needs trivia-sensitive lexical
  information such as comments, line continuations, or preprocessor directives.

The adapter should translate parser-core syntax nodes into DoxyVB6's own
documentation model. It should not require the parser core to know about
Doxygen output formats, DoxyVB6 repository layout, Excel workbook import/export
workflows, or VbaLanguageServer LSP behavior.

## Out Of Scope

The Full VbaSyntaxTree Parser Core PRD does not include:

- changes in the DoxyVB6 repository;
- NuGet packaging for `VbaLanguageServer.Syntax`;
- replacing DoxyVB6's current parser directly;
- a DoxyVB6 adapter implementation;
- DoxyVB6-specific diagnostics or documentation rendering behavior.

Those items may become future work after the parser core is stable enough to
consume outside VbaLanguageServer.

## Related Documents

- PRD: `docs/prd/full-vba-syntax-tree.md`
- ADR 0010: `docs/adr/0010-use-hand-written-reusable-csharp-vba-parser-core.md`
- ADR 0009: `docs/adr/0009-csharp-vba-language-server-architecture.md`
