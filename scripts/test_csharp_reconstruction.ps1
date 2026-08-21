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
$afterHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
if ($beforeHash -ne $afterHash) {
    throw "Raw GC input was modified by reconstruction."
}

Write-Output "PASS: C# reconstruction preserved raw input and generated exact endpoint outputs."
Write-Output "Test output: $outputDirectory"
