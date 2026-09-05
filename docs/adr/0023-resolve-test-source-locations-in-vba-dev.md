---
status: accepted
---

# Resolve test source locations in VbaDev

`VbaDev test` resolves each reported test identity against the selected
`DocumentSourceSet` with the reusable C# VBA syntax model and emits the
resulting `TestProcedureSourceLocation` on `testFinished`. `VscodeExtension`
only projects that location into the matching procedure node and test message;
project, document, and module nodes remain runnable scopes without a precise
source target. The extension does not parse VBA or query the running language
server for test identity. Resolution matches the case-insensitive workbook
module name to `ModuleIdentity` and the reported procedure name to one parsed
procedure declaration; the executed test identity remains authoritative, so
`VbaDev` does not duplicate the test framework's discovery signature.

Missing or ambiguous source locations are omitted without changing the test
outcome, because navigation metadata must not turn a completed test into a
`TestRunError`. The emitted location belongs to the output-derived
`TestDiscoverySnapshot`; changing the owning document's exported VBA source or
project definition invalidates its module and procedure nodes until another test
run creates a fresh snapshot. When a location is missing or ambiguous, the
procedure node and test outcome remain available and the extension appends a
non-failing source-location warning to Test Run output without setting a
discovery error or showing a popup.

Under ADR 0026, a default Test Explorer run materializes unsaved editor state in
a complete snapshot directory whose paths preserve the original
`DocumentSourceSet`-relative layout. Under issue #344 and ADR 0037, `VbaDev`
uses the same admitted snapshot syntax as Build/Test, without another source
read, ACP acquisition, or decode, so the emitted UTF-16 declaration range describes the code that
actually ran. It combines that range with the corresponding persistent source
URI derived from the relative path; it never emits an internal workspace URI
that would expire during cleanup. Unsafe, missing, or ambiguous provenance
omits the optional location. Ordinary test without a snapshot resolves against
saved source. A no-build run never saves dirty source or captures a snapshot; it
may intentionally execute older generated code. When scoped source is clean,
its navigation target remains current saved source. When scoped source is dirty
at invocation start, outcomes and workbook-reported test identities remain
visible but source locations are omitted with a non-failing warning because
saved-source ranges may not identify the current editor text. Ordinary/no-build source decoding
recognizes UTF-8 with or without BOM, BOM-marked UTF-16 LE or BE, and the
operation-fixed active Windows ANSI code page without BOM. It checks a
recognized BOM first, then strict UTF-8, then the strict ACP, and never
substitutes replacement characters; that lookup path is not migrated by #344.
Snapshot Build/Test instead uses BOM-or-fixed-ACP admission, with no UTF-8
probe, and must pass strict decode, exact byte round trip, and lossless ACP
projection before Excel starts. For ordinary or
`--no-build` source that was not prevalidated, a decoding failure omits only the
optional source location with the existing non-failing warning; it does not
change an executed test outcome.

The client records one document-level source and project revision when it
captures a snapshot. Editing during the run does not cancel or restart the
immutable test execution, and completed outcomes remain visible as results for
the captured source. Before committing output-derived module/procedure nodes or
locations, the client compares the captured revision with current state. Any
change within the selected document or its project definition invalidates the
whole resulting `TestDiscoverySnapshot`: project and document scopes remain
runnable, but procedure discovery and navigation are not committed. Test Run
output receives a non-failing stale-source warning without a popup, and a later
run may create a fresh discovery snapshot. The initial implementation does not
attempt per-file partial reuse.
