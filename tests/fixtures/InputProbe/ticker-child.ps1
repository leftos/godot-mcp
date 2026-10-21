$pidFile = Join-Path $PSScriptRoot 'ticker-child.pid'
# Written under a name of its own and renamed into place, so the pid file is never seen half written.
$written = "$pidFile.$PID"
Set-Content -LiteralPath $written -Value $PID
for ($attempt = 1; ; $attempt++) {
    try {
        Move-Item -LiteralPath $written -Destination $pidFile -Force -ErrorAction Stop
        break
    }
    catch [System.IO.IOException], [System.UnauthorizedAccessException] {
        # A reader holds the previous child's file for a moment; the rename replaces it once the reader lets go.
        if ($attempt -ge 100) {
            throw
        }
        Start-Sleep -Milliseconds 50
    }
}
while ($true) {
    Start-Sleep -Seconds 1
}
