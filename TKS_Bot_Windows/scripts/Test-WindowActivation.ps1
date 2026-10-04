[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$probeExe = (Resolve-Path (Join-Path $repo 'artifacts/activation-probe/WindowActivationProbe.exe')).Path
$probeKey = 'activation-' + [Guid]::NewGuid().ToString('N')
$evidenceDir = Join-Path $repo ('artifacts/activation-evidence/' + $probeKey)
New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null
$probeHost = Start-Process -FilePath $probeExe -ArgumentList @('--host', $probeKey, ('"' + $evidenceDir + '"')) -WindowStyle Hidden -PassThru
try {
    for ($attempt = 0; $attempt -lt 40 -and -not (Test-Path (Join-Path $evidenceDir 'ready')); $attempt++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path (Join-Path $evidenceDir 'ready'))) { throw 'Activation host did not become ready' }
    for ($round = 1; $round -le 4; $round++) {
        if ($round -eq 4) {
            for ($attempt = 0; $attempt -lt 40 -and -not (Test-Path (Join-Path $evidenceDir 'modal-ready')); $attempt++) { Start-Sleep -Milliseconds 250 }
            if (-not (Test-Path (Join-Path $evidenceDir 'modal-ready'))) { throw 'Modal dialog was not ready' }
        }
        $sender = Start-Process -FilePath $probeExe -ArgumentList @('--send', $probeKey) -WindowStyle Hidden -PassThru
        if (-not $sender.WaitForExit(6000)) { throw 'Wake sender hung' }
        if ($sender.ExitCode -ne 0) { throw ('Wake sender failed: ' + $sender.ExitCode) }
        $resultFile = Join-Path $evidenceDir ('round-' + $round + '.json')
        for ($attempt = 0; $attempt -lt 40 -and -not (Test-Path $resultFile); $attempt++) { Start-Sleep -Milliseconds 250 }
        if (-not (Test-Path $resultFile)) { throw ('Missing round ' + $round) }
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        $result | ConvertTo-Json -Compress
        if (-not $result.visible -or -not $result.active -or $result.state -eq 'Minimized') { throw ('Restore failed in ' + $result.scenario) }
        if ($round -eq 4 -and $result.ownerEnabled) { throw 'Modal ownership was not tested' }
    }
    if (-not $probeHost.WaitForExit(5000)) { throw 'Host did not exit' }
    if ($probeHost.ExitCode -ne 0) { throw ('Host failed: ' + $probeHost.ExitCode) }
    Write-Output ('Evidence: ' + $evidenceDir)
} finally {
    $probeHost.Refresh()
    if (-not $probeHost.HasExited) { Stop-Process -Id $probeHost.Id }
}
