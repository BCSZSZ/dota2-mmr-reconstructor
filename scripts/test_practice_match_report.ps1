param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$fixture = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot "tests\fixtures\synthetic-practice-collection.json")
)
$executable = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot "collector\bin\$Configuration\net8.0-windows\Dota2MmrReconstructor.exe")
)
$testRoot = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "dota2-practice-report-test-$([guid]::NewGuid().ToString('N'))"
)
$outputDirectory = Join-Path $testRoot "report"
$beforeHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
& $executable `
    --practice-report-existing $fixture `
    --output-dir $outputDirectory `
    --account-id 136619313
if ($LASTEXITCODE -ne 0) {
    throw "Practice report generation exited with code $LASTEXITCODE."
}

$workbookPath = Join-Path $outputDirectory "practice-match-report.xlsx"
if (-not (Test-Path -LiteralPath $workbookPath)) {
    throw "Practice report workbook was not generated."
}

$assemblyDirectory = Split-Path -Parent $executable
$closedXmlDependencies = @(
    "System.IO.Packaging.dll",
    "DocumentFormat.OpenXml.Framework.dll",
    "DocumentFormat.OpenXml.dll",
    "ExcelNumberFormat.dll",
    "RBush.dll",
    "SixLabors.Fonts.dll",
    "ClosedXML.Parser.dll",
    "ClosedXML.dll"
)
foreach ($dependency in $closedXmlDependencies) {
    $dependencyPath = Join-Path $assemblyDirectory $dependency
    if (Test-Path -LiteralPath $dependencyPath) {
        [System.Reflection.Assembly]::LoadFrom($dependencyPath) | Out-Null
    }
}

$collectorAssembly = [System.Reflection.Assembly]::LoadFrom(
    (Join-Path $assemblyDirectory "Dota2MmrReconstructor.dll")
)
$openDotaType = $collectorAssembly.GetType(
    "Dota2MmrCollector.PracticeOpenDotaClient",
    $true
)
$parseFlags = [System.Reflection.BindingFlags]"Static,NonPublic,Public"
$parseMethod = $openDotaType.GetMethod("ParseMatch", $parseFlags)
$sampleDocument = [System.Text.Json.JsonDocument]::Parse(
    '{"match_id":999,"start_time":1767621600,"lobby_type":1,"radiant_win":true,' +
    '"players":[{"account_id":42,"personaname":"A","player_slot":0,"hero_id":1},' +
    '{"account_id":43,"personaname":"B","player_slot":128,"hero_id":2}]}'
)
try {
    $parseArguments = [object[]]::new(1)
    $parseArguments[0] = $sampleDocument.RootElement
    $parsedMatch = $parseMethod.Invoke($null, $parseArguments)
    if ($parsedMatch.MatchId -ne 999 -or
        $parsedMatch.WinnerTeam -ne 2 -or
        $parsedMatch.Players.Count -ne 2 -or
        $parsedMatch.Players[0].Team -ne 2 -or
        $parsedMatch.Players[1].Team -ne 3) {
        throw "OpenDota match normalization is incorrect."
    }
}
finally {
    $sampleDocument.Dispose()
}

$workbook = [ClosedXML.Excel.XLWorkbook]::new($workbookPath)
try {
    $sheetNames = @($workbook.Worksheets | ForEach-Object Name)
    if ($sheetNames.Count -ne 2 -or
        $sheetNames[0] -ne "玩家统计" -or
        $sheetNames[1] -ne "组队胜率") {
        throw "Practice workbook does not contain the expected two worksheets in order."
    }

    $playerSheet = $workbook.Worksheet("玩家统计")
    if ($playerSheet.SheetView.SplitRow -ne 6) {
        throw "Player worksheet did not freeze the six heading rows."
    }
    $playerTable = $playerSheet.Table("PracticePlayers")
    if (-not $playerTable.ShowAutoFilter) {
        throw "Player table has no AutoFilter."
    }
    $aliceRow = $null
    $userRow = $null
    $playerFirstRow = $playerTable.DataRange.RangeAddress.FirstAddress.RowNumber
    $playerLastRow = $playerTable.DataRange.RangeAddress.LastAddress.RowNumber
    foreach ($rowNumber in $playerFirstRow..$playerLastRow) {
        $row = $playerSheet.Row($rowNumber)
        if ($row.Cell(1).GetString() -eq "111") {
            $aliceRow = $row
        }
        if ($row.Cell(1).GetString() -eq "136619313") {
            $userRow = $row
        }
    }
    if ($null -eq $aliceRow -or
        $aliceRow.Cell(3).GetDouble() -ne 2 -or
        $aliceRow.Cell(4).GetDouble() -ne 1 -or
        $aliceRow.Cell(5).GetDouble() -ne 3) {
        throw "Alice's player summary is incorrect."
    }
    if ($null -eq $userRow -or
        $userRow.Cell(3).GetDouble() -ne 2 -or
        $userRow.Cell(4).GetDouble() -ne 0 -or
        $userRow.Cell(5).GetDouble() -ne 2) {
        throw "Authenticated player's summary is incorrect."
    }
    if ($playerTable.DataRange.RowCount() -ne 9) {
        throw "Expected nine non-bot player identities, including one per-match anonymous identity."
    }

    $pairSheet = $workbook.Worksheet("组队胜率")
    if ($pairSheet.SheetView.SplitRow -ne 6 -or
        $pairSheet.SheetView.SplitColumn -ne 1) {
        throw "Pair matrix did not freeze its header row and player-name column."
    }

    $playerCount = $playerTable.DataRange.RowCount()
    $winRateHeaderRow = 6
    function Find-MatrixPosition([string]$accountId) {
        $column = $null
        for ($columnNumber = 2; $columnNumber -le ($playerCount + 1); $columnNumber++) {
            $parts = $pairSheet.Cell($winRateHeaderRow, $columnNumber).GetString() -split "`n"
            if ($parts[-1] -eq $accountId) {
                $column = $columnNumber
                break
            }
        }

        $row = $null
        for ($rowNumber = ($winRateHeaderRow + 1);
            $rowNumber -le ($winRateHeaderRow + $playerCount);
            $rowNumber++) {
            $parts = $pairSheet.Cell($rowNumber, 1).GetString() -split "`n"
            if ($parts[-1] -eq $accountId) {
                $row = $rowNumber
                break
            }
        }

        if ($null -eq $column -or $null -eq $row) {
            throw "Account $accountId was not found in the teammate matrix."
        }
        return [pscustomobject]@{ Row = $row; Column = $column }
    }

    $userPosition = Find-MatrixPosition "136619313"
    $alicePosition = Find-MatrixPosition "111"
    $bobPosition = Find-MatrixPosition "222"
    $carolPosition = Find-MatrixPosition "333"

    $userAlicePair = $pairSheet.Cell(
        $userPosition.Row,
        $alicePosition.Column
    ).GetString()
    $aliceUserPair = $pairSheet.Cell(
        $alicePosition.Row,
        $userPosition.Column
    ).GetString()
    if ($userAlicePair -ne "2/100.0%" -or $aliceUserPair -ne "2/100.0%") {
        throw "Authenticated player + Alice pair summary is incorrect or the matrix is not symmetric."
    }
    if ($pairSheet.Cell($userPosition.Row, $userPosition.Column).GetString() -ne "—") {
        throw "Pair matrix diagonal must identify the same player with an em dash."
    }

    $userBobPair = $pairSheet.Cell(
        $userPosition.Row,
        $bobPosition.Column
    ).GetString()
    if ($userBobPair -ne "1/100.0%") {
        throw "A one-match teammate combination was omitted from the pair matrix."
    }
    $aliceCarolPair = $pairSheet.Cell(
        $alicePosition.Row,
        $carolPosition.Column
    ).GetString()
    if ($aliceCarolPair -ne "1/0.0%") {
        throw "A zero-percent teammate combination was rendered as missing or non-zero."
    }

    $obsoleteCountHeading = @(
        $pairSheet.CellsUsed() |
            Where-Object { $_.GetString() -eq "共同场数矩阵" }
    )
    if ($obsoleteCountHeading.Count -ne 0) {
        throw "The obsolete separate shared-match-count matrix was still generated."
    }
}
finally {
    $workbook.Dispose()
}

$spreadsheetDocument = [DocumentFormat.OpenXml.Packaging.SpreadsheetDocument]::Open(
    $workbookPath,
    $false
)
try {
    $validator = [DocumentFormat.OpenXml.Validation.OpenXmlValidator]::new()
    $validationErrors = @($validator.Validate($spreadsheetDocument))
    if ($validationErrors.Count -ne 0) {
        throw "Practice workbook failed Open XML validation: $($validationErrors[0].Description)"
    }
}
finally {
    $spreadsheetDocument.Dispose()
}

$afterHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
if ($beforeHash -ne $afterHash) {
    throw "Synthetic practice collection fixture was modified."
}

Write-Output "PASS: practice report contains correct player statistics and combined teammate matrix values."
Write-Output "Test workbook: $workbookPath"
