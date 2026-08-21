param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$fixture = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot "tests\fixtures\synthetic-gc-collection.json")
)
$executable = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot "collector\bin\$Configuration\net8.0-windows\Dota2MmrReconstructor.exe")
)
$testRoot = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "dota2-mmr-reconstructor-test-$([guid]::NewGuid().ToString('N'))"
)
$outputDirectory = Join-Path $testRoot "mmr-reconstruction"
$beforeHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
& $executable `
    --reconstruct-existing $fixture `
    --output-dir $outputDirectory `
    --account-id 12345
if ($LASTEXITCODE -ne 0) {
    throw "C# reconstruction exited with code $LASTEXITCODE."
}

$summaryPath = Join-Path $outputDirectory "model-summary.json"
$datasetPath = Join-Path $outputDirectory "mmr-dataset.json"
$htmlPath = Join-Path $outputDirectory "mmr-history.html"
$pngPath = Join-Path $outputDirectory "complete-mmr-curve.png"
$heroReportPath = Join-Path $outputDirectory "hero-mmr-contribution.txt"
$manifestPath = Join-Path $testRoot "reconstruction-manifest.json"
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$dataset = Get-Content -LiteralPath $datasetPath -Raw | ConvertFrom-Json

if ($summary.account_id -ne 12345) {
    throw "Unexpected account ID in summary."
}
if ($summary.curve_reconstruction.matches -ne 4) {
    throw "Expected four reconstructed curve rows."
}
if ($summary.curve_reconstruction.endpoint_constrained_matches -ne 2) {
    throw "Expected two endpoint-constrained rows."
}
if ($summary.curve_reconstruction.all_hidden_endpoints_exact -ne $true) {
    throw "Hidden segment endpoint is not exact."
}
if ($dataset.rows[-1].curve_mmr_after -ne 3060) {
    throw "Curve does not end on the Current Rank anchor."
}
if (-not (Select-String -LiteralPath $htmlPath -SimpleMatch 'id="embedded-dataset"' -Quiet)) {
    throw "Standalone HTML has no embedded dataset."
}
if (-not (Test-Path -LiteralPath $pngPath)) {
    throw "Static PNG was not generated."
}
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
if ($pngBytes.Length -lt 8 -or $pngBytes[0] -ne 0x89 -or $pngBytes[1] -ne 0x50 -or
    $pngBytes[2] -ne 0x4E -or $pngBytes[3] -ne 0x47) {
    throw "Static chart output is not a PNG file."
}
Add-Type -AssemblyName System.Drawing.Common
$pngImage = [System.Drawing.Image]::FromFile($pngPath)
try {
    if ($pngImage.Width -ne 2300 -or $pngImage.Height -ne 1250) {
        throw "Unexpected static PNG dimensions: $($pngImage.Width)x$($pngImage.Height)."
    }
}
finally {
    $pngImage.Dispose()
}
if (-not (Select-String -LiteralPath $heroReportPath -SimpleMatch "总MMR贡献" -Quiet)) {
    throw "Hero contribution report is missing its sorted contribution table."
}
if (-not (Select-String -LiteralPath $heroReportPath -SimpleMatch "拟合贡献" -Quiet)) {
    throw "Hero contribution report does not separate fitted contribution."
}
if (-not (Select-String -LiteralPath $heroReportPath -SimpleMatch "Medusa" -Quiet)) {
    throw "Hero contribution report did not resolve HeroId 94."
}
$heroReport = Get-Content -LiteralPath $heroReportPath -Raw -Encoding utf8
if ($heroReport.IndexOf("Morphling", [System.StringComparison]::Ordinal) -gt
    $heroReport.IndexOf("Anti-Mage", [System.StringComparison]::Ordinal)) {
    throw "Hero contribution report is not sorted from positive to negative."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not ($manifest.outputs -match 'complete-mmr-curve.png')) {
    throw "Reconstruction manifest does not include the static PNG."
}
if (-not ($manifest.outputs -match 'hero-mmr-contribution.txt')) {
    throw "Reconstruction manifest does not include the hero contribution report."
}
$afterHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
if ($beforeHash -ne $afterHash) {
    throw "Raw GC input was modified by reconstruction."
}

Write-Output "PASS: C# reconstruction preserved raw input and generated exact endpoint outputs."
Write-Output "Test output: $outputDirectory"
