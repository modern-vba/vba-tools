# Source-analysis failure evidence

Issue #415 concerns a recurring managed exception whose underlying cause is not
yet established. Retrying successfully is not proof of a fix. This capture
supports later investigation; it does not repair the exception or retry a build.

## Automatic capture

Build, snapshot build, publish, and build-before-test save a local JSON report
when their captured source analysis contains a non-cancellation processing
exception. Ordinary syntax/validation diagnostics and cancellation-only failures
do not create reports. The default directory is:

```text
%LOCALAPPDATA%\VbaTools\Diagnostics\source-analysis
```

The CLI writes `Source-analysis failure evidence saved: <absolute path>` on
stderr. VS Code retains it in VBA Tools Output. Each invocation gets a unique
timestamped filename. The store retains the newest 20 of its own completed JSON
files, at most 2 MiB each; concurrent processes may temporarily exceed that count.
It does not recursively delete directories or delete unrelated files. Failure
to save or prune evidence produces a warning without replacing the primary
failure, changing its exit status, or claiming successful workbook generation.
If saving fails, preserve stderr, which includes bounded exception details.

For a local correlated diagnostic run, the launcher may set
`VBA_TOOLS_DIAGNOSTIC_RUN_ROOT` to an absolute, unique run directory whose final
name has the form `run-YYYYMMDDTHHmmssfffZ-<16 lowercase hex digits>`. Failure
reports then go to its `source-analysis` child, with that validated run name in
the JSON `diagnosticRunId` field. Retention remains the newest 20 completed
reports **within that child**, without pruning another run's files. An absent
variable keeps the default directory and report shape. An invalid run root
produces a warning and bounded stderr fallback instead of redirecting output
or hiding the source-analysis failure. Successful analysis and cancellation-only
failures still create no report or run directory.

## Evidence and limitations

The local report uses schema `1.0`, independent of the public `sourceAnalysis`
schema `3.0`. It records:

- UTC timestamp and invocation ID; project, document, command, and admission purpose.
- Original exception type, HResult, stack and inner-exception details, processing
  phase, and active source path when known.
- Successfully parsed source URIs, character counts, and SHA-256 hashes of the
  immutable parser text encoded as UTF-8. These are **not original-file byte hashes**.
- Successfully acquired reference/TypeLib identities and acquisition counts.
- OS/runtime/architecture, loaded assembly versions and MVIDs, and the executable
  file hash observed after failure (or an explicit unavailable reason).
- Explicit truncation indicators for bounded source, reference, and failure lists
  and text. Only acquired evidence is reported; missing data is not success.

Sources are not reopened or copied. A decode/parse failure can therefore leave
no tree/hash for the active source. Template contents, source text, raw environment
variable values, workbook bytes, and native dumps are not collected. The validated
run ID is the only environment-derived value added during an opt-in correlated run.
Exception messages may themselves include application-provided content. Paths
and reference names can be sensitive: inspect and redact a report before sharing
it. Nothing is uploaded automatically. Copy important reports outside the
retention directory before enough later failures can remove them.

The run ID allows this handled source-analysis failure to be matched with other
local evidence from the same launcher invocation. It does not imply that a
native process crash or a different failure was captured by this recorder.

This is a handled-managed-exception recorder, not crash monitoring. A native
access violation, process kill, stack overflow, or out-of-memory termination may
prevent it from running. The native crash investigation in #409 remains separate;
no shared cause is assumed. A report identifies inputs and a failing stage, but
is not a complete replay package and may not identify the individual expression
or URI involved inside project-wide analysis.

The opt-in `SourceAnalysisUriResolutionWindowsProbeTests` exact-input probe calls
shared analysis directly and does not use the CLI evidence store. If URI
identification throws there, its local xUnit output includes
`probeUriIdentification=` with bounded UTF-16 code units and available origin
context. Preserve that test output before another run. The probe does not write
a JSON report or change the project, and absence of this field on a successful
run is not evidence that the historical fault is fixed.

## After recurrence

1. Preserve the indicated JSON and corresponding Output/stderr before retrying.
   Note the command, approximate frequency, and whether a retry succeeded.
2. Preserve the exact source revision and local modifications separately. Hashes
   identify captured input but cannot recover source contents after edits. Do not
   attach private source or workbook data without reviewing what will be shared.
