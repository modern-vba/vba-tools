# Source-admission preparse evidence

Issue #409 tracks a rare native failure during VbaDev source admission. This
opt-in diagnostic records the identity of each source immediately before the
already-captured bytes enter URI construction and the VBA parser. It does not
change source selection, decoding, parser behavior, command output, or the
handling of an application failure.

Set `VBA_TOOLS_DIAGNOSTIC_RUN_ROOT` to an existing, ordinary, absolute local
directory and `VBA_TOOLS_DIAGNOSTIC_RUN_ID` to that run's identifier before
starting the owned test or `vba-dev` process. The diagnostic writes under
`<run-root>/source-admission/process-<pid>-<guid>/`. An unset or invalid root
leaves admission unchanged. Each process records at most 10,000 parse attempts.

For each attempt, `NNNNNNNN-preparse.json` is flushed and published before
URI construction or parsing. A successful parser return writes the matching
`NNNNNNNN-complete.json`. A preparse record without a completion record narrows
the last entered parse operation; it is **not** by itself proof that the parser
caused a crash. Compare the process exit, stderr, native dump, and neighboring
records before assigning a cause. A `.tmp` file is an incomplete diagnostic
write, not a published receipt.

Preparse records include process/run/sequence identities, the admission
purpose, module kind, exact source-path UTF-16 code units up to 4,096 units
with a completeness flag and full-path hash, byte length and SHA-256 of the
already-captured source, decoding token, active code page, and decoded-text
length. They do **not** contain source bytes or source text and do not reread
the file. Paths and hashes may still be sensitive; keep the directory local
unless separately authorized to share it. Diagnostic write failures are
secondary and never replace the original admission result.

## Scoped full-dump replay

`scripts/diagnostics/Invoke-VbaDevScopedDump.ps1` is a separate, opt-in replay
launcher for one exact diagnostic child command. Set
`VBA_TOOLS_PROCDUMP_PATH` to a Microsoft-signed ProcDump executable and use
the same `VBA_TOOLS_DIAGNOSTIC_RUN_ROOT` as the preparse receipts. Review and
accept ProcDump's EULA interactively first; the launcher neither accepts it
automatically nor changes a machine-wide debugger setting. It requires an
existing ordinary directory on a local fixed drive, creates
`<run-root>/source-admission-dumps/attempt-NNN/dumps/`, and passes
`-ma -e -n 1 -x <dump-directory> <exact-executable> <exact-arguments>` to
ProcDump. It never uses `-i`, `-w`, or a process-name attachment. The first
dump stops the bounded series, so at most one full dump is retained per run.

Invoke the script in PowerShell with explicit arguments, for example:

```powershell
& ./scripts/diagnostics/Invoke-VbaDevScopedDump.ps1 `
    -ExecutablePath 'C:\path\to\vba-dev.exe' `
    -CommandArguments @('doctor', '--project', 'C:\path\to\project') `
    -Count 10
