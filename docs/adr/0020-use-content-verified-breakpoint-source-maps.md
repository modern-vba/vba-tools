---
status: accepted
---

# Use content-verified breakpoint source maps

`BreakpointTransfer` maps `.bas`, `.cls`, and `.frm` exported-source positions
to VBE code-module positions by using the reusable `VbaLanguageServer.Syntax`
parser core to exclude export-only attributes and designer records and then
verifying the remaining code against the built `CodeModule`. Fixed line offsets
and a second debug-specific parser are rejected because exported source kinds
have different hidden records, and divergent parsing or a stale offset could
silently place a native breakpoint on the wrong executable statement.

Because VBIDE provides no breakpoint readback API, an exact source map and a
successful native VBE breakpoint command form the verification boundary.
Breakpoints remain unverified while setup is pending, and any missing, disabled,
or failing command aborts the launch rather than falling back to source
instrumentation.

Conditional-compilation eligibility is resolved against the generated
workbook's actual Excel/VBE environment. An inactive target or participating
breakpoint aborts setup; the adapter neither selects a sibling branch nor
accepts launch-specific compiler-constant overrides.

The map preserves physical-line identity. Colon-separated statements retain
the VBE's line-level stop semantics, and a rejected continuation line aborts
setup instead of relocating the breakpoint.
