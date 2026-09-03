---
status: accepted
---

# Replace template host projections with an environment-scoped UserForm Event catalog

VBA Tools no longer inspects every project document's source template to obtain
intrinsic host Events. In a trusted workspace, extension activation starts one
asynchronous environment-scoped discovery in an owned Excel process on an
invocation-scoped private desktop, creates an unsaved blank workbook and one
temporary UserForm, observes the locally installed UserForm Event surface, and
closes everything without saving. Exact-PID observation begins before process
resume, and neither startup nor unexpected UI falls back to the interactive
desktop. It opens no user workbook, imports no user source, does not block
language-server startup, and shares the resulting catalog with every
authoritative `.frm` source in the session.

Worksheet and `ThisWorkbook` code-behind are outside the supported source and
intrinsic Event model, while `.frm`/`.frx` import, export, build, and debug
remain supported independently. Control-specific Events such as
`CommandButton1_Click` are also outside this catalog because they require
designer-instance metadata that one generic UserForm cannot establish. If
environment discovery fails, UserForm intrinsic Event intelligence remains
unavailable and never falls back to inspecting project source templates.

This replaces the document-scoped `HostClassProjection` lifecycle from ADR
0031. The containing `VBProject.Name` authority required by manifest-backed
module Rename is not part of the environment catalog. At Rename request time,
the language-server side reads the `PROJECTNAME` record from the exact source
template's `vbaProject.bin`, binds it to the same captured template content,
and fails with `analysisIncomplete` when that evidence is missing, malformed,
or changes during planning. It does not start Excel, cache an observation, or
substitute the manifest project label, workbook name, or generated blank
project name. This supersedes ADR 0029 where it assigned acquisition of that
authority to `host-class list` and its projection snapshot. It also supersedes
ADR 0029's host-managed identity treatment for UserForms and intrinsic document
modules: UserForms use the source-owned model below, while document-module
source and Rename are outside the supported product scope.

The same replacement narrowly amends ADRs 0007, 0011, 0016, 0017, and 0030,
plus the `vba-dev` workbook-backed command-model ADR, wherever they describe the
old host-class command, document projection input, freshness authority,
template watcher, intrinsic worksheet/workbook examples, or host-managed form
ownership. Their unrelated versioning, diagnostics, snapshot, semantic
inventory, conditional-family, Rename, and command-lifecycle decisions remain
accepted.

The unreleased document-scoped `vba-dev host-class list` command,
`hostClass.list` capability, template watchers, per-document queue and status,
source-template association, and `vba/hostClassProjectionSnapshot` transport
are removed without a compatibility shim. They are replaced by the
environment-scoped `vba-dev host-event list --format json` command and a new
versioned full-catalog transport. The command accepts no project or document
selector and inspects only its generated blank workbook and temporary UserForm.

`VBA Tools: Refresh UserForm Events` explicitly repeats the same environment
discovery without a document chooser. A startup failure leaves the catalog
unavailable and appears in environment-level status and Output so the user can
retry after repairing Excel or VBIDE access. A failed explicit refresh retains
an already-current catalog, and neither path falls back to a source template.

An exported UserForm is source-owned as one `FormSourceUnit`: its `.frm` text,
optional matching `.frx`, and their paths are one mutation boundary. A current
environment catalog establishes the built-in UserForm Event contract but never
makes the form identity template-owned. Semantic module Rename therefore
remains available for manifest-backed forms and must update every
identity-bearing form record, sidecar reference, semantic occurrence, and
matching `.frm`/`.frx` basename in one version-fenced plan. Sidecar uncertainty
fails closed, and Windows Excel integration must prove a sidecar-backed form can
be renamed, imported, saved, reopened, and retain its controls. Intrinsic
handler names such as `UserForm_Initialize` remain fixed catalog contracts and
are not module Rename targets.
