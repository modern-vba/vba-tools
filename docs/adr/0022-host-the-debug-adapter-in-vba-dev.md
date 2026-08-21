---
status: superseded
---

# Host the debug adapter in vba-dev

Superseded by ADR 0027. The text below records the earlier decision.

The bundled `vba-dev.exe` hosts an internal stdio `VbaDebugAdapter` entry point
instead of implementing the adapter inline in TypeScript or shipping another
executable. `VscodeExtension` contributes the `vba` debug type and resolves
launch configuration, while `VbaDebugAdapter` owns build execution, Excel COM,
VBIDE breakpoint setup, process monitoring, and `DebugExcelProcess` lifetime;
this preserves the existing automation boundary and bundled distribution unit.
The extension may synthesize a transient `VbaLaunchConfiguration` from the
active source for zero-configuration F5, but it does not create or modify
`launch.json`. Under ADR 0025, it supplies an immutable source snapshot through
the adapter protocol without saving dirty editor buffers or exposing VS Code
dirty-state concepts to `VbaDebugAdapter`.

Adapter output is limited to `DebugLifecycleOutput`. VBA program output and
runtime state remain in the VBE; the adapter does not scrape the Immediate
Window or inject output-redirection code.
