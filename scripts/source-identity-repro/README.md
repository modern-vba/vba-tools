# Issue #415 source-identity URI reproducer

This opt-in probe repeats the captured right-then-left file-URI identity operation
in a fresh .NET child process for each trial. It does not open Excel, read source
files or TypeLibs, modify product behavior, or run in normal tests or release
verification. A passing run is a non-reproduction, not evidence of a fix.

From the repository root, build in Release and run a bounded synthetic case:

```powershell
dotnet build scripts/source-identity-repro/SourceIdentityRepro.csproj -c Release -m:1 -p:UseSharedCompilation=false
dotnet scripts/source-identity-repro/bin/Release/net10.0/SourceIdentityRepro.dll --input fixtures/source-identity/synthetic-uri-pair.json --trials 4 --iterations 5000000 --report .tmp/diagnostic-verification/issue-415/synthetic-run.json
```

The tracked fixture uses synthetic, repository-neutral directory names and preserves the two URI lengths and shared-directory comparison shape. It contains no captured project paths. Passing this synthetic case does not establish reproduction or resolution of an observed failure.

The focused harness smoke test is manual and not wired into CI:

```powershell
node --test scripts/source-identity-repro/smoke.test.mjs
```

`--input` may instead name a local, ignored source-analysis failure JSON receipt.
The probe requires complete `uriIdentification` and `comparisonOtherUri` UTF-16
hex captures with matching code-unit counts, decodes them without URI
canonicalization, and orders the captured right and left operands as observed.
Keep the original receipt under `.tmp` or another private directory; never add
the unsanitized URI strings or a dump to Git. Reports must be written under the
current repository's ignored `.tmp` directory and use a new filename for each
run. They contain hashes, lengths, runtime/OS/architecture, assembly hashes,
trial counts, last flushed progress, exit codes, and local stderr. Inspect and
redact stderr before sharing it. No data is uploaded automatically.

`--trials` is limited to 1–20, `--iterations` to 1–5,000,000 per trial, and
each owned child has a five-minute timeout. An unhandled exception remains
unhandled in the child so process-level diagnostics can observe the original
failure; the parent records its exit and continues other trials. This probe
does not establish whether the later `String.SplitInternal` failure has the
same cause as #415's original `System.Uri` NullReferenceException.
