---
status: accepted
---

# Separate syntax and validation diagnostics

VbaLanguageServer will keep `SyntaxDiagnostic`s limited to parser recovery and
malformed VBA source structure, while reporting parsed-source validity rules as
`VbaValidationDiagnostic`s. LSP `textDocument/publishDiagnostics` may publish
both diagnostic kinds together, but collectors remain separate so
document-local validation rules can ship before project-aware diagnostics such
as unresolved identifiers, duplicate declarations, and type mismatch.

Diagnostics publication is latest-only for a source URI. A committed document
analysis may enqueue a `textDocument/publishDiagnostics` notification, but the
publisher rechecks the captured document lifecycle epoch, reservation token,
and client document version before sending. If a later analysis, close
tombstone, or close/reopen lifecycle supersedes that captured revision, the
queued publication is skipped. This stale-revision suppression keeps V1
diagnostics from following and overwriting V2 diagnostics.

Publication is also transport-decoupled from document mutation. `didChange`
commits analysis before diagnostics serialization or transport I/O completes,
and a stalled diagnostics write must not block mutation admission or
latency-sensitive editor queries. When the client document version is known,
`publishDiagnostics` includes that version so a version-aware client can reject
late diagnostics that were already in flight before the newer revision existed.

## Consequences

Diagnostic codes use separate namespaces: `syntax.*` for `SyntaxDiagnostic`s
and `validation.*` for `VbaValidationDiagnostic`s. Document-local validation can
consume only `VbaSyntaxTree`, while future project-aware validation can consume
`VbaProjectSnapshot`, `NameResolution`, `TypeResolution`,
`VbaProjectReferenceSelection`, and available `VbaProjectReferenceCatalog`s
without blocking the initial validation slice.

Initial diagnostic codes follow the glossary distinction between declaration
`CallableParameter`s and call-site `CallArgument`s:

- `syntax.raiseEventArgumentListRequiresParentheses`
- `validation.duplicateCallableParameterName`
- `validation.duplicateNamedCallArgument`
- `validation.positionalCallArgumentAfterNamed`

Validation collectors should consume structured syntax nodes instead of
re-parsing source text. Duplicate named call arguments and positional call
arguments after named call arguments therefore require `VbaArgumentSyntax` to
model positional, named, and omitted `CallArgument`s before those validation
rules are implemented.

Document-local validation collectors take `VbaSyntaxTree` and the document URI
as their initial inputs. They should not take the raw document text unless a
future syntax-tree source-slice API proves insufficient. Project-aware
validation remains a separate collector that can consume project snapshots and
semantic resolution state.

Close and delete events publish an empty diagnostics tombstone through the same
latest-only publisher path. A close tombstone may supersede queued analysis
diagnostics, and a later reopen of the same URI starts a new lifecycle so
diagnostics captured from the earlier lifecycle cannot enter the reopened
document.
