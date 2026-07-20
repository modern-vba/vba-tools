---
status: accepted
---

# Use a hand-written reusable C# VBA parser core

VbaLanguageServer will replace its regex-based declaration scanner with a
hand-written C# parser core in a separate `VbaLanguageServer.Syntax` project.
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

The parser core must not depend on LSP, VS Code, workbook automation, or
`VbaDev` command behavior. It is owned by VbaLanguageServer for now, but its
syntax model and public Interface should remain reusable enough for a future DoxyVB6
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

`VbaLanguageServer.Syntax` becomes the parser ownership Seam. Language
server features derive `VbaDefinition`s, `CallableSignature`s,
`SyntaxDiagnostic`s, semantic tokens, completion context, and formatting inputs
from `VbaSyntaxTree` and consume `SyntaxChangeSet` during projection instead of
scanning source text directly. The initial
parser scope does not include unresolved-name diagnostics, duplicate
declaration diagnostics, type mismatch diagnostics, invalid assignment target
diagnostics, or broader VBA compiler-compatibility diagnostics.
