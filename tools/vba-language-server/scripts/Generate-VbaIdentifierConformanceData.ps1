[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MappingDirectory,

    [string] $OutputPath,

    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot '..\src\VbaLanguageServer.Syntax\VbaIdentifierConformanceData.g.cs'
}

$mappingSources = [ordered]@{
    874  = '663f43ca662e037c4534cb16298b560f29ce29c27b49b3589601ec3d97dd89fd'
    1250 = 'cef9f171e67b09445bcb3f9ffccdc89418250ff825f1bd2d29a92d2074d7a53b'
    1251 = '59ec85612ff908d9da0e877893c935941e56b13a2882b4fb9c9599be3d1ce4e7'
    1252 = '72ea23c939c5b26fae7aded0207b327e2f3902d7d3c168d7087f5cfc38ee76a9'
    1253 = 'ea80c442aff7f09b36da6335f85f8e527f51c146beeb9825ec00d1b6ca99a99e'
    1254 = '3d02512087634dc493b720992b590277736ffb2d5b0b665d69b6b9727e2c361a'
    1255 = 'fdd4bdda74f6571d89171b0070ac052cd3714c395dc3d1799bcd5e4a4da6f83a'
    1256 = '745c447ada04a838da8bea406c13f446c7453b6371e8c6c7863a632443d56007'
    1257 = 'b8c5d7f3b8c25c3d5625d44dd3d6ee7a06e652ddf77373d050282c1cb7517366'
    1258 = '5d52a9357b7d6b5b5014ed5a51be0ff9809b0c33625793d2a4feaf502e0682f1'
    932  = '2614cfea35c3c86c41d33198793a84ca44edee3cf0ee0013a61a43fba4ece331'
    936  = 'e5070a2d6ad26619f5872ddbe64d3381c11620af5adbb04cda0f0abb1a91fdae'
    949  = '50e13b60ea8fda66a8223ecc85270e0f182303222244e2345d3d57f3e839d20a'
    950  = 'cf8c23389a42a226ea707f7ec32c665556d1fc3364db25bd765ce64d54eaee2a'
}

function Get-ForwardMapping {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $mapping = [System.Collections.Generic.Dictionary[int, int]]::new()
    $mode = ''
    $remaining = 0
    $leadByte = 0

    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line -match '^WCTABLE') {
            break
        }

        if ($line -match '^MBTABLE\s+(\d+)') {
            $mode = 'single'
            $remaining = [int] $Matches[1]
            continue
        }

        if ($line -match '^DBCSRANGE') {
            $mode = ''
            $remaining = 0
            continue
        }

        if ($line -match '^DBCSTABLE\s+(\d+).*LeadByte\s*=\s*0x([0-9A-Fa-f]+)') {
            $mode = 'double'
            $remaining = [int] $Matches[1]
            $leadByte = [Convert]::ToInt32($Matches[2], 16)
            continue
        }

        if ($remaining -le 0 -or $line -notmatch '^\s*0x([0-9A-Fa-f]+)\s+0x([0-9A-Fa-f]+)') {
            continue
        }

        $encoded = [Convert]::ToInt32($Matches[1], 16)
        $unicode = [Convert]::ToInt32($Matches[2], 16)
        $codePoint = if ($mode -eq 'double') {
            ($leadByte -shl 8) -bor $encoded
        }
        else {
            $encoded
        }

        $mapping[$codePoint] = $unicode
        $remaining--
    }

    return ,$mapping
}

function Test-Cp932CodePoint {
    param([int] $CodePoint, [bool] $Initial)

    if ($CodePoint -le 0xff -and (
        ($CodePoint -ge 0x81 -and $CodePoint -le 0x9f) -or
        ($CodePoint -ge 0xe0 -and $CodePoint -le 0xfc))) {
        return $false
    }

    if ($CodePoint -eq 0x8140 -or
        ($CodePoint -ge 0x8143 -and $CodePoint -le 0x8151) -or
        ($CodePoint -ge 0x815e -and $CodePoint -le 0x8197)) {
        return $false
    }

    return -not $Initial -or $CodePoint -lt 0x824f -or $CodePoint -gt 0x8258
}

function Test-Cp936CodePoint {
    param([int] $CodePoint, [bool] $Initial)

    $isInitial =
        ($CodePoint -ge 0xa3c1 -and $CodePoint -le 0xa3da) -or
        ($CodePoint -ge 0xa3e1 -and $CodePoint -le 0xa3fa) -or
        ($CodePoint -ge 0xa1a2 -and $CodePoint -le 0xa1aa) -or
        ($CodePoint -ge 0xa1ac -and $CodePoint -le 0xa1ad) -or
        ($CodePoint -ge 0xa1b2 -and $CodePoint -le 0xa1e6) -or
        ($CodePoint -ge 0xa1e8 -and $CodePoint -le 0xa1ef) -or
        ($CodePoint -ge 0xa2b1 -and $CodePoint -le 0xa2fc) -or
        ($CodePoint -ge 0xa4a1 -and $CodePoint -le 0xfe4f)
    return $isInitial -or (-not $Initial -and (
        $CodePoint -eq 0xa3df -or
        ($CodePoint -ge 0xa3b0 -and $CodePoint -le 0xa3b9)))
}

