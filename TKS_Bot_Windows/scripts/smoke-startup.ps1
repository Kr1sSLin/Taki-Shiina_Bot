[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Executable).Path
if (Get-Process -Name TKSDesktop -ErrorAction SilentlyContinue) {
    throw 'Close the running TKSDesktop before startup verification (single-instance guard).'
}

# Isolate credentials/settings and preserve diagnostic logs for failed runs.
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('tks-startup-' + [Guid]::NewGuid().ToString('N'))
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $exePath
$start.WorkingDirectory = Split-Path -Parent $exePath
$start.UseShellExecute = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.EnvironmentVariables['TKS_CONFIG_DIR'] = Join-Path $probeRoot 'config'
$start.EnvironmentVariables['TKS_DATA_DIR'] = Join-Path $probeRoot 'data'
$start.EnvironmentVariables['TKS_STATE_DIR'] = Join-Path $probeRoot 'state'
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Could not launch the client.' }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $windowSeen = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.WaitForExit(250)) { throw "Client exited during startup: $($process.ExitCode). Logs: $probeRoot" }
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            $windowSeen = $true
        }
    }
    if (-not $windowSeen) { throw "No login window appeared. Logs: $probeRoot" }
    $logs = Get-ChildItem -LiteralPath $probeRoot -Recurse -Filter '*.jsonl' |
        ForEach-Object { Get-Content -LiteralPath $_.FullName }
    if (-not ($logs -match 'Login window shown and initial layout completed') -or
        ($logs -match '"level":"Critical"')) {
        throw "Login layout did not complete successfully. Logs: $probeRoot"
    }
    Write-Output "PASS: normal executable startup, window present for observation period. Logs: $probeRoot"
}
finally {
    # Only the process created above is stopped; no existing client is touched.
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
