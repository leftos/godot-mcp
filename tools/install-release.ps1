<#
.SYNOPSIS
Installs a released godot-mcp: the server into an install folder and the agent skill into the Claude skills folder.

.DESCRIPTION
Published with every release as the asset install.ps1, and meant to run straight from the download:

  & ([scriptblock]::Create((irm https://github.com/leftos/godot-mcp/releases/latest/download/install.ps1))) -InstallDotNet

It runs under Windows PowerShell 5.1 and PowerShell 7, and depends on no file beside it.

1. Checks for the .NET 10 runtime (Microsoft.NETCore.App 10.*) where the framework-dependent godot-mcp.exe looks for
   it: DOTNET_ROOT_X64, else DOTNET_ROOT, else the registered install location, else %ProgramFiles%\dotnet. When it is
   missing it installs it through winget with -InstallDotNet, asks first when a person is at the console, and
   otherwise stops.
2. Downloads the release zip (or takes -ZipPath) and checks it holds a godot-mcp release.
3. Stops every godot-mcp.exe running from the install folder, replaces the folder's contents with the zip's, and
   replaces <SkillsDir>\godot-mcp with the zip's skill/ folder. A link there (the junction a from-source install
   makes) is removed as a link: the folder it points to is never touched.
4. Prints where the server and the skill are, and the command that registers the server with Claude Code.

Every failure stops the installer with a message naming what failed and what to do.

.PARAMETER Version
The release to install, X.Y.Z (a leading v is accepted). The latest release when omitted.

.PARAMETER ZipPath
A release zip on disk to install instead of downloading one.

.PARAMETER InstallDotNet
Install the .NET 10 runtime through winget without asking when it is missing.

.PARAMETER InstallDir
The folder the server is installed into. GODOT_MCP_INSTALL_DIR when omitted, else %LOCALAPPDATA%\godot-mcp. A folder
that holds files but no godot-mcp.exe or VERSION is refused, never emptied.

.PARAMETER SkillsDir
The folder the godot-mcp skill folder is installed into. GODOT_MCP_SKILLS_DIR when omitted, else ~/.claude/skills.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'A console install script: its lines are for the person running it.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helpers of an installer; it takes no -WhatIf.')]
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$ZipPath = '',
    [switch]$InstallDotNet,
    [string]$InstallDir = '',
    [string]$SkillsDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 redraws a download's progress bar per chunk, which slows the download many times over.
$ProgressPreference = 'SilentlyContinue'

$repository = 'https://github.com/leftos/godot-mcp'
$assetName = 'godot-mcp-win-x64.zip'
$manualInstall = 'install the .NET Runtime 10 from https://dotnet.microsoft.com/download/dotnet/10.0'
# The winget package of the .NET 10 runtime: https://github.com/microsoft/winget-pkgs/tree/master/manifests/m/Microsoft/DotNet/Runtime/10
$wingetId = 'Microsoft.DotNet.Runtime.10'

# A path made absolute against the current location (not the process's working directory, which Windows PowerShell
# does not keep in step with it), without a trailing separator.
function Get-FullPath {
    param([Parameter(Mandatory)] [string]$Path)
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path).TrimEnd('\', '/')
}

# The folder a parameter names, else the one an environment variable names, else the default.
function Resolve-Folder {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Explicit,
        [Parameter(Mandatory)] [string]$Variable,
        [Parameter(Mandatory)] [string]$Default
    )
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        return Get-FullPath $Explicit
    }
    $configured = [Environment]::GetEnvironmentVariable($Variable)
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        return Get-FullPath $configured
    }
    return Get-FullPath $Default
}

# The x64 install location the .NET installer registers, read from the 32-bit registry view where it writes it.
function Get-RegisteredDotNetRoot {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry32')
    $key = $base.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
    if ($null -eq $key) {
        return ''
    }
    return [string]$key.GetValue('InstallLocation')
}

# Where an x64 apphost looks for .NET, in its order, the first location set winning: the architecture's DOTNET_ROOT_X64,
# then DOTNET_ROOT
# (https://learn.microsoft.com/dotnet/core/tools/dotnet-environment-variables#dotnet_root-dotnet_rootx86-dotnet_root_x86-dotnet_root_x64),
# then the registered install location, then %ProgramFiles%\dotnet
# (https://learn.microsoft.com/dotnet/core/runtime-discovery/troubleshoot-app-launch#check-the-install-location).
function Get-DotNetRoot {
    foreach ($name in @('DOTNET_ROOT_X64', 'DOTNET_ROOT')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [pscustomobject]@{ Path = $value; Source = $name }
        }
    }
    $registered = Get-RegisteredDotNetRoot
    if (-not [string]::IsNullOrWhiteSpace($registered)) {
        return [pscustomobject]@{ Path = $registered; Source = 'the registered install location' }
    }
    # ProgramW6432 is the 64-bit Program Files even from a 32-bit shell, whose ProgramFiles is the x86 one.
    $programFiles = $env:ProgramW6432
    if ([string]::IsNullOrWhiteSpace($programFiles)) {
        $programFiles = $env:ProgramFiles
    }
    return [pscustomobject]@{ Path = (Join-Path $programFiles 'dotnet'); Source = 'the default install location' }
}