function Test-Cp949CodePoint {
    param([int] $CodePoint, [bool] $Initial)

    $lead = $CodePoint -shr 8
    $trailing = $CodePoint -band 0xff
    $isInitial = $CodePoint -gt 0xff -and (
        $lead -lt 0xa1 -or
        $lead -gt 0xaf -or
        $trailing -lt 0xa1 -or
        $trailing -gt 0xfe -or
        ($CodePoint -ge 0xa3c1 -and $CodePoint -le 0xa3da) -or
        ($CodePoint -ge 0xa3e1 -and $CodePoint -le 0xa3fa) -or
        ($CodePoint -ge 0xa4a1 -and $CodePoint -le 0xa4fe))
    return $isInitial -or (-not $Initial -and (
        $CodePoint -eq 0xa3df -or
        ($CodePoint -ge 0xa3b0 -and $CodePoint -le 0xa3b9)))
}

function Test-Cp950CodePoint {
    param([int] $CodePoint, [bool] $Initial)

    $isInitial =
        ($CodePoint -ge 0xa2cf -and $CodePoint -le 0xa2fe) -or
        ($CodePoint -ge 0xa340 -and $CodePoint -le 0xf9dd)
    return $isInitial -or (-not $Initial -and (
        $CodePoint -eq 0xa1c5 -or
        ($CodePoint -ge 0xa2af -and $CodePoint -le 0xa2b8)))
}

function Get-IdentifierSet {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.Dictionary[int, int]] $Mapping,

        [Parameter(Mandatory)]
        [scriptblock] $Predicate,

        [Parameter(Mandatory)]
        [bool] $Initial
    )

    $result = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($entry in $Mapping.GetEnumerator()) {
        if ($entry.Key -gt 0x7f -and (& $Predicate $entry.Key $Initial)) {
            [void] $result.Add($entry.Value)
        }
    }

    return ,$result
}

function Get-Ranges {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.HashSet[int]] $Values
    )

    $ordered = @($Values) | Sort-Object
    $ranges = [System.Collections.Generic.List[object]]::new()
    if ($ordered.Count -eq 0) {
        return ,$ranges
    }

    $start = [int] $ordered[0]
    $end = $start
    for ($index = 1; $index -lt $ordered.Count; $index++) {
        $value = [int] $ordered[$index]
        if ($value -eq $end + 1) {
            $end = $value
            continue
        }

        $ranges.Add([pscustomobject]@{ Start = $start; End = $end })
        $start = $value
        $end = $value
    }

    $ranges.Add([pscustomobject]@{ Start = $start; End = $end })
    return ,$ranges
}

function Format-RangeData {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [System.Collections.Generic.HashSet[int]] $Values
    )

    $ranges = Get-Ranges -Values $Values
    $items = foreach ($range in $ranges) {
        '0x{0:x4}, 0x{1:x4}' -f $range.Start, $range.End
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("    internal static ReadOnlySpan<int> $Name =>")
    $lines.Add('    [')
    for ($index = 0; $index -lt $items.Count; $index += 4) {
        $last = [Math]::Min($index + 3, $items.Count - 1)
        $lines.Add('        ' + (($items[$index..$last] -join ', ') + ','))
    }
    $lines.Add('    ];')
    return $lines -join "`n"
}

$resolvedMappingDirectory = [System.IO.Path]::GetFullPath($MappingDirectory)
$mappings = @{}
foreach ($source in $mappingSources.GetEnumerator()) {
    $path = Join-Path $resolvedMappingDirectory "bestfit$($source.Key).txt"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing Microsoft mapping source: $path"
    }

    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $source.Value) {
        throw "Unexpected SHA-256 for bestfit$($source.Key).txt: $actualHash"
    }

    $mappings[$source.Key] = Get-ForwardMapping -Path $path
}

$cp2 = [System.Collections.Generic.HashSet[int]]::new()
foreach ($codePage in @(874, 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258)) {
    foreach ($entry in $mappings[$codePage].GetEnumerator()) {
        if ($entry.Key -ge 0x80 -and $entry.Key -le 0xff) {
            [void] $cp2.Add($entry.Value)
        }
    }
}

