param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputDirectory = Join-Path $projectRoot "collector\bin\$Configuration\net8.0-windows"
$assemblyPath = Join-Path $outputDirectory "Dota2MmrReconstructor.dll"
$executablePath = Join-Path $outputDirectory "Dota2MmrReconstructor.exe"

if (-not (Test-Path -LiteralPath $assemblyPath) -or
    -not (Test-Path -LiteralPath $executablePath)) {
    throw "Build output was not found. Run dotnet build first."
}

$version = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
if (-not $version.StartsWith("0.4.1", [System.StringComparison]::Ordinal)) {
    throw "Expected product version 0.4.1, got $version."
}

$helpOutput = (& $executablePath --help | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "--help exited with code $LASTEXITCODE."
}
if ($helpOutput -match "--password|--username|--steam-guard-code|--otp") {
    throw "Sensitive credential values must not be accepted through command-line arguments."
}
if ($helpOutput -notmatch "仅在 GUI 提供") {
    throw "Help output does not explain the GUI-only credential flow."
}

$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
$credentialsType = $assembly.GetType(
    "Dota2MmrCollector.EphemeralSteamCredentials",
    $true
)
$constructor = $credentialsType.GetConstructors(
    [System.Reflection.BindingFlags]"Instance,Public,NonPublic"
) | Select-Object -First 1
$credentials = $constructor.Invoke(@("temporary-user", "temporary-password"))
$usernameProperty = $credentialsType.GetProperty("Username")
$passwordProperty = $credentialsType.GetProperty("Password")
$clearedProperty = $credentialsType.GetProperty("IsCleared")

if ($usernameProperty.GetValue($credentials) -ne "temporary-user" -or
    $passwordProperty.GetValue($credentials) -ne "temporary-password") {
    throw "Ephemeral credential holder did not receive the supplied values."
}

$credentialsType.GetMethod("Clear").Invoke($credentials, @()) | Out-Null
if ($clearedProperty.GetValue($credentials) -ne $true -or
    $usernameProperty.GetValue($credentials) -ne "" -or
    $passwordProperty.GetValue($credentials) -ne "") {
    throw "Ephemeral credential holder did not release its managed references."
}

$authenticatorType = $assembly.GetType(
    "Dota2MmrCollector.InteractiveSteamAuthenticator",
    $true
)
$authenticatorConstructor = $authenticatorType.GetConstructors(
    [System.Reflection.BindingFlags]"Instance,Public,NonPublic"
) | Select-Object -First 1
$authenticator = $authenticatorConstructor.Invoke(@())
$confirmationTask = $authenticatorType.GetMethod(
    "AcceptDeviceConfirmationAsync"
).Invoke($authenticator, @())
if ($confirmationTask.GetAwaiter().GetResult() -ne $false) {
    throw "Credential login must fall back from mobile approval to a typed Steam Guard code."
}

$setupSource = Get-Content -LiteralPath (
    Join-Path $projectRoot "collector\CollectorSetupWindow.cs"
) -Raw
if ($setupSource -notmatch "UseSystemPasswordChar\s*=\s*true") {
    throw "The GUI password field is not masked."
}

Write-Output "PASS: v0.4.1 credentials are GUI-only, masked, ephemeral, and force OTP fallback."
