#requires -Version 7.2
<#
.SYNOPSIS
Measures ordinary workbook Builds against an already prepared, isolated project.
.DESCRIPTION
Prepare a complete copy of the project and any project-relative CommonModules
package below this repository's ignored .local/performance directory. Preserve
the manifest and every source/template byte. Reuse that exact project path for
baseline and candidate. This script neither copies nor deletes project files.

Each invocation performs one excluded warm-up followed by at least three measured
Builds. It starts Excel indirectly through vba-dev and therefore requires the
normal explicit approval for workbook operations. No process is ever killed.
Run variants serially, with other Excel/build activity stopped. Logs, source
inventories and process observations remain private in .local/performance.

Supply settled reference-catalog files/directories through CatalogPath when they
are available. Otherwise the report explicitly records that external catalogs
were not fingerprinted. Timings are wall-clock ordinary Build timings, not
semantic-phase or profiler measurements. The output workbook is reused between
trials; its existence alone is not proof that a failed Build produced it.
.EXAMPLE
pwsh -File scripts/measureWorkbookBuildPerformance.ps1 -ExecutablePath .local/performance/baseline/vba-dev.exe -ProjectPath .local/performance/inputs/ces/Project -DocumentName Book -EvidenceDirectory .local/performance/evidence/ces -Variant baseline
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $ProjectPath,
    [Parameter(Mandatory)][string] $DocumentName,
    [Parameter(Mandatory)][string] $EvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string] $Variant,
    [ValidateRange(3, 100)][int] $Repetitions = 3,
    [string[]] $CatalogPath = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullMeasurementPath([string] $Path, [string] $BasePath) {
    return [IO.Path]::GetFullPath($Path, $BasePath)
}

function Test-MeasurementDescendant([string] $Path, [string] $Root) {
    return $Path.StartsWith($Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Assert-PrivateMeasurementPath([string] $Path) {
    if (-not (Test-MeasurementDescendant $Path $script:privateRoot)) {
        throw "Measurement path must remain below the ignored private root '$script:privateRoot': $Path"
    }
    # Reject junctions/symlinks, including those on an ancestor that could redirect writes.
    $cursor = $Path
    while ($cursor -and (Test-MeasurementDescendant $cursor $script:repositoryRoot)) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not accepted for isolated measurement paths: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Get-FileIdentity([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer) { throw "Expected a file: $Path" }
    return [ordered]@{
        path = $item.FullName
        length = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}

function Get-TreeIdentity([string[]] $Paths) {
    $files = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($path in $Paths) {
        $item = Get-Item -LiteralPath $path -Force
        $entries = if ($item.PSIsContainer) {
            @(Get-ChildItem -LiteralPath $path -Recurse -Force)
        } else { @($item) }
        foreach ($entry in $entries) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Cannot fingerprint a tree with reparse points: $($entry.FullName)"
            }
            if (-not $entry.PSIsContainer) { $files[$entry.FullName] = Get-FileIdentity $entry.FullName }
        }
    }
    $records = @($files.Values)
    $canonical = ($records | ForEach-Object {
        "$(($_.path | ConvertTo-Json -Compress))`t$($_.length)`t$($_.sha256)"
    }) -join "`n"
    $digest = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))
    return [ordered]@{
        # Absolute paths deliberately participate: both variants must use identical corpus paths.
        sha256 = [Convert]::ToHexString($digest)
        fileCount = $records.Count
        totalBytes = ($records | ForEach-Object { [long] $_.length } | Measure-Object -Sum).Sum
        files = $records
    }
}

function Get-ExcelSnapshot {
    $records = foreach ($process in @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue)) {
        try {
            [ordered]@{ pid = $process.Id; startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O'); error = $null }
        } catch {
            [ordered]@{ pid = $process.Id; startTimeUtc = $null; error = $_.Exception.Message }
        } finally { $process.Dispose() }
    }
    return @($records | Sort-Object -Property { $_.pid })
}

