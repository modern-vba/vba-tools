---
status: accepted
---

# Bind debug Excel lifetime to the debug session

Each `DebugExcelProcess` is strongly owned by one `VbeDebugSession` and is
force-terminated whenever that session ends, including explicit stop, VS Code
shutdown, Extension Host restart, and Debug Adapter failure. A process-lifetime
mechanism such as a Windows Job Object prevents orphaned Excel processes and
locked generated workbooks; the accepted trade-off is that session loss
discards unsaved workbook changes and VBE state without an Excel save prompt.

The same cancellation contract applies throughout launch: any owned Excel
process is terminated and the adapter reports cancellation rather than
`DebugSetupError`. ADRs 0025 and 0027 supersede the completed-source-save and
command-owned-artifact wording: debug launch no longer saves source, `VbaDev`
cleans only its active build invocation, and the separate debug component
removes its caller-owned snapshot and session workbook while leaving persistent
project files unchanged.

ADR 0027 also extends strong ownership to an active `vba-dev` child process and
adds a lease-based reaper for adapter-owned session files. Job closure handles
process-tree termination; a session-ID-only cleanup operation and next-start
stale-lease scan recover filesystem state without accepting arbitrary paths.
