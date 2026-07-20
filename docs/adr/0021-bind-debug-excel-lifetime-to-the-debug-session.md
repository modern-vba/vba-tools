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
process is terminated, completed source saves are retained, and the adapter
reports cancellation rather than `DebugSetupError`.
