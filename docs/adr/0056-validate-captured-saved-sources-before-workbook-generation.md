---
status: accepted
---

# Validate captured saved sources before workbook generation

Issue #399 adds syntax and document-local validation to ordinary saved-source
Build. It extends the source authority in
[ADR 0037](0037-centralize-vba-source-admission.md) without adding a dependency
on the language-server product. The existing workbook ownership, import
verification, and output-commitment contracts remain in place.

## Context

Ordinary Build admitted source bytes and generated a workbook without using
the syntax and document-local findings already available in the language
server. Returning at the first malformed source would leave the rest of the
selected `DocumentSourceSet` unexamined. Calling language-server services from
VbaDev would create a product dependency and a second source authority. Using
only editor-open files would make Build depend on editor state instead of the
saved input being imported.

## Decision

`VbaSourceAdmission` retains ownership of selection and capture. Ordinary
`WorkbookMaterializationIntent.ProjectBuild` requests analyzed admission before
generation input is prepared. Analysis uses the exact captured tree whose
decoded text, original bytes, identity, projection, and form sidecar supply a
successful import. Capture remains invocation-scoped: it adds no authoring
lock, retry, source rewrite, or atomic snapshot guarantee for concurrent edits.

Syntax diagnostics remain in the neutral parser. The unchanged document-local
rules move into `VbaTools.Syntax.VbaDocumentValidationDiagnostics`: duplicate
callable and Event parameter names, duplicate named arguments, and positional
or omitted arguments after a named argument. The language server retains its
own diagnostic projection; VbaDev owns its report and public-output projection.
Codes, messages, severities, original ranges, recovery, suppression, and
conditional-compilation behavior remain shared. The foundation gains no LSP,
VS Code, DAP, workbook automation, or command execution dependency. VbaDev and
its tests do not depend on language-server implementation, protocol, executable,
or test assemblies.

## Aggregation and generation gate

`VbaSourceAnalysisReport` retains findings in filename analysis encounter order.
Successful admission retains its separate final import order, including the
existing manifest ordering of installed CommonModules. Parsing and successful
import consume the same captured authority without rereading authoring files.

Known file-local source read, strict-decode, and sidecar read failures identify
their source and allow independent files to continue. An unexpected or
project-wide failure terminates analysis with earlier findings, its stopping
reason, and incomplete status. This does not treat an arbitrary exception as
a safely recoverable file defect or introduce a new module-kind rule.
`OperationCanceledException` retains the cancellation path.

A report is complete exactly when its failure collection is empty. Complete
reports may contain source errors. Generation requires complete analysis with
no error-severity diagnostics; warnings, information, and semantic uncertainty
are not promoted to errors. Errors or incomplete analysis return a nonzero
terminal result before workbook generation. Source/template bytes and the
previous completed bin workbook remain unchanged, with no successful receipt
or replacement artifact.

## Public report and Problems ownership

Nonempty analysis reports use the versioned public Build boundary both when
analysis blocks generation and when Build succeeds with non-error findings.
Absent or empty reports add no output. Each report is one newline-terminated
JSON record on stderr with `type: "sourceAnalysis"`,
`schemaVersion: "2.0"`, `complete`, `diagnostics`, and `failures`.
Each diagnostic has `type: "diagnostic"`, owner `vba-dev`, an absolute original
file URI, code, message, severity, and zero-based start/end range. Exported-source
coordinates include `Attribute` lines and form headers, without remapping
through the VBE import mirror. Source failures have `scope: "source"` and their
file URI; project failures have `scope: "project"` and a null URI. Both retain
their message without a fabricated source position. An empty or whitespace-only
failure message becomes `Source analysis failed without an error message.`

Consumers validate the public schema rather than referencing provider DTOs.
The Build output schema is `2.0`; this is separate from the unchanged top-level
`contractVersion` `1.0`, snapshot feature versions `2.0`, and active-code-page
and cancellation-transport feature versions `1.0`.

VS Code diagnostic ownership is bound to the invocation's tool/command,
project, and selected document. Refresh replaces only that contribution and
recomputes the union for affected file identities. The existing Windows
path-identity policy matches files while retaining a usable original path for
navigation. A corrected Build clears its resolved findings even when another
scope shares the URI, without removing another scope's contribution. Processing
failures and incomplete-analysis reasons remain visible in output instead of
receiving invented source ranges.

## Consequences and scope

Build can report several actionable source errors before Excel work begins,
including errors in files that are not open in the editor. The ordinary
saved-source build stage of Test inherits the same materialization gate.

This decision covers syntax and document-local validation. It does not claim
full project-semantic parity, reference resolution, or native VBE compilation.
Raw Doctor admission and readiness profiles, Publish, snapshot Build/Test,
debug snapshots, standalone Import/Export, `test --no-build`, source editing,
and Test Explorer result integration retain their existing scope. Subsequent
issues #400-#403 cover semantic, snapshot, Test Explorer, and Publish work;
their completion is not implied here.

Conformance belongs in neutral data-only fixtures or downstream public-process
tests, with each product owning its loader and assertions. Coverage must
establish shared diagnostic results, aggregate continuation and recovery,
capture authority, original coordinates, cancellation, output preservation,
schema compatibility, and scoped Problems refresh/navigation without sharing
product test assemblies. Architecture checks continue to enforce the neutral
foundation and independent VbaDev dependency boundaries.
