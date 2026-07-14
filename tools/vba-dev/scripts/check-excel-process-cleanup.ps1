param(
    [string] $VbaDevExe = (Join-Path $PSScriptRoot '..\..\..\bin\vba-dev\win-x64\vba-dev.exe'),
    [string] $CommonModulesRepo = (Join-Path $PSScriptRoot '..\..\..\..\xls-common-devtools\common_modules_repo'),
    [string] $WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'vba-dev-excel-process-smoke')
)

$ErrorActionPreference = 'Stop'

function Get-ExcelProcessIds {
    @(Get-Process EXCEL -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
}

function Assert-NoNewExcelProcess {
    param(
        [string] $Label,
        [int[]] $Before
    )

    Start-Sleep -Seconds 2
    $after = Get-ExcelProcessIds
    $newProcesses = @($after | Where-Object { $Before -notcontains $_ })
    if ($newProcesses.Count -gt 0) {
        throw "$Label left EXCEL.EXE process(es): $($newProcesses -join ', ')"
    }
}

function Invoke-VbaDevChecked {
    param(
        [string] $Label,
        [string] $WorkingDirectory,
        [string[]] $Arguments
    )

    $before = Get-ExcelProcessIds
    Push-Location $WorkingDirectory
    try {
        & $VbaDevExe @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Label failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    Assert-NoNewExcelProcess -Label $Label -Before $before
}

$vbaDevExePath = [System.IO.Path]::GetFullPath($VbaDevExe)
if (-not (Test-Path -LiteralPath $vbaDevExePath)) {
    throw "vba-dev executable was not found: $vbaDevExePath"
}

$workRootPath = [System.IO.Path]::GetFullPath($WorkRoot)
$tempRootPath = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
if (-not $workRootPath.StartsWith($tempRootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "WorkRoot must be under the system temp directory: $tempRootPath"
}

if (Test-Path -LiteralPath $workRootPath) {
    Remove-Item -LiteralPath $workRootPath -Recurse -Force
}

New-Item -ItemType Directory -Path $workRootPath | Out-Null

$projectRoot = Join-Path $workRootPath 'ProcessSmoke'
Invoke-VbaDevChecked -Label 'new excel' -WorkingDirectory $workRootPath -Arguments @('new', 'excel', '--name', 'ProcessSmoke', '--output', $projectRoot)

$sourceSet = Join-Path $projectRoot 'src\ProcessSmoke'
@'
Attribute VB_Name = "SmokeTestMain"
Option Explicit

Public Sub UnitTestMain()
    Call ExerciseWorkbookHeavyAutomation

    Dim resultSheet As Worksheet

    On Error Resume Next
    Set resultSheet = ThisWorkbook.Worksheets("UNIT_TEST_SHEET")
    On Error GoTo 0

    If resultSheet Is Nothing Then
        Set resultSheet = ThisWorkbook.Worksheets.Add
        resultSheet.Name = "UNIT_TEST_SHEET"
    Else
        resultSheet.Cells.Clear
    End If

    resultSheet.Cells(1, 1).Value = "Module"
    resultSheet.Cells(1, 2).Value = "Test"
    resultSheet.Cells(1, 3).Value = "Result"
    resultSheet.Cells(1, 4).Value = "Message"
    resultSheet.Cells(2, 1).Value = "Smoke"
    resultSheet.Cells(2, 2).Value = "Test_ProcessCleanup"
    resultSheet.Cells(2, 3).Value = "OK"
    resultSheet.Cells(2, 4).Value = ""
End Sub

Private Sub ExerciseWorkbookHeavyAutomation()
    Dim index As Long
    For index = 1 To 20
        Dim targetBook As Workbook
        Set targetBook = Workbooks.Add
        Do While targetBook.Worksheets.Count < 2
            Call targetBook.Worksheets.Add(After:=targetBook.Worksheets(targetBook.Worksheets.Count))
        Loop
        targetBook.Worksheets(1).Name = "target_start"
        targetBook.Worksheets(2).Name = "target_activate"
        Call targetBook.Worksheets("target_start").Activate

        Dim controlBook As Workbook
        Set controlBook = Workbooks.Add
        controlBook.Worksheets(1).Name = "control_active"
        Call controlBook.Worksheets("control_active").Activate

        targetBook.Worksheets("target_activate").Range("C3").Value = index
        Call targetBook.Activate
        Call targetBook.Worksheets("target_activate").Activate
        Call targetBook.Worksheets("target_activate").Range("C3").Activate
        Call controlBook.Activate
        Call controlBook.Worksheets("control_active").Activate

        Call targetBook.Close(SaveChanges:=False)
        Call controlBook.Close(SaveChanges:=False)
    Next index
End Sub
'@ | Set-Content -LiteralPath (Join-Path $sourceSet 'SmokeTestMain.bas') -Encoding ASCII

Invoke-VbaDevChecked -Label 'build' -WorkingDirectory $projectRoot -Arguments @('build')
Invoke-VbaDevChecked -Label 'test' -WorkingDirectory $projectRoot -Arguments @('test', '--format', 'text')
Invoke-VbaDevChecked -Label 'publish' -WorkingDirectory $projectRoot -Arguments @('publish')
Invoke-VbaDevChecked -Label 'export' -WorkingDirectory $projectRoot -Arguments @('export')

Write-Output 'Excel COM process cleanup smoke passed.'
