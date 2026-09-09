---
status: accepted
---

# Share CommonModules package validation

## Context

ADR 0052 made a completely admitted CommonModulesPackage the immutable selection
authority. Its live-directory and captured-byte admission paths still duplicated
expected-name, CommonModuleName, source, and closed-package validation. They could
choose different defects for equivalent inputs: a child directory and a missing
manifest produced different errors, while the reported duplicate or unexpected
entry depended on inventory insertion order.

Two real input forms remain necessary. A live directory has physical object and
readability facts. Snapshot owns captured bytes, staging receipts, cancellation,
stable-generation proof, and cleanup. Shared logical policy must not erase that
distinction or make raw manifest entries into trusted package evidence.

## Decision

The sealed package reader adapts either input into one internal logical validation
path. The live Adapter supplies an ordinary flat inventory, exact physical names,
contextual paths, and checked byte reads. The captured Adapter supplies its fixed
names and byte arrays; it never opens a live or staging path to obtain missing
data. This small internal Seam conveys input facts, not validation authority.
There is no public substitutable validator and no new raw-entry Package factory.
The existing controlled factories issue a Package only after the shared path
finishes successfully, and the Package retains its independent immutable facts.

Live physical inventory acquisition is shared with Snapshot, which still decides
when to capture and recheck it. Sort inventory with StringComparer.Ordinal before
selecting duplicate or nonordinary entries. Sorting only successful inventories
would leave the error itself dependent on filesystem enumeration order.

Logical validation follows this order:

1. Validate the root or captured input.
2. Establish a closed flat inventory and intrinsic case-insensitive uniqueness.
3. Require the canonical manifest with its exact spelling.
4. Parse and validate manifest bytes, shape, values, declarations and dependencies.
5. Build the complete expected-name set, reject duplicate CommonModuleNames, and
   require listed sources and any matching optional form sidecars with exact names.
6. Validate listed source metadata in manifest declaration order, using the shared
   strict Windows-932, VBA source-kind, and authoritative ModuleIdentity rules.
7. Reject unexpected entries in StringComparer.Ordinal order.

The expected spelling of a manifest-declared source or sidecar cannot be checked
before the manifest is parsed. Canonical manifest spelling and inventory duplicate
checks do not have that dependency. Manifest grammar, module uniqueness, exact
dependency spelling, runtime-to-test prohibitions, and reference declarations
remain in the one existing manifest parser; package-level policy is no longer
copied between input paths.

Physical read failures remain Adapter-owned. The live path preserves readability
checks for expected files before source metadata validation. Snapshot captures
the complete physical inventory before logical validation, so physical I/O may
fail at a different time than on the live path. Equivalent logical defects have
the same category, identifying details and precedence once input adaptation
succeeds; this is not a promise of identical I/O scheduling. Read failures are
reported as package-entry read failures, not mislabeled as Windows-932 defects.

Snapshot continues to own stable-generation proof, scratch receipt creation,
cancellation checks, cleanup, retained-artifact evidence and byte-read lifetimes.
It validates its original captured arrays even if staging paths later change.
Immutable Package metadata may outlive snapshot cleanup, but it cannot authorize
source copying from disposed bytes or replace the fresh stable capture required
by a later mutation. Selection, reconciliation and transaction order are unchanged.

## Consequences

Both real input paths use the same manifest and package rules without a public
validation extension point. Multiple logical defects now produce reproducible
diagnostics, which makes the admission boundary easier to understand and maintain.

The unchanged versioned shared fixture corpus exercises both live Load and public
Snapshot Capture. Additional coverage pins competing-defect precedence, captured
inventory insertion permutations, real filesystem-entry permutations, physical
locks and reparse objects, and snapshot cancellation, cleanup and retained files.
Dependency components, request order, first-seen references and immutable Package
issuance remain covered through complete real admission.

This decision changes diagnostic precedence where it was inconsistent. It does
not change manifest grammar, CLI result schemas, release artifacts, selection
authority, captured source-unit selection APIs, or snapshot cleanup ownership.
