---
status: accepted
---

# Share native Run and inspection with verified VBE context

## Context

Source Run and Doctor Run/Continue/inspection independently selected code panes
and checked controls. Doctor created its modal baseline before preparing the
ordinary VBE UI, and final cancellation/currentness checks could differ.

## Decision

Extend ADR-0068's exact-binding native-operation Module with closed Run,
Continue and target-inspection operations. They share component/code membership,
native procedure-position/bounds, required mode, pane/selection, command identity,
enabled state and workbook-binding proof. Run requires design mode; Continue
requires break mode. Foreground activation remains best effort.

The existing session supplies a typed Run preparation Adapter. It starts the
modal baseline only after the ordinary native context is visible and verified.
Doctor's owned execution settings remain in its Adapter. Observation health and
currentness/cancellation are checked after preparation and before Execute.
A failed baseline or final validation invokes no Run. The existing owner retires
an acquired failed-start phase once, retaining both primary and retirement
failures. Late phases retain their original lifetime supervision; Doctor's
deferred phase is attached to observation of the same in-flight operation.

Inspection never invokes a command, changes execution-only settings, or starts
a Run phase. Continue shares native validation without starting another phase.
Reset remains a separate exact-active-project operation and does not select
another project. The Module accepts no arbitrary command ID, generic callback
registry, or validation-bypass policy and acquires no Excel lifetime.

This refines the TargetStart baseline timing in ADR-0044. Its unbounded prompt
policy, ADR-0048's retained evidence, ADR-0062's Source ownership, and ADR-0065's
foreground behavior remain. Native procedure-name selection, target eligibility,
source projection and launch admission order are unchanged.

## Consequences

The duplicated Run and native-command recipes are removed. Production-path
regressions cover both Adapters, baseline timing/failure, post-baseline mode
changes/cancellation, command failures, late observation faults and single
retirement. Real Excel checks verify Source and packaged Doctor lifecycle.
