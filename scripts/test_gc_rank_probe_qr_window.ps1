param(
    [string]$CollectorExe = (
        Join-Path $PSScriptRoot "..\dist\Dota2MmrCollector-win-x64\Dota2MmrCollector.exe"
    ),
    [int]$TimeoutSeconds = 20
)

$resolvedCollector = (Resolve-Path -LiteralPath $CollectorExe).Path
$runId = [Guid]::NewGuid().ToString("N")
$stdoutPath = Join-Path $env:TEMP "gc-rank-probe-qr-$runId.stdout.txt"
$stderrPath = Join-Path $env:TEMP "gc-rank-probe-qr-$runId.stderr.txt"
$outputPath = Join-Path $env:TEMP "gc-rank-probe-qr-$runId.json"
$collector = $null
$previousOffscreenTestMode = $env:DOTA2_MMR_QR_TEST_OFFSCREEN

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class CollectorWindowInspector
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    public static string[] VisibleTitlesForProcess(uint expectedProcessId)
    {
        var titles = new List<string>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId != expectedProcessId || !IsWindowVisible(window))
            {
                return true;
            }

            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            titles.Add(title.ToString());
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }
}
"@

try {
    # Keep the integration test invisible on the user's desktop while still
    # creating a real, visible Win32 top-level form that EnumWindows can assert.
    $env:DOTA2_MMR_QR_TEST_OFFSCREEN = "1"
    $collector = Start-Process `
        -FilePath $resolvedCollector `
        -ArgumentList @("--history-matches", "0", "--output", $outputPath) `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $qrEventSeen = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($collector.HasExited) {
            $stderr = if (Test-Path -LiteralPath $stderrPath) {
                Get-Content -LiteralPath $stderrPath -Raw
            } else {
                ""
            }
            throw "Collector exited before producing a QR login event. $stderr"
        }

        if ((Test-Path -LiteralPath $stdoutPath) -and
            ((Get-Content -LiteralPath $stdoutPath -Raw) -match "二维码窗口已打开或刷新")) {
            $qrEventSeen = $true
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if (-not $qrEventSeen) {
        throw "Collector did not produce a QR login event within $TimeoutSeconds seconds."
    }

    Start-Sleep -Milliseconds 500
    $windowTitles = [CollectorWindowInspector]::VisibleTitlesForProcess([uint32]$collector.Id)
    $qrWindowTitle = $windowTitles | Where-Object { $_ -match "Steam.*(QR|扫码)|扫码.*Steam" } | Select-Object -First 1
    if ($null -eq $qrWindowTitle) {
        throw "QR event arrived, but no explicit Steam QR scan window is visible for PID $($collector.Id)."
    }

    Write-Output "PASS: visible QR scan window '$qrWindowTitle' for PID $($collector.Id)."
}
finally {
    if ($null -ne $collector -and -not $collector.HasExited) {
        Stop-Process -Id $collector.Id
        $collector.WaitForExit(5000) | Out-Null
    }

    foreach ($path in @($stdoutPath, $stderrPath, $outputPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    $env:DOTA2_MMR_QR_TEST_OFFSCREEN = $previousOffscreenTestMode
}
