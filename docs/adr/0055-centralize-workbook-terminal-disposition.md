---
status: accepted
---

# Centralize workbook terminal disposition

Issue #398 deepens the VbaDev terminal facts established by ADR 0019 into a
single shell-neutral analysis. Build, Publish, Import, Export, Test and Host
Event discovery previously repeated cancellation-authority checks and selected
COM wording differently for top-level and nested exceptions. The visible debug
process and its DAP failure-completion contract in ADR 0048 remain independent.

## Decision

`WorkbookAutomationTerminalFacts.Analyze` traverses one complete exception tree
and returns a private-constructor immutable result. It fixes ordered known and
independent unknown evidence, the selected primary candidate, ordered secondary
evidence, process-release and dispatcher-retirement proof, observed cancellation,
the first typed workbook cancellation, the caller's actually requested token
state, trusted cancellation authority and a nullable recognized disposition.
The recognized enum contains only `Cancelled` and `Failed`; null represents an
unclassified failure for the caller's existing unexpected-failure path.

The result fixes classification at analysis time. Its exception references keep
their original types, causes and context; it does not claim that exception
objects themselves are deeply immutable. There is no global exception cache:
the runtime can attach final lifecycle evidence after cleanup, and a later
analysis must observe that evidence. A caller may analyze an operation failure
to decide whether dependent scratch can be cleaned, then analyze the final tree
after cleanup adds evidence. It does not reanalyze an unchanged final tree for
each output or cancellation decision.

The first traversal occurrence at the highest priority is the primary candidate:

1. Unproved exact process release.
2. Unproved STA dispatcher retirement.
3. Cleanup failure after proved release.
4. Process loss.
5. Timeout.
6. COM failure.
7. Cancellation.

Secondary evidence retains traversal order and removes only the selected
occurrence. The same exception can supply both process and dispatcher evidence;
selecting one must not discard the other. Known evidence can exist in an
unclassified mixed tree, so a non-null primary candidate alone never authorizes
recognized cancellation or friendly COM rendering. Consumers use `Disposition`.

## Cancellation and proof

Any current unproved process or dispatcher evidence produces `Failed`, even
when cancellation or an independent unknown defect is also present. Without
that safety uncertainty, independent unknown defects remain unclassified.
Other recognized non-cancellation categories produce `Failed` regardless of
the caller's cancellation state.

`Cancelled` requires cancellation to be primary, both lifecycle proofs and
either a typed `WorkbookAutomationCanceledException` or an actually requested
caller token. `CanBeCanceled`, token identity and a lifecycle observation of
cancellation alone are not authority. Plain or nested `OperationCanceledException`
without that authority remains unclassified. A distinct untrusted-cancellation
fact applies only when cancellation is primary and independent unknown evidence
is absent; this preserves the existing rethrow behavior of Build, Import and
Export without expanding it to their unknown-mixture input-error fallbacks.
Test and Host Event retain their existing raw failed-result fallback.

An enclosing final lifecycle observation retains its original subtree scope.
Proved release can supersede an earlier cleanup attempt's uncertainty in that
operation without erasing its cancellation or operational failures. Independent
aggregate siblings keep their own evidence. No process observation, cleanup,
retry or termination authority moves into the analysis.

Process-release proof remains distinct from dispatcher retirement. Existing
dependent scratch retention and `OwnedProcessReleaseProof` marking depend on
process proof where they did before this change. Dispatcher-only uncertainty
still fails the operation but does not invent an unproved process mark or retain
otherwise releasable scratch. Infrastructure callers that need either proof
use the precomputed combined lifecycle-uncertainty fact.

## Command adapters

Commands own operation names, cancellation wording, exit codes, stderr/stdout
schemas, commitment and recovery. When the recognized primary category is COM,
every participating command uses `CommandErrorMessages.ExcelComAutomationFailed`
with its own operation name and the whole terminal exception. This preserves
wrapper and bounded-stage context; a COM leaf under a higher-priority timeout
does not switch the output to COM guidance. Host Event keeps its ordered,
distinct secondary-message rendering in its adapter.

Saved workbook staging is not commitment. Workbook materialization preserves the
previous output until atomic destination replacement; Export retains its
existing recoverable destination transaction, including incomplete-rollback
evidence when recovery cannot finish. Cancellation after completed commitment
cannot undo success. Completed Test outcomes, NDJSON order and location warnings retain
their existing authority. Neither the neutral module nor its result contains
`CommandResult`, a public message renderer, an operation-name table, a generic
exception handler or a cleanup action.

The conformance matrix owns category, priority, authority, lifecycle scope,
unknown-mixture, nested/aggregate COM and evidence-order rules. Command tests
retain exact rendering, public entry paths, saved-versus-committed boundaries,
release marking and scratch ownership. This changes no CLI grammar, result
schema, timeout, Excel ownership mechanism or cross-product dependency.
