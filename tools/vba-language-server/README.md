# vba-language-server

`vba-language-server` is the C# Language Server Protocol implementation used by
the VBA Tools Visual Studio Code extension.

The executable is a stdio JSON-RPC language server. It is primarily launched by
the extension from the bundled runtime path:

```text
bin/vba-language-server/win-x64/vba-language-server.exe
```

It also supports a direct version probe:

```text
vba-language-server --version
```

## Scope

The language server provides editing features for exported VBA source files:

- diagnostics for syntax and supported validation rules;
- completion;
- hover;
- signature help;
- document symbols and workspace symbols;
- go to definition and find references;
- rename;
- semantic tokens;
- document formatting.

It reads `.bas`, `.cls`, and `.frm` files. When a workspace contains a
`vba-project.json` manifest, project context and manifest-defined VBA project
references are used to improve cross-file and external reference resolution.

## Interactive readiness and project diagnostics

For a manifest-backed source, Interactive Semantic Readiness is reached when
the exact immutable project snapshot and its `VbaSemanticInventory` are ready
for editor queries. Snapshot construction does not eagerly construct complete
Project Validation Diagnostics. Completion, hover, signature help, symbols,
definition, references, rename, formatting, and semantic tokens can therefore
use that snapshot while project-wide validation is pending.

Document-local syntax and validation diagnostics remain available directly from
the accepted document analysis. Complete project validation runs later as
bounded `workspace/diagnostic` work in a typed, project-authority-keyed
latest-only mailbox. A newer source, manifest, reference-catalog, close, or
retirement revision replaces pending work and cancels obsolete active work.
Only a complete current result is partitioned into the separate URI-owned
`textDocument/diagnostic` publication mailboxes; cancellation or failure
publishes no partial batch. Unchanged documents retain their last accepted
project diagnostics until the new complete result is ready.

Each successful selected catalog commit cancels affected current validation
without scheduling a per-commit replacement. When the shared catalog batch
settles, it requests one validation for each still-current dirty authority; a
failed or no-op batch with no commit requests none. Ordinary invalidation keeps
active-URI and project-member routing for that refresh. Retirement removes the
routing and mailbox work, so a late batch completion cannot resurrect the
authority.

Both phases consume the same immutable Semantic Inventory and exact revision
fences. Neither editor readiness nor background project validation invokes
`vba-dev`, launches Excel, or reads a live workbook.

## Closed source encoding

Closed exported source is decoded strictly by a process-wide policy: a
recognized UTF-8 or UTF-16 BOM wins, otherwise valid UTF-8 wins, and only on
Windows may invalid UTF-8 fall back to the active ANSI code page captured once
with `GetACP`. ACP 65001 is UTF-8 and non-Windows hosts have no implicit legacy
fallback. Invalid bytes produce `invalid-disk-source-encoding`; they are not
replaced, guessed, or parsed as different text.

Open editor documents are already authoritative Unicode and bypass this byte
decoder. Encoding never selects the accepted VBA identifier forms. The
separate `vba-dev` VBE import pipeline owns any ACP staging needed by
`VBComponents.Import`.

## Runtime Boundary

The first extension release bundles the Windows x64 C# executable. There is no
TypeScript language-server fallback path in the VSIX package.

The language server is separate from `vba-dev.exe`. Workbook automation,
building, testing, publishing, exporting, CommonModules updates, and project
reference manifest edits stay in `vba-dev`; the language server owns editor
language features.

The VS Code extension owns one environment-scoped UserForm Event catalog. It
sends `vba/intrinsicHostEventCatalog` schema `1.0` notifications containing a
monotonically increasing revision and either one complete catalog or `null` to
clear unavailable state. The language server validates the full payload,
rejects stale revisions, atomically replaces or clears the catalog, and
invalidates project inventories so every authoritative `.frm` `FormModule`
binds the same current Event surface by source kind. The payload contains no
project, document, source-template, component, VBA-project-name, fingerprint,
or source-association identity.

An unavailable catalog is indeterminate rather than an authoritative empty
UserForm Event surface. Interactive requests capture committed immutable state;
they never invoke `vba-dev`, launch Excel, or wait for discovery or notification
completion. Worksheet and `ThisWorkbook` code-behind and control-instance
Events receive no catalog-derived semantics.

### Semantic module-identity Rename protocol

`textDocument/prepareRename` selects only the unquoted payload of the
authoritative valid `Attribute VB_Name`. `textDocument/rename` returns one
ordered `documentChanges` edit containing every required text edit and
non-overwriting `RenameFile` operation, or returns no edit. Matching `.bas`,
`.cls`, and `.frm` basenames follow the identity, and a matching `.frx` follows
its form; a deliberately different basename is preserved.

