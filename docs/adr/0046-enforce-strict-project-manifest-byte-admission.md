---
status: accepted
---

# Enforce strict ProjectManifest byte admission

Issue #373 removes permissive and duplicated disk-manifest decoding. Previously,
StreamReader BOM detection could select replacement decoders and accept UTF-32,
while extension Buffer conversions could silently replace invalid UTF-8 or
truncate malformed UTF-16. A successfully parsed manifest could therefore
identify a project using text different from the authored bytes.

## Accepted bytes

Disk `vba-project.json` accepts strict UTF-8 with or without a BOM and strict
UTF-16LE or UTF-16BE with the matching BOM. Classify UTF-32 signatures before
the UTF-16LE prefix and reject them. Reject malformed/truncated BOMs, BOM-less
UTF-16, invalid UTF-8, malformed/odd-length UTF-16, and decoder replacement
fallback before JSON parsing. Literal NUL characters cannot enter manifest
text; escaped JSON content remains a structural-validation concern. A genuinely
encoded U+FFFD is ordinary Unicode, not evidence of decoder fallback.

The C# products use `ProjectManifestByteDecoding` through their existing shared
manifest-contract dependency. TypeScript uses one local strict decoder with
fatal decoding and explicit BOM handling. A repository-neutral data-only corpus
in `fixtures/project-manifest-encoding/` proves matching classifications; no new
runtime product dependency or general source-encoding service is introduced.

## Validated VbaDev values

`ProjectManifestCodec` owns strict byte decode, JSON parsing, schema and domain
validation, physical DocumentSourceSet isolation, expected exception projection,
and canonical encoding order. Ordinary load, mutation snapshots, atomic writes,
recovery serialization, and initial-project staging use this same codec.

Disk reads and writes, raw-byte compare-and-swap authority, the mutation lease,
recovery ownership, and atomic commitment remain outside it. An invalid input
fails before mutation rebasing. Canonical output remains byte-exact UTF-16LE
with BOM, two-space indentation, stable property order, CRLF, and one trailing
CRLF. The schema and command contract versions do not change.

## Product boundaries

Every language-server disk-manifest read follows this byte contract. Open
Unicode editor overlays retain their existing authority and validation rules.
VBA `DiskSourceDecoding` and `SnapshotSourceEncoding` keep their separate
fixed-ACP and editor-transport policies; neither applies to manifests.

Extension project selection, Test Explorer, debug configuration, export, and
mutation coherence share `decodeProjectManifestBytes`. Invalid bytes cannot
establish a command target; mutation preflight and the final launch check reject
invalid disk bytes before a companion process starts.
