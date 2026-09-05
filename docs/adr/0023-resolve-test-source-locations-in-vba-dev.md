---
status: accepted
---

# Resolve test source locations in VbaDev

`VbaDev test` emits an optional `TestProcedureSourceLocation` on
`testFinished` by resolving each reported test identity against an immutable
`ExecutedSourceIndex`. `VscodeExtension`
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
`DocumentSourceSet`-relative layout. Ordinary build-before-test and snapshot
test both receive the exact `VbaSourceAdmission` returned with the workbook
materialization that succeeded. Before test execution, `VbaDev` copies only its
module identities, callable declaration-name ranges, and safely mapped
persistent source URIs into an immutable `ExecutedSourceIndex`. The index is the
sole location authority for that workbook. It retains no path-backed content
authority, and location resolution performs no source inventory, file read,
existence check, encoding detection, decode, or parse. Editing, replacing, or
deleting authoring source after materialization therefore cannot change the
locations reported for that run.

For snapshot input, declaration ranges come from the admitted snapshot bytes
while persistent URIs are derived from their preserved
`DocumentSourceSet`-relative provenance; internal workspace paths never appear
in results. For ordinary build-before-test, both ranges and persistent URIs are
derived from the saved-source admission that produced the committed bin
workbook. Unsafe, missing, or ambiguous module, procedure, or provenance
mapping omits only the optional location. The executed identity and outcome
remain unchanged, and the completed built run reports a deterministic
non-failing source-location warning for each distinct unresolved test identity.

`test --no-build` intentionally has no proved source capture for the existing
bin workbook. It never constructs an `ExecutedSourceIndex`, never inspects the
current project source for navigation, and always omits every optional source
location, whether the working source is clean, dirty, changed, or absent. Each
completed no-build invocation emits exactly one fixed non-failing warning:
`Warning: Source locations were omitted because --no-build runs an existing workbook without a proved source capture.`
A usage or infrastructure failure that prevents a completed test-result run
does not gain that warning.

These changes do not alter ADR 0037's BOM-or-fixed-ACP admission policy. Built
test source must still pass strict decode, exact byte round trip, and lossless
ACP projection before Excel starts; `ExecutedSourceIndex` only consumes the
already-admitted syntax and provenance. The `testFinished.location` object
remains optional with its existing URI and zero-based UTF-16 range shape, and
the NDJSON schema version remains `1.2`. True event streaming remains deferred
to issue #155. `VbaDev` owns this index and result behavior without depending on
`VscodeExtension`, Test Explorer, the language server, or the debug adapter.

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