function Test-DotNetRuntime {
    param([Parameter(Mandatory)] [string]$Root)
    $shared = Join-Path $Root 'shared\Microsoft.NETCore.App'
    if (-not (Test-Path -LiteralPath $shared -PathType Container)) {
        return $false
    }
    $versions = @(Get-ChildItem -LiteralPath $shared -Directory | Where-Object { $_.Name -like '10.*' })
    return $versions.Count -gt 0
}

function Test-ConsoleUser {
    return [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
}

function Install-DotNetRuntime {
    if ($null -eq (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "install: winget is not available to install the .NET 10 runtime. $manualInstall, then run the installer again."
    }
    Write-Host "install: installing the .NET 10 runtime with winget ($wingetId)"
    & winget install --id $wingetId --exact --source winget --silent --accept-package-agreements --accept-source-agreements | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "install: winget install $wingetId failed with status $LASTEXITCODE. $manualInstall, then run the installer again."
    }
}

# Makes sure the runtime godot-mcp.exe needs is where it will look, installing it when allowed.
function Confirm-DotNetRuntime {
    param([switch]$Install)
    $location = Get-DotNetRoot
    if (Test-DotNetRuntime -Root $location.Path) {
        Write-Host "install: .NET 10 runtime found in $($location.Path)"
        return
    }
    $missing = "install: the .NET 10 runtime (Microsoft.NETCore.App 10.*) is missing from $($location.Path), " +
        "where godot-mcp.exe looks for it ($($location.Source))."
    if (-not $Install) {
        if (-not (Test-ConsoleUser)) {
            throw "$missing Run the installer again with -InstallDotNet to install it through winget, or $manualInstall."
        }
        Write-Host $missing
        $answer = Read-Host 'Install the .NET 10 runtime with winget now? [y/N]'
        if ($answer -notmatch '^\s*(y|yes)\s*$') {
            throw "install: stopped. $manualInstall, or run the installer again with -InstallDotNet."
        }
    }
    Install-DotNetRuntime
    $location = Get-DotNetRoot
    if (-not (Test-DotNetRuntime -Root $location.Path)) {
        throw "install: winget installed $wingetId, but Microsoft.NETCore.App 10.* is still missing from $($location.Path) " +
            "($($location.Source)). If DOTNET_ROOT or DOTNET_ROOT_X64 points elsewhere, clear it or install the runtime there."
    }
}

function Get-ReleaseUrl {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Wanted)
    if ([string]::IsNullOrWhiteSpace($Wanted)) {
        return "$repository/releases/latest/download/$assetName"
    }
    if ($Wanted -notmatch '^v?(\d+\.\d+\.\d+)$') {
        throw "install: -Version '$Wanted' is not a version; give one as X.Y.Z, e.g. 0.3.2 (the releases are listed at $repository/releases)."
    }
    return "$repository/releases/download/v$($Matches[1])/$assetName"
}

# The zip to install: -ZipPath when given, else the release's asset downloaded into the work folder.
function Get-ReleaseZip {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Wanted,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$LocalZip,
        [Parameter(Mandatory)] [string]$WorkDir
    )
    if (-not [string]::IsNullOrWhiteSpace($LocalZip)) {
        $full = Get-FullPath $LocalZip
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "install: -ZipPath $full does not exist."
        }
        return $full
    }
    $url = Get-ReleaseUrl -Wanted $Wanted
    $target = Join-Path $WorkDir $assetName
    # Windows PowerShell 5.1 on an older .NET Framework offers TLS 1.0 only unless asked; GitHub requires 1.2.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-Host "install: downloading $url"
    try {
        Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
    }
    catch {
        throw "install: downloading $url failed: $($_.Exception.Message) Check that the release and its $assetName exist at $repository/releases."
    }
    return $target
}

# Unpacks the zip into an empty folder and checks it holds a godot-mcp release.
function Expand-ReleaseZip {
    param(
        [Parameter(Mandatory)] [string]$Zip,
        [Parameter(Mandatory)] [string]$Destination
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Destination)
    }
    catch {
        throw "install: $Zip could not be unpacked: $($_.Exception.Message) Download the release again."
    }
    foreach ($entry in @('godot-mcp.exe', 'godot-mcp.dll', 'VERSION', 'bridge', 'skill\SKILL.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Destination $entry))) {
            throw "install: $Zip is not a godot-mcp release: it has no $($entry -replace '\\', '/')."
        }
    }
}