For a manifest-backed module, Rename captures the exact selected source-template
package bytes and obtains the containing VBA project name through a
request-scoped static `VbaProjectIdentityRead`. It validates the OPC and CFB
structure, decompresses the MS-OVBA directory stream, and decodes `PROJECTNAME`
with `PROJECTCODEPAGE` without Excel, VBIDE, discovery, or a persisted cache.
Manifest, document, workbook, generated-workbook, reference-alias, and
environment-catalog values never substitute. Missing, unreadable, malformed,
encrypted, subject to unsupported protection, or otherwise unsupported content
fails with `analysisIncomplete`. An unconditional final whole-package content
fence rejects a change before every complete module Rename `WorkspaceEdit`,
including one with no file operation.

The environment catalog supplies only a form's fixed built-in Event contracts;
it never owns the form identity. Every authoritative form is a source-owned
`FormSourceUnit` and follows the same complete designer, sidecar, semantic, and
file Rename plan whether or not the catalog is available.

A recognized rejection uses Request Failed (`-32803`) with
`error.data.reason`. Module-specific reasons include
`moduleIdentityNotExplicit`, `moduleIdentityInvalid`,
`managedModuleIdentity`, `clientCapabilityMissing`, `analysisIncomplete`,
`sameScopeCollision`, and
`resourceOperationConflict`. Invalid metadata may add `condition: "duplicate"`
or `"malformed"`. Resource conflicts add `condition`, `path`, and `guidance`;
conditions are `sourceMissing`, `sourceChanged`, `destinationExists`, and
`sidecarConflict`. A scope collision carries its complete deterministic
`conflicts` array.

`WorkspaceEditApplicationFailure` is not a server rejection reason. It occurs
after a valid complete edit reaches the client; recovery is client Undo,
filesystem repair, and a fresh Rename request rather than server rollback.

### Contract declaration-name completion

The server owns one kind-first, two-stage completion path for intrinsic Host
Event handlers, external `WithEvents` handlers, and members required by
`Implements`, including derived Public-variable Property accessors. A valid
empty or partial `Sub`, `Function`, `Property Get`, `Property Let`, or
`Property Set` name slot first returns a semantic prefix ending in one ASCII
underscore. An exact viable prefix returns canonical member names and edits
only the suffix.

Every request re-resolves admitted origins from its captured immutable
inventory. Prefixes and members coalesce case-insensitively after the shared
MS-VBAL declaration-collision policy runs, while distinct signatures,
documentation, and conditional provenance remain available for presentation
and Signature Help. Completion chooses no origin, signature, parameter, or
conditional-compilation branch.

For continuation, prefix items carry `data.retriggerCompletion: true`; the
server emits no editor command and retains no completion session. The first-party client maps
that neutral intent to reopening suggestions. Other clients can issue an
ordinary completion request after applying the prefix and receive the same
member results. Explicit completion preserves ordinary completion. The
advertised space trigger specializes contract results only in an empty proven
name slot and preserves ordinary completion elsewhere. An `_` trigger produces
contract results only in a proven contract declaration-name context; outside
one it produces none.

This feature returns name-only edits. Parameter lists, bodies, terminators,
snippets, and multi-line stubs are outside its boundary and remain future
`MemberStubGeneration` work.

## Development

Build the language server:

```text
dotnet build tools/vba-language-server/VbaLanguageServer.slnx
```

Run language-server tests:

```text
dotnet test tools/vba-language-server/tests/VbaLanguageServer.Tests/VbaLanguageServer.Tests.csproj -m:1 -p:UseSharedCompilation=false
```

Run the Release performance category:

```text
dotnet test tools/vba-language-server/tests/VbaLanguageServer.Tests/VbaLanguageServer.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false --filter "Category=Performance"
```

Run the focused CommonModules cold-readiness benchmark by pointing its active
URI at the real manifest-backed source set. Without this environment variable,
the test reports that the benchmark was not run and returns without measuring:

```text
$env:VBA_TOOLS_COMMON_MODULES_ACTIVE_SOURCE = '<CommonModules-repository>\CommonModules\src\CommonModules\Lib_Common.bas'
dotnet test tools/vba-language-server/tests/VbaLanguageServer.Tests/VbaLanguageServer.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false --filter "FullyQualifiedName~VbaInteractiveSemanticReadinessPerformanceTests"
```

### Semantic-readiness benchmark evidence

The CommonModules cold benchmark uses a fresh Release workspace and an empty
in-memory project-snapshot cache. Complete fixture and workspace setup before
the timed interval. Start that interval immediately before
`CreateProjectSnapshot(activeUri)`; stop it when the exact snapshot returns with
a readable Semantic Inventory. Do not start Project Validation Diagnostics in
the primary run. Record semantic-token projection from the returned inventory
separately, and use a separate validation run to verify the eventual complete
diagnostic result.

The benchmark reports `openDocument` outside the primary interval; `capture`
(`scopeCapture` plus `snapshotAdmission`), `diskInventory`,
`semanticInventory`, and `storeReturn` inside it; and
`semanticTokenProjection` separately afterward. It also verifies that the
primary snapshot build started Project Validation zero times.