3. Compare phases, stacks, assembly MVIDs, parser-text hashes, and reference
   identities across failures and known successful versions. An incomplete
   semantic-input acquisition is not evidence that no Excel/COM work occurred.
4. For a follow-up reproducer, use a separate source snapshot and an explicit
   output outside the real project's source, template, bin, and publish paths.
   Do not overwrite the real outputs merely to retry the failure. Reproduction
   may still require owned Excel metadata discovery; obtain the usual approval.
5. Add reviewed recurrence evidence to #415. Keep the issue open until the actual
   cause and regression behavior are established, not merely because logging works.

The opt-in #415 URI reproducer (`scripts/source-identity-repro/README.md`)
accepts either its sanitized synthetic fixture or a private exact failure receipt.
Its bounded stress loop is not part of ordinary test or release gates.

## Sanitized URI isolation on 2026-09-26

With the captured URI pair's five-character account segment replaced by `local`,
the frozen SourceIdentity DLL (SHA-256
`91D812876BAF5B45909635BD6D7D990D86E61D290DE97749BD41A247C13EECDF`)
on Windows .NET 10.0.8 failed with `ArgumentOutOfRangeException` in
`String.SplitInternal` in two of six fresh child processes, each bounded to
5,000,000 identity iterations; four passed. The full original private receipt
also replayed successfully once, which is only a non-reproduction. The sanitized
fixture retains the URI lengths (269 and 276 UTF-16 code units) and comparison
order. It contains no personal account name.

The failing-side decoded remainder was 121 UTF-16 code units, with slash offsets
5, 11, 18, 24, 47, 61, 71, 75, 85, and 100. A separate ignored local control
called only `remainder.Split('/', StringSplitOptions.RemoveEmptyEntries)` on that
exact sanitized remainder: six fresh processes and 30,000,000 total calls all
passed. This negative control narrows the observation but does not exonerate
`String.Split`, establish a product defect, or prove a fix.

A signed ProcDump exact-child run with `-ma -e 1 -f '*ArgumentOutOfRangeException*'
-n 1 -x` produced one local full dump on its first bounded attempt, after the
child reported 2,300,000 completed iterations. The dump's exception object and
generated stack confirm `ArgumentOutOfRangeException` through
`String.SplitInternal`, `SourceIdentity.NormalizeSegments`, and `TryFromUri`.
However, the dump comment says `Unhandled exception`, and CDB reports that the
first/second-chance distinction is unavailable. The live stack is at later
exception propagation, so this dump does **not** establish first-chance capture
or reveal the failing split indices, length, or separator list. ProcDump exited
with code 1; the child's exit code was not observed and must not be inferred.
The dump (SHA-256
`FDB436BB81B89C4CC89E8ED477681BCE2687A8A4E9DA4BA31FE622CF5882B1B7`),
trial reports, and private receipt remain only under ignored local `.tmp` paths.
The actual root cause, corrective action, and regression boundary remain open.

## Live first-chance follow-up

The ProcDump limitation above led to a separate **live** CDB run with SOS
`!soe -create System.ArgumentOutOfRangeException 1`. A short startup preflight
confirmed that the type-filtered first-chance break was installed. With the
same frozen DLL and sanitized fixture, one five-million-iteration child passed;
the next stopped at a first-chance CLR notification after 1,000,000 reported
iterations. The live managed and native stacks still contained
`String.SplitInternal` and `SourceIdentity.NormalizeSegments`. A single local
full-memory user dump was saved at that stop (SHA-256
`4A5EBE9C67B0803EB8376E7F54A52E6221517DFDF2511C1FD0E5FF08D0E51E34`).
The owned child was then terminated under the debugger, not resumed.

Offline inspection found a 121-code-unit input string and the expected ten
ascending separator offsets in the live stack buffer. The allocated 11-element
result array had no populated elements. The optimized .NET 10.0.8 Tier1 code
has three bounds-check branches converging on the same `start`
`ArgumentOutOfRangeException` helper; the dump does not identify the branch or
retain its exact volatile start/length operands. A recovered callee-saved
register conflicts with the stored separator-buffer pointer, but its provenance
at the throw is not established. Neither a corrupted separator list nor a
particular bad index is proven. A new controlled breakpoint at the bounds-check
branch, before the helper call, is needed to distinguish those possibilities.
The dump and debugger log remain ignored and local; the historical semantic
`System.Uri` NullReferenceException has not thereby been reproduced or fixed.

