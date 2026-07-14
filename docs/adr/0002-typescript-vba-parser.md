---
status: superseded
superseded_by: 0009-csharp-vba-language-server-architecture
---

# TypeScript VBA parser

This ADR described the earlier plan to build and maintain a VBA AST inside the
TypeScript language-server process instead of invoking DoxyVB6 or another
external parser at request time. It is retained only as historical context.

The accepted direction is a C#-only VbaLanguageServer runtime, parser,
`VbaProject` state, `LanguageServerManifestResolution`, semantic model, and LSP
feature implementation, with TypeScript kept for the `VscodeExtension` adapter,
as described by ADR 0009.
