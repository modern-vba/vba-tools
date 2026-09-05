---
status: accepted
---

# Run tests from command-owned snapshot workbooks

`vba-dev test` accepts an optional caller-owned complete source directory through
`--source-snapshot <snapshot-directory>`. It uses the same recursive source
inventory, flat exported-file identity, form-sidecar, byte-preservation, and
strict snapshot-source encoding contract as snapshot-aware `vba-dev build`.

When the VS Code default test profile produces that directory, it uses the same
`SnapshotSourceInventory` as debug capture: one capture-start disk inventory
overlaid by every then-open in-scope dirty file-backed editor, including an
in-scope path not yet on disk. Each editor value and selected disk file is read
once without a closing stability comparison or automatic retry. Later source
changes belong to the next run; a selected disk path that cannot be read fails
capture. Pathless documents cannot participate. These are producer rules;
`vba-dev test` receives only the completed directory and has no editor-state
dependency.

Issue #344 aligns `build.sourceSnapshot` and `test.sourceSnapshot` at `2.0`
with adapter protocol `2.0` and DAP source-snapshot schema `2`. Before producing
a test snapshot, the extension independently validates both providers and pins
the chosen compatible CLI path for capture and invocation. Ordinary/no-build
commands do not acquire an adapter dependency. `VbaDev` advertises only its
own caller-neutral capabilities and accepts no consumer validation proof.

Dirty source is limited to BOM-marked UTF-8, BOM-marked UTF-16 LE or
BE, and the operation-fixed active Windows ANSI code page without BOM. The
workflow reads that code page directly from `GetACP` once; ACP 65001 is UTF-8,
and any dirty encoding without a BOM is accepted only when it equals the fixed
ACP. Every clean and dirty text source must strict-decode and re-encode to its
original bytes before Excel starts. Detection checks a recognized BOM first,
then only the strict fixed ACP for bytes without a BOM. BOM-less UTF-8 requires
ACP 65001. Clean source retains its exact disk
bytes, `.frx` remains binary-only, and snapshot capture and transport do not
rewrite either. `VbaDev` derives a separate invocation-internal ACP import copy
under ADR 0027 and ADR 0037 without replacement characters or lossy conversion. A decode,
source byte-round-trip, or ACP text-round-trip failure is a command error before
build rather than an optional source-location warning.
`VbaDev` fixes the accepted input in invocation-internal scratch but never
deletes the caller's snapshot directory.

Implementation of this shared encoding contract is gated by the real-Excel
`VBComponents.Import` compatibility probe defined by ADR 0027. Snapshot test
mode must not assume that successful source decoding means Excel imports the
same text. The first Excel 16.0 / ACP 932 probe failed that assumption for
BOM-less UTF-8, BOM-marked UTF-8, and BOM-marked UTF-16 LE and BE. ADR 0027
therefore requires the ACP import copy for snapshot test builds.

Snapshot test mode does not accept `--output`. `VbaDev` creates a unique internal
`SnapshotTestExecutionWorkspace` and materializes its workbook through the same
closed `SourceSnapshotBuild` intent used by snapshot-aware `vba-dev build`.
Test execution opens the exact `CommittedArtifactPath` returned after that
intent has committed output and proved release of its hidden build Excel
process; it does not perform an independent build or read, open, create,
replace, or delete the manifest-defined bin workbook path. Only that path's file
name is reused for the workspace workbook. The source template, references,
project identity, document identity, selectors, result format, and other test
configuration continue to come from the project and ordinary test options.
Internal source and workbook paths remain absent from test output.

The command removes its workspace after success, failed assertions, build or
automation failure, and cancellation. Cleanup occurs only after owned Excel
processes have ended. Failure to prove process release is a command-level
infrastructure error and never becomes an individual test failure. After release
is proved, workspace deletion receives bounded retries. If only deletion still
fails, the command reports the retained absolute workspace path as a warning,
preserves every workbook-owned test outcome, and leaves the exit status
determined by those outcomes. Persistent source, manifest state, and the
manifest-defined bin workbook are never read as source input, created, replaced,
or deleted by snapshot test mode.

