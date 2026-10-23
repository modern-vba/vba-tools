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

## Consequences

Dependency construction cannot revive obsolete ordinary bin generation.
Saved-source validation and generated-output commitment remain independently
testable at the operations that own those behaviors. This supersedes older
four-intent descriptions only for the retired ProjectBuild output route.
