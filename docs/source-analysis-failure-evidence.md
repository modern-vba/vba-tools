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

## Historical second-parse replay on 2026-09-27

To exercise the URI operation actually present in the 2026-09-11 stack,
commit `ef2c35b` was exported into an ignored local `.tmp` directory. This
added no Git branch or worktree and did not change the current checkout. Its
Release Syntax and Semantics assemblies built with .NET SDK 10.0.300; the
resulting SHA-256 values were
`E4439B64CB5D958EC19BFAD6DED48BBB4440D9804A07FE5C0F0EFBE8E7460C6D`
and `00877DDFFC366F70450026AB24ED5F46ABBE2ABD80FBE29BFB95AB0CE371282D`,
respectively. These are newly built assemblies, **not** the historical
published binary.

A COM-free, read-only harness parsed the same 35 current BFW sources after
checking every raw UTF-16 text hash against the frozen baseline, and built
six reference catalogs from the locally captured TypeLib metadata JSON. One
uninstrumented trial completed. A diagnostic-only version then surrounded
the old `TryGetLocalPath(string)` second `new Uri(uri)` with a catch that would
retain the exact UTF-16 URI only in a local receipt and rethrow an observed
`NullReferenceException`. Ten fresh .NET 10.0.8 x64 hosts each completed ten
semantic analyses (100/100), with no exception or URI receipt. A later
read-only check found all 35 source hashes unchanged and no Excel process.

This is a bounded **non-reproduction**, not a correction. The old assembly
derived 16,120 active definitions and one diagnostic from the frozen catalog
data, versus the current replay baseline's 16,246 definitions and zero
diagnostics. The historical published binary, precise 2026-09-11 catalog
state, and exact failing URI are unavailable, and the diagnostic catch can
alter rare timing/code generation. Current semantic code no longer makes this
second parse, but the original runtime failure's cause remains unsupported.
The native crash observed with current code is separate evidence; neither
result justifies closing #415 or claiming release readiness.

## Standalone current-build comparisons on 2026-09-27

The same ignored local harness was linked to the current product assemblies,
keeping the 35 raw-UTF-16 source hash checks and six frozen catalogs. With a
Release build it reproduced the baseline's 16,246 active definitions and zero
diagnostics; ten fresh standalone hosts completed ten analyses each (100/100).
This is a configuration/process-boundary comparison with the Debug xUnit
replay, not a proof that Release code is safe.

The current Debug standalone build, without xUnit or live COM, returned a
`NullReferenceException` in `VbaLexer.LexerState.Peek` on trial 5 after four
successful analyses. With only `DOTNET_TieredCompilation=0` changed, another
fresh Debug process failed on trial 10 in
`VbaPositionSyntaxIndex.IsWord` after nine successful analyses. Neither
failure was in `System.Uri`; disabling tiered compilation alone did not
prevent this class of intermittent failure.

To test whether reusing parsed syntax trees is required, the Debug standalone
harness then reread and rehashed all 35 original sources and reparsed them
before **each** analysis, while retaining the frozen catalogs. One fresh host
completed ten analyses. The next completed four and failed during trial 5
with `ArgumentOutOfRangeException` in `VbaLexer.LexerState.Slice` /
`String.Substring`, reached from lexical comment inspection during semantic
resolution. This occurrence saved the exception stack but not its diagnostic
`Exception.Data`; actual slice operands therefore remain unobserved. After a
local-only receipt change to include allowlisted lexer evidence, the next
fresh-syntax process instead terminated with native `0xC0000005` in
`VbaTokenStream.FromText` while initially parsing sources, before trial 1.
The changed harness is a separate trial condition. A later read-only check
again found all 35 source hashes matching the baseline and zero Excel
processes. No additional dump was requested or copied.

These observations rule out xUnit, live TypeLib/COM acquisition, tiered
compilation, and reuse of parsed trees as **necessary conditions** for at
least one current manifestation. They do not establish a common cause or
show whether the old URI failure has the same cause. Windows Application
events in the surrounding four-hour period also recorded `0xC0000005` in
unrelated `VBCSCompiler.exe`, `codex.exe`, and `sppsvc.exe` processes; no
WHEA-Logger event appeared in that window. This makes an environment-wide
factor worth checking, but neither proves hardware/OS corruption nor
exonerates product code. Keep the different stacks and binaries distinct.