```

For a suspected native access violation that the CLR may later convert or
re-raise, opt in to `-FirstChanceAccessViolation`. This replaces the default
unhandled-exception trigger with
`-ma -e 1 -g -f C0000005 -n 1 -x <dump-directory> <exact-executable> ...`.
The filter captures the first matching native exception in the exact launched
child before managed exception handling changes its register context. The
`captureMode` field in each attempt receipt distinguishes the two modes.
Check the dump's first-chance marker and exception context before interpreting
it. A first-chance access violation may be handled, so a dump alone does not
prove that the child ultimately failed; ProcDump may stop before observing the
child's final exit. Keep the first-chance mode separate from normal verification
and use a fresh local run directory for each trial.

`-PlanOnly` emits the exact ProcDump argument vector without starting a
child, accepting a license, or writing any evidence. The executable's hash,
length and timestamp, child arguments, ProcDump PID and exit code, output
hashes, timeout status, and dump filenames/lengths are recorded per attempt.
The launcher also extracts the exact launched child PID and, when ProcDump
reports it, the child's unsigned hexadecimal exit code from ProcDump's own
UTF-16LE lines. Child stdout can use a different encoding in that same file;
the launcher scans the raw bytes for ProcDump lines instead of decoding the
whole stream as one encoding. A missing child exit remains `null` with a
reason, not a guessed copy of ProcDump's exit code. A nonzero child exit stops
the bounded series even if ProcDump itself exits zero.
With `-n 1`, ProcDump may stop after the first dump before it reports the
child's eventual exit. In that case, the dump and exception event remain
available but the exact child exit code is unknown. Opening a process handle
after seeing its PID would be only best-effort for an immediately failing
child, so this launcher does not substitute a guessed exit code.

An opted-in `vba-dev` child writes a separate startup receipt under
`<run-root>/source-admission-processes/` before command dispatch. It includes
the actual process ID, .NET runtime and framework versions, runtime identifier,
and target framework without command arguments or environment variables. The
launcher includes that runtime identity in `finished.json` only when run ID,
attempt ID, child PID, and executable path all match. A missing receipt is
reported explicitly; a native failure before managed startup may require the
dump's loaded modules to recover runtime details.
The already-captured source-byte hashes in the matching preparse receipts
provide the source-input identity; the launcher does not reread source files.
The ProcDump exit code is **not** treated as the child's exit code. On timeout
the launcher records the owned monitor PID and does not kill it or Excel;
inspect that process before starting another run.

A controlled native-failure probe builds
`fixtures/diagnostics/ControlledAccessViolation/ControlledAccessViolation.csproj`
and invokes its generated `.exe` through the same launcher with
`-CommandArguments @('--trigger')` and `-Count 1`. Without `--trigger`, the
fixture exits without crashing. Run it only after reviewing the ProcDump
EULA; confirm one local `.dmp` appears under the fresh attempt directory.
The fixture writes to invalid memory deliberately and is never run by the
ordinary test suite. This tests dump capture plumbing, not the historical
`vba-dev` access violation. A managed `Environment.FailFast` or an AV caught
by a PowerShell host is not a suitable proof of unhandled native capture.

Full dumps and command stdout/stderr may contain source, credentials, or
environment variables. Keep them local, out of Git and external issue/PR
comments unless separately authorized. A matched startup receipt establishes
the observed .NET runtime identity, but does not establish the JIT or
application root cause. Opt-in disk writes can change timing, so a passing
instrumented run is not stability proof.

## Historical evidence and remaining gap

The original 2026-09-10 published `vba-dev` failure was a native read access
violation during source admission (`0xC0000005`, executable SHA-256
`C335CC1CA179BFF28CC04E6A4C3DE30BE162C903DA64C4B1156B1BF504EE438E`).
Its reported JIT location mapped to `VbaLexer.LexerState.get_Position`.
The Issue #409 record describes a small dump lacking the relevant managed
heap and native-helper pages; that original file was not found at its later
documented location. A later fatal-handler stack is not the first fault's
register and memory state, and cannot fill those missing pages. Ten subsequent
fresh-project workflows (70 CLI invocations, including 20 Build/Test calls)
did not reproduce that original crash. None of this identifies a bad source
file, a CLR/JIT defect, or an environmental cause.

A separate 2026-09-26 standalone lexer replay used three fixed lines and
checked each completed token's kind, text, and range against a golden
sequence. One fresh process exited with `0xC0000005` after 2.7 million
completed calls, without reporting a token mismatch. An earlier local
first-chance full dump from this minimizer retained a valid rooted lexer
state, source object, and expected input string, while the faulting receiver
register held a non-object-like value. The matching later Windows Error
Reporting small dump cannot walk the managed stack or inspect heap objects;
it does not replace that first-chance context. These minimizer crashes are related
evidence, not a proven replay of the original CLI's exact failure.

For the next in-scope occurrence, retain the exact-child first-chance full
dump alongside its run/attempt/PID identity, available runtime and
source-admission receipts, executable and source hashes, exit observation,
and loaded-module list. Managed startup and preparse receipts may be absent
if the child fails before writing them, or if it is a standalone minimizer.
Compare the *first* exception context with the source object and
receiver provenance before choosing a product change or runtime/environment
mitigation. ProcDump may stop at the first dump before reporting the child's
final exit; record that exit as unknown rather than substituting the monitor
exit. A native access violation cannot be made safe by an in-process retry.

## Exact BFW-input first-chance follow-up on 2026-09-26

An opt-in Debug test host read the original BFW source tree and the six installed
TypeLibs, then called shared analysis repeatedly without starting Excel. Eight
complete analyses produced zero diagnostics. The ninth failed with a managed
`NullReferenceException` at `VbaLexer.ReadIdentifierOrKeyword` line 339. The
post-test source tree still had 41 files and SHA-256
`038839696C32C2D0DED55672FCD4BC19D6C5EC44EDC205087B1983B5D551C772`,
and Excel process IDs were unchanged.

An exact-child, first-chance ProcDump monitor recorded a
`C0000005.ACCESS_VIOLATION` on that test host and completed one 358,058,938-byte
full dump (SHA-256
`E91E2B2A25DC68070F5FF0905120D0997399C520AED9FDE6A1E972C9ADC09A74`).
Its one-dump limit was reached. ProcDump exited 1 and a local wrapper reported
`dumpFinalized=false`, but the completed dump file, size, and hash were verified
independently. Neither monitor exit code nor wrapper flag is the child exit
status; the test itself reported the managed failure.

In the saved native context, the faulting instruction read address zero
immediately after an indirect call resolving to `LexerState.get_Position`.
The saved return value was null. The inspected lexer state, source text, start
position, and cached position were non-null, with raw offset 77 and a six-unit
identifier starting at offset 71. SOS verified those objects and found zero
errors while checking the managed heap. The getter's ordinary source and
inspected native branches return either the existing cached position or a new
position, so the observed null is not explained by a simple null field.
Snapshot timing, code-version provenance, and transient process state still
prevent a supported JIT, CLR, hardware, or product-code root-cause claim. This
is evidence for a lexer manifestation using the affected source input, not a
reproduction of #415's historical `System.Uri` exception and not a fix.
The dump and raw logs remain only in the ignored local diagnostic directory;
do not commit or upload them.

Read-only follow-up on the same dump confirmed the caller's `state` stack slot
and null return slot, both indirect call targets, and the getter's IL/native
null-coalescing branches. SOS displayed one `MinOptJitted` native version and
`ReJIT ID 0` for caller and getter. The loaded module's PE/PDB identity and
embedded product commit matched the current disk build; an in-dump MVID/hash
comparison was unavailable. No obvious profiler module or heap-verification
error was found. These checks weaken a stale-binary or simple field-race
explanation but do not exclude transient stack/code corruption, runtime state,
or other process-environment effects.

## Current-Syntax COM-free replay on 2026-09-30

The same three fixed lines, token oracle, and .NET 10.0.12 runtime were replayed
against current Syntax assembly SHA-256
`2BDB96A902DA7E4F3B335D4C08C2C1B0FFF305FB9585289F65A2903D83919C60`.
The diagnostic executable SHA-256 was
`F4F848C7173BD3446100AEAE32C65EB0D8C38294E2809020C0E47298E00896AE`;
the three input UTF-8 SHA-256 values were
`0E859B90703B3ADC3B3F63328B2C2A71CA2527722CFB585B79DEF8521B6B4E4B`,
`2B13873395B6CB313C571B39F33D7F39689392FA73A14193A29D6F32190A808A`,
and `EAE681E6053EA0BC819D86CCAB5A8CBF9D75EB40522E97246607A6CB0EC56FFD`.
One unmonitored fresh process raised a `NullReferenceException` in
`LexerState.IsAtEnd` after 596,445 completed iterations. Ten fresh processes
under a first-chance ProcDump monitor each completed five million iterations;
their success is not a correction, because capture changes timing. With only
the per-child .NET full-dump crash variables enabled, two fresh processes
completed five million iterations and the third raised native `0xC0000005`
after at least 1.6 million completed iterations. The 116,732,734-byte local
full dump has SHA-256
`151006676A5E6FA00370C38041707030E9DA8D2F3F66F9E723F89FFAF9251073`.
No Excel, COM, TypeLib, or extension host was loaded in this replay.

The dump's original exception record reports a write access violation at
`0x7ffbebe0a239`, targeting `0x44fffffef4`. This is the `mov [rbp-0x12c], eax`
local-variable store in `VbaLexer.Tokenize`, just after the call to
`ReadCandidateLength`. The saved `RBP=0x4500000020` calculates exactly that
invalid target; saved `RSP=0x456078d920` remains in the stack. The inspected
lexer state, source text, and 128-character input were valid; heap verification
checked 121,843 objects with zero errors. Both JIT methods were reported as
`MinOptJitted`. The bad frame pointer's origin is unknown: these observations
do not isolate product code, JIT/runtime, CPU, or another source of transient
stack/register corruption.

For a one-factor comparison, `DOTNET_TieredCompilation=0` was applied only to
the fresh minimizer child while binary, runtime, input, and five-million-call
limit stayed fixed. Seven attempts completed; attempt eight raised another
`0xC0000005` after at least 4.1 million calls, this time with the managed
stack in `LexerState.get_Source` / `get_IsAtEnd` / `Advance` /
`ReadIdentifierOrKeyword`. Disabling tiered compilation is therefore **not**
a demonstrated workaround. The first full dump and WER event records remain
local; no dump or source content is committed or uploaded. This is a related
COM-free lexer failure, not proof of the original published CLI fault's cause
or of Issue #415's historical `System.Uri` exception.

## Exact Windows Excel release-gate CLR failure on 2026-09-30

The standard `npm run verify:release:windows-excel` command ran under the
opt-in diagnostic wrapper at clean commit `701cc7335250e08ef054e051a56dceb67fd7f7f3`
with Node 26.9.0, npm 12.1.0, .NET 10.0.12, and Windows build 26200. The
architecture check, 1,161 extension unit tests, Extension Host integration,
3,017 vba-dev tests, 653 debug-adapter tests, and 1,938 syntax tests passed.
Language Server testing then finished with 2,936 passes and one failure out of
2,937 tests. The failure was
`Server_omits_no_op_form_source_unit_file_renames_when_the_requested_identity_matches_the_exact_basename`:
its exact `vba-language-server.exe` child exited with native `0xC0000005`.
The standard command stopped there, before the Windows Excel integration stage;
this run is **not** a release-gate pass. The failing test passed when rerun alone
without rebuilding, so its source input is not a deterministic reproducer. Its
fixture is the five-line `Dialog.frm` UserForm source in
`LanguageServerProcessTests.cs`; the child failed while processing `didOpen`,
before the rename response.

On the same product-code state, a separate clean-HEAD diagnostic profile ran
the nine release stages after LSP testing; all passed, including
`package:verify` and its VSIX verification. A separate direct
`npm run test:windows-excel-integration` passed all 48 vba-dev, six debug-adapter,
and six cross-product Excel tests. These independent results expose no second
failure but cannot replace the failed standard gate or an exact-main run.

The child capture receipt identifies process 19564 and one completed
125,500,117-byte local full dump (SHA-256
`E2C8BB53D17AE14CE6935F93AF10416D5F1618B360C295D5F345EA5DC3C473C0`).
The .NET Runtime Event 1023 reports internal CLR error `0x80131506` in
`coreclr.dll` at module offset `0x8F12E`. Microsoft public symbols resolve
that location to `StackTraceInfo::AppendElement+0x156`. The saved native stack
passes through `PreStubWorker` while `VbaModuleSyntax..ctor` is entered from
`VbaSyntaxTreeParser.ParseModule` during LSP document-open analysis. The fault
frame's `R15` was zero; SOS verified 27,193 managed objects with zero errors.
The child-local `coreclr.dll` is byte-identical to the installed .NET 10.0.12
runtime (`128AEE8C62A673D64739E585E3876B61133571CEC83A46240A62581D5465639B`),
and both copies have valid Microsoft signatures. The dump's 61 loaded modules
show no third-party DLL outside Windows, the .NET installation, and this build.
These observations locate the fatal runtime path, not the first cause of the
invalid state. The full dump and stderr remain local and must not be uploaded.

Six other .NET Runtime Event 1023 failures observed on this PC on the same day
also report module offset `0x8F12E`: four in LSP children and one each in
`csc.exe` and `dotnet.exe` (seven total including this gate failure). The
contexts include LSP startup and document analysis as well as compilation.
This cross-process signature weakens a single fixture-input explanation, but
does not distinguish a CLR defect, injected component, operating-system issue,
or hardware/transient corruption. It does not establish that the historical
#409 CLI crash or #415 URI exception has recurred. Do not treat an in-process
retry as a recovery from the native process termination.
