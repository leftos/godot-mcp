<#
.SYNOPSIS
The versioned install layout both installers share: each build in a folder of its own behind a stable launcher.

.DESCRIPTION
Used by tools/install.ps1, and by tools/install-release.ps1 when it runs from the repo. The release's published
install.ps1 runs with no file beside it, so tools/package.ps1 copies this file's text into its InstallLayout region. Runs
under Windows PowerShell 5.1 and PowerShell 7. Calls Stop-InstalledServer, Get-InstalledServer and Get-ClientProject
from tools/InstalledServers.psm1, which each installer loads first.

An install folder holds godot-mcp.exe (the launcher), current (a file whose first line names the current version
folder), VERSION (a copy of the current version's), install.lock, versions\<name>\ (one complete server tree each) and
any godot-mcp.exe.old-<guid> a replaced launcher left while it ran. A version folder is never written once current has
named it; a running server keeps its folder, since a folder a process runs from cannot be renamed, and only a folder
renamed to versions\.trash-<guid> is deleted recursively.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The seconds a second install waits for the lock: GODOT_MCP_INSTALL_LOCK_WAIT_S when set, else 60.
function Get-LockWaitSecond {
    $configured = [Environment]::GetEnvironmentVariable('GODOT_MCP_INSTALL_LOCK_WAIT_S')
    if ([string]::IsNullOrWhiteSpace($configured)) {
        return 60
    }
    $seconds = 0
    if (-not [int]::TryParse($configured, [ref]$seconds) -or $seconds -lt 0) {
        throw "install: GODOT_MCP_INSTALL_LOCK_WAIT_S is '$configured', not a whole number of seconds."
    }
    return $seconds
}

function Test-IOException {
    param([Parameter(Mandatory)] [Exception]$Exception)
    for ($inner = $Exception; $null -ne $inner; $inner = $inner.InnerException) {
        if ($inner -is [System.IO.IOException]) {
            return $true
        }
    }
    return $false
}

<#
.SYNOPSIS
Holds <InstallDir>\install.lock, so two installs never interleave.

.DESCRIPTION
Opens the lock with no sharing, creating the folder and the file when missing. While another install holds it, tries
again once a second for up to GODOT_MCP_INSTALL_LOCK_WAIT_S seconds (60 when unset), then refuses. Returns the open
stream; disposing it releases the lock.

.PARAMETER InstallDir
The install folder, as a full path.
#>
function Enter-InstallLock {
    [OutputType([System.IO.FileStream])]
    param([Parameter(Mandatory)] [string]$InstallDir)
    $lock = Join-Path $InstallDir 'install.lock'
    $deadline = [DateTime]::UtcNow.AddSeconds((Get-LockWaitSecond))
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    while ($true) {
        try {
            return New-Object System.IO.FileStream $lock, 'OpenOrCreate', 'ReadWrite', 'None'
        }
        catch {
            if (-not (Test-IOException -Exception $_.Exception)) {
                throw
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "install: another install holds $lock; try again once it ends"
            }
            Start-Sleep -Seconds 1
        }
    }
}

<#
.SYNOPSIS
The version folder current names, or '' when there is no current.

.PARAMETER InstallDir
The install folder, as a full path.
#>
function Get-CurrentVersion {
    [OutputType([string])]
    param([Parameter(Mandatory)] [string]$InstallDir)
    $current = Join-Path $InstallDir 'current'
    if (-not (Test-Path -LiteralPath $current -PathType Leaf)) {
        return ''
    }
    $lines = @(Get-Content -LiteralPath $current -TotalCount 1)
    if ($lines.Count -eq 0) {
        return ''
    }
    return ([string]$lines[0]).Trim()
}

<#
.SYNOPSIS
Points current at a version folder in one step, so a launcher reading it sees the old name or the new one, whole.

.DESCRIPTION
Writes current.tmp-<guid>, then renames it over current with MoveFileEx's replace (a plain rename when there is no
current yet). A replace that fails, as one can while an indexer or antivirus holds current, is tried again up to 20
times, 100 ms apart; then the install fails with the old current standing.

.PARAMETER InstallDir
The install folder, as a full path.

