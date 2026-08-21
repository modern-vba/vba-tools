---
status: accepted
---

# Build debug sessions from ephemeral source snapshots

`VscodeExtension` captures one immutable `DebugSourceSnapshot` for the selected
project document without saving editor buffers. Open dirty exported VBA source
contributes its in-memory state, while source without a dirty editor contributes
its disk state. The separate debug component materializes those states as a
caller-owned complete source directory and sends that build-neutral inventory
through the explicit snapshot-aware `vba-dev build` contract; `VbaDev` does not
inspect or model VS Code dirty state.

Capture starts from the complete recursive disk inventory and overlays every
open dirty file-backed `.bas`, `.cls`, or `.frm` editor whose canonical URI is
inside the selected `DocumentSourceSet`, not only the active editor. Such an
editor is included even when its file does not yet exist on disk or was removed
after being opened; its stable source-set-relative path supplies identity.
`Untitled` and other pathless documents cannot join the inventory. If one is the
requested target or owns a participating breakpoint, selection fails with an
instruction to save it under the source set. The extension fixes that disk
inventory and the then-open editor set, text, URI, and encoding once at capture
start. It reads each selected clean source and sidecar once, performs no final
inventory or editor-version comparison, and does not retry automatically.
Later edits, additions, deletions, and renames belong to the next invocation. An
inventoried path that is deleted or renamed before its one read fails capture;
a path already read remains part of the snapshot. Ordinary `vba-dev build`
remains disk-only.

Clean source and `.frx` sidecars retain their exact disk bytes. Dirty editor
source is limited initially to UTF-8 with or without BOM, BOM-marked UTF-16 LE
or BE, and the operation-fixed active Windows ANSI code page without BOM. The
producer reads that code page directly from `GetACP` once rather than inferring
language or culture; ACP 65001 is UTF-8. A dirty legacy source encoding is
accepted only when its code page equals that ACP. Every clean and dirty text
source must strict-decode and re-encode to its original bytes before Excel
starts. Snapshot capture fails instead of silently changing encoding,
substituting characters, or guessing. Detection checks a recognized BOM first,
then strict UTF-8, then the strict fixed ACP. The captured bytes remain
authoritative and unchanged. Before `VBComponents.Import`, `VbaDev` derives a
separate invocation-internal ACP import copy under the contract settled by ADR
0027; `.frx` remains binary-only.

The extension transports source and sidecar bytes to the separate adapter as
base64 DAP fields with safe source-set-relative paths. Text source also carries
its persistent URI and one canonical `utf8`, `utf8bom`, `utf16le`, `utf16be`,
or `windows-<decimal-code-page>` token; `.frx` is binary-only. The adapter
revalidates token, BOM policy, strict decoding, and byte round trip before it
materializes and owns the session source directory passed to `vba-dev build`.
No extension-created temporary directory or caller-supplied deletion path
crosses the adapter boundary.

`vba-dev build` validates the supplied source inventory and output before
starting Excel. The output must be outside the snapshot directory and every
manifest document's `DocumentSourceSet`, and distinct from the resolved
`vba-project.json` and every document's source template, bin workbook, and
publish workbook after case-insensitive, filesystem-canonical comparison,
including reparse-point aliases. If safety cannot be established, validation
fails without writing output. Any other caller-owned target, including an
existing file, is eligible for atomic replacement. The command returns after
generation, owns its hidden build process and internal scratch only for that
invocation, and does not rewrite any `DocumentSourceSet` or
`vba-project.json`. It does not delete the successful snapshot output after
returning. Project selection, template
selection, references, and other manifest-owned configuration continue to come
from disk.

The debug component owns the snapshot input and successful `DebugWorkbook` for
the debug-session lifetime. It keeps them outside the project, removes them
after their owned Excel process has ended, and uses the same workbook file name
as the manifest-defined bin workbook, such as `Sample.xlsm`. Consequently,
`ThisWorkbook.Name` matches a normal build without replacing completed bin
output, while `ThisWorkbook.Path` intentionally identifies the temporary
directory. A restart captures a new snapshot, invokes a new build, and replaces
the session artifacts; edits made after launch do not affect the active workbook
until restart or a new session.

This supersedes the saved-source and persistent-bin-output portions of ADRs
0019 and 0021. Their process-separation and strong process-ownership decisions
remain in force. ADR 0027 supersedes this ADR's earlier adapter-hosting and
command-owned session-artifact lifecycle. The exact snapshot representation and
output-path ownership and the lossless editor-encoding contract are settled by
ADR 0027.
