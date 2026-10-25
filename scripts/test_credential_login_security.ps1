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
if (-not $version.StartsWith("0.6.0", [System.StringComparison]::Ordinal)) {
    throw "Expected product version 0.6.0, got $version."
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

$secretType = $assembly.GetType("Dota2MmrCollector.EphemeralSecret", $true)
$secretConstructor = $secretType.GetConstructors(
    [System.Reflection.BindingFlags]"Instance,Public,NonPublic"
) | Select-Object -First 1
$secret = $secretConstructor.Invoke(@("temporary-opendota-key"))
$secretType.GetMethod("Clear").Invoke($secret, @()) | Out-Null
if ($secretType.GetProperty("IsCleared").GetValue($secret) -ne $true -or
    $secretType.GetProperty("Value").GetValue($secret) -ne "") {
    throw "Ephemeral OpenDota key holder did not release its managed reference."
}

$keyStoreType = $assembly.GetType(
    "Dota2MmrCollector.PracticeOpenDotaApiKeyStore",
    $true
)
$storeFlags = [System.Reflection.BindingFlags]"Static,NonPublic,Public"
$keyStoreRoot = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "dota2-opendota-key-test-$([guid]::NewGuid().ToString('N'))"
)
$keyStorePath = Join-Path $keyStoreRoot "opendota-api-key.bin"
$testKey = "local-opendota-key-$([guid]::NewGuid().ToString('N'))"
try {
    $saveArguments = [object[]]::new(2)
    $saveArguments[0] = [string]$testKey
    $saveArguments[1] = [string]$keyStorePath
    $keyStoreType.GetMethod("Save", $storeFlags).Invoke(
        $null,
        $saveArguments
    ) | Out-Null
    if (-not (Test-Path -LiteralPath $keyStorePath)) {
        throw "Encrypted OpenDota key store was not created."
    }
    $rawKeyStore = [System.IO.File]::ReadAllBytes($keyStorePath)
    if ([System.Text.Encoding]::UTF8.GetString($rawKeyStore).Contains($testKey)) {
        throw "OpenDota key store contains the plaintext key."
    }
    $pathArgument = [object[]]::new(1)
    $pathArgument[0] = [string]$keyStorePath
    $loadedKey = $keyStoreType.GetMethod("TryLoad", $storeFlags).Invoke($null, $pathArgument)
    if ($loadedKey -ne $testKey) {
        throw "Encrypted OpenDota key store did not round-trip for the current Windows user."
    }
    $keyStoreType.GetMethod("Delete", $storeFlags).Invoke(
        $null,
        $pathArgument
    ) | Out-Null
    if (Test-Path -LiteralPath $keyStorePath) {
        throw "Encrypted OpenDota key store was not deleted."
    }
}
finally {
    if (Test-Path -LiteralPath $keyStorePath) {
        Remove-Item -LiteralPath $keyStorePath -Force
    }
    if (Test-Path -LiteralPath $keyStoreRoot) {
        Remove-Item -LiteralPath $keyStoreRoot -Force
    }
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
if ($setupSource -notmatch "saveOpenDotaApiKeyCheck" -or
    $setupSource -notmatch "Windows 当前用户加密") {
    throw "The GUI does not expose the encrypted local OpenDota key option."
}

Write-Output "PASS: v0.6.0 Steam credentials remain GUI-only, masked, and ephemeral; OpenDota key storage is Windows-user encrypted."
