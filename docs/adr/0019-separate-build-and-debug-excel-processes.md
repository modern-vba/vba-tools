---
status: accepted
---

# Separate build and debug Excel processes

A `VbeDebugSession` uses the existing dedicated hidden Excel automation for
building, closes that process after the build completes, and then creates a
fresh visible `DebugExcelProcess` for native VBE breakpoint setup and procedure
execution. Reusing the build process risks preventing break mode after
programmatic VBIDE changes, while attaching to a user's Excel session would make
breakpoint state, ownership, and debug-session termination ambiguous.

Cancellation during build terminates the hidden build Excel process and removes
only `vba-dev build` invocation scratch. ADRs 0025 and 0027 supersede the earlier
persistent-bin output and ownership contract: the separate debug component asks
`VbaDev` to build a caller-owned session workbook from an ephemeral source
snapshot, leaves the previous completed bin workbook unchanged, and owns cleanup
of the successful session workbook after the build invocation exits.
