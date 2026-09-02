# Excel document-module source control

VBA Tools does not source-control or round-trip the code-behind of Excel's
`ThisWorkbook` and worksheet document modules, and it does not provide their
intrinsic host Event completion or handler intelligence.

## Why this is out of scope

Excel document modules are identities owned by workbook structure rather than
replaceable `.bas`, `.cls`, or `.frm` source units. A safe implementation would
need a separate adapter for CodeName and worksheet lifecycle, workbook/source
conflict ownership, and a recoverable mutation boundary across workbook
structure and exported text. That machinery would also reintroduce per-workbook
host inspection into an editor model that otherwise needs only source files and
one generic UserForm Event catalog.

The product instead supports ordinary source modules, class modules, and
source-owned UserForms. UserForms retain `.frm`/`.frx` import, export, build,
test, debug, Event intelligence, and complete semantic source-unit Rename.
Excel `ThisWorkbook` remains available as the read-only host global supplied by
the active Excel reference catalog; that expression does not imply support for
`ThisWorkbook` code-behind source.

## Prior requests

- #275 — Round-trip Excel document modules as source-controlled files
- #306 — Rename host-managed VBA components through a workbook-backed refactoring (document-module portion; its UserForm portion was superseded by source-owned Rename)
