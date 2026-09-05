# Cross-product integration tests

`tests/VbaTools.Integration.Tests` owns workflows spanning independently owned
products. It is a test owner, not a shared production foundation. Product test
projects do not reference it, and it neither links product test sources nor
references or builds the language-server or debug-adapter projects.

The UserForm rename roundtrip test sends public LSP `initialize`,
`textDocument/didOpen`, and `textDocument/rename` messages to an already-built
language server. It applies the returned workspace edit and invokes the public
`vba-dev build` and `vba-dev export` process contracts. It preserves the original
Excel assertions for renamed form identity, exact sidecar bytes during rename,
nested controls and non-ASCII content after build, and matching exported `.frm`
and `.frx` files. The neutral owner keeps its own process clients and native
fixture helpers. Its only product assembly reference is `VbaDev.Infrastructure`
for the owned Excel fixture and COM cleanup; the fixture's internal access is
limited to this test assembly through `InternalsVisibleTo`.

## Running

```powershell
npm run test:cross-product-integration
```

The ordinary command compiles and discovers the suite, with the real Excel case
skipped by default. It does not require product executables or start Excel.

The complete opted-in Windows suite builds the product executables explicitly
before running this neutral owner together with product-local Excel tests:

```powershell
npm run test:windows-excel-integration
```

To run just this real Excel test, first build `VbaLanguageServer.Cli` and
`VbaDev.Cli`, then run:

```powershell
dotnet test tools/vba-integration-tests/tests/VbaTools.Integration.Tests/VbaTools.Integration.Tests.csproj --filter Category=WindowsExcelIntegration --environment VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1 -m:1 -p:UseSharedCompilation=false
```

Opted-in execution requires Windows, Excel, and trusted VBA project object model
access. By default the owner selects the already-built `net10.0/win-x64`
apphosts under each CLI project's `bin/<configuration>` directory, using the
test assembly's build configuration. Optional overrides are absolute paths to
existing executable files:

| Environment variable | Executable |
| --- | --- |
| `VBA_TOOLS_INTEGRATION_LANGUAGE_SERVER_PATH` | `vba-language-server.exe` |
| `VBA_TOOLS_INTEGRATION_VBA_DEV_PATH` | `vba-dev.exe` |

Executable resolution runs only inside the opted-in test body. Missing builds
or invalid explicit overrides fail before any Excel fixture is created; there
is no implicit build, download, or `PATH` lookup. CLI invocations drain stdout
and stderr together and use `stdin-v1` cooperative cancellation. The language
server uses an isolated temporary reference-catalog cache and exits through LSP
shutdown on the normal path.
