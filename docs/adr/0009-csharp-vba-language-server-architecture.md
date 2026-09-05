---
status: accepted
---

# C# VbaLanguageServer architecture

VbaLanguageServer runtime behavior, `VbaProject` state,
`LanguageServerManifestResolution`, semantic model, and LSP feature logic live
in C#. `VscodeExtension` remains TypeScript because VS Code extension APIs and
the `vscode-languageclient` integration are TypeScript-facing, but that
TypeScript layer is an adapter for extension activation, command registration,
Test Explorer integration, packaging, and launching the C# VbaLanguageServer
executable.

The reusable VBA parser remains C# but is owned by the product-neutral
`VbaTools.Syntax` foundation, not the language-server executable or its source
tree. ADR 0039 establishes that ownership and the one-way VbaDev provider
boundary without changing the editor-intelligence responsibility here.

Editor-intelligence LSP features such as completion, hover, signature help,
diagnostics, document symbols, definition, references, and semantic tokens are
part of that C# language-server responsibility.

The TypeScript adapter may keep thin VS Code integration logic such as command
registration, QuickPick flows, Test Explorer item projection, diagnostic
collection projection, `ProjectManifest` discovery for UI selection, and parsing
stable `VbaDev` JSON output. It must not own VbaLanguageServer
editor-intelligence logic such as VBA parsing, semantic resolution, reference
catalog resolution, semantic token generation, or LSP feature behavior.

This supersedes the older TypeScript parser direction. Because
`VscodeExtension` has not been released with the TypeScript language-server
runtime, development does not need a compatibility switch or fallback for that
runtime.

The C# implementation also does not preserve compatibility with removed
HostApplication settings, TypeScript parser runtime configuration, or
TypeScript-owned language-server data shapes.

The C# VbaLanguageServer stays a separate executable from `vba-dev.exe`.
`VbaDev` remains the user-facing and automation-facing CLI for
workbook-backed project operations, while shared ProjectManifest,
VbaProjectReferenceSelection, and VbaProjectReferenceCatalog logic should move
toward explicitly public VbaDev-owned non-command libraries or product-neutral
C# foundations when sharing removes real duplication. Command behavior stays
behind the public process contract. No production, build-only, or test reference
from VbaDev to another product is permitted.