The 2026-09-11 stack belongs to the older `ef2c35b` identity implementation:
`TryIdentifyDocument` admitted a file URI with `Uri.TryCreate`, then
`TryGetLocalPath(string)` constructed a second `new Uri(uri)` for that same
string. Commit `a78ea0a` removed the second parse from that call path by
reusing the admitted `Uri`; later shared lexical identity work changed the
file-path implementation again. This establishes that the exact historical
second-parse call is absent from current semantic identity admission, not why
the runtime threw or which URI triggered it. A generated non-file reference
URI is not supported as the historical second-parse input by that stack alone.
The managed NRE and newer `String.SplitInternal` exception remain distinct
observations until an exact failing input or shared causal evidence is found.

## Scoped segment-scan compatibility trial

`SourceIdentity.NormalizeSegments` now scans the decoded path remainder without
calling `String.SplitInternal`. It retains empty-segment removal, `.` and `..`
normalization, and root clamping. This avoids the runtime call on the newly
observed `ArgumentOutOfRangeException` path; it does not establish why that
runtime call failed or address the historical semantic `System.Uri`
`NullReferenceException`. The sanitized failure pair is included as a
deterministic identity test; it does not reproduce the intermittent crash by
itself. The high-volume reproducer remains opt-in.

On the same Windows .NET 10.0.8 host, the new SourceIdentity DLL (SHA-256
`BA862E205AF0641814AA689A0802DF439653B9EAE5497E3CF08475FB1B01A6DF`)
completed ten fresh child runs of five million identity iterations each with
the sanitized pair: 50,000,000 calls, ten passes, no observed exceptions. The
frozen pre-change DLL had failed in two of six analogous runs. These bounded
observations support the mitigation but do not prove long-term stability or a
root cause. A three-trial read-only semantic-analysis probe of the affected
six-TypeLib BFW project also passed. A later ten-trial run completed with zero
diagnostics on each trial and unchanged project-tree and Excel-process checks.
After adding SourceIdentity to the probe's assembly inventory, a further
one-trial run passed and recorded the loaded test-path SourceIdentity DLL as
SHA-256 `AB58698598A61EF7F345D82C9B6230F74B4FBDE4DF4E41E9EFC49253121C2DCA`.
That DLL is distinct from the Release standalone reproducer DLL above. These
are non-reproductions, not proof that the historical semantic failure is gone.
Keep #415 open for the original failure's cause and regression boundary, and
keep the local dump private.

The freshly published Windows vba-dev executable (SHA-256
`DD9C25D5F383B3DCCAABC1AEB890A50DCDD8444D8901821EDB03179790EB6CBC`)
also completed one ordinary `build --project` against an isolated copy of the
affected BFW project. The copy contained the same 41 files and 2,468,110
bytes before Build; the command imported 35 source files and wrote a nonempty
workbook only under the ignored diagnostic copy. The original bin workbook
retained its pre-run length and UTC modification time, no Excel process
remained, and the original repository status was unchanged. Because the copy
has a different absolute path, this validates the published CLI workflow but
does not replay the historical URI spellings exactly or prove that a rare NRE
cannot recur.

## Exact BFW input and process-boundary investigation on 2026-09-26

The opt-in, read-only `SourceAnalysisUriResolutionWindowsProbeTests` used the
original BFW source root and installed TypeLibs on Windows .NET 10.0.8. It
admitted 35 source trees and six catalogs with 16,246 active definitions; the
successful trials produced zero diagnostics. It compared the original project
tree (41 files, 2,468,110 bytes) and Excel process IDs before and after each
test. Fresh Debug test hosts ran at most ten `Analyze` calls each, rather than
copying sources or building a workbook.

With normal tiered compilation, intermittent `NullReferenceException`s arose
in different managed paths, including syntax-position lookup, lexer advance,
semantic resolution, and callable-signature presentation. One monitored trial
instead returned `projectFatal=true` during source admission, before any
`Analyze` call; its then-current assertion omitted the original exception
details. Another test host terminated with `Internal CLR error (0x80131506)`
while parsing source, and its exact-child ProcDump monitor observed
`C0000005.ACCESS_VIOLATION` without saving a dump. These are distinct
observations, not multiple captures of the historical `System.Uri`
`NullReferenceException`; no failing URI was acquired in these trials. The
completed successful and handled-failure trials checked the original project
tree and Excel-process state. The fatal crash exited before the test's
after-state checks, so its immediate invariants were not recorded; subsequent
trials observed the original tree hash again. Local logs remain under ignored
`.tmp/diagnostic-verification/issue-415-*` directories.

