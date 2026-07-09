---
status: accepted
---

# Migrate VbaLanguageServer to C# before reference catalogs

Reference-aware editor intelligence should be implemented after a tracer-bullet
C# VbaLanguageServer is in place. `VscodeExtension` remains the TypeScript
language client, but it should launch a self-contained C#/.NET VbaLanguageServer
process over LSP before `VbaProjectReferenceCatalog`, TypeLib discovery, and
manifest-driven `VbaProjectReferenceSelection` are added.

ADR 0009 records the resulting C# VbaLanguageServer architecture boundary after
this migration.

This order avoids implementing project manifest parsing, reference resolution,
catalog identity handling, and TypeLib discovery twice. Those concerns overlap
with `VbaDev`, which is already C#/.NET and Windows-focused for workbook and
COM automation. Moving the VbaLanguageServer process first lets the language
server share domain code and tests with `VbaDev` while keeping completion,
hover, and signature help on a dedicated LSP process.

The migration should not be a big-bang rewrite. The first slice is a minimal C#
LSP server that `VscodeExtension` can launch and validate through
initialize, shutdown, text synchronization, and a simple completion response.
Existing language features can then move behind tests before
reference-aware completions are introduced. Until that migration slice is
complete, new reference-catalog behavior should not be built deeply into the
TypeScript server.

The C# VbaLanguageServer should be a separate executable from `vba-dev.exe`.
`VbaDev` remains the user-facing and automation-facing CLI for workbook
operations, diagnostics, and reference changes, while the language-server
executable owns the long-lived stdio LSP process launched by `VscodeExtension`.
Shared manifest, reference, and catalog logic should live in C#
libraries referenced by both executables instead of exposing VbaLanguageServer
as a `VbaDev` subcommand.

`VscodeExtension` should not ship a Marketplace release that depends on the
partially migrated C# VbaLanguageServer. Because releases are held until the C#
server migration is complete, `VscodeExtension` does not need a user-facing
TypeScript/C# runtime switch or a released TypeScript fallback path. Development
can proceed directly toward the C# server as the only supported language-server
runtime for the next release.

The first Marketplace-ready package should bundle a Windows x64 self-contained
language-server executable under `bin/vba-language-server/win-x64/` alongside
the bundled `vba-dev.exe`. Users should not need to install a separate .NET
runtime. `VscodeExtension` should treat non-Windows environments as unsupported
for the initial release, while leaving room to revisit cross-platform parser and
VbaLanguageServer behavior later.

Implementation should be sequenced so the C# LSP runtime exists before
reference-aware behavior is added: first add a C# LSP tracer bullet, then shared
domain libraries and manifest fixtures, then migrate existing TypeScript server
features, then remove the TypeScript server and HostApplication settings, then
add manifest-driven `LanguageServerManifestResolution` and
`VbaProjectReferenceSelection`, then bundled/cache reference catalog behavior,
and finally Windows TypeLib discovery with background catalog refresh.