.PARAMETER Name
The version folder's name.
#>
function Set-CurrentVersion {
    param(
        [Parameter(Mandatory)] [string]$InstallDir,
        [Parameter(Mandatory)] [string]$Name
    )
    $current = Join-Path $InstallDir 'current'
    $staged = Join-Path $InstallDir ('current.tmp-' + [guid]::NewGuid().ToString('N'))
    # One line, LF, no BOM (WriteAllText's default encoding is UTF-8 without one).
    [System.IO.File]::WriteAllText($staged, "$Name`n")
    for ($attempt = 1; ; $attempt++) {
        try {
            Move-StagedPointer -Staged $staged -Current $current
            return
        }
        catch {
            if ($attempt -ge 20) {
                [System.IO.File]::Delete($staged)
                throw "install: pointing $current at $Name failed: $($_.Exception.Message) It still names the version it named before."
            }
            Start-Sleep -Milliseconds 100
        }
    }
}

# Renames the staged pointer over current in one step: File.Move with overwrite under PowerShell 7; under Windows
# PowerShell 5.1, whose File.Move cannot overwrite, MoveFileEx with MOVEFILE_REPLACE_EXISTING
# (https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-movefileexw). File.Replace is no substitute: a reader
# racing it was measured to find current missing (error 2) or held (error 32), where both of these never were.
function Move-StagedPointer {
    param(
        [Parameter(Mandatory)] [string]$Staged,
        [Parameter(Mandatory)] [string]$Current
    )
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        [System.IO.File]::Move($Staged, $Current, $true)
        return
    }
    if (-not ('GodotMcpInstall.NativeMethods' -as [type])) {
        $signature = '[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] ' +
            'public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);'
        Add-Type -Namespace GodotMcpInstall -Name NativeMethods -MemberDefinition $signature
    }
    $replaceExisting = 1
    if (-not [GodotMcpInstall.NativeMethods]::MoveFileEx($Staged, $Current, $replaceExisting)) {
        throw (New-Object System.ComponentModel.Win32Exception ([System.Runtime.InteropServices.Marshal]::GetLastWin32Error()))
    }
}

# A version folder's name: the version with + turned into -, refused when it could name anything but one plain folder.
function Get-VersionFolderBase {
    param([Parameter(Mandatory)] [string]$Version)
    $base = $Version.Trim() -replace '\+', '-'
    if ($base -notmatch '^[0-9A-Za-z][0-9A-Za-z.\-]*$') {
        throw "install: the version '$Version' cannot name a version folder; it needs letters, digits, '.', '-' and '+' only."
    }
    return $base
}

<#
.SYNOPSIS
Copies a server tree into a new version folder and returns its path.

.DESCRIPTION
The folder is versions\<version with + turned into ->, or that name with .2, .3, ... when it is taken: a folder is
never reused. Everything in -Source but the -Exclude names is copied in, then a VERSION file holding -Version.

.PARAMETER InstallDir
The install folder, as a full path.

.PARAMETER Version
The build's version, <semver>+<sha>.

.PARAMETER Source
The server tree to copy.

.PARAMETER Exclude
Names in -Source that are not copied.
#>
function New-VersionFolder {
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string]$InstallDir,
        [Parameter(Mandatory)] [string]$Version,
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string[]]$Exclude
    )
    $base = Get-VersionFolderBase -Version $Version
    $versions = Join-Path $InstallDir 'versions'
    $folder = Join-Path $versions $base
    for ($suffix = 2; Test-Path -LiteralPath $folder; $suffix++) {
        $folder = Join-Path $versions "$base.$suffix"
    }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $Source -Force | Where-Object { $Exclude -notcontains $_.Name })) {
        Copy-Item -LiteralPath $item.FullName -Destination $folder -Recurse
    }
    [System.IO.File]::WriteAllText((Join-Path $folder 'VERSION'), "$($Version.Trim())`n")
    return $folder
}

# Runs the new version's own exe with --list-tools and requires exit 0, so current only ever names a server that starts.
# A version that fails is removed again.
function Assert-VersionStart {
    param([Parameter(Mandatory)] [string]$Folder)
    $exe = Join-Path $Folder 'godot-mcp.exe'
    try {
        $null = & $exe --list-tools
        $status = $LASTEXITCODE
    }
    catch {
        $null = Remove-VersionFolder -Folder $Folder
        throw "install: the new version's start check could not run `"$exe`" --list-tools: $($_.Exception.Message) The installed version is unchanged."
    }
    if ($status -ne 0) {
        $null = Remove-VersionFolder -Folder $Folder
        throw "install: the new version's start check failed: `"$exe`" --list-tools exited with status $status. The installed version is unchanged."
    }
}

<#
.SYNOPSIS
Removes a version folder if nothing runs from it, never deleting a folder in use.

.DESCRIPTION
Renames the folder to versions\.trash-<guid> first, which fails while any process runs from it or holds a file in it;
then the folder is left whole for a later install. Only the renamed folder is deleted recursively, and a delete that
fails leaves it for a later install too. Returns whether the folder is gone.

