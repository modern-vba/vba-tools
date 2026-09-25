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

`-PlanOnly` emits the exact ProcDump argument vector without starting a
child, accepting a license, or writing any evidence. The executable's hash,
length and timestamp, child arguments, ProcDump PID and exit code, output
hashes, timeout status, and dump filenames/lengths are recorded per attempt.
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
comments unless separately authorized. Receipts and dumps do not establish
the runtime, JIT, or application root cause. Opt-in disk writes can change
timing, so a passing instrumented run is not stability proof.
