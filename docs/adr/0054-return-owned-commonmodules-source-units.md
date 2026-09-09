---
status: accepted
---

# Return owned CommonModules source units

## Context

ADR 0052 established immutable Package identity and dependency authority, and
ADR 0053 unified complete package validation. Installation and new-project
consumers still selected metadata and then joined source and form sidecar bytes
by filenames. Each consumer had to understand the same package-side relationship.

Snapshot already captured stable exact bytes, returned defensive copies, and
owned cleanup. This decision adds a selected source-unit contract; it does not
claim that the preceding raw-copy behavior lost or mixed captured bytes.

## Decision

`CommonModulesPackageSnapshot.SelectCapturedSources` accepts ordinary module
requests and returns a sealed CommonModulesCapturedSelection. The Package alone
resolves names, filenames, dependency order and RequiredReferences. Each ordered
CommonModulesCapturedSourceUnit exposes the canonical immutable Entry, exact
SourceBytes, and optional matching SidecarFileName and SidecarBytes.

Constructors are private. Controlled factories obtain canonical entries from the
snapshot's admitted Package and establish source/sidecar correspondence inside
the snapshot boundary. There is no public constructor accepting arbitrary entry
and byte tuples. Manifest or sidecar filenames are not selected modules. Raw
arbitrary-filename byte reads become internal operations used by the unit owner.
The live Package continues to retain metadata rather than source bytes.

Units refer to their existing snapshot owner. All unit and captured-selection
getters use its disposal guard, including Entry, references and absent-sidecar
access. SourceBytes and SidecarBytes return independent copies, while selection
collections are immutable views. Null denotes an absent sidecar; an empty byte
array denotes a present, zero-byte sidecar. Source data always comes from the
original captured arrays, even if live files or staging files later change.

Snapshot remains the sole byte-lifetime and cleanup owner. Neither a unit nor a
selection exposes independent disposal, new scratch receipts or new cleanup
policy. Successful or retained cleanup ends owner-bound access through the
existing guard. Already returned byte copies and immutable Entry values are
detached values and remain usable; there is no attempt to revoke them. The
metadata-only CommonModulesSelectionPlan and Package retain their independent
post-cleanup lifetime for provisional checks.

### Reconciliation and Update

Update has a distinct ordered selection: requested dependency closure followed
by retained installed entries, without expanding new dependencies from those
retained entries. Do not pass that result back as new requested roots.

The sealed CommonModulesReconciliation privately retains its originating Package
identity. An internal snapshot operation accepts it only when that exact Package
is the snapshot's Package. Equivalent paths, names or contents do not establish
this provenance. Both ordinary and reconciled selection then use the same private
unit materialization, in their already established order with their established
reference union. Raw entry lists do not grant callers materialization authority.

For example, Feature -> Base plus retained Extra -> Another produces units for
Base, Feature, Extra. It does not select Another or its additional references.

### Consumers and target authority

Final installation and new-project plans consume units directly. They no longer
join selected metadata back to snapshot source filenames or reconstruct matching
package sidecars. Provisional Add and Update still capture metadata-only plans or
Packages, release their snapshots, and use that metadata for early checks. Final
rebased mutation still obtains a fresh stable snapshot; provisional source units
must not be carried across cleanup or used as authority for later source copying.

Installed target identity validation, destination filename and canonical-path
conflicts, overwrite decisions, obsolete sidecar removal, absence preconditions,
reference evidence, transaction commitment, recovery and file ownership stay with
their current consumers. Those checks concern target state, not package validity.
CommonModulesSourceMutationWriter continues to receive desired bytes and target
preconditions without becoming a Package or snapshot owner.

## Consequences

Consumers operate on selected source units while Package and Snapshot retain
separate responsibilities. Package identity and dependency rules are not repeated,
and no source bytes are forced into live listing or diagnostic operations.

Verification covers exact standard/class/form bytes, correct sidecar pairing,
empty versus absent sidecars, request/dependency/reference order, immutable views,
foreign-Package rejection, post-cleanup access, retained cleanup evidence and
invalid admission. Existing installation, NewProject, target collision, sidecar
normalization, provisional metadata and Update non-expansion regressions remain.

The CLI schemas, package grammar, installed ordering and mutation/recovery
guarantees are unchanged. Direct assembly consumers of the former public raw-byte
methods must migrate to selected units; snapshot-internal diagnostics may still
inspect the captured manifest without reopening staging files.