A further standalone control referenced **only** the current Debug Syntax
assembly. It re-read and checked each of the 35 original source-text hashes
before every `ParseModule` call, with no semantic analysis, catalogs, COM, or
Excel. Twenty fresh hosts each completed ten full 35-source parses (200/200),
with no observed exception or native termination. Afterward, all 35 source
hashes still matched the baseline and no Excel process existed. This finite
non-reproduction does not establish that Semantics is required: the earlier
current Debug standalone process terminated while parsing its initial source
set, before catalog reconstruction or analysis, and process histories differ.
The specific failing slice operands still have not been captured.

With the allowlisted receipt enabled, ten more fresh current-Debug hosts
were scheduled for ten fresh-tree analyses each. Nine hosts completed all
ten (90/90); one terminated before its initial preparation message with
native `0xC0000005`. Windows Application Error event 1000 recorded
`dotnet.exe`, an unknown faulting module, and offset zero for that process;
there was no managed exception receipt. No `Slice` recurrence or actual
operand evidence appeared in this bounded run. These counts must not be
combined with the separate historical or Release cohorts as one pass rate.

The ignored local harness then exercised the same exception-receipt path
with an intentional private `Slice(0, 6)` call on a five-character synthetic
string. It recorded the original `ArgumentOutOfRangeException`, phase
`LexerState.Slice`, start `0`, end `6`, pre-call source length `5`, and an
unchanged source reference. This validates capture and serialization for a
known invalid range, not the cause of a real failure. The first canary
attempt instead terminated with an unhandled `AccessViolationException`
while initially parsing the 35 sources, before the intentional call; the
Windows .NET event stack passed through `ReadOnlySpan<char>.Length` and
`VbaIdentifier.ReadCandidateLength`. A second attempt skipped that initial
parse and reached the expected canary. No extra dump was collected.