function Get-ExcelSnapshotKey([object[]] $Snapshot) {
    return (@($Snapshot | ForEach-Object { "$($_.pid)/$($_.startTimeUtc)/$($_.error)" }) -join "`n")
}

function Write-MeasurementJson([string] $Path, [object] $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Get-Median([double[]] $Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$privateRoot = Join-Path $repositoryRoot '.local/performance'
$invocationDirectory = (Get-Location).ProviderPath
$executable = Get-FullMeasurementPath $ExecutablePath $invocationDirectory
$project = Get-FullMeasurementPath $ProjectPath $invocationDirectory
$evidence = Get-FullMeasurementPath $EvidenceDirectory $invocationDirectory
Assert-PrivateMeasurementPath $project
Assert-PrivateMeasurementPath $evidence
if ((Test-MeasurementDescendant $evidence $project) -or $evidence -eq $project) {
    throw 'Evidence must be outside the prepared project so that logs cannot change measured inputs.'
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Executable not found: $executable" }
$executableDirectory = [IO.Path]::GetDirectoryName($executable)
if ((Test-MeasurementDescendant $evidence $executableDirectory) -or
    (Test-MeasurementDescendant $project $executableDirectory)) {
    throw 'The frozen executable directory must be separate from the project and evidence trees.'
}
$manifestPath = Join-Path $project 'vba-project.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $manifest.documents.Contains($DocumentName)) { throw "Manifest document not found: $DocumentName" }
$inputPaths = [Collections.Generic.List[string]]::new()
$inputPaths.Add($manifestPath)
foreach ($document in $manifest.documents.Values) {
    foreach ($property in @('sourcePath', 'templatePath')) {
        if ([string]::IsNullOrWhiteSpace($document[$property])) { throw "Manifest is missing $property." }
        $inputPath = Get-FullMeasurementPath $document[$property] $project
        Assert-PrivateMeasurementPath $inputPath
        if (-not (Test-Path -LiteralPath $inputPath)) { throw "Prepared input is missing: $inputPath" }
        $inputPaths.Add($inputPath)
    }
    foreach ($property in @('binPath', 'publishPath')) {
        if ($document.Contains($property) -and -not [string]::IsNullOrWhiteSpace($document[$property])) {
            Assert-PrivateMeasurementPath (Get-FullMeasurementPath $document[$property] $project)
        }
    }
}
if ($manifest.Contains('commonModulesRepository') -and $manifest.commonModulesRepository) {
    $commonModules = Get-FullMeasurementPath $manifest.commonModulesRepository $project
    Assert-PrivateMeasurementPath $commonModules
    if (-not (Test-Path -LiteralPath $commonModules -PathType Container)) {
        throw "Prepared CommonModules package is missing: $commonModules"
    }
    $inputPaths.Add($commonModules)
}
$selectedDocument = $manifest.documents[$DocumentName]
if (-not $selectedDocument.Contains('binPath') -or [string]::IsNullOrWhiteSpace($selectedDocument.binPath)) {
    throw 'The selected document must declare its ordinary Build binPath.'
}
$outputPath = Get-FullMeasurementPath $selectedDocument.binPath $project
Assert-PrivateMeasurementPath $outputPath
foreach ($inputPath in $inputPaths) {
    if ($outputPath -eq $inputPath -or (Test-MeasurementDescendant $outputPath $inputPath)) {
        throw "Build output overlaps a preserved input: $outputPath"
    }
    if ($evidence -eq $inputPath -or (Test-MeasurementDescendant $evidence $inputPath)) {
        throw "Evidence overlaps a preserved input: $evidence"
    }
}
$catalogs = @($CatalogPath | ForEach-Object { Get-FullMeasurementPath $_ $invocationDirectory })
$runDirectory = Join-Path $evidence $Variant
Assert-PrivateMeasurementPath $runDirectory
if (Test-Path -LiteralPath $runDirectory) { throw "Evidence variant already exists; use a new name: $runDirectory" }
$initialInputs = Get-TreeIdentity $inputPaths.ToArray()
$initialExecutable = Get-TreeIdentity @($executableDirectory)
$initialCatalogs = if ($catalogs.Count -gt 0) { Get-TreeIdentity $catalogs } else { $null }
$initialExcel = @(Get-ExcelSnapshot)
if (@($initialExcel | Where-Object { $_.error }).Count -gt 0) { throw 'Cannot identify every existing Excel process; no Build was started.' }
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
Write-MeasurementJson (Join-Path $runDirectory 'inputs-before.json') $initialInputs
Write-MeasurementJson (Join-Path $runDirectory 'executable-before.json') $initialExecutable
Write-MeasurementJson (Join-Path $runDirectory 'catalogs-before.json') $initialCatalogs
$report = [ordered]@{
    schemaVersion = 1; variant = $Variant; startedUtc = [DateTime]::UtcNow.ToString('O')
    projectPath = $project; documentName = $DocumentName; outputPath = $outputPath
    executable = Get-FileIdentity $executable
    executableFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    executableTreeSha256 = $initialExecutable.sha256; inputTreeSha256 = $initialInputs.sha256
    catalogTreeSha256 = if ($initialCatalogs) { $initialCatalogs.sha256 } else { $null }
    externalCatalogsFingerprinted = $catalogs.Count -gt 0
    environment = [ordered]@{
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        logicalProcessorCount = [Environment]::ProcessorCount
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        measurementHostRuntime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        targetRuntimeIdentity = 'See all frozen executable/runtime files and SHA-256 values in executable-before.json.'
    }
    excludedWarmups = 1; requestedMeasuredTrials = $Repetitions
    timingMethod = 'Stopwatch from Process.Start through process exit; ordinary Build; no profiler; hashing and Excel observation excluded.'
    trials = [Collections.Generic.List[object]]::new(); completed = $false; failure = $null
    allMeasuredSecondsMedian = $null; successfulMeasuredSecondsMedian = $null
}
$reportPath = Join-Path $runDirectory 'report.json'
Write-MeasurementJson $reportPath $report
try {
    for ($trialIndex = 0; $trialIndex -le $Repetitions; $trialIndex++) {
        $name = if ($trialIndex -eq 0) { 'warmup' } else { 'trial-{0:D2}' -f $trialIndex }
        $inputsBefore = Get-TreeIdentity $inputPaths.ToArray()
        $executableBefore = Get-TreeIdentity @($executableDirectory)
        if ($inputsBefore.sha256 -ne $initialInputs.sha256 -or $executableBefore.sha256 -ne $initialExecutable.sha256) {
            throw 'Inputs or frozen executable files changed before a trial; measurement stopped.'
        }
        $catalogsBefore = if ($catalogs.Count -gt 0) { Get-TreeIdentity $catalogs } else { $null }
        if ($catalogsBefore -and $catalogsBefore.sha256 -ne $initialCatalogs.sha256) {
            throw 'Settled catalog inputs changed before a trial; measurement stopped.'
        }
        $excelBefore = @(Get-ExcelSnapshot)
        if ((Get-ExcelSnapshotKey $excelBefore) -ne (Get-ExcelSnapshotKey $initialExcel)) {
            throw 'Excel process inventory changed between trials; measurement stopped without killing a process.'
        }
        $outputBefore = if (Test-Path -LiteralPath $outputPath -PathType Leaf) { Get-FileIdentity $outputPath } else { $null }
        $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
        $startInfo.WorkingDirectory = $project
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('build', '--project', $project, '--document', $DocumentName)) {
            $startInfo.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $startedUtc = [DateTime]::UtcNow.ToString('O')
        $processId = $null
        $exitCode = $null
        $stdout = ''
        $stderr = ''
        $launchError = $null
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try {
            if (-not $process.Start()) { throw 'The Build process did not start.' }
            $processId = $process.Id
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $nextProgressSeconds = 30
            while (-not $process.WaitForExit(1000)) {
                if ($clock.Elapsed.TotalSeconds -ge $nextProgressSeconds) {
                    Write-Host "$Variant/${name}: Build PID $($process.Id) is still running ($([int]$clock.Elapsed.TotalSeconds)s)."
                    $nextProgressSeconds += 30
                }
            }
            $clock.Stop()
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            $exitCode = $process.ExitCode
        } catch {
            $launchError = $_.Exception.Message
        } finally {
            $clock.Stop()
            $process.Dispose()
        }
        $stdout | Set-Content -LiteralPath (Join-Path $runDirectory "$name.stdout.txt") -Encoding utf8NoBOM
        $stderr | Set-Content -LiteralPath (Join-Path $runDirectory "$name.stderr.txt") -Encoding utf8NoBOM
        $excelAfter = @(Get-ExcelSnapshot)
        $inputsAfter = Get-TreeIdentity $inputPaths.ToArray()
        $executableAfter = Get-TreeIdentity @($executableDirectory)
        $catalogsAfter = if ($catalogs.Count -gt 0) { Get-TreeIdentity $catalogs } else { $null }
        $outputAfter = if (Test-Path -LiteralPath $outputPath -PathType Leaf) { Get-FileIdentity $outputPath } else { $null }
        $inputStable = $inputsAfter.sha256 -eq $initialInputs.sha256
        $executableStable = $executableAfter.sha256 -eq $initialExecutable.sha256
        $catalogsStable = -not $catalogsAfter -or $catalogsAfter.sha256 -eq $initialCatalogs.sha256
        $excelStable = (Get-ExcelSnapshotKey $excelBefore) -eq (Get-ExcelSnapshotKey $excelAfter)
        $trial = [ordered]@{
            name = $name; excludedWarmup = $trialIndex -eq 0; seconds = $clock.Elapsed.TotalSeconds
            startedUtc = $startedUtc; processId = $processId; launchOrCaptureError = $launchError
            exitCode = $exitCode; succeeded = $exitCode -eq 0 -and $null -ne $outputAfter
            inputUnchanged = $inputStable; executableUnchanged = $executableStable
            suppliedCatalogsUnchanged = $catalogsStable; excelProcessInventoryUnchanged = $excelStable
            excelBefore = $excelBefore; excelAfter = $excelAfter
            outputBefore = $outputBefore; outputAfter = $outputAfter
            inputTreeSha256After = $inputsAfter.sha256; executableTreeSha256After = $executableAfter.sha256
            stdoutPath = "$name.stdout.txt"; stderrPath = "$name.stderr.txt"
        }
        $report.trials.Add($trial)
        Write-MeasurementJson (Join-Path $runDirectory "$name.json") $trial
        Write-MeasurementJson $reportPath $report
        Write-Host "$Variant/$name completed: exit=$exitCode seconds=$($clock.Elapsed.TotalSeconds) outputExists=$($null -ne $outputAfter) ExcelUnchanged=$excelStable"
        if ($launchError) { throw "Build launch/output capture failed: $launchError" }
        if (-not ($inputStable -and $executableStable -and $catalogsStable -and $excelStable)) {
            throw 'Input/executable/catalog identity or Excel process inventory changed; inspect the trial evidence. No process was killed.'
        }
    }
    $report.completed = $true
} catch {
    $report.failure = $_.Exception.Message
    throw
} finally {
    $measured = @($report.trials | Where-Object { -not $_.excludedWarmup })
    $report.allMeasuredSecondsMedian = Get-Median @($measured | ForEach-Object { $_.seconds })
    $report.successfulMeasuredSecondsMedian = Get-Median @($measured | Where-Object { $_.succeeded } | ForEach-Object { $_.seconds })
    $report['finishedUtc'] = [DateTime]::UtcNow.ToString('O')
    Write-MeasurementJson $reportPath $report
}
Write-Host "Private evidence: $reportPath"
if (@($report.trials | Where-Object { -not $_.succeeded }).Count -gt 0) {
    throw 'At least one Build failed; all attempted outcomes are recorded and must not be reported as a successful performance comparison.'
}
