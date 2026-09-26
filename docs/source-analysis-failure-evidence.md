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

The [opt-in #415 URI reproducer](../scripts/source-identity-repro/README.md)
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
six-TypeLib BFW project also passed; that is a non-reproduction, not proof that
the historical semantic failure is gone. Keep #415 open for the original
failure's cause and regression boundary, and keep the local dump private.
