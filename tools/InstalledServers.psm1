<#
.SYNOPSIS
Finds and stops the godot-mcp servers running from an install folder, naming the Claude Code session that started each.

.DESCRIPTION
Used by tools/install.ps1, and by tools/install-release.ps1 when it runs from the repo. The release's published install.ps1
runs with no file beside it, so tools/package.ps1 copies this file's text into its InstalledServers region. Runs under
Windows PowerShell 5.1 and PowerShell 7.

A server's client is the process that started it: Claude Code's claude.exe, whose command line carries
--session-id <uuid>. That session's transcript is $env:USERPROFILE\.claude\projects\<folder>\<uuid>.jsonl, and its lines
carry the session's project folder as "cwd".
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The --session-id on the command line of the process that started the server, or $null when that process is gone or
# carries none. A parent created after the server is another process that took the id of the one that exited.
function Get-ClientSessionId {
    param([Parameter(Mandatory)] [ciminstance]$Process)
    $parent = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($Process.ParentProcessId)"
    if ($null -eq $parent -or $parent.CreationDate -gt $Process.CreationDate) {
        return $null
    }
    $uuid = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
    if ([string]$parent.CommandLine -match "--session-id[\s=]+`"?($uuid)") {
        return $Matches[1]
    }
    return $null
}

# The first -Length bytes of a file as UTF-8 text, read without locking out the process that is writing it.
function Read-FileHead {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [int]$Length
    )
    $stream = New-Object System.IO.FileStream $Path, 'Open', 'Read', 'ReadWrite'
    try {
        $buffer = New-Object byte[] $Length
        $total = 0
        do {
            $read = $stream.Read($buffer, $total, $Length - $total)
            $total += $read
        } while ($read -gt 0 -and $total -lt $Length)
        return [System.Text.Encoding]::UTF8.GetString($buffer, 0, $total)
    }
    finally {
        $stream.Dispose()
    }
}

# The project folder of a Claude Code session: the first "cwd" in the first 64 KB of its transcript, or $null when the
# transcript is missing or unreadable.
function Get-ClientProject {
    param([Parameter(Mandatory)] [string]$SessionId)
    if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        return $null
    }
    $projects = Join-Path $env:USERPROFILE '.claude\projects'
    if (-not (Test-Path -LiteralPath $projects -PathType Container)) {
        return $null
    }
    try {
        $transcript = @(Get-ChildItem -Path (Join-Path $projects "*\$SessionId.jsonl") -File) | Select-Object -First 1
        if ($null -eq $transcript) {
            return $null
        }
        $head = Read-FileHead -Path $transcript.FullName -Length 65536
        if ($head -notmatch '"cwd"\s*:\s*"((?:[^"\\]|\\.)*)"') {
            return $null
        }
        return [string](ConvertFrom-Json ('"' + $Matches[1] + '"'))
    }
    catch {
        Write-Verbose "The transcript of Claude session $SessionId could not be read: $($_.Exception.Message)"
        return $null
    }
}

<#
.SYNOPSIS
The godot-mcp.exe processes whose image is inside an install folder.

.DESCRIPTION
Win32_Process gives the image path without the access checks Get-Process's Path needs under Windows PowerShell. Each
server is returned as {ProcessId, SessionId, Project}: the Claude Code session that started it and that session's project
folder, each $null when unknown.

.PARAMETER InstallDir
The install folder, as a full path.
#>
function Get-InstalledServer {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] [string]$InstallDir)
    $prefix = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\', '/') + '\'
    $running = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'godot-mcp.exe'" |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($process in $running) {
        $sessionId = Get-ClientSessionId -Process $process
        $project = $null
        if ($null -ne $sessionId) {
            $project = Get-ClientProject -SessionId $sessionId
        }
        [pscustomobject]@{ ProcessId = [int]$process.ProcessId; SessionId = $sessionId; Project = $project }
    }
}

function Format-StoppedServer {
    param([Parameter(Mandatory)] [pscustomobject]$Server)
    $client = 'an unknown client'
    if ($null -ne $Server.SessionId) {
        $client = "Claude session $($Server.SessionId)"
        if ($null -ne $Server.Project) {
            $client += " in $($Server.Project)"
        }
    }
    return "install: stopped the godot-mcp server (pid $($Server.ProcessId)) of $client; run /mcp there to reconnect it"
}

<#
.SYNOPSIS
Stops every godot-mcp.exe running from an install folder, and names the Claude Code session each served.

.DESCRIPTION
Stops each server Get-InstalledServer finds, waits up to 10 s for it to exit, and writes one line for it to the host, so
the person installing can reconnect those sessions. Returns the stopped servers.

.PARAMETER InstallDir
The install folder, as a full path.
#>
function Stop-InstalledServer {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSAvoidUsingWriteHost', '', Justification = 'An install step: its lines are for the person running the install.')]
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] [string]$InstallDir)
    foreach ($server in @(Get-InstalledServer -InstallDir $InstallDir)) {
        if (-not $PSCmdlet.ShouldProcess("godot-mcp.exe (pid $($server.ProcessId))", 'Stop')) {
            continue
        }
        $handle = Get-Process -Id $server.ProcessId -ErrorAction SilentlyContinue
        Stop-Process -Id $server.ProcessId -Force
        if ($null -ne $handle) {
            [void]$handle.WaitForExit(10000)
        }
        Write-Host (Format-StoppedServer -Server $server)
        $server
    }
}