As one controlled comparison, the same Debug DLL with
`DOTNET_TieredCompilation=0` completed ten fresh processes with ten analyses
each (100/100), while normal-tiered trials had failed. That finite
non-reproduction is a reason to investigate runtime/code-generation or
process-history effects; it does not establish a JIT, CLR, hardware, COM, or
product-code root cause, nor does it prove that disabling tiering is a fix.
An earlier lexical access violation was also observed with tiering disabled.

For managed `NullReferenceException` and native access-violation follow-up,
a local signed ProcDump was attached to the **exact owned** Debug
`testhost.exe` PID after validating its executable path and start time. The
monitor used a first-chance filter, `-ma`, and `-n 1`, with a finite test and
monitor lifetime; it did not register a machine-wide handler. Initial monitored
processes either passed, failed before analysis, or crashed without producing
a matching dump. A monitor reporting no dump is not proof that no exception
occurred: the preceding admission failure had no captured exception detail,
and the observed CLR fatal error did not satisfy the saved-dump condition.
Stop at the first matching full dump, verify its size/hash and exception context,
and keep it local; dumps can contain private source, paths, and secrets. Do not
upload the dump or treat a monitored test as a release-gate pass.

To separate installed TypeLib acquisition history from later managed analysis,
an opt-in `SourceAnalysisTypeLibReplayWindowsProbeTests` captured a baseline
and six `input.json` TypeLib metadata snapshots in an ignored local directory.
The baseline (SHA-256
`5C86F4E457BE9E45E840743E6C2062C30C0E453B22107847DAAFA013204CEEB2`)
records the ordered 35 source URI/raw-UTF-16 hashes, catalog input hashes,
selection, 16,246 active definitions, and zero-diagnostic fingerprint. Each of
six separate fresh test hosts re-read the **same original source root**,
verified those hashes, rebuilt the six catalogs from the snapshots without
COM or registry discovery, and completed ten analyses with matching results
(60/60). This finite replay is a non-reproduction, not proof that COM history
causes the other failures. A future COM-free failure with matching baseline
and input fingerprints would show that prior COM acquisition is unnecessary;
continued passes would not establish the converse. The snapshots contain
private TypeLib metadata and absolute paths; keep them local. None of these
observations resolves #415 or satisfies its original-URI regression criterion.

A later original-input control independently acquired the installed TypeLibs
in five fresh Debug test hosts and completed ten analyses per host (50/50),
again with unchanged BFW project-tree and Excel-process checks. Thus this
bounded comparison saw no failure on either the COM-backed (50/50) or
COM-free (60/60) path. It provides no causal distinction between them and is
not a correction or release-gate result.

A third, acquisition-conditioned arm performed the installed TypeLib acquisition
first, verified that all six acquired identities and serialized metadata hashes
matched the frozen baseline, then analyzed with the **frozen** catalogs rather
than reusing the acquired live catalogs. Its ordered 35-source URI/raw-UTF-16
fingerprint was SHA-256
`DA3786223771D8556A61BBB9E66F50CD221B3D3E955495AF2C5B7E1DBA044AFE`.
Three fresh Debug hosts each completed ten analyses, and one post-build host
completed three more (33/33); every trial had zero diagnostics and the baseline
diagnostic SHA-256
`4F53CDA18C2BAA0C0354BB5F9A3ECBE5ED12AB4D8E11BA873C2F11161202B945`.
The earlier COM-free and COM-backed arms used a prior test-assembly build but
the same product DLLs. Since all three bounded arms passed, this experiment
does not distinguish their process histories, identify a cause, or complete
#415's original-URI regression criterion.

## First-chance lexer fault dump on 2026-09-26

In the exact-input BFW probe's `issue-415-nre-procdump-20260926-j01` run,
the first eight of ten analyses completed with zero diagnostics. Trial 9
failed with `NullReferenceException` at `VbaLexer.ReadIdentifierOrKeyword`
line 339. The post-test project tree remained 41 files with SHA-256
`038839696C32C2D0DED55672FCD4BC19D6C5EC44EDC205087B1983B5D551C772`;
Excel process IDs were unchanged.

