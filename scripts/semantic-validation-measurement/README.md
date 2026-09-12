# Shared semantic validation measurement

This opt-in, product-neutral harness measures the public `VbaTools.Semantics`
foundation. It references **already built DLLs**, not a project checkout, the
language-server executable, VbaDev, or either product's test helpers. It does not
start Excel, read VBA projects, acquire TypeLibs, or write source/workbook files.
It writes one JSON report to stdout and returns nonzero if any measured outcome
fails its independently specified correctness checks.

## Prepare matched variants

Freeze the baseline commit and build it in Release **before** changing production
code. Preserve the resulting `VbaTools.Semantics.dll` and `VbaTools.Syntax.dll`
together in a baseline directory. Repeat for the candidate. Existing bundled
executables are not a baseline unless their exact source identity is established.

Build the harness against each variant into a distinct directory. Both DLLs are
copied next to the harness; the report hashes the DLLs actually loaded there.
The same harness source and arguments must be used for both variants. From the
repository root, for example:

```powershell
$measurementRoot = Join-Path (Get-Location) '.local/performance/419'
$baselineAssemblies = Join-Path (Get-Location) '.local/performance/419-baseline/semantics'
$baselineHarness = Join-Path $measurementRoot 'baseline-harness'
dotnet build scripts/semantic-validation-measurement/SemanticValidationMeasurement.csproj -t:Rebuild -c Release -o $baselineHarness "-p:SemanticAssemblyDirectory=$baselineAssemblies" -m:1 -p:UseSharedCompilation=false
dotnet (Join-Path $baselineHarness 'SemanticValidationMeasurement.dll') --variant baseline --revision <exact-baseline-commit> --calls-per-document 128 --lookup-repetitions 2000 > (Join-Path $measurementRoot 'baseline.json')
```

Use different candidate assembly, harness, and report directories, supplying its
exact commit to `--revision`. That value is explicitly caller-supplied evidence,
not an inferred or verified association between checkout and DLL. Preserve the
build command and output alongside the report. The report also includes loaded
assembly versions, informational versions, configurations and SHA-256 values,
the harness build SDK, runtime/core-library identity, OS, architecture and CPU.
Only Release baseline/candidate results count as performance evidence.

The main request's end-to-end CES and small-control Build measurements remain
separate. Use unchanged complete source/template/catalog inputs and isolated
workbook output, verify Excel cleanup, and distinguish profiler timings from
ordinary elapsed time. This generated corpus is not a replacement for those
measurements or the project's regression suites.

## Workload and correctness

The fixed eight-document corpus comprises four standard-module libraries and
four caller modules. Each caller repeats six call forms: source-module
qualification, unqualified cross-module lookup, a private source declaration
shadowing a same-name reference declaration, global reference calls, qualified
reference calls, and calls through a reference-class variable. The synthetic
Automation catalog includes explicit parameter direction and ABI evidence.
`--calls-per-document` sets the **number of six-call groups per caller**; the
report records both the group size and group count. Increase this value without
changing document/candidate cardinality. Four deliberate missing-required-
argument calls must remain the only semantic findings, at specified source lines.

The complete generated text is parsed once before trials. Each timed public
`VbaProjectSourceAnalysis.Analyze` invocation consumes all parsed sources and
creates fresh semantic inventory/resolution state; parsing is separately timed
and not included in the semantic-analysis result. Corpus source text and catalog
metadata are fingerprinted, but their contents are not dumped into reports.

A second phase constructs a fresh public `VbaNameResolutionService`, then times
repeated `Resolve` queries over that service. It uses the same fixture's explicitly
declared public `VbaSourceDocument`/definition facts for module-level lookups.
The production projector is internal: the harness neither bypasses it through
reflection nor implements a second generic projector. Public definition facts
are fixture data, omit unqueried local variables, and have their own fingerprint.
This phase is intentionally distinct from complete parsed-source analysis.
Expected source identity, source/reference origin, qualification, private
visibility, shadowing and unresolved outcomes are checked after timing every
result. The recorded diagnostic and resolution fingerprints include original
locations, allowing exact cross-variant comparison.

All four trials run in one process: one complete excluded warm-up followed by
three measured trials. Every trial creates a new semantic analysis and new
resolution service; there is no process-global harness cache. Resolver
construction and repeated lookup have separate timings. No first-read warm-up
is hidden inside a measured service. Result buffers are allocated outside the
lookup timing, and correctness checks and JSON serialization are outside both
timed operations. The harness does not force collections or discard outliers.
All failures are retained; medians are withheld if **any** trial fails, including
warm-up. Run serially without other builds, tests or profilers, and inspect all
trial timings rather than reporting only the best one.

For workload-scaling controls, use the same arguments for both variants at small
and larger `--calls-per-document` and `--lookup-repetitions` values. Timing is
supplemental evidence: deterministic normalization/work-count regression belongs
in the real shared-resolution tests, not in a wall-clock threshold here.

## Smoke verification

After building the harness, run its tiny public-process contract test:

```powershell
$env:VBA_SEMANTIC_MEASUREMENT_ASSEMBLY = Join-Path $baselineHarness 'SemanticValidationMeasurement.dll'
node --test --test-isolation=none scripts/semantic-validation-measurement/smoke.test.mjs
```

The smoke invocation uses two call groups and two lookup repetitions, still
retaining one warm-up and three measured trials. It checks successful expected
findings, resolution counts, stable fingerprints, loaded DLL hashes and report
shape. Its timings are not performance evidence. The test requires ordinary
local child-process launch permission; it never opts into Excel integration.
