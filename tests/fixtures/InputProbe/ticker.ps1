$child = Join-Path $PSScriptRoot 'ticker-child.ps1'
Start-Process pwsh -NoNewWindow -ArgumentList '-NoProfile', '-File', $child
$line = 0
while ($true) {
    $line++
    Write-Output "ticker $line"
    Start-Sleep -Seconds 1
}
