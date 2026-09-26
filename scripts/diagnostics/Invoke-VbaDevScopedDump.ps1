#requires -Version 7.2
<#
.SYNOPSIS
Runs one exact diagnostic child command under ProcDump, stopping after one dump.
.DESCRIPTION
This is an opt-in replay launcher, not a replacement for the normal vba-dev
invocation. It uses ProcDump -x to launch only the supplied executable; it never
installs a global WER debugger or attaches by process name. The diagnostic root
must already exist on a local fixed drive. Dumps and command output can contain
secrets and must not be uploaded or committed.

The caller must review and accept the ProcDump license interactively before
using this script. This script never passes -accepteula.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string[]] $CommandArguments,
    [string] $ProcDumpPath = $env:VBA_TOOLS_PROCDUMP_PATH,
    [string] $RunRoot = $env:VBA_TOOLS_DIAGNOSTIC_RUN_ROOT,
    [ValidateRange(1, 100)][int] $Count = 1,
    [ValidateRange(10, 3600)][int] $TimeoutSeconds = 300,
    [switch] $FirstChanceAccessViolation,
    [switch] $PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Read-ProcDumpChildEvidence.ps1')

function Assert-OrdinaryLocalPath([string] $Path, [bool] $IsDirectory) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw "Expected a local absolute path: $Path"
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($fullPath))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw "Diagnostic paths must be on a local fixed drive: $fullPath"
    }
    $cursor = if ($IsDirectory) { $fullPath } else { [IO.Path]::GetDirectoryName($fullPath) }
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Diagnostic paths require ordinary directories: $cursor"
        }
        $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    if ($IsDirectory) { return $fullPath }
    $file = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Expected an ordinary file: $fullPath"
    }
    return $fullPath
}

