---
status: accepted
---

# Use event-based NDJSON for VS Code test integration

`vba-dev test --format ndjson` should emit event records rather than completed
`result` and `summary` records so the VS Code Testing API can update run and
test states from the command output. The `test.outputSchemaVersion` for this
contract is `1.2`.

The `1.2` event kinds are `runStarted`, `testStarted`, `testFinished`, and
`runFinished`. A run that reaches workbook test execution emits `runStarted`,
then a `testStarted` and `testFinished` pair for each `TestProcedure`, then
`runFinished`.

The first `1.2` implementation may emit these records after workbook execution
as a batched event replay. In that mode, `testStarted` preserves the stable
record shape but does not guarantee real-time stdout flushing. True in-progress
streaming should reuse the same event kinds and is tracked separately from this
contract clarification.

The initial snapshot-test implementation holds that batch until it has proved
release of every owned Excel process. Failure to prove process release is a
command-level infrastructure error and emits no batch or terminal event. After
process release, internal workspace deletion receives bounded retries. A
remaining file-deletion failure does not suppress the batch: the CLI emits the
complete event sequence, reports the retained absolute path as a warning on
stderr, and keeps the exit status determined by workbook-owned test outcomes.
Consequently, `runFinished` proves test execution and mandatory process
ownership cleanup, but not successful removal of every temporary file.

`runStarted`, `testStarted`, `testFinished`, and `runFinished` include `project`
from `ProjectManifest.ProjectName` and `document` from the manifest document
name. `testStarted` and `testFinished` identify tests with `module` and
`procedure`. `testFinished` includes `outcome` (`passed`, `failed`, or `error`),
`message` as a string, optional `durationMilliseconds`, and optional `location`
when a `TestProcedureSourceLocation` is available. The location identifies the
procedure declaration name in exported VBA source and applies independently of
the test outcome. Its canonical shape is a file `uri` plus a `range` containing
zero-based, UTF-16 `start` and `end` positions; the range is half-open and
covers the declaration name. This specifies the previously optional `1.2`
field without changing the schema version. `runFinished` includes `outcome`
(`passed` when every test passed, otherwise `failed`) and `total`, `passed`,
`failed`, and `errors` counts.

A complete `runFinished` is the authoritative distinction between
workbook-owned test failure and command infrastructure failure. A `passed` run
exits zero; a run with failed or error outcomes exits nonzero while retaining
those individual outcomes. The VS Code consumer therefore accepts a nonzero
exit accompanied by one valid matching `runFinished` as a completed failed test
run. A nonzero exit without that terminal record is a `TestRunError`, even if
partial `testFinished` records were produced.

Command-level failures such as manifest resolution errors, build failures,
missing bin workbooks, Excel COM automation failures, workbook locks, and
selector errors remain `TestRunError`s reported by non-zero exit and stderr, not
NDJSON events. Expiry of the test macro execution deadline is likewise a
command-level timeout rather than an individual failed test. A post-release
workspace-deletion warning is not a `TestRunError`; the VS Code consumer appends
it to Test Run output without changing any test state. The CLI should not emit
legacy `result` or `summary` records for schema `1.2`, and VS Code consumers
should not rely on those legacy records.
