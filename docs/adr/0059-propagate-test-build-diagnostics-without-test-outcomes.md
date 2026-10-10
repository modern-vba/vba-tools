# ADR 0059: Propagate Test build diagnostics without fabricating test outcomes

- Status: Accepted
- Date: 2026-09-10
- Issue: #402

## Context

Ordinary and snapshot Test already delegate their build stage to the complete
shared source-analysis gate (ADRs 0057 and 0058). Build failure returns before
the workbook test runner, with no fallback to a previous bin. Test Explorer
previously had no diagnostic projection and rendered an analysis report as a
raw execution-error message. A caller-owned Test snapshot retained no origin
association after its temporary source directory was deleted.

## Decision

Keep the CLI gate and its existing test execution contract. Test schema 1.2
NDJSON describes actual workbook test outcomes only; sourceAnalysis schema 3.0
remains a separate stderr record. Required input failure keeps complete=false
and every recoverable finding. Module/procedure selectors affect test execution,
not the source set needed to validate the generated workbook. Existing provider
contracts and the snapshot compatibility matrix are checked before execution;
this slice adds no new CLI payload or schema version.

Capture primitive origin identities and copied source bytes together before
materialization can yield. Freeze the resulting snapshot-to-authoring URI map
and retain it after cleanup. Test Explorer reuses the existing strict diagnostic
parser and origin projector for both primary and related exported-source ranges.
Missing origins produce explicit output warnings and no guessed navigation;
malformed reports preserve previous Problems and become an execution error.

Before consuming test events, inspect validated analysis reports. Any Error or
incomplete report terminates that invocation as a source-validation execution
error, including processing-failure reasons. Do not create assertion failures,
passing procedures or successful empty runs from source diagnostics. A corrected
build rerun replaces only the selected project/document Test contribution, while
other commands and documents retain their contributions. The Command Palette
Test route continues to diagnose saved sources using its existing reporter.

No-build retains its old workbook and outcome contract, performs no new source
analysis, and cannot refresh or clear Build-derived Test diagnostics. Cancellation
and process-release/cleanup terminal semantics remain unchanged. Neutral analysis
and VbaDev gain no dependency on editor, Test Explorer, or adapter assemblies.

## Consequences

Problems survive temporary directory cleanup and navigate to the captured
original files. Test results continue to describe only executed workbook tests.
The native integration proof uses the public CLI, real Excel, VS Code diagnostics
and Testing API adapters; it verifies failure without macro execution followed
by a corrected unsaved run, related navigation and preserved persistent bytes.

## Issue #449 follow-up: source-workbook preparation without Save

Ordinary and snapshot Test retain the complete source-analysis/admission gate,
but their preparation no longer generates or saves an execution workbook.
They import the admitted saved-source or captured-editor generation into the
exact manifest-selected source workbook and execute there. `BuildFirst` denotes
that preparation, not an implicit ordinary Build or Save. Debug's analysis
exemption does not propagate to Test. A source-analysis failure still returns
before import/Run, with no fallback to old live code or bin output and no
fabricated test outcomes.

SourceAnalysis `3.0` stderr records, Test NDJSON `1.2`, snapshot `2.0`, captured
origins, primary/related navigation, stale-generation handling and scoped
diagnostic replacement remain unchanged. The new `test.sourceWorkbook: 1.0`
capability and seven-feature Test snapshot projection admit the changed execution
semantics; Debug's exact five-entry dependency declaration remains separate.

No-build runs the selected source workbook's current VBA without external source
capture, import or analysis. It cannot refresh or clear preparation-derived Test
Problems, and its outcomes carry no optional external-source locations even when
editors are clean. Open borrowed versus closed hidden/no-save lifetime, mode-bound
dirty-workbook consent, pre-Run recovery and the absence of rollback after possible
VBA execution follow ADR 0026's #449 follow-up. Test does not initiate Save; test
VBA's explicit Save and external effects are not suppressed or guaranteed reversible.