.PARAMETER Folder
The version folder, or a .trash- folder an earlier install left.
#>
function Remove-VersionFolder {
    [OutputType([bool])]
    param([Parameter(Mandatory)] [string]$Folder)
    $trash = Join-Path (Split-Path -Parent $Folder) ('.trash-' + [guid]::NewGuid().ToString('N'))
    try {
        [System.IO.Directory]::Move($Folder, $trash)
    }
    catch {
        Write-Verbose "Kept $Folder for a later install: it is in use ($($_.Exception.Message))."
        return $false
    }
    try {
        [System.IO.Directory]::Delete($trash, $true)
        return $true
    }
    catch {
        Write-Verbose "Left $trash for a later install: $($_.Exception.Message)"
        return $false
    }
}

<#
.SYNOPSIS
Removes every version folder but the ones kept, and the trash earlier installs left, skipping any still in use.

.PARAMETER InstallDir
The install folder, as a full path.

.PARAMETER Keep
The names of the version folders to keep: the current one and the previous one.
#>
function Remove-OldVersion {
    param(
        [Parameter(Mandatory)] [string]$InstallDir,
        [Parameter(Mandatory)] [AllowEmptyString()] [string[]]$Keep
    )
    $versions = Join-Path $InstallDir 'versions'
    foreach ($folder in @(Get-ChildItem -LiteralPath $versions -Directory -Force)) {
        if ($Keep -notcontains $folder.Name) {
            $null = Remove-VersionFolder -Folder $folder.FullName
        }
    }
}

# Removes one entry of a flat install: a link as a link, a file, or a folder renamed into versions\.trash-<guid> first.
function Remove-FlatEntry {
    param(
        [Parameter(Mandatory)] [string]$InstallDir,
        [Parameter(Mandatory)] [System.IO.FileSystemInfo]$Entry
    )
    $isFolder = ($Entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0
    $isLink = ($Entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    if (-not $isFolder) {
        [System.IO.File]::Delete($Entry.FullName)
    }
    elseif ($isLink) {
        [System.IO.Directory]::Delete($Entry.FullName)
    }
    else {
        $trash = Join-Path (Join-Path $InstallDir 'versions') ('.trash-' + [guid]::NewGuid().ToString('N'))
        [System.IO.Directory]::Move($Entry.FullName, $trash)
        [System.IO.Directory]::Delete($trash, $true)
    }
}

<#
.SYNOPSIS
Turns a flat install (the server's files straight in the install folder, no current) into the start of the layout.

.DESCRIPTION
Does nothing when current exists. Otherwise stops the godot-mcp.exe processes running as <InstallDir>\godot-mcp.exe,
printing a line for each, then removes every entry but versions, current and install.lock.

.PARAMETER InstallDir
The install folder, as a full path.
#>
function Convert-FlatInstall {
    param([Parameter(Mandatory)] [string]$InstallDir)
    if (Test-Path -LiteralPath (Join-Path $InstallDir 'current') -PathType Leaf) {
        return
    }
    $null = Stop-InstalledServer -InstallDir $InstallDir
    New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir 'versions') | Out-Null
    foreach ($entry in @(Get-ChildItem -LiteralPath $InstallDir -Force)) {
        if (@('versions', 'current', 'install.lock') -contains $entry.Name) {
            continue
        }
        try {
            Remove-FlatEntry -InstallDir $InstallDir -Entry $entry
        }
        catch {
            throw "install: removing $($entry.FullName) from the old install failed: $($_.Exception.Message) A godot-mcp.exe still " +
                'running from it holds its files: stop the Claude sessions that use it, then run the installer again.'
        }
    }
}

# Deletes the launchers earlier installs renamed aside; one still running is left for a later install.
function Remove-OldLauncher {
    param([Parameter(Mandatory)] [string]$InstallDir)
    foreach ($old in @(Get-ChildItem -LiteralPath $InstallDir -Filter 'godot-mcp.exe.old-*' -File -Force)) {
        try {
            [System.IO.File]::Delete($old.FullName)
        }
        catch {
            Write-Verbose "Left $($old.FullName) for a later install: $($_.Exception.Message)"
        }
    }
}

<#
.SYNOPSIS
Puts the launcher at <InstallDir>\godot-mcp.exe, renaming a different one aside first.

.DESCRIPTION
Leaves an identical launcher alone. A different one is renamed to godot-mcp.exe.old-<guid>, which works while it runs,
and the new one copied in. Then deletes the renamed launchers that no longer run.

.PARAMETER InstallDir
The install folder, as a full path.

.PARAMETER Source
The launcher to install.
#>
function Install-Launcher {
    param(
        [Parameter(Mandatory)] [string]$InstallDir,
        [Parameter(Mandatory)] [string]$Source
    )
    $launcher = Join-Path $InstallDir 'godot-mcp.exe'
    $installed = Test-Path -LiteralPath $launcher -PathType Leaf
    if ($installed -and (Get-FileHash -LiteralPath $launcher).Hash -eq (Get-FileHash -LiteralPath $Source).Hash) {
        Remove-OldLauncher -InstallDir $InstallDir
        return
    }
    if ($installed) {
        [System.IO.File]::Move($launcher, "$launcher.old-$([guid]::NewGuid().ToString('N'))")
    }
    Copy-Item -LiteralPath $Source -Destination $launcher
    Remove-OldLauncher -InstallDir $InstallDir
}

<#
.SYNOPSIS
Installs a server tree as a new version and makes it current. Call it holding the install lock.

.DESCRIPTION
Migrates a flat install, copies the tree into a new version folder, runs its start check, points current at it, copies
its VERSION beside the launcher and installs the launcher. Returns {Folder, Name, Previous}: the new version folder, its
name, and the name current held before ('' for none).

.PARAMETER InstallDir
The install folder, as a full path.

.PARAMETER Version
The build's version, <semver>+<sha>.

.PARAMETER Source
The server tree to copy.

.PARAMETER Exclude
Names in -Source that are not copied into the version folder.

.PARAMETER Launcher
The launcher exe to install as <InstallDir>\godot-mcp.exe.
#>
function Install-Version {
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string]$InstallDir,
        [Parameter(Mandatory)] [string]$Version,
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string[]]$Exclude,
        [Parameter(Mandatory)] [string]$Launcher
    )
    Convert-FlatInstall -InstallDir $InstallDir
    $previous = Get-CurrentVersion -InstallDir $InstallDir
    $folder = New-VersionFolder -InstallDir $InstallDir -Version $Version -Source $Source -Exclude $Exclude
    Assert-VersionStart -Folder $folder
    $name = Split-Path -Leaf $folder
    Set-CurrentVersion -InstallDir $InstallDir -Name $name
    Copy-Item -LiteralPath (Join-Path $folder 'VERSION') -Destination (Join-Path $InstallDir 'VERSION') -Force
    Install-Launcher -InstallDir $InstallDir -Source $Launcher
    return [pscustomobject]@{ Folder = $folder; Name = $name; Previous = $previous }
}

