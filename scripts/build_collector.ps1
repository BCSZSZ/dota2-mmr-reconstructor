param(
    [string]$OutputDirectory = "dist"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $distRoot "Dota2MmrCollector-win-x64")
)
$archivePath = [System.IO.Path]::GetFullPath(
    (Join-Path $distRoot "Dota2MmrCollector-win-x64.zip")
)

if (-not $publishDirectory.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside the selected output directory: $publishDirectory"
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

dotnet publish (Join-Path $projectRoot "collector\Dota2MmrCollector.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --no-self-contained `
    --output $publishDirectory `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -File | Remove-Item -Force
Get-ChildItem -LiteralPath $publishDirectory -Filter "*.xml" -File | Remove-Item -Force

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath

$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
Write-Host "Collector folder: $publishDirectory"
Write-Host "Release archive: $archivePath"
Write-Host "SHA256: $($hash.Hash)"
