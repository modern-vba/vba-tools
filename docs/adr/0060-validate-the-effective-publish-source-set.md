# ADR 0060: Validate the effective Publish source set

- Status: Accepted
- Date: 2026-09-10
- Issue: #403

## Context

Build validates captured syntax and project semantics before generation. Publish
previously admitted importable source without that complete gate. An invalid
included module could replace a completed publication, while validating the
full editor source set would incorrectly include deliberate publish exclusions.

## Decision

Keep the existing private source selection in `VbaSourceAdmission`. Flat filename
collisions precede filtering. Manifest `testOnly` entries and their sidecars are
skipped without reading; local source must strictly decode before its
`'#ExcludePublish` marker is accepted. Included CommonModules ignore the marker.
The final included-source order and ACP projection requirements do not change.

Closed analyzed Build and Publish admission operations share one collector and
completion gate. Only included captured syntax trees enter document and project
analysis. Excluded declarations cannot bind calls, names, or types. Run the
existing shared analyzer with selected reference catalogs, host Event contracts,
and module/project/reference identities. Equal inputs yield equal diagnostics;
an editor or Build source set containing excluded modules is a different input.
Preserve modeled semantic indeterminacy instead of adding unresolved-name rules.

Accumulate recoverable included-file findings and processing failures. A
project-fatal stop retains previous findings and reports incomplete analysis.
Required evidence acquisition failures are actionable incomplete outcomes, not
successful empty analysis. Any Error or incomplete analysis prevents generation.
Publish preserves original sources, the template, and the prior completed output.

One materializer path captures the template and accepted semantic inputs for
Build and Publish. Successful generation consumes that template and the same
admitted included-source authority; it never reopens authoring paths to select
or analyze again. Existing generation-time identity checks and transactional
output replacement remain in force.

Publish command output schema advances from `1.0` to `3.0`, using the existing
`sourceAnalysis` schema `3.0` stderr record and human-readable success output.
The extension contract rejects older Publish providers. The global contract,
Test `1.2`, snapshot encoding and feature contracts are unchanged. VS Code uses
the common strict diagnostic consumer and invocation scope. Primary and related
locations retain original exported-source coordinates; a corrected rerun clears
only that Publish/project/document contribution.

## Consequences

- Excluded malformed VBA does not block Publish merely because it contains a
  syntax or semantic error; decode and inventory obligations still apply before
  a local marker can be trusted.
- Ordinary Build continues to analyze its full source set. Import, Export,
  `test --no-build`, dirty-editor policy, and native VBE compile policy are unchanged.
- Neutral literal corpora run through both public CLI commands, complemented by
  exclusion, aggregation, immutable-capture, preservation, and native VS Code /
  Excel tests. No product depends on another product's test helper or DTO.
- Publish now requires the same project metadata evidence as Build. Evidence
  cannot be omitted merely because a template already contains a reference.