The exact-child ProcDump monitor was configured for first-chance exceptions.
It logged `C0000005.ACCESS_VIOLATION`, a 350 MB full dump completed in 0.3
seconds, and its one-dump limit reached. The monitor nevertheless exited 1
and its wrapper reported `dumpFinalized=false`; the completed file was
independently verified at 358,058,938 bytes with SHA-256
`E91E2B2A25DC68070F5FF0905120D0997399C520AED9FDE6A1E972C9ADC09A74`.
ProcDump's dump comment calls this first-chance, while CDB's generic exception
display says the first/second-chance distinction is unavailable offline.

At the saved fault, CDB identified OS thread `0x2c68` at RIP
`0x7FF9A72D9386`: `cmp dword ptr [rcx],ecx` attempted to read address zero
with `RCX=0`. The instruction follows a call to `LexerState.get_Position`;
the returned and stored position reference was null. The captured `LexerState`,
`VbaSourceText`, `start` position, and `cachedPosition` were non-null. Its raw
offset was 77, `start.Offset` was 71, `identifierLength` was 6, and the source
string was 127 UTF-16 code units (object size 276 bytes), with SHA-256
`813C67DFA5F08AEA60B7F42C38EB582DB602E35C49EEF7C37E0B523E38FEC15A`.
Its exact line matches `common-modules/WorksheetService.cls:1010` in the
unchanged BFW source tree; the line content is not copied into this document.
The getter source uses
`cachedPosition ??= new VbaSyntaxPosition(...)`, which should not return null
under ordinary managed execution. SOS `verifyobj` found four inspected objects
valid, and `verifyheap` checked 2,145,953 objects with zero errors. These
checks do not establish why the null return occurred; no JIT, CLR, hardware,
or product-code root cause is supported yet. This is fault-time evidence for
one lexer manifestation, not a reproduction of the historical `System.Uri`
exception or completion of #415's regression criterion.

Release validation remains incomplete: the full verification command stopped
only at the known VSIX packaging Node `0xC0000005` failure after all preceding
suites passed. A separate Windows Excel suite passed its 48, 6, and 5 tests.
The dump and diagnostic logs remain private under ignored local `.tmp` paths;
do not commit or upload them.

A proposed shortcut that skipped document identification for generated
`vba-reference://` URIs with escaped spaces in the authority was discarded.
The historical stack entered a second parse of an admitted file URI, not this
generated-reference case. The shortcut would also change identity behavior for
some admitted reference URIs. Its finite unit-test passes therefore could not
justify a #415 correction. The normal identity admission path remains in use.

After extending the temporary lexer recorder to the faulted
`ReadIdentifierOrKeyword` position access, the exact-input BFW probe passed ten
analyses in one fresh Debug host (10/10, zero diagnostics). The next fresh host
failed on its first attempted analysis, before any completed trial, with a
managed `NullReferenceException` at `VbaPositionSyntaxIndex.FindIdentifier`
line 683. That location is a LINQ ordering key over a statement's token range,
not the historical URI operation or the newly instrumented lexer access.
The same 41-file source-tree hash was observed before and after; no Excel
process appeared. No new dump was collected because the authorized one-dump
limit had already been reached. This second post-change manifestation prevents
using the single successful host as stability evidence and still does not
establish a shared cause.

With both the lexer and `FindIdentifier` failure-only recorders built, a new
bounded sequence of fresh hosts completed 80 analyses across its first eight
hosts. Host 9 completed three analyses and then failed on attempt 4 with a
`NullReferenceException` at `ReadIdentifierOrKeyword` line 306. Its attached
`probeLexer` evidence identified `ReadIdentifierOrKeyword.PositionBeforeSlice`:
`state`, `sourceText`, `cachedPosition`, and a post-failure reread of `Position`
were non-null; raw and reread offsets were both 148, start offset 137,
identifier length 11, and source length 195 UTF-16 code units. The source-line
SHA-256 over UTF-16LE units was
`6F17A8ADE7FDD515BA38451BC1937D064B0831C34B2A65604DF71DBC447AD21F`,
matching the unchanged BFW `common-modules/WorksheetService.cls` line 1021.
The reread describes state *after* the exception; without a second fault-time
dump it does not itself prove the getter's return value at the failing
instruction. The earlier one-dump native context supplies that stronger
observation for a different line and trial. The probe again verified the
41-file tree hash and unchanged Excel process IDs. It produced no URI or
`FindIdentifier` graph evidence, because neither boundary failed in this host.

