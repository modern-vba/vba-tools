---
status: accepted
---

# Use a hand-written reusable C# VBA parser core

ADR 0039 supersedes the original project name, location, and product ownership
in this decision. The parser is now the product-neutral `VbaTools.Syntax` module
at `tools/vba-syntax/src/VbaTools.Syntax`; the hand-written strategy, syntax
interface, recovery behavior, and lexical decisions below remain accepted.

VbaLanguageServer will replace its regex-based declaration scanner with a
hand-written C# parser core in the separate `VbaTools.Syntax` project.
The parser core will produce a source-range-preserving `VbaTokenStream` and
`VbaSyntaxTree` for the syntax structure needed by syntax highlighting, parser
recovery diagnostics, and completion candidate discovery, while keeping
semantic binding concerns such as unresolved-name diagnostics and compile-time
type inference outside the parser.

Rubberduck's public grammar and declaration-resolution source are a
compatibility reference for VBA syntax coverage, not a dependency or source to
copy. Excel Live Server is not used as a parser design source because no public
source code or developer-oriented detailed design documentation suitable for
parser comparison was available. Keeping the implementation hand-written
preserves control over editor-oriented recovery, `ModuleMember` incremental
parsing, trivia retention, and incomplete-code completion behavior.

The parser core must not depend on LSP, VS Code, DAP, workbook automation,
command behavior, or any product consumer. Its product-neutral
syntax model and public Interface remain reusable enough for a future DoxyVB6
adapter to consume without forcing DoxyVB6 integration into the initial parser
replacement work.

`VbaSyntaxTree.ParseOrUpdate` returns the closed `SyntaxChangeSet` hierarchy.
Each variant carries the complete current tree and exposes only a semantic
reuse proof: `Unchanged`, `ModuleMember`, or `Module`. Constructors are
internal, so external consumers can inspect proofs but cannot manufacture
them. Parser routes, line-difference calculations, fallback reasons,
source-window dimensions, and segment counters remain implementation
observations. Only an unmodified parser-produced previous tree carries the
internal provenance required for an `Unchanged` or `ModuleMember` proof;
publicly constructed or modified trees remain valid inputs but return
`Module`.

The parser core recognizes every MS-VBAL `lex-identifier` form through one
shared lexical authority used by tokenization, parsing, preprocessing,
formatting, and identifier-aware editor features. That authority tracks which
complete forms remain possible across the whole name instead of accepting the
union of their character sets. It also owns identifier boundaries and the
MS-VBAL whitespace distinction where code-page identifier characters differ
from generic Unicode or .NET categories. Recognition does not vary with the
host's active Windows ANSI code page. Typed-name suffixes and `FOREIGN-NAME`
remain separate syntax rather than becoming identifier characters.

Identifier conformance data records the applicable MS-VBAL revision and the
Microsoft code-page mapping provenance. For MS-VBAL revision 2.4, the malformed
CP936 range `%xA1A2A1AA` is interpreted as `%xA1A2-A1AA`, and the statement that
`CP936-subsequent-character` is identical to `CP949-initial-character` is
interpreted as referring to `CP936-initial-character`. These interpretations
remain explicit and versioned rather than silently substituting CP949 or generic
Unicode categories. Corresponding-ACP VBE compatibility checks are separate
validation work; an observed implementation difference requires an explicit
compatibility decision.

The production implementation remains generalized across every supported
identifier form and must not encode Japanese-only control flow. Automated
coverage concentrates on Japanese identifiers, supplemented only by a compact
multilingual sentinel set needed to prove the shared form model, form-specific
whole-name validation, CP2 word and whitespace distinctions, and the documented
CP936 interpretations. It does not attempt exhaustive per-code-point tests for
every legacy code page.

Every consumer of the MS-VBAL `IDENTIFIER` production composes the shared
whole-name form authority with the complete case-insensitive
`reserved-identifier` set instead of maintaining a local keyword list. A
`RenameName` is validated exactly as supplied, must contain 1 through 255
characters, and must be an `IDENTIFIER`; typed-name suffixes and `FOREIGN-NAME`
are rejected rather than stripped or normalized. Declaration and Rename
validation therefore cannot diverge from tokenization as identifier forms
expand.

## Considered Options

- Keep the current regex scanner and add targeted fixes. This is too brittle
  for statements, expressions, line continuations, preprocessor blocks, and
  parser recovery.
- Adopt ANTLR or another grammar generator. This improves grammar coverage, but
  makes incomplete-code recovery, source trivia retention, and `ModuleMember`
  incremental parsing harder to keep aligned with editor feature needs.
- Depend on Rubberduck's parser. Rubberduck is useful as a compatibility
  reference, but directly depending on or copying its parser would couple this
  repository to another product's parser architecture and licensing surface.

## Consequences

`VbaTools.Syntax` is the product-neutral parser ownership Seam. Language
server features derive `VbaDefinition`s, `CallableSignature`s,
`SyntaxDiagnostic`s, semantic tokens, completion context, and formatting inputs
from `VbaSyntaxTree` and consume `SyntaxChangeSet` during projection instead of
scanning source text directly. Identifier-aware consumers must use the shared
MS-VBAL authority instead of local ASCII regexes, `\b`, or generic Unicode
letter and whitespace predicates. The initial
parser scope does not include unresolved-name diagnostics, duplicate
declaration diagnostics, type mismatch diagnostics, invalid assignment target
diagnostics, or broader VBA compiler-compatibility diagnostics.
