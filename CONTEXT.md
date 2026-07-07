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

**CommonModulesRepository**:
A directory named `common_modules_repo` that provides shared VBA source files for a **WorkbookBackedProject**.
_Avoid_: common-modules-repo, common modules folder, package cache

**CommonModulesRuntimeBaseline**:
The shared VBA source files required for ordinary runtime use of CommonModules inside a **DocumentSourceSet**.
_Avoid_: all common modules, test modules

**CommonModulesTestFoundation**:
The shared VBA source files required to author and run VBA unit tests inside a **DocumentSourceSet**.
_Avoid_: runtime baseline, project-specific tests

**CommonModuleDependency**:
A shared VBA source file that must accompany another CommonModules file for that file to work inside a **DocumentSourceSet**.
_Avoid_: optional module, copied file list

**WorkbookBackedProject**:
A macro project whose workflow may involve both exported text modules and a primary Office macro document. The initial supported document kind is an Excel `.xlsm` workbook.
_Avoid_: source-only project, package

**PrimaryOfficeDocument**:
The single Office macro document that a **WorkbookBackedProject** treats as the subject of project lifecycle commands.
_Avoid_: arbitrary workbook, generated output, secondary document

**DocumentSourceSet**:
The exported VBA source files and source template document that belong to one Office macro document inside a **WorkbookBackedProject**.
_Avoid_: project source root, loose modules folder

**PublishableVbaSource**:
An exported VBA source file from a **DocumentSourceSet** that should be imported into the distributed Office macro document.
_Avoid_: test-only source, build-only source

**TestOnlyVbaSource**:
An exported VBA source file used for authoring or running VBA unit tests and excluded from published Office macro documents by default.
_Avoid_: runtime source, publishable source

**PublishExclusionMarker**:
The `'#ExcludePublish` source comment marker that declares a project-local exported VBA source file as **TestOnlyVbaSource** or otherwise not publishable.
_Avoid_: filename-only test detection, implicit publish exclusion

**TestResultOutput**:
The command-line report emitted by a **ToolingCommand** after running VBA unit tests for a **PrimaryOfficeDocument**.
_Avoid_: worksheet result sheet, internal test state

**EnvironmentDiagnostic**:
A read/check-oriented **ToolingCommand** that reports whether the local Windows, Excel, COM, VBIDE, and project prerequisites can support workbook-backed automation.
_Avoid_: build, test run, repair command

**ProjectManifest**:
A project-local manifest that identifies a **WorkbookBackedProject** and carries default values for project-scoped **ToolingCommand** execution.
_Avoid_: config-only file, metadata-only file

**ModernVbaWorkspace**:
The local multi-repository workspace that may contain `vba-devtools`, `VBA-LanguageServer`, `DoxyVB6`, and Excel macro repositories for integration work.
_Avoid_: monorepo, single repo
