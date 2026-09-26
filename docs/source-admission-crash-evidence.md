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
