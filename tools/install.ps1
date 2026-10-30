#requires -Version 7

<#
.SYNOPSIS
Installs a published godot-mcp: a new version folder behind the stable launcher, the agent skills linked from it, and the
agent files swept.

.DESCRIPTION
Run by `pwsh run.ps1 install` after its publish. Lays out -InstallDir as tools/InstallLayout.psm1 describes, holding
-InstallDir\install.lock from start to end (a second install waits up to 60 s, then refuses):

1. An install from before the launcher (no current) is migrated: every godot-mcp.exe running as -InstallDir\godot-mcp.exe
   is stopped, with one line naming the Claude session it served (tools/InstalledServers.psm1), so the session can be
   reconnected with /mcp; then everything but versions, current and install.lock is removed.
2. <Root>\bin\publish, all but its launcher folder, is copied into a new -InstallDir\versions\<version> (the published
   godot-mcp.dll's product version with + turned into -, .2, .3, ... when taken), with a VERSION file holding that version.
3. The new folder's godot-mcp.exe --list-tools must exit 0; then current is pointed at it in one rename, its VERSION is
   copied beside the launcher, and bin/publish/launcher/godot-mcp.exe is installed as -InstallDir\godot-mcp.exe (a
   different one, even running, is renamed aside to godot-mcp.exe.old-<guid> first). Servers already running keep the
   version they started with.
4. <SkillsDir>\godot-mcp is linked to the new folder's skill and <SkillsDir>\godot-agent-sweep to its agent-sweep-skill as
   directory junctions: each created when missing, left alone when it already points there, replaced when it is a
   junction pointing elsewhere, and refused when it is anything else (a real folder is never deleted).
5. One line names each Claude session whose server still runs an older version, so it can be /mcp'd; version folders
   other than the current and the previous one are removed when nothing runs from them.
6. The new version's godot-mcp.exe --sweep-agents brings the godot tools of their marked classes into the Claude Code
   agent files, and its lines are printed. A sweep that cannot start is reported and the install still succeeds; a sweep
   that exits non-zero is reported and the install exits 1, though nothing it installed is undone.

Stops at the first failure with status 1.

.PARAMETER Root
The checkout to install from.

.PARAMETER InstallDir
The install folder.

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
Import-Module -Name (Join-Path $PSScriptRoot 'InstallLayout.psm1') -Force

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

# Everything the install copies out of bin/publish, checked before the install folder is touched.
function Assert-PublishLayout {
    param([Parameter(Mandatory)] [string]$Publish)
    if (-not (Test-Path -LiteralPath (Join-Path $Publish 'launcher\godot-mcp.exe') -PathType Leaf)) {
        Exit-Install 'install: bin/publish/launcher/godot-mcp.exe is missing; run publish first.'
    }
    foreach ($skill in @('skill', 'agent-sweep-skill')) {
        $file = Join-Path (Join-Path $Publish $skill) 'SKILL.md'
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            Exit-Install "install: $file is missing; run publish first."
        }
    }
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
        Exit-Install "install: $Source\SKILL.md is missing; run publish first."
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

# Runs the new version's agent sweep and prints its lines. Returns its exit status, or 0 when it could not start.
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

# The install itself, run holding the install lock. Returns the agent sweep's exit status.
function Invoke-LockedInstall {
    param(
        [Parameter(Mandatory)] [string]$Publish,
        [Parameter(Mandatory)] [string]$Destination,
        [Parameter(Mandatory)] [string]$Version
    )
    $request = @{
        InstallDir = $Destination; Version = $Version; Source = $Publish; Exclude = @('launcher')
        Launcher = (Join-Path $Publish 'launcher\godot-mcp.exe')
    }
    $installed = Install-Version @request
    # The skills come from the new version folder, not the checkout: publish put them in bin/publish and the copy carried
    # them on, so a released build and a from-source install ship the same skills.
    $skill = Join-Path $installed.Folder 'skill'
    $link = Join-Path (Get-FullPath $SkillsDir) 'godot-mcp'
    $sweepSkill = Join-Path $installed.Folder 'agent-sweep-skill'
    $sweepLink = Join-Path (Get-FullPath $SkillsDir) 'godot-agent-sweep'
    Set-SkillJunction -Source $skill -Link $link
    Set-SkillJunction -Source $sweepSkill -Link $sweepLink
    $launcher = Join-Path $Destination 'godot-mcp.exe'
    Write-Host "install: server at $launcher (version $Version), skill linked at $link (from $skill)"
    Write-Host "install: agent sweep skill linked at $sweepLink (from $sweepSkill)"
    Write-OlderSession -InstallDir $Destination -Current $installed.Name -NewVersion $Version
    Remove-OldVersion -InstallDir $Destination -Keep @($installed.Name, $installed.Previous)
    return Invoke-AgentSweep -Exe (Join-Path $installed.Folder 'godot-mcp.exe')
}

$publish = Get-FullPath (Join-Path $Root 'bin/publish')
$destination = Get-FullPath $InstallDir
$version = Get-PublishVersion -Publish $publish
Assert-PublishLayout -Publish $publish
try {
    $lock = Enter-InstallLock -InstallDir $destination
    try {
        $sweep = Invoke-LockedInstall -Publish $publish -Destination $destination -Version $version
    }
    finally {
        $lock.Dispose()
    }
}
catch {
    Exit-Install $_.Exception.Message
}
if ($sweep -ne 0) {
    $exe = Join-Path $destination 'godot-mcp.exe'
    Exit-Install ("install: the agent sweep exited with status $sweep; the server and skills are installed. " +
        "Fix what its lines name, then run `"$exe`" --sweep-agents.")
}
exit 0