For `--format ndjson`, the initial implementation records the schema `1.2`
events in memory and replays them only after process release succeeds. A
process-release failure emits no batch or `runFinished`. A post-release
workspace-deletion warning does not suppress the replay: `runFinished` means
test execution and mandatory process ownership cleanup completed, not that
every temporary file was deleted. True real-time streaming remains a future
schema revision that must represent a later infrastructure failure explicitly.

`--source-snapshot` and `--no-build` are mutually exclusive because a snapshot
has no effect unless it is built; combining them is a usage error rather than
silently ignoring the snapshot. Ordinary `vba-dev test` without a snapshot
continues to build and use manifest-defined bin output. `--no-build` without a
snapshot retains its existing meaning and runs the existing bin workbook. It
has no proved source capture for that artifact, so `VbaDev` does not inspect
project source for navigation, always omits optional source locations, and emits
exactly one fixed non-failing source-location warning for each completed
no-build invocation. The VS Code no-build profile does not save dirty source
before invoking it. Workbook outcomes and test identities remain authoritative
regardless of current source state.

Every test mode accepts
`--timeout-seconds <positive-whole-seconds>` for only its test macro execution
stage. The project-level manifest default is
`commandDefaults.test.executionTimeoutSeconds`; precedence is CLI option,
manifest value, then 600 seconds. Zero, negative, fractional, and infinite
values are invalid. Build, workbook open/save, reference normalization, and
cleanup retain their own stage deadlines. The VS Code caller adds no separate
timeout or shorter watchdog and continues to use ordinary process cancellation.

Snapshot input remains a client-neutral explicit contract rather than implicit
VS Code integration. Test-result identity and outcome remain workbook-owned,
and the existing machine-readable result schema remains in force. Snapshot
paths preserve their original `DocumentSourceSet`-relative layout. `VbaDev`
receives the exact admission paired with the committed snapshot workbook and
copies its module identities, callable declaration-name ranges, and safely
mapped persistent URIs into an immutable `ExecutedSourceIndex` before test
execution. Declaration ranges therefore describe the admitted code that ran,
while URIs identify corresponding persistent paths rather than internal
snapshot or workspace paths. The index is the only location authority and
performs no source read, existence check, ACP acquisition, decoding, or parsing
during result resolution. If a module, procedure, or provenance mapping is
unsafe, missing, or ambiguous, `VbaDev` omits only that optional location,
preserves the workbook-owned identity and outcome, and reports the non-failing
built-run warning. Encoding validity is no longer optional at this point because
snapshot input passed VbaDev's independent pre-Excel admission. Ordinary
build-before-test now creates the same index from the exact saved-source
admission paired with its committed workbook; no-build creates no index.

The optional `testFinished.location` shape and NDJSON schema `1.2` remain
unchanged. True real-time streaming remains issue #155. The index and location
resolution are wholly owned by `VbaDev`; this decision adds no dependency on
the extension, Test Explorer, language server, or debug adapter and does not
change the source-admission encoding policy.

The VS Code caller records the selected document's source/project revision when
it creates the snapshot. Later edits do not cancel the immutable command or
discard its test outcomes. If that revision is no longer current when the run
finishes, the caller omits all output-derived module/procedure discovery and
source locations for that document, retains its project/document runnable
scopes, and reports a non-failing stale-source warning. It does not automatically
rerun the tests or attempt per-file partial publication.

The VS Code caller owns and removes the supplied snapshot directory only after
the `vba-dev` process exits. It applies bounded deletion retries. If only that
post-exit deletion still fails, it reports the retained absolute path in Test
Run output as a housekeeping warning and does not change the completed test
states, CLI-derived run outcome, or error-notification behavior.
