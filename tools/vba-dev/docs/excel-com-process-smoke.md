# Excel COM process cleanup smoke

Use `tools/vba-dev/scripts/check-excel-process-cleanup.ps1` on Windows to verify that workbook-backed commands do not leave new hidden `EXCEL.EXE` processes.

The smoke script creates a temporary project, adds a minimal `UnitTestMain` module that writes `UNIT_TEST_SHEET`, and checks process IDs after these commands:

- `vba-dev new excel`
- `vba-dev build`
- `vba-dev test --format text`
- `vba-dev publish`
- `vba-dev export`

Example after publishing `vba-dev.exe`:

```powershell
tools\vba-dev\scripts\check-excel-process-cleanup.ps1 -VbaDevExe bin\vba-dev\win-x64\vba-dev.exe
```
