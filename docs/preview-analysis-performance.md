# Explorer preview performance measurement

The Windows Release measurement for issue #365 exercises the actual VS Code
Explorer tree. It prepares CommonModules by previewing `Lib_Common.bas`,
settling references, revisiting that source, and previewing
`WorksheetService.cls`, then measures twenty preview operations across
`.bas` and `.cls` sources. Exactly two sources receive individual tokens
before measurement. The
set includes large sources, previously visited sources, and peers not
individually tokenized during preparation.

The [recorded issue #365 results](measurements/issue-365-preview-results.md)
include all twenty final timings, the exact pre-change comparison, cold
phases, retained-size accounting, and inspected renderer screenshots. The
final resident-cache sequence passed 20/20 at 115–823 ms; cold preparation
and first individual projection remain separate from that result.

Build and run from the repository root:

```powershell
npm run publish:language-server
npm run compile
$env:VBA_TOOLS_PREVIEW_PROJECT = '<absolute CommonModules project directory>'
$env:VBA_TOOLS_PREVIEW_RESULT_DIRECTORY = '<new absolute evidence directory>'
node client/out/extensionHost/previewPerformanceRun.js
```

`npm run measure:preview-retention` publishes the server, compiles the client,
and invokes the same runner with these environment variables.

The project directory contains `vba-project.json` and
`src/CommonModules/Lib_Common.bas`. `VSCODE_EXECUTABLE_PATH` may select an
already installed VS Code runtime; otherwise the runner uses the repository's
pinned minimum version. Each run requires a new evidence directory. The
isolated VS Code profile and scheduler records are retained for inspection.
`VBA_TOOLS_PREVIEW_LOAD_NOTE` supplies the measured machine's competing-load
description. Stop unrelated builds and tests before taking reference values.

The runner sets Shift JIS decoding, enables semantic highlighting and preview
tabs, and launches a fresh language server and reference-catalog cache. It
does not control the operating-system filesystem cache. The companion test
probe remains blocked, so no companion command or Excel automation prepares
analysis. Normal Project Validation Diagnostics remain enabled; the setup
waits only for initial semantic readiness and reference-catalog settlement.
Reference settlement requires every admitted catalog job to complete and
2,000 ms without another catalog event, resetting on each such event. This
preparation interval accommodates delayed catalog publications and is recorded
separately; it does not wait for Project Validation Diagnostics.
The same `Lib_Common.bas` setup source is then closed and previewed again so
its full tokens reflect the settled reference inputs. `WorksheetService.cls`,
the largest class source, is then previewed normally. Exactly these two large
sources are individually tokenized during preparation; the other eight
measured peers remain unvisited, including `WorkbookService.cls`.

An earlier diagnostic with only the initial `Lib_Common.bas` preparation
passed 18 of 20 latency checks. Its first `WorksheetService.cls` token
projection took 1,520 ms end to end, and the first `Lib_Common.bas` projection
after reference publication took 2,764 ms. The remaining samples took
83–567 ms and all token arrays matched fresh analysis. Initial per-source
projection for these two large files remains a documented bottleneck; the
retention change does not claim a one-second first projection for every
unvisited source.

## Measurement boundaries

`revealInExplorer(uri)` selects and focuses the actual Explorer item without
opening it. Timing starts immediately before
`list.selectAndPreserveFocus`, which invokes the Explorer list's native
preview selection handler. Closing and opening caused by that action are
inside the measured interval. The runner checks the resulting real tab's
`isPreview` property. It never requests tokens through a synthetic provider
command and creates no anchor or pinned editor.

Passive, explicitly enabled language-client middleware records the natural
full semantic-token provider result. The end timestamp is taken after the
provider completes and before the observer copies its tokens. Cancelled work
cannot finish a measurement. The report records the accepted and response
document versions and SHA-256 of the exact open text.

Every second operation explicitly closes all editors first and waits for the
actual VS Code document-close lifecycle. Alternating operations replace the
one remaining preview. Both modes retain their observed document lifecycle;
the harness does not assume replacement notification ordering. VS Code
1.125.0 baseline observations show `close, open` during replacement, including
a transition through zero open sources.

After all samples, a separate fresh server opens the recorded source texts,
finishes reference preparation, and produces full tokens from those current
inputs. Every integer of every measured full-token array must match this
oracle. Nonempty tokens alone do not pass. Oracle work cannot prewarm any
target in the measured VS Code process.

## Renderer and evidence

A local debugging endpoint is enabled only for the harness-owned VS Code
process. Read-only CDP observation captures rendered editor spans and their
computed colors, followed by a PNG screenshot, after two animation frames.
It sends no input and requests no tokens. The captured screenshots permit
verification that resolved names visibly update without further action.
The isolated test profile gives semantic tokens the distinct foreground
`#01ff87` through `*:vba`; each sample asserts that a provider-classified
identifier in the matching active editor has the corresponding computed
color `rgb(1, 255, 135)`. This is a test theme override, with no production
theme change. Ordinary syntax-colored text alone cannot satisfy the proof.
The exact provider-to-paint completion interval remains unmeasured; recorded
renderer observation times give the later observation boundary. Screenshot
work is outside each provider latency interval.

`launch.json` records source/corpus commits and dirty state, runtime, SDK,
power scheme, cache definition, and load notes. `report.json` records all
twenty samples, exact full-token correctness, source and corpus fingerprints,
actual document open/close events, hardware, and raw scheduler phase evidence.
The scheduler records separate reference preparation, mutation, immutable
capture, token execution, and eventual validation work. Individual PNGs show
the renderer after each operation. Partial samples and a failure record are
preserved when a gate fails; failed runs are not acceptance evidence.
The launch evidence also hashes the complete relevant production-source and
build-input trees, including newly created untracked sources. A sorted file
SHA-256 inventory and combined tree hash supplement the commit, dirty state,
and executable hash, so a tracked-only diff cannot omit a measured new file.

For an exact pre-change baseline, build the original commit in an isolated
worktree and set `VBA_TOOLS_PREVIEW_EXTENSION_ROOT` to that worktree. Copy the
same compiled passive measurement client into its `client/out`; its server
must be built from that worktree's unchanged sources. Use a fresh result
directory and profile. Existing published executables with unverified source
provenance are useful only for diagnosis and must not establish a before/after
claim. Cold server measurements use fresh processes and the same corpus,
with initial source capture, semantic inventory, token production, and
reference preparation reported separately.
