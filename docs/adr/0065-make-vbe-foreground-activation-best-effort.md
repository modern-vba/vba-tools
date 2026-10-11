---
status: accepted
---

# Make VBE foreground activation best effort

- Date: 2026-10-11
- Supersedes: the exact operating-system foreground-window requirement for
  Debug in [ADR 0024](0024-run-debug-targets-through-the-vbe.md) and the
  corresponding `VbeCommandContext` description.

## Context

Windows may keep another application in front after the adapter restores and
requests foreground activation of the exact owned VBE window. A release-gate
run failed solely at the exact foreground-window assertion while another
application remained in front. Foreground ownership is controlled by Windows
and is not itself proof of which VBE COM object receives a native command.

## Decision

The adapter attempts to restore and foreground the exact VBE window bound to
the owned Excel process. It rejects a window belonging to another process.
Failure to obtain exact operating-system foreground ownership after the
bounded attempt is non-fatal by itself.

The adapter continues to validate the exact source workbook, VBE project,
active code pane, selected line, design mode and enabled native command before
invoking that command through the verified VBE COM object. It never substitutes
keystrokes to the foreground window. A missing or disabled command, changed
target context, or native command failure remains a Debug setup error.

Release verification requires successful native VBE behavior in the selected
workbook and project, not exact foreground-window ownership. Unrelated release
gate failures are not waived.

## Consequences

Users may need to select the VBE window manually when Windows leaves another
application in front. The launch remains safe because native commands still
target the verified VBE COM context, and no wrong-window keystroke fallback is
introduced.