A separate read-only 30-day System log check found four WHEA-Logger event 19
warnings (2026-08-28, 09-04, 09-09, and 09-22 local time), all reporting
Processor Core, Corrected Machine Check, and Internal parity error, with
APIC IDs 0, 1, 1, and 9 respectively. There was no Application Error event
within 30 minutes of the latest warning (09-22 09:44:42).
[Microsoft's WHEA definitions](https://learn.microsoft.com/en-us/windows-hardware/drivers/whea/windows-hardware-error-architecture-definitions)
describe a corrected machine check as a processor-detected condition
corrected by hardware or firmware; it is
nonfatal. This record is an independent reason to investigate system
stability, **not** a proven explanation of any VBA analysis failure. The
last 30 days contained no Windows Memory Diagnostic result in the System
log, and its enabled Results/Debug log had zero records; that absence is
not a clean memory-test result. The physical disk
reported `Healthy`/`OK`, which likewise does not rule out CPU, RAM,
firmware, or software faults.

In the same 30-day Application Error event-1000 window, `0xC0000005`
also appeared in unrelated executables, including `MsMpEng.exe` (5),
`sppsvc.exe` (8), and `Explorer.EXE` (1). These counts exclude the deliberately
crashing test executable and are broader than the VBA tool processes, but
event co-occurrence alone cannot identify a common failure mechanism.

A static audit of the latest `ReadOnlySpan<char>.Length` access-violation
stack found no explicit unsafe/native or span-escaping operation in its
immediate Syntax path. `ParseModule` creates a source wrapper around an
immutable managed string; the lexer forms `AsSpan` from that string while
inside its end-of-source loop, and `ReadCandidateLength` uses managed rune
decoding and bounded span slices (`VbaSyntaxTreeParser.cs:122-130`,
`VbaLexer.cs:34,84-86`, `VbaIdentifier.cs:157-187`). Invalid ordinary
offsets or malformed UTF-16 would be expected to produce managed range
handling or token results, not by themselves an access violation at span
length. The event stack and static audit cannot locate the actual corruption,
distinguish runtime/JIT
from other process influences, or absolve product code.

## Windows integrity checks on 2026-09-27

The maintainer ran an elevated `sfc /verifyonly` twice. The first attempt
stopped at 3% with Windows Resource Protection unable to perform the
requested operation. CBS records the SFP verification request at 07:38:05
local time, seven completed 100-component batches, and an eighth batch
started without a completion record at 07:38:08. Application Error event
1000 at that same second reports `TiWorker.exe` terminating in `ntdll.dll`
with `0xC0000409`; the later CBS worker-restart message confirms the worker
crash. The event does not identify why the worker terminated, and this first
attempt says nothing about whether protected files are intact.

The second elevated `sfc /verifyonly` started at 07:48:31 and reached 100%,
reporting integrity violations. Its CBS verification batches end at
07:49:42; the sole explicit corruption entry in the current CBS log is
`DEPLOY [Pnp] Corrupt file: C:\Windows\System32\drivers\bthmodem.sys` at
that time. No `[SR]` entry specifies a mismatching hash, corruption subtype,
or a repair result. The existing file was readable (114,688 bytes, file
version 10.0.26100.5074), but that does not contradict SFC's integrity
finding. `/verifyonly` performed no repair.

Separately, at 07:46:50, a `WinMgmt` CBS session reported
`CBS_E_XML_PARSER_FAILURE` while reading a RollupFix package `.mum`. A
read-only .NET XML reader subsequently traversed the current 1,708,640-byte
file without a well-formedness error; this does not validate CBS-specific
metadata or reconstruct the bytes seen at failure time. Neither that
separate parser event nor the `bthmodem.sys` finding is established as the
cause of the 07:38 worker crash, the VBA-analysis failures, or the corrected
machine checks. No system repair, reboot, configuration change, or new dump
was performed by this investigation.

The maintainer subsequently ran elevated
`DISM /Online /Cleanup-Image /RestoreHealth`, which completed successfully.
The CBS summary at 08:25:04
records 1,608 detected corruptions repaired and zero CSI manifest
corruptions detected by that DISM pass. The following `sfc /scannow` reached
100% but reported that some files could not be repaired. At 08:27:48 CBS
records an XML parser failure at line 188, column 7; at 08:27:50 its `[SR]`
entry says it cannot verify files for
`Microsoft-Windows-Power-Policy-Definitions` version 10.0.26100.3912
because the manifest is damaged. CBS does not give that manifest's full
path or identify a particular unrepaired component file. At 08:28:26 the
same SFC run records a successful one-component repair and both
`Corrupt file` and `Repaired file` entries for `bthmodem.sys`; that earlier
driver finding is therefore not the supported remaining SFC blocker. DISM's
successful repair of its detected set did not establish that this later
SFC manifest parse would succeed. Neither outcome proves a cause for the
VBA-analysis failures, and this investigation did not initiate the system
repair or collect a new dump.

The maintainer then reran elevated `sfc /verifyonly`. It reached 100% and
reported no integrity violations. CBS records the verification from 08:34:23
through 08:38:01, ending with `Repairing 0 components` and `Repair complete`.
The prior `Power-Policy-Definitions` manifest error, XML parse error, and
`bthmodem.sys` corruption entry do not recur in this verification interval.
This supports that the protected-file integrity check passed on this run; it
does not establish that the earlier manifest failure's cause is understood.

The verification interval was not crash-free: Application Error event 1000
records `TiWorker.exe` crashes at 08:35:49 (`ntdll.dll`, `0xC0000409`) and
08:36:23 (`wcp.dll`, `0xC0000005`), and CBS records worker relaunches. The
08:35:46 and 08:36:08 verification attempts stopped mid-batch; the 08:37:01
attempt completed all batches. The successful final SFC result therefore does
not establish servicing-stack or machine stability.

Two separate `dotnet.exe` application crashes occurred nearby. A .NET Runtime
1025 event at 08:37:01 records a `FailFast` stack ending in
`VbaLexer.CreateToken`; a 1026 event at 08:38:51 records an unhandled
`AccessViolationException` in `VbaLexer`/`VbaProjectSourceAnalysis.Analyze`.
Application Error 1000 events match their respective process IDs. The
available event and WER records do not establish the command lines or parent
processes. This is further evidence that lexer-path failures can recur, not
that SFC caused them or that a particular VBA input is faulty. The historical
URI and Slice root causes and release acceptance remain open.

## Fixed-input runtime and Syntax-only comparisons on 2026-09-27

The branch's opt-in frozen-catalog test used the unchanged Debug
`VbaDev.Tests.dll` (SHA-256
`4FB241CFE4D6741274161FBD68C83DE0DB440EB29ECB3A9709C3A527A265A389`)
and 2026-09-26 six-catalog baseline (SHA-256
`5C86F4E457BE9E45E840743E6C2062C30C0E453B22107847DAAFA013204CEEB2`).
Each fresh testhost verified the same 35 raw-UTF-16 source fingerprints.
The first local .NET 10.0.8 host completed ten zero-diagnostic analyses.
The next host terminated with native `0xC0000005` before a completed trial;
its .NET event stack reached `VbaPositionSyntaxIndex.GetEnclosingBlocks`.
An independent live-TypeLib host terminated with native `0xC0000005`
at `VbaSourceText.get_Text` during semantic re-lexing. A further
COM-free host completed eight analyses and failed on trial nine with a
managed `NullReferenceException` in `LexerState.Slice`; its failure-only
receipt found the source text and position non-null, with a 68-code-unit
source and unchanged source-tree fingerprint. These are three distinct
manifestations, **not** reproductions of the historical `System.Uri`
failure. The local TRX files are under ignored
`.tmp/diagnostic-verification/issue-415-step2-20260927`; no dump was
collected for these runs.

To compare runtimes without rebuilding the test binary or changing the
VSTest runner DLL, the same `vstest.console.dll` was invoked directly
under each host. Module inspection of the running `testhost.exe`
confirmed `coreclr.dll` 10.0.8 from the locally installed SDK or
10.0.12 from `C:\Program Files\dotnet`, respectively. Five fresh hosts
per runtime completed ten zero-diagnostic frozen-catalog analyses each
(50/50 under each runtime). The direct-runner cohorts cannot be merged
with the earlier `dotnet test` cohort because the launch path changed;
the successes do not demonstrate that either runtime is safe.

An ignored, standalone Syntax-only control then re-read and rehashed
the original 35 sources before each parse and queried `GetPositionSyntax`
at each argument-list callee. Completed trials yielded 17,036 position
queries and the same 27,707 enclosing-block count. Under .NET 10.0.8,
two fresh processes completed ten trials each; the third terminated
before its first completed trial with native `0xC0000005` at
`LexerState.get_Position` during `ParseModule`. Under .NET 10.0.12,
one fresh process completed ten trials; the second completed three and
then exited after a managed `NullReferenceException` at
`LexerState.Slice` during `ParseModule`. The latter's .NET Runtime
event 1026 names the managed exception, while Application Error 1000
records `0xC0000005`; neither record alone identifies the first corrupt
state. The same current Debug Syntax DLL was used in both hosts (SHA-256
`8FE3CDCDA24305E21562E29B7975857E87187234E6381E4747FFB438A06E2AAC`).
Afterward all 35 source hashes still matched, and no Excel process was
running. This control excludes TypeLib acquisition, semantic analysis,
Excel, and xUnit as necessary conditions for these lexer-path failures;
it does **not** prove a common cause with the original URI exception.
Upgrading only to .NET 10.0.12 is not a validated correction.

A local artifact audit found no retained executable matching the
historical published CLI SHA-256
`AD2C508061ADFFED05DBFDE7A63AE281C9207F9122ECC6874024AE9B5707FD03`.
The 2026-09-26 frozen six-catalog baseline is not the 2026-09-11 catalog
state, and the historical failing URI was never captured. Thus an exact
original-binary/input replay cannot be claimed. The URI-root-cause,
actual failure-boundary regression, and release gate remain unresolved.

## Fresh release-gate and debugger observations on 2026-10-02

After the development environment update, an exact
`npm run verify:release:windows-excel` run on the existing release branch
passed the architecture, extension, Extension Host, vba-dev, debug-adapter,
and 1,938 Syntax tests. Its `VbaLanguageServer.Tests` testhost then exited
with a fatal `AccessViolationException` at `VbaSourceText.get_Text`, reached
from `LexerState.Slice` during the large manifest-project validation test.
The Windows Excel phase was not reached, so this was **not** a passing
release-gate run. The generated 96-module, 1,324,622-byte project was
preserved under ignored
`.tmp/diagnostic-verification/release-gate-20261002-124914-fixture`;
the copied files match the original source hashes. This preserves the
fixture, not necessarily the bytes read by the failing process at the
instant of failure.

A failure-only assertion path now reports the active source URI, UTF-16
length and hash, and exception type if the test's post-semantic module
reparse fails. It preserves the original exception. In five fresh targeted
VSTest hosts with crash blame enabled, four passed and one failed with a
managed `NullReferenceException` in `VbaLexer.CreateToken` at the numeric
literal path. This is a different manifestation from the full-gate access
violation; the failing URI still was not captured for the fatal case.

An isolated .NET 10.0.12 check showed that ProcDump's type-name filter
`-f System.NullReferenceException` did not produce a dump for a first-chance
managed NRE, while filtering on the CLR exception code would capture other
managed exceptions too. A local CDB/SOS script was therefore checked with
an isolated process: it ignored a preceding unrelated managed exception and
captured a first-chance NRE before execution resumed. In the actual targeted
test, three fresh debugged hosts passed. A later host terminated with an
internal CLR error (`0x80131506`) during validation, before a matching NRE
was observed. With first-chance access-violation capture also armed, five
more hosts passed and the sixth failed with
`ArgumentOutOfRangeException` in
`VbaCallSyntaxParser.IsAssignmentTarget` at its token-index access. No
NRE or access-violation dump was produced in these latter runs. The local
logs remain under ignored `.tmp/diagnostic-verification/cdb-nre-20261002*`.
Debugger attachment changes timing, and the different exceptions do not
prove a single cause. They do show that one exception-specific dump filter
alone cannot capture every observed failure mode. No source-analysis root
cause or release fix is established by these results.

A subsequent scoped CDB run captured a first-chance native access violation
in its third fresh testhost. The 328,608,733-byte full-memory user dump and
the matching testhost identity, binary hashes, and debugger log remain only
under ignored
`.tmp/diagnostic-verification/cdb-nre-20261002T045403Z-887670cb`.
The captured thread was inside the named large-project test, following
project diagnostics through `ParseModule`, `VbaLexer.Tokenize`,
`VbaIdentifier.IsWhitespace(U+0020)`, and `Cp2Ranges` into CoreCLR's
`RuntimeHelpers.CreateSpan<int>`. The fault was therefore during analysis,
not debugger startup. At the fault, CoreCLR attempted an indirect call with
a noncanonical target read through a stack address; the OS CFG thunk raised
`0xC0000005`, and the subsequent same-process `0xC0000409` is consistent
with an indirect-call guard failure. The dump identifies that invalid
target, but not why CoreCLR obtained it. It does not distinguish a runtime
or JIT fault from earlier memory corruption, and it does not establish a
VBA lexer defect. No dump was uploaded. No WHEA-Logger event was found in
the 13:45–14:05 local-time interval around this run.

As a separate control, the self-contained Windows x64 Syntax-only probe
published from the current source parsed the preserved 96 modules in ten
fresh host processes, all passing with 85,502 argument lists. This control
uses a Release-published Syntax DLL, not the Debug DLL loaded by the
failing testhost, and success cannot clear the full release gate.

## Fixed-binary runtime and VM comparisons on 2026-10-02

The self-contained Release Syntax-only package was copied into the dedicated
Windows 11/Excel test VM through the existing enhanced session and extracted
without changing VM security settings. One guest execution under its bundled
.NET 10.0.12 completed. Its locally returned JSONL has 195 records: 97 input
files, 96 source modules, 96 parse starts and completions, and 85,502
argument lists with `passed: true`. After excluding machine-specific paths
and process ID, every ordered record, source byte/UTF-16 hash, module count,
and running total matches the host sanity run. This verifies the parsed
inputs and result; the guest log alone does not independently attest the
executable or DLL hashes or the process exit code. The returned log is under
ignored `.tmp/diagnostic-verification/syntax-only-probe/guest-results-20261002`.
One successful guest execution is not a stability comparison.

An independent self-contained Syntax-only package retained the **exact**
Debug `VbaTools.Syntax.dll` used by the failing testhost (SHA-256
`7C91051EB95A24AACB6EE701C97C94F558B3DD68262E29BFD7D0E825A48A772B`).
Ten fresh host processes with process-local `DOTNET_TieredCompilation=0`
each parsed the same 96 modules and passed. Thus sequential Syntax parsing
with that Debug DLL and setting alone did not reproduce the targeted test's
failure; this does not exclude concurrency or other testhost paths.

For a closer comparison, ten interleaved pairs ran the same prebuilt
large-project xUnit test with `--no-build --no-restore`, changing only the
testhost setting `DOTNET_TieredCompilation` between explicit `1` and `0`.
The test DLL SHA-256 was
`496EEC4A994C0B06ED9DFFDB3BD4E386FE33B0A27CFF11422819398435414152`;
the Syntax DLL was the exact Debug binary above. The `1` arm passed 10/10.
The `0` arm passed 5/10 and failed 5/10: two fatal native access violations
in `VbaLexer.LexerState.Position`, and three managed null-reference failures
in lexer cursor/slice paths. No failure was merely the test timeout. The
failure-only receipt identified `Caller032.bas` in one `0`-arm failure with
UTF-16 SHA-256
`BED6430FD5F383CF7765393A82ECB5DA134943A1667EC75CD3EA8FB6EE30B2FA`,
matching that module's successful guest input exactly. Logs and per-run TRX
files remain under ignored
`.tmp/diagnostic-verification/tiering-ab-20261002T134609Z-adf0f08d`.
This is a useful high-frequency feedback loop, not proof that tiered
compilation causes the defect: disabling it changes JIT code versions and
timing, and the ordinary release gate had already crashed with the default
setting. Neither disabling tiering nor a successful VM probe is a release
fix or an acceptance result.

## Isolated Syntax and runtime-setting controls on 2026-10-02

A two-worker, self-contained Syntax probe ran the exact Debug Syntax DLL and
the same frozen 96-module input in one fresh process per trial. Each worker
created its own `ParseModule` calls; the input strings were immutable. With
`DOTNET_TieredCompilation=0`, the first 10 trials passed 9/10. The failure was
a managed `NullReferenceException` in `VbaLexer.CreateToken` while one worker
parsed `Caller045.bas`; the other worker completed that same module. The input
UTF-16 SHA-256 was
`F2FB2349FB7F97C5B5182A9C98026D66F856849AE6705659C106E61ABF287527`.
Thirty further fresh trials of the unmodified probe passed 29/30. The failure
was a process fast-fail (`0xC0000409`), with Windows Error Reporting naming
`coreclr.dll` version `10.0.1226.42308`. The archived WER report has no stack
or retained dump, so the faulting thread and input are unknown. The probe
source, per-run JSONL, summaries, and local WER details remain ignored under
`.tmp/diagnostic-verification/syntax-only-probe/concurrent-probe`. This
establishes that the failure can occur without VS Code, Excel, or the language
server. It does not establish a Syntax-owned race or a CLR root cause. A
read-only audit found fresh, private lexer cursor state per tokenization and
no credible shared mutable state on the failing path.

A disposable diagnostic build changed only the `Cp2Ranges` getter from an
embedded `ReadOnlySpan<int>` blob to an equivalent static array. Ten
interleaved TieredCompilation-off xUnit pairs passed 7/10 for both the original
and the variant. The variant still had a native access violation. Ten more
interleaved pairs held the original Debug DLL fixed and changed only
`DOTNET_ReadyToRun`: both explicit `1` and `0` passed 9/10, with a managed
null-reference failure in the former and a range failure in the latter.
Neither code-shape change nor runtime setting is a demonstrated prevention.
All 40 per-run TRX files and binary-hash receipts remain ignored under
`.tmp/diagnostic-verification/cp2-array-variant-20261002T140204Z`.

A failure-only `CreateToken` observer was built only in an ignored clone. Its
initial package parsed 30/30 two-worker trials, but those successes do not
show a fix: both chance and changed JIT/timing are plausible. The first clone
also omitted the original friend-assembly attributes and was incompatible
with the xUnit test output; its failed xUnit comparison is invalid. After
restoring all three original `InternalsVisibleTo` attributes and checking the
assembly identity and API, the corrected clone passed a smoke test. Ten
interleaved xUnit pairs then passed 9/10 for both original and observer DLLs;
each had one managed lexer null-reference failure. The test result XML did
not expose the observer's `Exception.Data`, so the failing operands remain
unknown. The clone outputs, hashes, and per-run TRX files remain ignored under
`.tmp/diagnostic-verification/create-token-ivt-repair-20261002T144825Z`.
No clone change has been adopted in the product. The release gate remains
failed, and no issue closure or release conclusion follows from these trials.

## Single-input lexer reproduction on 2026-10-03

A failure-only, local JSONL observer in an ignored Syntax clone captured a
managed `NullReferenceException` in `LexerState.Slice` on the second fresh
TieredCompilation-off xUnit host. At the operation reported on the
`currentSource.Length` line, the captured local `currentSource` and
`sourceAtCall` were null and the length had not been assigned. In the same
catch path, `SourceText` and its `Text`
were nonnull, with a 26-character UTF-16 source and valid slice offsets
22–25. The outer `CreateToken` observer independently read the same source
object and valid start/end positions. Its source hash matches the trimmed
line `result = ResolveValue(174)` in the frozen `Caller068.bas` input. This
shows an inconsistent value at the failure point; it does not distinguish a
transient null source getter, an inlining/JIT fault before local assignment,
or earlier corruption. Both JSONL events and the TRX are retained under
ignored `.tmp/diagnostic-verification/create-token-slice-jsonl-20261003`.

A self-contained microprobe then repeatedly called only the public
`VbaTokenStream.FromText` API with that exact 26-character input, checking
all eight token kinds, texts, and ranges. It pinned the original Debug
Syntax DLL (SHA-256
`7C91051EB95A24AACB6EE701C97C94F558B3DD68262E29BFD7D0E825A48A772B`)
and .NET 10.0.12. With TieredCompilation off, two workers passed four fresh
processes and failed one with a lexer-position `NullReferenceException` at
iteration 671,108 of one worker. A separate one-worker build failed its
first fresh process at iteration 1,171,447, with a `NullReferenceException`
in `CreateToken`. Thus the full 96-module input, language server, VS Code,
Excel, and a race between two parsing workers are not necessary to reproduce
the symptom. A single process result is not an independent trial per token.

An ignored staged-locals diagnostic DLL retained the same assembly identity,
friend attributes, and public API. Its isolated xUnit smoke passed; ten
fresh TieredCompilation-off xUnit hosts passed eight times, failed once with
a managed position-getter null reference, and once with a native access
violation at the same getter. No staged `Slice` event occurred, so the source
getter stage remains unidentified. In the single-worker microprobe with this
diagnostic DLL, four of five fresh processes passed. The other failed at
iteration 948,559 with a range exception: `Slice` received start offset 9
and end offset `1,606,418,432` (`0x5FC00000`), while pre/post raw cursor
offsets were 21 and a catch-time position reread was 21, the same source
object had length 26, and its complete UTF-16 hash matched the fixed input.
The passed argument and later cursor observation disagree; the evidence does
not establish where the argument changed or whether the observer perturbed
the failure. Microprobe code and per-process JSONL remain ignored under
`.tmp/diagnostic-verification/syntax-only-probe/lexer-microprobe`; binary
hashes are in run logs and the comparison package README.
No workaround or product fix has been accepted from these observations.

## Syntax-independent managed control on 2026-10-03

A separate self-contained .NET 10.0.12 program contains no Syntax DLL and
never loads the Syntax assembly. It replays the same eight fixed token
boundaries using independent snapshot, cursor, string-slice, and record
objects, then checks every token. Its bundled `coreclr.dll`, `clrjit.dll`,
and CoreLib hashes match the lexer microprobe. It does not copy the lexer
algorithm, and its allocation profile is not matched to the Syntax probe.

With one worker, the first two-million-iteration fresh process failed at
iteration 1,647,036 in `ControlReader.AdvanceTo` with an end-offset range
exception. The post-catch source was nonnull, length 26, and the cursor was
at offset 21; that initial build did not record the actual argument. A
failure-only instrumented build then ran in three fresh processes: one
passed, one reported a token-validation mismatch, and one failed the range
guard at iteration 1,259,606. The guard's catch path recorded an actual
`endOffset` argument of 7 against raw cursor offset 25 and source length
26. The loop's catch-time expected index was 7, but its local expected
token was the earlier whitespace token (6–7); a post-failure reread of
static `Expected[7]` was the final punctuation token (25–26), and all eight
static boundaries were intact. The input's complete UTF-16 hash matched
`result = ResolveValue(174)`. An independent read-only review found no
deterministic fixture boundary error or code path that ordinarily selects
token 1 at index 7. The recorded argument origin string is derived from the
catch-time index, not a separate observation of the earlier array read.
Likewise the guard operands and static array were read again after the
condition fired. These facts show an inconsistent local/argument state,
but do not distinguish JIT/runtime behavior, host memory corruption, or an
unseen control-probe defect. The instrumented token mismatch lacks actual
token fields and cannot be further localized. Source, binary hashes, and
per-process JSONL remain ignored under
`.tmp/diagnostic-verification/syntax-only-probe/managed-control`. This
independent failure weakens a Syntax-only explanation but does not establish
a root cause or authorize a product-code workaround.
