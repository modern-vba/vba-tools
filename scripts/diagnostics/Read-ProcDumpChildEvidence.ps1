#requires -Version 7.2

function Read-ProcDumpUtf16Lines([byte[]] $Bytes, [string] $Marker) {
    $markerBytes = [Text.Encoding]::Unicode.GetBytes($Marker)
    for ($offset = 0; $offset -le $Bytes.Length - $markerBytes.Length; $offset++) {
        $found = $true
        for ($index = 0; $index -lt $markerBytes.Length; $index++) {
            if ($Bytes[$offset + $index] -ne $markerBytes[$index]) {
                $found = $false
                break
            }
        }
        if (-not $found) { continue }

        # A child's UTF-8 output can occur between ProcDump's UTF-16 lines and
        # change alignment. Find each exact UTF-16 marker in raw bytes instead
        # of decoding the entire mixed stream from one offset.
        $end = $offset + $markerBytes.Length
        while ($end + 1 -lt $Bytes.Length) {
            if ($Bytes[$end] -eq 10 -and $Bytes[$end + 1] -eq 0) { break }
            if ($Bytes[$end] -eq 13 -and $Bytes[$end + 1] -eq 0 -and
                $end + 3 -lt $Bytes.Length -and $Bytes[$end + 2] -eq 10 -and
                $Bytes[$end + 3] -eq 0) { break }
            $end += 2
        }
        if ($end + 1 -ge $Bytes.Length) { continue }
        $line = [Text.UnicodeEncoding]::new($false, $false, $true).GetString(
            $Bytes, $offset, $end - $offset)
        Write-Output $line
        $offset = $end + 1
    }
}

function Read-ProcDumpChildEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $OutputPath,
        [Parameter(Mandatory)][string] $ExecutablePath
    )

    $bytes = [IO.File]::ReadAllBytes($OutputPath)
    $processLines = @(Read-ProcDumpUtf16Lines $bytes 'Process:')
    if ($processLines.Count -ne 1) {
        throw "Expected one ProcDump child identity, found $($processLines.Count)."
    }
    $process = [regex]::Match($processLines[0],
        '^Process:\s*(?<image>[^\r\n]+?)\s+\((?<pid>\d+)\)$')
    if (-not $process.Success) { throw 'ProcDump child identity line is malformed.' }
    $imageName = $process.Groups['image'].Value.Trim()
    $expectedName = [IO.Path]::GetFileName($ExecutablePath)
    if (-not [string]::Equals($imageName, $expectedName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ProcDump child image '$imageName' does not match '$expectedName'."
    }
    $childPid = [int]::Parse($process.Groups['pid'].Value, [Globalization.CultureInfo]::InvariantCulture)

    $exitLines = @(Read-ProcDumpUtf16Lines $bytes 'Process Exit:')
    $exitCodeHex = $null
    $exitCodeUnsigned = $null
    $exitReason = if ($exitLines.Count -eq 0) { 'No complete Process Exit line in ProcDump output.' } else { $null }
    $exitIntegrityError = $false
    if ($exitLines.Count -gt 1) {
        $exitReason = "Multiple ProcDump child exit lines: $($exitLines.Count)."
        $exitIntegrityError = $true
    }
    if ($exitLines.Count -eq 1) {
        $exit = [regex]::Match($exitLines[0],
            '^Process Exit:\s*PID (?<pid>\d+),\s*Exit Code 0x(?<code>[0-9a-fA-F]{8})$')
        if (-not $exit.Success) {
            $exitReason = 'ProcDump child exit line is malformed.'
            $exitIntegrityError = $true
        } else {
            $exitPid = [int]::Parse($exit.Groups['pid'].Value, [Globalization.CultureInfo]::InvariantCulture)
            if ($exitPid -ne $childPid) {
                $exitReason = "ProcDump child exit PID $exitPid does not match launched PID $childPid."
                $exitIntegrityError = $true
            } else {
                $exitCodeHex = '0x' + $exit.Groups['code'].Value.ToUpperInvariant()
                $exitCodeUnsigned = [Convert]::ToUInt32($exit.Groups['code'].Value, 16)
            }
        }
    }

    [ordered]@{
        childPid = $childPid
        childPidSource = 'ProcDump Process line'
        childImageName = $imageName
        childExitObserved = $null -ne $exitCodeHex
        childExitCodeHex = $exitCodeHex
        childExitCodeUnsigned = $exitCodeUnsigned
        childExitCodeSource = if ($null -ne $exitCodeHex) { 'ProcDump Process Exit line' } else { $null }
        childExitReason = $exitReason
        childExitIntegrityError = $exitIntegrityError
    }
}
