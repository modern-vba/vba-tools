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
