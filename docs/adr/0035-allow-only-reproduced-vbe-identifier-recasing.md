---
status: accepted
---

# Allow only reproduced VBE identifier recasing

The exact `VbeImportVerification` contract in ADR 0027 remains the default. A
post-import difference may become a non-fatal warning and allow workbook save
or output commit only when it is `VbeIdentifierRecasing` and a minimal,
convention-compliant, multi-component real-Excel fixture reproduces that
behavior; token sequence and position, component identity, component kind,
line structure, and all non-identifier source text must remain exact. The
accepted implementation emits that difference as `vbeIdentifierRecased` and
continues to fail every other difference before save or output commit.

This ADR accepts a partial supersession of ADR 0027's rule that every unmodeled
projected-code difference fails materialization. This partial supersession is
now operational: only classifier-proven `VbeIdentifierRecasing` is non-fatal,
while exact verification remains authoritative for every other difference.

`VbeImportVerification` does not enforce a project-wide source-casing style.
Each corresponding identifier occurrence is classified independently, and
mixed identifier casing in caller-owned source does not add another import
verification failure. Source-style enforcement remains a separate concern.

Both projected code streams are tokenized with the canonical
`VbaLanguageServer.Syntax` lexer. Token count, `VbaTokenKind`, and source range
must match at every position. Only corresponding tokens classified exactly as
`VbaTokenKind.Identifier` may have different text, and each such pair must be
equal with `OrdinalIgnoreCase`. Every other token text compares with `Ordinal`,
and the fail-closed lexical exclusions below remain `Ordinal`-exact even when
their fragmented words have identifier token kind.

Before token differences are considered, each physical line is split with
`VbaLexicalFacts.SplitCodeAndComment`; the complete apostrophe or recognized
`Rem` comment suffix compares with `Ordinal`. A word inside that suffix is never
warning-eligible even when the lexer exposes it as `VbaTokenKind.Identifier`.

`VbaTokenKind.Identifier` is necessary but not sufficient. An
identifier-classified token remains exact when contiguous tokens make it part
of a numeric literal: an `H` or `O` based-literal body after `&`, or a `D` or
`E` decimal exponent after a decimal mantissa, including a contiguous optional
sign and exponent digits. This exclusion applies to both projected streams and
fails closed. Therefore changes such as `&HAF` to `&Haf`, `&O77` to `&o77`,
`1E3` to `1e3`, `1D+3` to `1d+3`, and `1.E3` to `1.e3` remain fatal despite
lexer fragmentation.

Recasing findings are aggregated into at most one warning per imported
component. The warning uses the stable code `vbeIdentifierRecased` and lists
distinct source-to-VBE casing pairs in first-occurrence order, so repeated
occurrences do not produce repeated warning entries. Component warnings retain
the `VbeImportSourceSet` import order and are not sorted independently.

The warning is non-fatal and keeps exit code zero. Build, publish, import, and
test commands render it on standard error without changing their standard
output or machine-readable test streams. Debug builds surface the same warning
through DAP console output. Existing warning streams remain unchanged.

Each operation re-evaluates and re-emits the warning when the difference is
present. Verification does not rewrite caller-owned source, persist warning
suppression, or cache a prior recasing decision.

The evidence gate requires reproducibility on one supported real-Excel
environment. Verification classifies the actual post-import result on every
operation, so it does not maintain an Excel-version allowlist.

A successful minimized reproduction becomes a source-based Windows Excel
integration test and must pass reliably before the production contract is
relaxed. The fixture is constructed by the test and does not add a binary
workbook. An unstable or manual-only reproduction does not satisfy the gate.

Reliable reproduction means two consecutive targeted test passes. In each
pass, a fresh owned Excel process imports, compares, saves, and closes the
workbook; a separate fresh owned Excel process reopens and compares the saved
workbook. The test also verifies unchanged caller-owned source. The evidence
records the Excel version and active Windows code page for both passes.

The historical reconstruction record is separate from the minimized gate
record. It identifies the repository revision and source paths, conflicting
spellings and semantic roles, initial workbook provenance and component state,
ordered imports, changed component and directional source-to-VBE pairs, Excel
version, and active Windows code page. The minimized record identifies the
source-built fixture and test, both consecutive command results, import and
reopen pairs, owned-process cleanup, and caller-owned source-byte proof.

The gate also requires deterministic classifier coverage. Positive tests admit
only case-insensitive-equal identifier tokens at corresponding positions;
negative tests retain fatal verification for component identity, component
kind, line count or structure, token kind or position, and every
non-identifier spelling, including keywords, literals, strings, comments, and
date literals.

