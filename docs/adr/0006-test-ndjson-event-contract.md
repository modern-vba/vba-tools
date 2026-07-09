---
status: accepted
---

# Use event-based NDJSON for VS Code test integration

`vba-dev test --format ndjson` should emit an event stream rather than only a
final summary so the VS Code Testing API can update run and test states while a
workbook-backed test run is in progress. The initial contract has four event
kinds: `runStarted`, `testStarted`, `testFinished`, and `runFinished`.
`testFinished` identifies the project, document, module, procedure, outcome,
duration, optional message, and optional source location when that location is
available.
