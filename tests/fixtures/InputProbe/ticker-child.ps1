$pidFile = Join-Path $PSScriptRoot 'ticker-child.pid'
$PID | Set-Content -Path $pidFile
while ($true) {
    Start-Sleep -Seconds 1
}