Passing the evidence gate established evidence only and did not relax
production verification. The evidence was presented while this ADR remained
`proposed` and production remained exact. The evidence checkpoint moved the
issue to `ready-for-human` and project status `In review`, then stopped. On
2026-08-31 the maintainer explicitly accepted the evidence, returned the issue
through `ready-for-agent` and project status `Ready`, and authorized the
implementation phase. The issue then moved to `In progress`, and this ADR was
changed to `accepted` before production code changes. These ordered gates
cannot be collapsed or inferred from one another.

Before acceptance, failure to reproduce the previously observed behavior in an
initial attempt would not have rejected the proposal or closed #324. The
investigation instead reconstructed the original workbook state, component
import order, and source conditions, then minimized the reproduced behavior
into the compliant fixture required by the gate.

The reconstruction fixture may deliberately retain historical mixed casing or
source-style violations and is diagnostic only. It does not satisfy the
evidence gate until minimization preserves the behavior in convention-compliant
source.

## Evidence recorded on 2026-08-31

The historical reconstruction used `xls-bfw-tools` revision
`2e6e96b0d4015477ae776c849155203077a012e1`, project and document
`メール生成`, and the tracked template at
`メール生成/src/メール生成/メール生成.xlsm`. The template SHA-256 was
`A20D96D81716D7E0A37867F62AE9E4C366DC0B82CDCC51CF4A1559D8ADB86A6B`
before and after read-only inspection. Excel 16.0 reported project
`VBAProject` with 35 initial components: document components `ThisWorkbook`
and `Sheet1`, plus 33 importable components. Ordinary generation removed the
importable components and imported the 49-file planned source set.

In that order,
`メール生成/src/メール生成/common-modules/WorkbookService.cls` was
component 20 and used `FileName` at line 560, while
`メール生成/src/メール生成/tests/Test_MailGeneration.bas` was the last
component, number 49, and later used `Filename` at line 380. Both spellings are
the named-argument identifier for `Workbooks.Open`. The ordinary build
reproduced the historical exact-verifier failure on `WorkbookService` line 560.
Diagnostic instrumentation of the same run captured the directional change
from expected `FileName:=OpenArgs.FilePath` to actual
`Filename:=OpenArgs.FilePath`. The instrumentation was then removed. The run
used Excel 16.0 and active Windows ANSI code page 932. Issue #324 records the
complete 49-component order.

The minimized durable fixture is
`ConventionCompliantIdentifierRecasingPersistsAcrossOwnedSaveAndReopen`. It
creates a blank `.xlsm`, confirms initial components `ThisWorkbook` and
`Sheet1`, then imports convention-compliant class components in the order
`FileNameProvider` followed by `FilenameAuthority`. A fresh owned Excel 16.0
process observes `FileName -> Filename`, saves, and closes; a separate fresh
owned process reopens the workbook and observes the same single directional
pair. Both consecutive targeted runs passed on code page 932. The test verifies
source bytes, staging cleanup, and owned-process cleanup.

At the evidence checkpoint, thirty-five deterministic classifier cases passed,
including positive distinct-pair ordering and fail-closed structural, comment,
and numeric-fragment boundaries. The complete non-Excel test project rerun
passed 1,104 tests with 12 opt-in integration tests skipped. The classifier was
then referenced only by evidence tests, and production remained exact until the
maintainer accepted the evidence and this ADR changed to `accepted`.

## Accepted implementation

Production now invokes `VbeIdentifierRecasingClassifier` from
`VbeImportedComponentVerifier`. A complete verification returns an ordered
`VbeImportVerificationReport`; it contains at most one structured
`vbeIdentifierRecased` warning per affected component, and each warning contains
distinct directional identifier pairs in first-occurrence order. A malformed
report or any non-eligible difference remains fatal before save or output
commit.

Build and publish preserve their existing standard output, including existing
protected-reference warnings, and render recasing warnings on standard error.
Import renders them only after verification and save both succeed. Test retains
its text or NDJSON output and exit semantics while forwarding successful-build
warnings on standard error, including when a workbook test later fails. Debug
builds forward successful child-process standard error through lifecycle output
to DAP `console` events.

The production Windows Excel integration fixture imports two independently
recased provider components before their casing authorities, receives two
component warnings in import order, commits the generated workbook, and verifies
the same projected code after a fresh owned-process reopen. It also proves
unchanged caller-owned source bytes, staging cleanup, transaction cleanup, and
owned-process cleanup.
