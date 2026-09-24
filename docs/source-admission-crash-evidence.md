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

These receipts provide source-input provenance for the next occurrence. They
do not capture a memory dump, reproduce the historical access violation, or
establish a runtime, JIT, or application root cause. Opt-in disk writes can
also change timing, so a passing instrumented run is not stability proof.
