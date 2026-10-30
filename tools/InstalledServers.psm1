<#
.SYNOPSIS
Finds the godot-mcp processes an install folder runs, naming the Claude Code session that started each, and stops a
flat server.

.DESCRIPTION
Used by tools/install.ps1 and tools/InstallLayout.psm1, and by tools/install-release.ps1 when it runs from the repo.
The release's published install.ps1 runs with no file beside it, so tools/package.ps1 copies this file's text into its
InstalledServers region. Runs under Windows PowerShell 5.1 and PowerShell 7.

A server's client is the process that started it, or for a versioned server the process that started its launcher:
Claude Code's claude.exe, whose command line carries
--session-id <uuid>. That session's transcript is $env:USERPROFILE\.claude\projects\<folder>\<uuid>.jsonl, and its lines
carry the session's project folder as "cwd".
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The process that started a process, or $null when it is gone. A parent created after the child is another process that
# took the id of the one that exited.
function Get-ParentProcess {
    param([Parameter(Mandatory)] [ciminstance]$Process)
    $parent = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($Process.ParentProcessId)"
    if ($null -eq $parent -or $parent.CreationDate -gt $Process.CreationDate) {
        return $null
    }
    return $parent
}

# The --session-id on the command line of the client that started the server, or $null when that process is gone or
# carries none. A versioned server's parent is its launcher, a godot-mcp.exe too, so its client is one step further up.
function Get-ClientSessionId {
    param([Parameter(Mandatory)] [ciminstance]$Process)
    $parent = Get-ParentProcess -Process $Process
    if ($null -ne $parent -and $parent.Name -eq 'godot-mcp.exe') {
        $parent = Get-ParentProcess -Process $parent
    }
    if ($null -eq $parent) {
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

# What a godot-mcp.exe at a path is to an install folder, @{ Kind; Version }, or $null when it is none of its own:
# <folder>\godot-mcp.exe is the Launcher when current is beside it and the Flat server of an install from before the
# launcher when it is not; <folder>\versions\<name>\godot-mcp.exe is a Versioned server of version folder <name>.
function Get-ServerKind {
    param(
        [Parameter(Mandatory)] [string]$Folder,
        [Parameter(Mandatory)] [string]$Path
    )
    if ($Path -ieq (Join-Path $Folder 'godot-mcp.exe')) {
        if (Test-Path -LiteralPath (Join-Path $Folder 'current') -PathType Leaf) {
            return @{ Kind = 'Launcher'; Version = $null }
        }
        return @{ Kind = 'Flat'; Version = $null }
    }
    $versions = (Join-Path $Folder 'versions') + '\'
    if (-not $Path.StartsWith($versions, [StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    $parts = $Path.Substring($versions.Length).Split('\')
    if ($parts.Count -ne 2 -or $parts[1] -ine 'godot-mcp.exe') {
        return $null
    }
    return @{ Kind = 'Versioned'; Version = $parts[0] }
}

<#
.SYNOPSIS
The godot-mcp.exe processes an install folder runs: its launchers, its versioned servers and a flat server.

.DESCRIPTION
Win32_Process gives the image path without the access checks Get-Process's Path needs under Windows PowerShell. Each
process is returned as {ProcessId, SessionId, Project, Kind, Version}: the Claude Code session that started it and that
session's project folder, each $null when unknown; Kind is Launcher (<InstallDir>\godot-mcp.exe with current beside
it), Versioned (<InstallDir>\versions\<name>\godot-mcp.exe, Version being <name>) or Flat (<InstallDir>\godot-mcp.exe
of an install from before the launcher, with no current).

.PARAMETER InstallDir
The install folder, as a full path.
#>
function Get-InstalledServer {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] [string]$InstallDir)
    $folder = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\', '/')
    $running = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'godot-mcp.exe'" | Where-Object { $_.ExecutablePath })
    foreach ($process in $running) {
        $kind = Get-ServerKind -Folder $folder -Path $process.ExecutablePath
        if ($null -eq $kind) {
            continue
        }
        $sessionId = Get-ClientSessionId -Process $process
        $project = $null
        if ($null -ne $sessionId) {
            $project = Get-ClientProject -SessionId $sessionId
        }
        [pscustomobject]@{
            ProcessId = [int]$process.ProcessId; SessionId = $sessionId; Project = $project; Kind = $kind.Kind; Version = $kind.Version
        }
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
Stops the flat server an install folder from before the launcher runs, and names the Claude Code session each served.

.DESCRIPTION
Stops each Flat server Get-InstalledServer finds (a process whose image is exactly <InstallDir>\godot-mcp.exe, with no
current beside it), waits up to 10 s for it to exit, and writes one line for it to the host, so the person installing
can reconnect those sessions. Launchers and versioned servers keep running. Returns the stopped servers.

.PARAMETER InstallDir
The install folder, as a full path.
#>
function Stop-InstalledServer {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSAvoidUsingWriteHost', '', Justification = 'An install step: its lines are for the person running the install.')]
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] [string]$InstallDir)
    foreach ($server in @(Get-InstalledServer -InstallDir $InstallDir | Where-Object { $_.Kind -eq 'Flat' })) {
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
