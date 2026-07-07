# VBA Developer Tools

vba-devtools is the working repository for C# / .NET Core console tooling that supports modern VBA source workflows. This glossary is intentionally small because the repository is newly initialized; expand it when concrete commands, libraries, or workflows are defined.

## Tooling

**VbaDevTools**:
The repository-level product area for tools that help developers work with exported VBA source files, workbook-backed macro projects, and related automation.
_Avoid_: VBA-LanguageServer, DoxyVB6, CommonModules

**ToolingCommand**:
A user-facing or automation-facing command in vba-devtools. It should have explicit inputs, outputs, side effects, and verification behavior.
_Avoid_: script, helper, task

**ConsoleEntryPoint**:
The C# entry point that parses command-line arguments, invokes a `ToolingCommand`, and returns a meaningful process exit code.
_Avoid_: UI, macro, language-server endpoint

**DotNetProject**:
A .NET project that builds the console tooling, tests, or shared implementation code in this repository.
_Avoid_: workbook project, npm package

**ExportedVbaSource**:
A `.bas`, `.cls`, or `.frm` text file exported from a VBA project and edited or analyzed outside the VBE.
_Avoid_: workbook, code blob

**WorkbookBackedProject**:
An Excel macro project whose workflow may involve both exported text modules and a `.xlsm` workbook.
_Avoid_: source-only project, package

**ModernVbaWorkspace**:
The local multi-repository workspace that may contain `vba-devtools`, `VBA-LanguageServer`, `DoxyVB6`, and Excel macro repositories for integration work.
_Avoid_: monorepo, single repo
