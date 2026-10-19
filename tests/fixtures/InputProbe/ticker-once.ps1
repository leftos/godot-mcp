$child = Join-Path $PSScriptRoot 'ticker-child.ps1'
$childPidFile = Join-Path $PSScriptRoot 'ticker-child.pid'
Start-Process pwsh -ArgumentList '-NoProfile', '-File', $child
while (-not (Test-Path $childPidFile)) {
    Start-Sleep -Milliseconds 50
}
Write-Output 'once'
Start-Sleep -Seconds 1