## COM-free frozen-catalog recurrence on 2026-09-27

The `ReplayCapturedMetadataWithoutComOrRegistry` arm then used the same original
BFW source root and the previously hashed six TypeLib metadata snapshots in
fresh Debug hosts. It verifies ordered source URI/text fingerprints, catalog
input hashes, reference selection, definition count, and the zero-diagnostic
baseline before and during each analysis, without installed TypeLib/COM or
registry acquisition in that host. The first fresh host completed ten analyses.
The second completed five with the expected diagnostic fingerprint, then failed
on trial 6 with `NullReferenceException` at
`VbaPositionSyntaxIndex.GetProcedureSyntaxWords` line 1569 while filtering
significant tokens by `token.Range.Start.Offset`. This is a different syntax
position from the URI and lexer failures. It demonstrates that fresh installed
TypeLib/COM acquisition is **not necessary** for at least this intermittent
failure. It does not establish whether the catalog contents, source input,
runtime, or process state is causal, nor whether this is the same root cause as
the historical URI failure.

The replay has a post-action project-tree and Excel-process guard, but its
failure output did not include the guard results because they were attached
only as secondary exception data. A later read-only check found 41 project
files and no Excel process; that alone is weaker than the in-run before/after
receipt. The probe now emits explicit bounded before/after tree hashes,
Excel-process counts/IDs, and a protection-check result even after an analysis
exception, without replacing the primary failure. That revised failure path
has not yet been observed in a fresh recurrence. No further native dump was
collected.

After adding that failure output and a `GetProcedureSyntaxWords.prefix`
token-graph recorder, ten further fresh COM-free hosts each completed ten
analyses (100/100) against the same frozen baseline. Every completed test
also passed its project-tree and Excel-process guards. This finite
non-reproduction does not reverse the earlier COM-free recurrence or prove
that the instrumentation, runtime, or product code fixed it. No token-graph
evidence was acquired in this follow-up because no failure occurred.

## COM-free pre-catalog range failure and later native crash on 2026-09-27

The next fresh frozen-catalog replay failed during source admission, **before**
it rebuilt any catalog from the snapshot or called semantic `Analyze`. The
failure was `ArgumentOutOfRangeException` from `System.String.Substring`,
reached through `VbaLexer.LexerState.Slice`, `CreateToken`, and
`ReadFixedLength` while parsing one of the original BFW sources. Its in-test
post-state guard passed: the project tree hash was identical before and after,
and no Excel process appeared. A separate read-only check found all 35 source
text hashes equal to the frozen baseline. The ordinary cursor transitions in
this lexer do not appear able to pass an out-of-range slice for an immutable
string, but the failed invocation did not record its actual offsets, so a
product cursor defect, runtime behavior, or process-state fault cannot yet be
distinguished. This is not the historical `System.Uri` exception.

A temporary `[DEBUG-415-lexer-v1]` failure-only recorder now preserves the
original range exception and records `Slice` start/end offsets, the pre-call
source length and cursor, post-failure source length/hash and cursor, and
whether both reads used the same string object. It does not retain source
content or change an error into a successful analysis. The probe formatter
and handled-failure JSON store accept only these bounded fields. Syntax
tests passed 1,938/1,938; VbaDev tests passed 3,017 with 57 skipped. These
tests validate evidence behavior, not the cause of the intermittent fault.

With that build, 13 fresh COM-free test hosts completed ten analyses each
(130/130) against the frozen baseline. The 14th `dotnet test` invocation
returned 1 without a handled-test failure record. Windows Application Error
and .NET Runtime events
at 2026-09-27 00:54 JST identify `testhost.exe` exit `0xC0000005` with an
unhandled `AccessViolationException` in `System.Runtime.EH.DispatchEx` /
`List<T>.Add`, reached from
`VbaCallableSignaturePresentation.PresentParameter` during semantic analysis.
The trial number within that host is unknown because its process terminated
before test output could be retained. The in-test after-state guard also could
not run. A later read-only check again found all 35 source hashes matching
the baseline and zero Excel processes; this weaker after-the-fact check is not
an in-run guard receipt. No additional dump was requested or copied. The
Windows event stack is useful evidence of a native failure in a **different**
analysis location, not proof of a common root cause or of a lexer fix. The
original URI operation remains uncaptured and #415 is not release-ready.
