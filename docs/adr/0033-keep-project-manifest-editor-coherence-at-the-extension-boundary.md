---
status: accepted
---

# Keep project-manifest editor coherence at the VS Code extension boundary

An open `ProjectManifest` buffer can contain state that `VbaDev` cannot see,
while a manifest-mutating CLI command can commit state that VS Code has not yet
loaded. `VscodeExtension` therefore owns `ProjectManifestMutationPreflight`,
`ProjectManifestMutationOutcome`, and `ProjectManifestPostMutationCoherence`.
`VbaDev` remains disk-authoritative and editor-neutral, and its existing
`ProjectManifestMutationLease` remains the only cross-process writer lock.

Immediately before launching a mutation against an existing manifest, the
extension identifies the matching file-backed buffer. A dirty buffer requires
an explicit `Save and Continue` or `Cancel`; only that manifest is saved, and a
successful save is followed by a disk reread, selection-projection validation,
and exact target re-resolution. Cancellation, save failure, an unusable disk
projection, or a missing selected target launches no process. One VS Code
window rejects a second simultaneous mutation for the same canonical manifest
identity rather than queuing stale input, while other manifests may proceed
independently. A clean open buffer that differs from disk offers immutable
comparison, explicit reload, or cancellation and restarts preflight only after
buffer-to-disk equality is proved.

For every launched mutation, the extension captures the pre-launch manifest
bytes and observes distinct buffer content revisions until coherence is
classified. It combines the child process result with the post-exit disk bytes,
because cancellation or abnormal termination alone does not prove that an
atomic commit did not occur. Byte-identical manifest state proves only that no
manifest editor reconciliation is required; it does not prove that the whole
operation was a no-op or that changes to other files were rolled back.
Operation success and no-op status remain owned by the command's schema-valid
result. A structurally usable manifest change enters coherence even after a
nonzero or cancelled result; a missing, unreadable, or structurally unusable
manifest is untrusted and blocks another mutation for that identity pending
recovery.

The extension treats a content transition as passive-safe only when the observed
buffer moves cleanly and directly from its pre-launch text to the exact
post-invocation disk snapshot, without another distinct text or dirty-state
observation. Such a buffer receives up to two seconds to converge through VS
Code's native external-file synchronization. Failure to prove clean equality
with both current disk and the immutable snapshot offers explicit recovery
without moving focus or rewriting the buffer automatically. Any competing
revision, including one later made clean by Auto Save, is preserved. Recovery
offers `Compare Changes` against the immutable post-invocation disk snapshot,
confirmation-gated `Reload from Disk`, and non-mutating `Keep Editing`. A
user-invoked compare or reload may focus the manifest as part of that explicit
action; passive synchronization never does. Competing evidence requires an
explicit recovery action and a fresh equality proof before the same-manifest
mutation block clears. Reference List, CommonModules List, and project
automation Doctor may continue when their disk-state basis is visible; other
workflow behavior during divergence remains outside this decision. Reload
checks disk, editor revision, and active-editor identity before and after VS
Code's revert action. A non-cooperating writer can race between those checks,
so a failed postcheck retains divergence and does not trigger an automatic retry
or compensating edit.

The extension never performs an automatic structural merge, saves a merged
manifest, silently applies last-writer-wins behavior, or sends editor state to
`VbaDev`. `VbaDev` retains complete manifest-validation authority; the extension
validates only the structural and selection evidence required for safe targeting
and coherence. This deliberately favors loss prevention and explicit recovery
over automatic conflict resolution, without changing the CLI schema,
`primaryDocument`, or direct-CLI default behavior.
