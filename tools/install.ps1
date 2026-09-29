#requires -Version 7

<#
.SYNOPSIS
Installs a published godot-mcp: mirrors bin/publish into an install folder, links the agent skills and sweeps the agent files.

.DESCRIPTION
Run by `pwsh run.ps1 install` after its publish. Stops every godot-mcp.exe running from -InstallDir, printing one line for
each that names the Claude session it served (tools/InstalledServers.psm1), so the session can be reconnected with /mcp.
Then mirrors <Root>\bin\publish into -InstallDir with robocopy /MIR (no retries), writes -InstallDir\VERSION holding
the published godot-mcp.dll's product version (the mirror leaves that file alone, so a failed mirror keeps the old one
beside the old exe), then links <SkillsDir>\godot-mcp to <Root>\skills\godot-mcp and <SkillsDir>\godot-agent-sweep to
<Root>\skills\godot-agent-sweep as directory junctions: each created when missing, left alone when it already points
there, replaced when it is a junction pointing elsewhere, and refused when it is anything else (a real folder is never
deleted). Warns when <Root> is a linked worktree, since the junctions then point into it. Stops at the first failure with
status 1.

Last, runs the installed godot-mcp.exe --sweep-agents, which brings the godot tools of their marked classes into the
Claude Code agent files, and prints its lines. A sweep that cannot start is reported and the install still succeeds; a
sweep that exits non-zero is reported and the install exits 1, though nothing it installed is undone.

.PARAMETER Root
The checkout to install from.

.PARAMETER InstallDir
The folder bin/publish is mirrored into; files in it that bin/publish lacks are removed, VERSION aside.

.PARAMETER SkillsDir
The folder the skill junctions godot-mcp and godot-agent-sweep are created in.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'A console install script: its lines are for the person running it.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helpers of a script run by run.ps1; it takes no -WhatIf.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Root,
    [Parameter(Mandatory)] [string]$InstallDir,
    [Parameter(Mandatory)] [string]$SkillsDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path $PSScriptRoot 'InstalledServers.psm1') -Force

# A path made absolute, without the \??\ or \\?\ prefix a junction's target can carry and without a trailing separator.
function Get-FullPath {
    param([Parameter(Mandatory)] [string]$Path)
    $plain = $Path -replace '^(\\\?\?\\|\\\\\?\\)', ''
    return [System.IO.Path]::GetFullPath($plain).TrimEnd('\', '/')
}

function Exit-Install {
    param([Parameter(Mandatory)] [string]$Message)
    [Console]::Error.WriteLine($Message)
    exit 1
}

# A linked worktree has a git dir of its own under the main checkout's common one; the main checkout's two are the same.
function Write-WorktreeWarning {
    $dirs = @(git -C $Root rev-parse --path-format=absolute --git-dir --git-common-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or $dirs.Count -ne 2) {
        return
    }
    if ((Get-FullPath $dirs[0]) -ne (Get-FullPath $dirs[1])) {
        Write-Host "install: warning: installing from a linked worktree ($Root); the skill junction will point here."
    }
}

# Robocopy's exit codes 0-7 are success and 8 and above a failure: "Any value equal to or greater than 8 indicates that
# there was at least one failure during the copy operation"
# (https://learn.microsoft.com/windows-server/administration/windows-commands/robocopy#exit-return-codes).
function Copy-Publish {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )
    & robocopy $Source $Destination /MIR /XF VERSION /R:0 /W:0 /NP /NFL /NDL | Out-Host
    $status = $LASTEXITCODE
    if ($status -ge 8) {
        Exit-Install ("install: copying bin/publish to $Destination failed (robocopy status $status, log .tmp/install.log). " +
            'A running godot-mcp.exe holds the old copy: stop the Claude sessions that use the godot server, then run install again.')
    }
}

# The published dll's ProductVersion is the assembly's InformationalVersion, <semver>+<7-char sha>.
function Get-PublishVersion {
    param([Parameter(Mandatory)] [string]$Publish)
    $dll = Join-Path $Publish 'godot-mcp.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        Exit-Install 'install: bin/publish/godot-mcp.dll is missing; run publish first.'
    }
    $version = (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        Exit-Install 'install: bin/publish/godot-mcp.dll carries no product version; run publish again.'
    }
    return $version
}

function New-SkillJunction {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Link
    )
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Link) | Out-Null
    New-Item -ItemType Junction -Path $Link -Target $Source | Out-Null
}

# The attributes are read from the link itself, so a junction whose target is gone still counts as existing.
function Set-SkillJunction {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Link
    )
    if (-not (Test-Path -LiteralPath (Join-Path $Source 'SKILL.md') -PathType Leaf)) {
        Exit-Install "install: skills/$(Split-Path -Leaf $Source)/SKILL.md is missing."
    }
    $info = [System.IO.DirectoryInfo]::new($Link)
    if ([int]$info.Attributes -eq -1) {
        New-SkillJunction -Source $Source -Link $Link
        return
    }
    if ($info.LinkType -ne 'Junction') {
        Exit-Install "install: $Link exists and is not a junction; move it away and run install again."
    }
    if ((Get-FullPath $info.LinkTarget) -eq $Source) {
        return
    }
    [System.IO.Directory]::Delete($Link)
    New-SkillJunction -Source $Source -Link $Link
}

# Runs the installed server's agent sweep and prints its lines. Returns its exit status, or 0 when it could not start.
function Invoke-AgentSweep {
    param([Parameter(Mandatory)] [string]$Exe)
    # Each line is printed as it comes, so a long sweep shows its progress and a killed install keeps the lines so far.
    try {
        & $Exe --sweep-agents | ForEach-Object { Write-Host $_ }
        return $LASTEXITCODE
    }
    catch {
        Write-Host "install: agent sweep could not run: $($_.Exception.Message)"
        return 0
    }
}

$publish = Get-FullPath (Join-Path $Root 'bin/publish')
$destination = Get-FullPath $InstallDir
$skill = Get-FullPath (Join-Path $Root 'skills/godot-mcp')
$link = Join-Path (Get-FullPath $SkillsDir) 'godot-mcp'
$sweepSkill = Get-FullPath (Join-Path $Root 'skills/godot-agent-sweep')
$sweepLink = Join-Path (Get-FullPath $SkillsDir) 'godot-agent-sweep'

Write-WorktreeWarning
$version = Get-PublishVersion -Publish $publish
$null = Stop-InstalledServer -InstallDir $destination
Copy-Publish -Source $publish -Destination $destination
# One line, LF, no BOM (WriteAllText's default encoding is UTF-8 without one).
[System.IO.File]::WriteAllText((Join-Path $destination 'VERSION'), "$version`n")
Set-SkillJunction -Source $skill -Link $link
Set-SkillJunction -Source $sweepSkill -Link $sweepLink
$exe = Join-Path $destination 'godot-mcp.exe'
Write-Host "install: server at $exe (version $version), skill linked at $link"
Write-Host "install: agent sweep skill linked at $sweepLink"
$sweep = Invoke-AgentSweep -Exe $exe
if ($sweep -ne 0) {
    Exit-Install ("install: the agent sweep exited with status $sweep; the server and skills are installed. " +
        "Fix what its lines name, then run `"$exe`" --sweep-agents.")
}
exit 0
