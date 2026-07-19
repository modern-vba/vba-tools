---
status: accepted
---

# Own exact document analysis revisions

## Context

The C# language server previously stored open-buffer text and syntax together,
but document-local source definitions and diagnostics were rebuilt by later
features. Guarded Enter created an exact-version snapshot by projecting some of
those artifacts again. A document change could also perform parsing while
holding the workspace state lock, and overlapping builds had no revision
identity with which to reject an older completion.

ADR 0011 separates parser recovery diagnostics from validation diagnostics.
ADR 0012 requires guarded Enter to use an exact open-document version and to
fail closed within the extension's native-first deadline. ADR 0013 keeps
interactive mutations causally ordered, but the workspace must still be safe
when tests, future schedulers, or other callers overlap document analysis.

## Decision

`VbaDocumentAnalysis` is the immutable owner of all document-local artifacts
derived from one source state:

- canonical URI and complete text;
- `VbaSourceText` coordinates;
- syntax tree and module kind;
- projected source definitions and semantic shape;
- category-preserving syntax and document-validation diagnostics; and
- parse granularity plus safe `ModuleMember` update metadata.

The workspace reserves an accepted revision under its state lock, builds the
analysis outside that lock, and performs a short compare-and-commit. A
reservation carries document authority, client version, lifecycle epoch, and a
monotonic reservation token. Commit succeeds only while all four still match
the accepted head. A newer revision, close, reopen, deletion, or competing disk
reload therefore prevents an older build from becoming authoritative.

The accepted version is a high-water mark even when analysis fails. Failure
does not expose artifacts from the previously committed version as if they
belonged to the failed version, and a later higher version can recover. Reopen
starts a new lifecycle epoch, including when the client reuses the same version
number.

`VbaVersionedDocumentSnapshot` is created once from the committed analysis.
An exact read succeeds only when the requested version, lifecycle epoch, and
reservation token all describe the current accepted open-buffer revision and
no build is pending. A returned snapshot pins its analysis by ordinary strong
reference. The workspace retains one committed analysis and one accepted-head
record per tracked source identity; it does not retain an unbounded revision
history.

Diagnostics publication consumes the committed analysis diagnostics rather
than collecting them again. Syntax diagnostics and document-validation
diagnostics remain separate collections until the LSP projection boundary;
project-aware diagnostics remain outside document analysis.

Guarded Enter captures only `VbaVersionedDocumentSnapshot`. It does not resolve
projects or manifests, access disk, inspect reference catalogs, start
discovery, or read live Office application state. A narrow declaration-tail
proof may trust artifacts only when they retain reference identity with their
owning analysis. Hand-constructed or mixed snapshots use the existing
conservative speculative proof.

Document analysis itself is host-neutral. The currently shipped document
experience targets Excel projects, while future Word, PowerPoint, or other VBA
hosts can supply project and catalog inputs at their existing boundaries
without adding host conditions to document analysis.

## Considered options

- Rebuilding definitions or diagnostics in each feature permits mixed-version
  artifacts and repeats work on the editor path.
- Building analysis while holding the workspace lock makes unrelated reads wait
  for parsing and validation.
- Comparing only client version does not prevent close/reopen ABA when a client
  reuses a version number.
- Keeping every completed revision in a workspace history simplifies old reads
  but creates unbounded retention and contradicts exact-current semantics.
- Treating syntax and validation diagnostics as one analysis-stage list would
  erase the diagnostic ownership boundary established by ADR 0011.

## Consequences

Features can share exact text, coordinates, syntax, source definitions,
diagnostics, and incremental metadata without reprojection. While a newer build
is pending, guarded Enter returns no plan for either the superseded committed
version or the not-yet-committed version.

Overlapping builds may consume CPU until later coalescing work is introduced,
but only the accepted head can commit. The existing full-text LSP
synchronization contract remains unchanged.

Performance tests use an 8,000-line fixture and record document-analysis and
block-skeleton percentiles in Release. Replacing the current full-length
masked-source `ModuleMember` parser is a separate parser decision; exact
analysis ownership neither weakens its fallback rules nor introduces a second
parser.