function Write-Receipt([string] $Path, [object] $Value) {
    $bytes = $utf8.GetBytes(($Value | ConvertTo-Json -Depth 20) + "`n")
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $stream.Write($bytes)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    throw 'VBA_TOOLS_DIAGNOSTIC_RUN_ROOT or -RunRoot is required.'
}
$RunRoot = Assert-OrdinaryLocalPath $RunRoot $true
$runId = [IO.Path]::GetFileName($RunRoot.TrimEnd('\', '/'))
$ExecutablePath = Assert-OrdinaryLocalPath $ExecutablePath $false
if ([string]::IsNullOrWhiteSpace($ProcDumpPath)) {
    throw 'VBA_TOOLS_PROCDUMP_PATH or -ProcDumpPath is required.'
}
if (-not $PlanOnly) {
    $ProcDumpPath = Assert-OrdinaryLocalPath $ProcDumpPath $false
    $signature = Get-AuthenticodeSignature -LiteralPath $ProcDumpPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw 'ProcDump must have a valid Microsoft Corporation Authenticode signature.'
    }
    $eulaAccepted = Get-ItemPropertyValue -Path 'HKCU:\Software\Sysinternals\ProcDump' `
        -Name EulaAccepted -ErrorAction SilentlyContinue
    if ($eulaAccepted -ne 1) {
        throw 'Review and accept the ProcDump EULA interactively before running this launcher; it never uses -accepteula.'
    }
}

$executable = Get-Item -LiteralPath $ExecutablePath
$identity = [ordered]@{
    path = $ExecutablePath
    length = $executable.Length
    lastWriteTimeUtc = $executable.LastWriteTimeUtc.ToString('O')
    sha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash
}
$dumpRoot = Join-Path $RunRoot 'source-admission-dumps'
$captureMode = if ($FirstChanceAccessViolation) {
    'first-chance-access-violation'
} else {
    'unhandled-exception'
}
$triggerArguments = if ($FirstChanceAccessViolation) {
    @('-ma', '-e', '1', '-g', '-f', 'C0000005')
} else {
    @('-ma', '-e')
}
$plans = @()
for ($attempt = 1; $attempt -le $Count; $attempt++) {
    $attemptId = 'attempt-{0:d3}' -f $attempt
    $attemptDirectory = Join-Path $dumpRoot ('attempt-{0:d3}' -f $attempt)
    $dumpDirectory = Join-Path $attemptDirectory 'dumps'
    $arguments = @($triggerArguments) + @('-n', '1', '-x', $dumpDirectory, $ExecutablePath) +
        $CommandArguments
    $plan = [ordered]@{
        schemaVersion = '1.0'
        kind = 'scoped-vba-dev-dump-attempt'
        runId = $runId
        attemptId = $attemptId
        attempt = $attempt
        captureMode = $captureMode
        executable = $identity
        commandArguments = $CommandArguments
        procdumpPath = $ProcDumpPath
        procdumpArguments = $arguments
        dumpDirectory = $dumpDirectory
        sourceAdmissionEvidenceRoot = (Join-Path $RunRoot 'source-admission')
        timeoutSeconds = $TimeoutSeconds
        invocationMode = 'ProcDump -x exact child; no process-name or global attach'
    }
    if ($PlanOnly) {
        $plans += $plan
        continue
    }

    New-Item -ItemType Directory -Path $dumpDirectory -ErrorAction Stop | Out-Null
    $plan['startedUtc'] = [DateTime]::UtcNow.ToString('O')
    Write-Receipt (Join-Path $attemptDirectory 'started.json') $plan
    $start = [Diagnostics.ProcessStartInfo]::new($ProcDumpPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($ExecutablePath)
    $start.Environment['VBA_TOOLS_DIAGNOSTIC_RUN_ROOT'] = $RunRoot
    $start.Environment['VBA_TOOLS_DIAGNOSTIC_RUN_ID'] = $runId
    $start.Environment['VBA_TOOLS_DIAGNOSTIC_ATTEMPT_ID'] = $attemptId
    foreach ($argument in $arguments) { [void] $start.ArgumentList.Add($argument) }
    $monitor = [Diagnostics.Process]::new()
    $monitor.StartInfo = $start
    try {
        [void] $monitor.Start()
        $plan['procdumpPid'] = $monitor.Id
        $stdoutPath = Join-Path $attemptDirectory 'stdout.txt'
        $stderrPath = Join-Path $attemptDirectory 'stderr.txt'
        $stdoutFile = [IO.File]::Open($stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $stderrFile = [IO.File]::Open($stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try {
            $stdoutTask = $monitor.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
            $stderrTask = $monitor.StandardError.BaseStream.CopyToAsync($stderrFile)
            if (-not $monitor.WaitForExit($TimeoutSeconds * 1000)) {
                $plan['timedOut'] = $true
                $plan['observedUtc'] = [DateTime]::UtcNow.ToString('O')
                $plan['handling'] = 'No process was killed. Inspect this owned ProcDump PID and its child before another run.'
                Write-Receipt (Join-Path $attemptDirectory 'timeout.json') $plan
                throw "Diagnostic launch timed out; retained ProcDump PID $($monitor.Id)."
            }
            [void] $stdoutTask.GetAwaiter().GetResult()
            [void] $stderrTask.GetAwaiter().GetResult()
        } finally {
            if ($monitor.HasExited) {
                $stdoutFile.Dispose()
                $stderrFile.Dispose()
            } else {
                # Keep output streams and drain tasks alive while the timed-out child remains active.
                $script:retainedDiagnostic = @($monitor, $stdoutFile, $stderrFile, $stdoutTask, $stderrTask)
            }
        }
        $dumps = @(Get-ChildItem -LiteralPath $dumpDirectory -Filter '*.dmp' -File | ForEach-Object {
            [ordered]@{ path = $_.FullName; length = $_.Length }
        })
        $plan['child'] = $null
        $plan['runtimeReceiptPath'] = $null
        $plan['runtimeIdentity'] = $null
        $plan['runtimeCorrelation'] = 'not observed'
        $observationError = $null
        try {
            $child = Read-ProcDumpChildEvidence -OutputPath $stdoutPath -ExecutablePath $ExecutablePath
            $plan['child'] = $child
            $processEvidenceRoot = Join-Path $RunRoot 'source-admission-processes'
            $runtimeReceipts = @()
            if (Test-Path -LiteralPath $processEvidenceRoot -PathType Container) {
                $processEvidenceItem = Get-Item -LiteralPath $processEvidenceRoot -Force
                if (($processEvidenceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'The runtime-receipt directory is not an ordinary directory.'
                }
                $runtimeReceipts = @(Get-ChildItem -LiteralPath $processEvidenceRoot -File `
                    -Filter "process-$($child.childPid)-*.json" | ForEach-Object {
                    $record = Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
                    if ($record.runId -eq $runId -and $record.attemptId -eq $attemptId -and
                        $record.processId -eq $child.childPid) {
                        [ordered]@{ path = $_.FullName; record = $record }
                    }
                })
            }
            if ($runtimeReceipts.Count -gt 1) {
                throw "Multiple runtime receipts match child PID $($child.childPid) and $attemptId."
            }
            if ($runtimeReceipts.Count -eq 1) {
                $runtimeReceipt = $runtimeReceipts[0]
                if (-not [string]::Equals($runtimeReceipt.record.processPath,
                    $ExecutablePath, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Runtime receipt process path does not match child PID $($child.childPid)."
                }
                $plan['runtimeReceiptPath'] = $runtimeReceipt.path
                $plan['runtimeIdentity'] = [ordered]@{
                    runtimeVersion = $runtimeReceipt.record.runtimeVersion
                    frameworkDescription = $runtimeReceipt.record.frameworkDescription
                    runtimeIdentifier = $runtimeReceipt.record.runtimeIdentifier
                    targetFramework = $runtimeReceipt.record.targetFramework
                }
                $plan['runtimeCorrelation'] = 'matched run ID, attempt ID, child PID, and executable path'
            } else {
                $plan['runtimeReceiptPath'] = $null
                $plan['runtimeIdentity'] = $null
                $plan['runtimeCorrelation'] = 'no managed-child startup receipt'
            }
            if ($child.childExitIntegrityError) { throw $child.childExitReason }
        } catch {
            $observationError = $_.Exception.Message
        }
        $plan['finishedUtc'] = [DateTime]::UtcNow.ToString('O')
        $plan['procdumpExitCode'] = $monitor.ExitCode
        $plan['dumpFiles'] = $dumps
        $plan['observationError'] = $observationError
        $plan['stdoutSha256'] = (Get-FileHash -LiteralPath $stdoutPath -Algorithm SHA256).Hash
        $plan['stderrSha256'] = (Get-FileHash -LiteralPath $stderrPath -Algorithm SHA256).Hash
        Write-Receipt (Join-Path $attemptDirectory 'finished.json') $plan
        if ($observationError) { throw "ProcDump child evidence is incomplete: $observationError" }
        if ($dumps.Count -gt 0 -or $monitor.ExitCode -ne 0 -or
            ($child.childExitObserved -and $child.childExitCodeUnsigned -ne 0)) { break }
    } finally { if ($monitor.HasExited) { $monitor.Dispose() } }
}

if ($PlanOnly) {
    $plans | ConvertTo-Json -Depth 20
}