$japaneseInitial = Get-IdentifierSet -Mapping $mappings[932] -Predicate ${function:Test-Cp932CodePoint} -Initial $true
$japaneseSubsequent = Get-IdentifierSet -Mapping $mappings[932] -Predicate ${function:Test-Cp932CodePoint} -Initial $false
$simplifiedChineseInitial = Get-IdentifierSet -Mapping $mappings[936] -Predicate ${function:Test-Cp936CodePoint} -Initial $true
$simplifiedChineseSubsequent = Get-IdentifierSet -Mapping $mappings[936] -Predicate ${function:Test-Cp936CodePoint} -Initial $false
$koreanInitial = Get-IdentifierSet -Mapping $mappings[949] -Predicate ${function:Test-Cp949CodePoint} -Initial $true
$koreanSubsequent = Get-IdentifierSet -Mapping $mappings[949] -Predicate ${function:Test-Cp949CodePoint} -Initial $false
$traditionalChineseInitial = Get-IdentifierSet -Mapping $mappings[950] -Predicate ${function:Test-Cp950CodePoint} -Initial $true
$traditionalChineseSubsequent = Get-IdentifierSet -Mapping $mappings[950] -Predicate ${function:Test-Cp950CodePoint} -Initial $false

# These are unique non-ASCII Unicode members. In particular, the CP949 rule
# describes defined 16-bit code points; single-byte mappings are supplied by
# the shared Latin and code-page productions rather than the CP949 table.
$expectedCounts = [ordered]@{
    Cp2 = 633
    JapaneseInitial = 9192
    JapaneseSubsequent = 9202
    SimplifiedChineseInitial = 17220
    SimplifiedChineseSubsequent = 17231
    KoreanInitial = 16394
    KoreanSubsequent = 16405
    TraditionalChineseInitial = 13612
    TraditionalChineseSubsequent = 13623
}
$actualSets = [ordered]@{
    Cp2 = $cp2
    JapaneseInitial = $japaneseInitial
    JapaneseSubsequent = $japaneseSubsequent
    SimplifiedChineseInitial = $simplifiedChineseInitial
    SimplifiedChineseSubsequent = $simplifiedChineseSubsequent
    KoreanInitial = $koreanInitial
    KoreanSubsequent = $koreanSubsequent
    TraditionalChineseInitial = $traditionalChineseInitial
    TraditionalChineseSubsequent = $traditionalChineseSubsequent
}
foreach ($entry in $expectedCounts.GetEnumerator()) {
    $actual = $actualSets[$entry.Key].Count
    if ($actual -ne $entry.Value) {
        throw "Unexpected $($entry.Key) mapping count: expected $($entry.Value), found $actual."
    }
}

$hashEntries = foreach ($source in $mappingSources.GetEnumerator()) {
    "        [$($source.Key)] = `"$($source.Value)`""
}
$rangeBlocks = foreach ($entry in $actualSets.GetEnumerator()) {
    Format-RangeData -Name "$($entry.Key)Ranges" -Values $entry.Value
}

$generated = @"
// <auto-generated />
// MS-VBAL 2.4, published 2025-05-20.
// Unicode membership is generated only from forward MBTABLE/DBCSTABLE mappings in
// https://www.unicode.org/Public/MAPPINGS/VENDORS/MICSFT/WindowsBestFit/ (2006).
// Source SHA-256 values are checked by Generate-VbaIdentifierConformanceData.ps1.

namespace VbaLanguageServer.Syntax;

internal static class VbaIdentifierConformanceData
{
    internal const string SpecificationRevision = "MS-VBAL 2.4 (2025-05-20)";
    internal const string MappingProvenance =
        "Microsoft WindowsBestFit 2006 forward MBTABLE/DBCSTABLE mappings from the Unicode Consortium vendor archive";

    internal static readonly IReadOnlyDictionary<int, string> MappingSha256 =
        new Dictionary<int, string>
        {
$($hashEntries -join ",`n")
        };

$($rangeBlocks -join "`n`n")

    internal static bool Contains(ReadOnlySpan<int> ranges, int value)
    {
        var lower = 0;
        var upper = (ranges.Length / 2) - 1;
        while (lower <= upper)
        {
            var middle = lower + ((upper - lower) / 2);
            var rangeIndex = middle * 2;
            if (value < ranges[rangeIndex])
            {
                upper = middle - 1;
            }
            else if (value > ranges[rangeIndex + 1])
            {
                lower = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
"@

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$generatedText = $generated.Replace("`r`n", "`n").TrimEnd() + "`n"
if ($Check) {
    if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
        throw "Generated output does not exist: $resolvedOutputPath"
    }

    $existingText = [System.IO.File]::ReadAllText($resolvedOutputPath)
    if (-not [string]::Equals($existingText, $generatedText, [System.StringComparison]::Ordinal)) {
        throw "Generated output is stale: $resolvedOutputPath"
    }

    Write-Output "Verified $resolvedOutputPath"
}
else {
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedOutputPath)) | Out-Null
    [System.IO.File]::WriteAllText(
        $resolvedOutputPath,
        $generatedText,
        [System.Text.UTF8Encoding]::new($false))
    Write-Output "Generated $resolvedOutputPath"
}

foreach ($entry in $actualSets.GetEnumerator()) {
    Write-Output "$($entry.Key): $($entry.Value.Count) code points, $((Get-Ranges -Values $entry.Value).Count) ranges"
}
