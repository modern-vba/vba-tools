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

## Runtime Boundary

The first extension release bundles the Windows x64 C# executable. There is no
TypeScript language-server fallback path in the VSIX package.

The language server is separate from `vba-dev.exe`. Workbook automation,
building, testing, publishing, exporting, CommonModules updates, and project
reference manifest edits stay in `vba-dev`; the language server owns editor
language features.

The VS Code extension also owns Host Event inspection and retained projection
state. It sends `vba/hostClassProjectionSnapshot` schema `1` notifications as
immutable full replacements or clears for one manifest document. Each payload
has an independent, monotonically increasing document-local revision and exact
canonical project, document, and source-template context. The language server
strictly validates the payload, rejects stale revisions and mismatched current
contexts, coalesces queued notifications for the same document to the greatest
revision, atomically replaces the accepted snapshot, and invalidates only that
project's semantic inventory.

`current` entries are authoritative Host Event evidence,
`lastKnownGood` entries are advisory, and `indeterminate` entries provide no
projected Event candidate. Interactive requests capture committed immutable
state; they never invoke `vba-dev`, launch Excel, or wait for inspection or
notification completion.

## Development

Build the language server:

```text
dotnet build tools/vba-language-server/VbaLanguageServer.slnx
```

Run language-server tests:

```text
dotnet test tools/vba-language-server/tests/VbaLanguageServer.Tests/VbaLanguageServer.Tests.csproj -m:1 -p:UseSharedCompilation=false
```

Publish the Windows executable into the extension bundle layout:

```text
npm run publish:language-server
```

The VSIX package excludes `tools/vba-language-server/**` source files and ships
only the bundled executable output required by the extension.
