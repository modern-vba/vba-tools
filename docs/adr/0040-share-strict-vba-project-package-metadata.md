---
status: accepted
---

# Share strict VBA project package metadata

`VbaProjectPackageMetadata` is a product-neutral foundation implemented by
`VbaTools.ProjectMetadata`, outside the executable product trees. It reads one
caller-fixed package byte array and cancellation token, and returns immutable
project name, declared code page, system kind, project constants, and the exact
VBA project part content identity, or a typed neutral failure. It has no
filesystem, Excel, LSP, DAP, product DTO, or product-test dependency.

The language server and debug adapter use the same reader and strict package
topology. The supported macro-enabled workbook content type, workbook
relationships part, unique internal VBA relationship, canonical VBA part, and
effective content-type uniqueness are required. Part sizes, archive entry count,
XML processing, compound-file access, decompression, and project-information
records have one set of bounds. There is no compatibility or parsing-policy
switch. A custom part does not become another VBA project solely because of its
basename.

The reader owns one private MS-OVBA decompressor and one project-information
parse. Compression is no longer syntax, and the debug adapter has no forwarding
decompressor. This completes the temporary mechanical placement described in
ADR 0039. The reader validates the project-information prefix rather than
becoming a reader for unrelated later directory records or source modules.

Both consumers adopt the language server's existing strict LCID and LCIDINVOKE
value of 0x0409 and zero LIBFLAGS. The declared project code page is independent
of those LCIDs and of source-text ACP admission. PROJECTNAME retains its
1..128 encoded-byte boundary and supported non-identifier values; module naming
limits are not applied to it. Constants retain their strict encoded-string
agreement, grammar, signed-16-bit values, case-insensitive uniqueness, and
built-in-name exclusion. Their names share the debug adapter's existing
255-character support boundary. This deliberately removes the language
server's previously uncapped constant-name acceptance without changing global
syntax identifier recognition or the debug settings constructor. See
[PROJECTCONSTANTS](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ovba/042a3b56-56bc-4897-bcb1-4138e05b996e)
and [the VBA identifier limit](https://learn.microsoft.com/en-us/office/vba/language/reference/user-interface-help/identifier-too-long).

Callers fix bytes before entry. The neutral reader does not acquire a file,
recapture a source, fence external changes, or decide a product lifecycle.
Cancellation is checked before admission and before publishing either success
or failure. Failures distinguish package, topology, VBA part, compound file,
compressed directory, project information, unsupported code page, and project
name. Consumer-specific wording and failure projection stay in each adapter.

The language-server adapter retains its defensive byte capture, request-scoped
whole-package identity, and unconditional final content fence. The debug
adapter retains .xlsm file I/O and sharing, settings projection, and comparison
of generated and opened workbook identities. A whole-package identity and a
VBA-part identity are intentionally different values: unrelated package changes
must invalidate the former without being mistaken for VBA-part changes.

Format conformance belongs to the neutral reader's tests. Product tests retain
their own I/O, sharing, projection, content-fencing, and lifecycle checks.
Data-only fixtures are permitted; product test assemblies and linked helper
source are not dependencies of another product or the foundation. The
repository architecture guard explicitly recognizes this foundation. VbaDev
does not acquire a dependency on either consumer or its harness.

## Consequences

- Duplicate package and directory parsers become one bounded implementation.
- Structurally incomplete packages previously accepted only by the debug
  adapter are rejected consistently; no lax fallback is retained.
- Shared metadata remains immutable and independent of product result types.
- Existing file access and lifecycle authorities stay with their products.
- `npm run test:project-metadata` owns the shared format-conformance suite and
  is part of the repository test and release-verification workflows.
