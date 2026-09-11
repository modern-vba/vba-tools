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
no tree/hash for the active source. Template contents, source text, environment
variables, workbook bytes, and native dumps are not collected. Exception messages
may themselves include application-provided content. Paths and reference names
can be sensitive: inspect and redact a report before sharing it. Nothing is
uploaded automatically. Copy important reports outside the retention directory
before enough later failures can remove them.

This is a handled-managed-exception recorder, not crash monitoring. A native
access violation, process kill, stack overflow, or out-of-memory termination may
prevent it from running. The native crash investigation in #409 remains separate;
no shared cause is assumed. A report identifies inputs and a failing stage, but
is not a complete replay package and may not identify the individual expression
or URI involved inside project-wide analysis.

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