# The version a version folder holds: the first line of its VERSION, else the folder's name.
function Get-FolderVersion {
    param([Parameter(Mandatory)] [string]$Folder)
    $file = Join-Path $Folder 'VERSION'
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        $lines = @(Get-Content -LiteralPath $file -TotalCount 1)
        if ($lines.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($lines[0])) {
            return ([string]$lines[0]).Trim()
        }
    }
    return Split-Path -Leaf $Folder
}

function Format-OlderSession {
    param(
        [Parameter(Mandatory)] [pscustomobject]$Server,
        [Parameter(Mandatory)] [string]$OldVersion,
        [Parameter(Mandatory)] [string]$NewVersion
    )
    $session = "an unknown client's server (pid $($Server.ProcessId))"
    if ($null -ne $Server.SessionId) {
        $session = "Claude session $($Server.SessionId)"
        if ($null -ne $Server.Project) {
            $session += " ($($Server.Project))"
        }
    }
    return "install: $session still runs $OldVersion; /mcp it to use $NewVersion"
}

<#
.SYNOPSIS
Names every Claude session whose server still runs a version other than the current one, so it can be /mcp'd.

.PARAMETER InstallDir
The install folder, as a full path.

.PARAMETER Current
The current version folder's name.

.PARAMETER NewVersion
The version just installed.
#>
function Write-OlderSession {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSAvoidUsingWriteHost', '', Justification = 'An install step: its lines are for the person running the install.')]
    param(
        [Parameter(Mandatory)] [string]$InstallDir,
        [Parameter(Mandatory)] [string]$Current,
        [Parameter(Mandatory)] [string]$NewVersion
    )
    $older = @(Get-InstalledServer -InstallDir $InstallDir | Where-Object { $_.Kind -eq 'Versioned' -and $_.Version -ne $Current })
    foreach ($server in $older) {
        $oldVersion = Get-FolderVersion -Folder (Join-Path (Join-Path $InstallDir 'versions') $server.Version)
        Write-Host (Format-OlderSession -Server $server -OldVersion $oldVersion -NewVersion $NewVersion)
    }
}
