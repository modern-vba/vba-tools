---
status: accepted
---

# Resolve test source locations in VbaDev

`VbaDev test` resolves each reported test identity against the selected
`DocumentSourceSet` with the reusable C# VBA syntax model and emits the
resulting `TestProcedureSourceLocation` on `testFinished`. `VscodeExtension`
only projects that location into the matching procedure node and test message;
project, document, and module nodes remain runnable scopes without a precise
source target. The extension does not parse VBA or query the running language
server for test identity. Resolution matches the case-insensitive workbook
module name to `ModuleIdentity` and the reported procedure name to one parsed
procedure declaration; the executed test identity remains authoritative, so
`VbaDev` does not duplicate the test framework's discovery signature.

Missing or ambiguous source locations are omitted without changing the test
outcome, because navigation metadata must not turn a completed test into a
`TestRunError`. The emitted location belongs to the output-derived
`TestDiscoverySnapshot`; changing the owning document's exported VBA source or
project definition invalidates its module and procedure nodes until another
test run creates a fresh snapshot. Before a Test Explorer run, the extension
saves dirty exported VBA source within the selected scopes; a failed save stops
the command as a `TestRunError`. A no-build run may intentionally execute older
generated workbook code, but its navigation target remains the current saved
exported source. When a location is missing or ambiguous, the procedure node
and test outcome remain available and the extension appends a non-failing
source-location warning to Test Run output without setting a discovery error or
showing a popup. Source decoding follows the existing language-server rules for
UTF-8, BOM-marked UTF-16, and CP932 so emitted UTF-16 coordinates identify the
same text users edit in VS Code.
