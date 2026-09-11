param(
    [string]$Configuration = "Release",
    [string]$ExecutablePath
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $projectRoot "collector\bin\$Configuration\net8.0-windows\Dota2MmrReconstructor.exe"
}
$fixture = Join-Path $projectRoot "tests\fixtures\synthetic-gc-collection.json"
$fixtureHash = (Get-FileHash -LiteralPath $fixture).Hash
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dota2-locked-workbook-$([guid]::NewGuid().ToString('N'))"
$outputDirectory = Join-Path $testRoot "mmr-reconstruction"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$lockedPath = Join-Path $outputDirectory "hero-mmr-contribution.xlsx"
[System.IO.File]::WriteAllText($lockedPath, "Existing workbook must remain untouched.")
$lockedHash = (Get-FileHash -LiteralPath $lockedPath).Hash
$lock = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
try {
    $messages = (& $ExecutablePath --reconstruct-existing $fixture --output-dir $outputDirectory --account-id 12345 --teammates 2>&1 | Out-String)
    $result = $LASTEXITCODE
} finally {
    $lock.Dispose()
}
Write-Output $messages
if ($result -ne 0) { throw "Locked workbook interrupted reconstruction (exit $result)." }
if ((Get-FileHash -LiteralPath $lockedPath).Hash -ne $lockedHash) { throw "Locked workbook was modified." }
$alternates = @(Get-ChildItem -LiteralPath $outputDirectory -Filter "hero-mmr-contribution-*.xlsx")
if ($alternates.Count -ne 1) { throw "Expected one alternate Excel report." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($alternates[0].FullName)
try {
    if (-not $archive.GetEntry("xl/workbook.xml")) { throw "Alternate report is not an XLSX workbook." }
} finally { $archive.Dispose() }
$manifest = Get-Content -LiteralPath (Join-Path $testRoot "reconstruction-manifest.json") -Raw | ConvertFrom-Json
if ($manifest.outputs -notcontains $alternates[0].FullName -or $manifest.outputs -contains $lockedPath) {
    throw "Manifest does not identify the new workbook."
}
if ($messages -notmatch [regex]::Escape($alternates[0].Name) -or $manifest.notices.Count -ne 1) {
    throw "Alternate workbook was not reported to the user."
}
foreach ($file in @("mmr-history.html", "teammate-report.html", "teammate-requests.json")) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputDirectory $file))) { throw "Missing downstream output: $file" }
}
$dataset = Get-Content -LiteralPath (Join-Path $outputDirectory "mmr-dataset.json") -Raw | ConvertFrom-Json
if ($dataset.rows.Count -ne 4 -or $dataset.rows[-1].curve_mmr_after -ne 3060) { throw "Reconstruction changed." }
if ((Get-FileHash -LiteralPath $fixture).Hash -ne $fixtureHash) { throw "Raw input was modified." }
if (@(Get-ChildItem -LiteralPath $outputDirectory -Filter "*.tmp*").Count -ne 0) { throw "Temporary workbook leaked." }
Write-Output "PASS: locked workbook preserved; alternate XLSX, HTML, teammate report and manifest completed."
Write-Output "Test output: $outputDirectory"
