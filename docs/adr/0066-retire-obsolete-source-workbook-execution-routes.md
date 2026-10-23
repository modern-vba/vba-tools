# ADR 0066: Retire obsolete source-workbook execution routes

## Status

Accepted.

## Context

ADR 0061 moved ordinary Build to the selected source workbook, but nullable
construction still exposed a staged ProjectBuild/bin-generation fallback.
Unused Test-build forwarding and dedicated results kept that route reachable
from fixtures despite the public command already using bound source execution.

## Decision

BuildCommand requires SourceWorkbookBuildCommand. Ordinary Build always admits
analyzed saved sources, uses their semantic inputs, replaces modules in the
selected source session and Saves in place through shared finalization.
It never chooses bin output based on how dependencies were constructed.

Remove ProjectBuild materialization intent, legacy ordinary generated-output
entries, Test-only Build/snapshot forwarding and their result wrappers. Keep
purpose-specific ProjectBuild source admission for Build and prepared Test;
Publish exclusion rules are not a substitute. Preserve Publish, paired
snapshot/output Build, and Explicit Import transactions and output protection.
Build retains its current CLI schema and feature version.

Generated-output assertions use Publish or explicit snapshot/output Build.
Ordinary Build fixtures exercise the required source implementation, including
exact cleanup of flat import files while leaving their shared parent unowned.
Export behavior and production helpers are outside this decision.

## Direct Test composition

Issue #481 supplies SourceWorkbookTestCommand directly to the CLI composition.
Remove the forwarding TestCommand, Build-owned Test lookup, unused runner and
formatter/locator injections, snapshot-factory cloning, and the path-based test
runner plus its generation-only private plumbing. Construct the snapshot-input
factory once with the real Test Module. Synchronous entry and private-path
sanitization live inside that Module.

Saved-input, snapshot-input and no-build Test retain the exact selected source
session. Prepared navigation comes only from the executed admission; no-build
invents no captured source or locations. Preserve bound macro execution, result
reading, COM release, completed rows, cancellation and cleanup semantics through
the real source-session Interface. This changes no CLI schema or feature version.
## Input-only snapshot Test

Issue #482 replaces SnapshotTestExecutionWorkspace with SnapshotTestInput.
Delete the fictional execution-workbook basename/path, registration/receipt and
output validation. Validate canonical placement of the actual scratch directory
and captured input instead, preserving create-only exact ownership, caller and
persistent-source isolation, rejection of foreign/linked captures, and nested
cleanup evidence even after materialization consumes the capture.

The shared Test finalization facts still govern retention and cleanup. Saved
and no-build Test allocate no snapshot-input workspace. The real paired-output
Build validator is unchanged. Input ownership tests use real captured files for
changed/replaced/locked/missing evidence; a fictional output alias no longer
rejects input-only Test. No CLI schema or feature changes are introduced.
This slice depends on shared source Test finalization, not direct Test wiring.
## Consequences

Dependency construction cannot revive obsolete ordinary bin generation.
Saved-source validation and generated-output commitment remain independently
testable at the operations that own those behaviors. This supersedes older
four-intent descriptions only for the retired ProjectBuild output route.
