---
status: accepted
---

# Keep VbaDev independent of product-owned modules

This decision supersedes the project name, location, and VbaLanguageServer
ownership portions of ADR 0010. Its hand-written parser strategy, syntax
interface, recovery behavior, and lexical decisions remain accepted. This
decision also refines the sharing and process-composition guidance in ADRs
0008, 0009, 0020, 0027, and 0035.

`VbaDev` is a standalone provider. Its production and test projects may depend
on the platform, third-party packages, VbaDev-owned modules, and explicitly
designated product-neutral foundation modules. A `VbaTools.*` name alone does
not establish neutrality. VbaDev projects must not reference `VscodeExtension`,
`VbaLanguageServer`, `VbaDebugAdapter`, their protocol DTOs, compatibility
manifests, implementation assemblies, test assemblies, or linked test source.
This rule applies to build ordering and test harnesses as well as production
types; a reference that does not expose another assembly's types is still a
dependency.

Other product modules may depend on `VbaDev`; dependency in that direction does
not violate this decision. Command behavior is consumed through the
**PublicToolProcessContract**: command spelling, standard streams, exit status,
JSON or NDJSON results, cancellation transport, and provider-owned capability
versions. An explicitly reusable VbaDev-owned non-command library interface may
also have downstream consumers, but it never gains a reference back to them.
`VbaDev` never invokes or discovers an extension, language-server process, or
debug-adapter process.

The reusable parser and syntax model move from the product-owned
`VbaLanguageServer.Syntax` project into the product-neutral `VbaTools.Syntax`
module at `tools/vba-syntax/src/VbaTools.Syntax`, outside every executable
product tree. Its tests live at
`tools/vba-syntax/tests/VbaTools.Syntax.Tests`. `VbaDev`,
`VbaLanguageServer`, `VbaDebugAdapter`, and future documentation adapters may
depend on that module. `VbaTools.Syntax` depends on none of those consumers and
contains no LSP, VS Code, DAP, workbook automation, command behavior, or
product-specific projection. This preserves one parser implementation while
making its ownership match its actual multi-product interface.

The existing internal `MsOvbaCompression` helper is carried mechanically to
keep this migration buildable, without becoming a public syntax API. Issue #362
owns its final private placement in the neutral workbook metadata reader.
Moving syntax ownership does not broaden syntax into OOXML, CFB, or MS-OVBA
metadata parsing.

A **CrossProductConformanceFixture** is a repository-neutral, data-only input:
byte payloads, declarative metadata, and expected classifications or failures.
Each product owns its loader, assertions, and lifecycle tests. `VbaDev` test
projects do not reference another product executable or test project, link
another product's test source, or use another product's helper as their test
seam. No product consumes another product's test assembly or linked test source.
A downstream consumer test may launch an already-built `VbaDev` executable
through its **PublicToolProcessContract** or use an explicitly public
VbaDev-owned non-command library interface. Product-spanning process
verification may be owned by that consumer or by a neutral packaging or
integration test.

The UserForm rename/build/export round-trip process coverage belongs to
`tools/vba-integration-tests/tests/VbaTools.Integration.Tests`. Its own LSP
process client invokes already-built language-server and VbaDev executables;
the integration project does not build those executables by referencing them.
This test owner is not a foundation dependency of VbaDev or any other product.
The real Excel case remains opt-in through
`VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1`. Explicit executable overrides use
`VBA_TOOLS_INTEGRATION_LANGUAGE_SERVER_PATH` and
`VBA_TOOLS_INTEGRATION_VBA_DEV_PATH`.

The source-snapshot release retains independent provider and consumer
contracts. `VbaDev` owns and advertises only
`build.sourceSnapshot`, `test.sourceSnapshot`, and
`sourceSnapshot.activeWindowsCodePage` for that surface; it accepts
caller-neutral snapshot input and knows no DAP version or schema.
`VbaDebugAdapter` owns and validates the DAP protocol and `sourceSnapshot`
schema, validates the supplied CLI's required capability, and invokes only the
`vba-dev` **PublicToolProcessContract**.
`VscodeExtension` owns its compatibility matrix and validates the independent
CLI and adapter capabilities it consumes. A coordinated release may change all
three in one vertical slice, but coordination never becomes a reverse runtime
or project dependency.

The repository-level `npm run verify:architecture` dependency guard enumerates
production and test project references, assembly references, linked compile
inputs, and product contract imports. It fails when `VbaDev` reaches into
another product tree or when an explicitly designated neutral foundation
depends on one of its product consumers. Downstream references from another
product to an explicitly owned VbaDev interface remain permitted. The guard
checks architecture; it does not replace ordinary build, packaging, or
process-contract tests.

## Consequences

- Moving the parser project and namespace is an intentional internal breaking
  change for its current in-repository consumers; parser behavior and syntax
  meaning remain compatible, including recovery, trivia, and source spans.
  This ownership migration does not change VbaDev commands or their public
  process contract.
- The parser has genuinely product-neutral ownership instead of a reusable
  implementation hidden behind a Language Server-owned name and path.
- `VbaDev` can be built and tested without building or linking another product
  executable or test harness.
- Extension, language-server, debug-adapter, and future documentation work may
  depend on stable provider or foundation interfaces without making `VbaDev`
  depend on those consumers.
- Coordinated package releases remain permitted where a version matrix changes
  atomically; they do not justify sharing runtime DTOs or implementation
  assemblies across product seams.
