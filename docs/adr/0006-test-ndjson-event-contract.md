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

Command-level failures such as manifest resolution errors, build failures,
missing bin workbooks, Excel COM automation failures, workbook locks, and
selector errors remain `TestRunError`s reported by non-zero exit and stderr, not
NDJSON events. The CLI should not emit legacy `result` or `summary` records for
schema `1.2`, and VS Code consumers should not rely on those legacy records.