The recorded comparison corpus has one manifest document definition whose
recursive source set produces 94 `SourceDocuments` and 49,097 parsed argument
lists. Its baseline cold snapshot was 49.907 seconds, of which 43.262 seconds
was complete-call validation; bypassing only that pass produced 6.191 seconds.
A conforming Windows Release result is no more than 10 seconds and at least 80
percent faster than the baseline, so the effective ceiling is 9.9814 seconds.
The separate repository-owned synthetic fixture supplies at least 90 documents
and 40,000 argument lists for deterministic barrier and cancellation tests; it
does not replace the real CommonModules timing run.

For a supplemental end-to-end LSP process run, set
`VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY` to an empty temporary directory.
The server writes one `.admitted` file containing `inputSequence`, `readFence`,
`kind`, `method`, `requestId`, and `admissionMilliseconds`, followed by one
`.completed` file containing the same identity plus `queueMilliseconds`,
`executionMilliseconds`, `cancelled`, and `faulted`. Keep separate phase
records for `textDocument/didOpen`, `textDocument/semanticTokens/full`,
`workspace/diagnostic`, and `textDocument/diagnostic`.

Run the deterministic blocked-validation process case with that variable set,
then preserve the timing directory with the verification evidence:

```text
dotnet test tools/vba-language-server/tests/VbaLanguageServer.Tests/VbaLanguageServer.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false --filter "FullyQualifiedName~VbaInteractiveWorkSchedulerProcessTests.Server_keeps_local_diagnostics_and_semantic_tokens_responsive_while_project_validation_is_blocked"
```

Record the following fields with every result:

| Group | Fields |
| --- | --- |
| Source | Commit SHA, clean/dirty worktree, corpus path and revision |
| Build | Command, Release configuration, target framework, runtime/SDK versions, architecture |
| Environment | Windows version, CPU, logical cores, RAM, power mode, competing load |
| Corpus | Manifest document count, source-document count, line count, argument-list count |
| Cache/sample policy | Fresh-process and in-memory-cache definition, filesystem/OS-cache treatment, warm-ups, samples, aggregation, outliers |
| Snapshot phases | `capture`, `scopeCapture`, `snapshotAdmission`, `diskInventory`, `semanticInventory`, `storeReturn`, `interactiveSemanticReadiness` |
| Separate projections | Semantic-token projection from the returned inventory, eventual Project Validation Diagnostics |
| Supplemental LSP phases | Initialize; `didOpen` admission/queue/execution; semantic-token admission/queue/execution/response; `workspace/diagnostic`; `textDocument/diagnostic` publication |
| Correctness | Token revision/content assertion and final project-diagnostic equivalence |

The test output uses `not measured` instead of silently omitting a field it
cannot discover. Treat those values as provisional: before acceptance, the
verification note must supply every field or explain why an unavailable
observation cannot affect either performance threshold. The full methodology
and warm/mixed-load budgets are in the
[interactive architecture guide](../../docs/language-server-interactive-architecture.md).

### Identifier conformance data

`VbaIdentifier` is the lexical authority for VBA names. Its generated Unicode
membership data implements [MS-VBAL 2.4, published 2025-05-20](https://learn.microsoft.com/openspecs/microsoft_general_purpose_programming_languages/ms-vbal/),
using only the forward `MBTABLE` and `DBCSTABLE` mappings in the Unicode
Consortium's [Microsoft WindowsBestFit archive](https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WindowsBestFit/).

Generation requires these 14 source files from that archive:

```text
bestfit874.txt
bestfit932.txt
bestfit936.txt
bestfit949.txt
bestfit950.txt
bestfit1250.txt
bestfit1251.txt
bestfit1252.txt
bestfit1253.txt
bestfit1254.txt
bestfit1255.txt
bestfit1256.txt
bestfit1257.txt
bestfit1258.txt
```

The generator pins the SHA-256 digest of every source file and fails before
generation when any digest differs. Regenerate the checked-in data with:

```text
powershell.exe -NoProfile -File tools\vba-language-server\scripts\Generate-VbaIdentifierConformanceData.ps1 -MappingDirectory <path-to-WindowsBestFit>
```

Verify that the checked-in file is current without rewriting it with:

```text
powershell.exe -NoProfile -File tools\vba-language-server\scripts\Generate-VbaIdentifierConformanceData.ps1 -MappingDirectory <path-to-WindowsBestFit> -Check
```

The TextMate grammar is a conservative editor fallback and is not another VBA
identifier authority. Parser-backed language features use `VbaIdentifier`.

Publish the Windows executable into the extension bundle layout:

```text
npm run publish:language-server
```

The VSIX package excludes `tools/vba-language-server/**` source files and ships
only the bundled executable output required by the extension.