# Removes a file or a folder; a link (a junction or a symbolic link) is removed as a link, never followed, so the folder
# it points to keeps everything in it. The attributes are read from the path itself, so a link whose target is gone
# still counts as existing, and a missing path as -1.
function Remove-Entry {
    param([Parameter(Mandatory)] [string]$Path)
    $info = New-Object System.IO.DirectoryInfo $Path
    if ([int]$info.Attributes -eq -1) {
        return
    }
    $isFolder = ($info.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0
    if (($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($isFolder) {
            [System.IO.Directory]::Delete($Path)
        }
        else {
            [System.IO.File]::Delete($Path)
        }
        Write-Host "install: removed the link at $Path; the folder it pointed to is left alone"
        return
    }
    Remove-Item -LiteralPath $Path -Recurse:$isFolder -Force
}

# Stops every godot-mcp.exe whose image is inside the folder, and waits up to 10 s for each to exit. Win32_Process gives
# the path without the access checks Get-Process's Path needs under Windows PowerShell.
function Stop-InstalledServer {
    param([Parameter(Mandatory)] [string]$Folder)
    $prefix = $Folder + '\'
    $running = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'godot-mcp.exe'" |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($process in $running) {
        Write-Host "install: stopping godot-mcp.exe (pid $($process.ProcessId)) running from $Folder"
        Stop-Process -Id $process.ProcessId -Force
        $handle = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $handle) {
            [void]$handle.WaitForExit(10000)
        }
    }
}

# Replaces the install folder's contents with the unpacked release, the skill folder aside.
function Install-Server {
    param(
        [Parameter(Mandatory)] [string]$Unpacked,
        [Parameter(Mandatory)] [string]$Folder
    )
    if (Test-Path -LiteralPath $Folder -PathType Leaf) {
        throw "install: $Folder is a file, not a folder. Choose another -InstallDir."
    }
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    $existing = @(Get-ChildItem -LiteralPath $Folder -Force)
    $isInstall = (Test-Path -LiteralPath (Join-Path $Folder 'godot-mcp.exe')) -or (Test-Path -LiteralPath (Join-Path $Folder 'VERSION'))
    if ($existing.Count -gt 0 -and -not $isInstall) {
        throw "install: $Folder holds files but no godot-mcp.exe or VERSION, so it is not a godot-mcp install; it is left alone. " +
            'Choose another -InstallDir.'
    }
    Stop-InstalledServer -Folder $Folder
    try {
        foreach ($item in $existing) {
            Remove-Entry -Path $item.FullName
        }
    }
    catch {
        throw "install: emptying $Folder failed: $($_.Exception.Message) A godot-mcp.exe still running from it holds its files: " +
            'stop the Claude sessions that use it, then run the installer again.'
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $Unpacked -Force | Where-Object { $_.Name -ne 'skill' })) {
        Copy-Item -LiteralPath $item.FullName -Destination $Folder -Recurse
    }
}

# Replaces <Folder>\godot-mcp with the unpacked skill folder and returns its path.
function Install-Skill {
    param(
        [Parameter(Mandatory)] [string]$Unpacked,
        [Parameter(Mandatory)] [string]$Folder
    )
    $skill = Join-Path $Folder 'godot-mcp'
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    Remove-Entry -Path $skill
    Copy-Item -LiteralPath (Join-Path $Unpacked 'skill') -Destination $skill -Recurse
    return $skill
}

function Remove-WorkFolder {
    param([Parameter(Mandatory)] [string]$Folder)
    try {
        Remove-Item -LiteralPath $Folder -Recurse -Force
    }
    catch {
        Write-Warning "install: could not remove the temporary folder $Folder ($($_.Exception.Message)); delete it by hand."
    }
}

function Invoke-ReleaseInstall {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Wanted,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$LocalZip,
        [Parameter(Mandatory)] [bool]$AllowDotNetInstall,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$ServerDir,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$SkillDir
    )
    $serverFolder = Resolve-Folder -Explicit $ServerDir -Variable 'GODOT_MCP_INSTALL_DIR' -Default (Join-Path $env:LOCALAPPDATA 'godot-mcp')
    $skillsFolder = Resolve-Folder -Explicit $SkillDir -Variable 'GODOT_MCP_SKILLS_DIR' -Default (Join-Path $HOME '.claude\skills')
    Confirm-DotNetRuntime -Install:$AllowDotNetInstall
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ('godot-mcp-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        $zip = Get-ReleaseZip -Wanted $Wanted -LocalZip $LocalZip -WorkDir $work
        $unpacked = Join-Path $work 'unpacked'
        Expand-ReleaseZip -Zip $zip -Destination $unpacked
        $installed = (Get-Content -LiteralPath (Join-Path $unpacked 'VERSION') -Raw).Trim()
        Install-Server -Unpacked $unpacked -Folder $serverFolder
        $skill = Install-Skill -Unpacked $unpacked -Folder $skillsFolder
    }
    finally {
        Remove-WorkFolder -Folder $work
    }
    $exe = Join-Path $serverFolder 'godot-mcp.exe'
    Write-Host "install: server at $exe (version $installed), skill at $skill"
    Write-Host 'install: to use it in a Godot project, register it from that project''s folder:'
    Write-Host "  claude mcp add godot -s local -e GODOT_PATH=<your Godot console exe> -- `"$exe`""
}

$request = @{
    Wanted = $Version; LocalZip = $ZipPath; AllowDotNetInstall = $InstallDotNet.IsPresent; ServerDir = $InstallDir; SkillDir = $SkillsDir
}
Invoke-ReleaseInstall @request
