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
only incomplete temporary output. The previous completed bin workbook remains
until the generation pipeline atomically replaces it; cancellation after
replacement retains the new bin but does not start the visible debug process.
