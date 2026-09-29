#requires -Version 7

<#
.SYNOPSIS
Zips a published godot-mcp into the download a release carries.

.DESCRIPTION
Run by `pwsh run.ps1 package` after its publish. Empties -OutputDir, then zips <Root>\bin\publish (the exe, bridge/,
headless/, dotnet/) with <Root>\skills\godot-mcp as skill/, <Root>\skills\godot-agent-sweep as agent-sweep-skill/ and a
VERSION file holding the published godot-mcp.dll's
product version (the value tools/install.ps1 writes beside an installed exe) into
<OutputDir>\godot-mcp-<X.Y.Z>-win-x64.zip, X.Y.Z being that version without its +<sha>. Beside it, writes the release's
<OutputDir>\install.ps1: tools/install-release.ps1 with tools/InstalledServers.psm1 inlined. Prints the zip's path last.
Stops at the first failure with status 1.

.PARAMETER Root
The checkout whose bin/publish and skills are zipped.

.PARAMETER OutputDir
The folder the zip is written to; everything already in it is removed first.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'A console build script: its lines are for the person running it.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Root,
    [Parameter(Mandatory)] [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Exit-Package {
    param([Parameter(Mandatory)] [string]$Message)
    [Console]::Error.WriteLine($Message)
    exit 1
}

# Every entry the installer and a running server rely on; a publish missing one is refused before anything is zipped.
function Assert-PublishLayout {
    param([Parameter(Mandatory)] [string]$Publish)
    foreach ($file in @('godot-mcp.exe', 'godot-mcp.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Publish $file) -PathType Leaf)) {
            Exit-Package "package: bin/publish/$file is missing; run publish first."
        }
    }
    foreach ($folder in @('bridge', 'headless', 'dotnet')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Publish $folder) -PathType Container)) {
            Exit-Package "package: bin/publish/$folder/ is missing; run publish first."
        }
    }
}

# The published dll's ProductVersion is the assembly's InformationalVersion, <semver>+<7-char sha>, read as
# tools/install.ps1 reads it.
function Get-PublishVersion {
    param([Parameter(Mandatory)] [string]$Publish)
    $version = (Get-Item -LiteralPath (Join-Path $Publish 'godot-mcp.dll')).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        Exit-Package 'package: bin/publish/godot-mcp.dll carries no product version; run publish again.'
    }
    return $version
}

# The release's install.ps1: tools/install-release.ps1 with its InstalledServers region, which imports
# tools/InstalledServers.psm1 from beside it, replaced by the module's text, so the script runs with no file beside it.
# Both are read from beside this script. Written with LF endings and no BOM.
function Write-Installer {
    param([Parameter(Mandatory)] [string]$Destination)
    $script = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'install-release.ps1')) -replace "`r`n", "`n"
    $module = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'InstalledServers.psm1')) -replace "`r`n", "`n"
    $regions = [regex]::Matches($script, '(?ms)^#region InstalledServers\n.*?^#endregion\n')
    if ($regions.Count -ne 1) {
        Exit-Package "package: tools/install-release.ps1 has $($regions.Count) InstalledServers regions; it needs exactly one."
    }
    $region = $regions[0]
    $inlined = "#region InstalledServers`n" + $module.TrimEnd("`n") + "`n#endregion`n"
    $text = $script.Substring(0, $region.Index) + $inlined + $script.Substring($region.Index + $region.Length)
    [System.IO.File]::WriteAllText($Destination, $text)
}

$publish = Join-Path $Root 'bin/publish'
$skill = Join-Path $Root 'skills/godot-mcp'
$sweepSkill = Join-Path $Root 'skills/godot-agent-sweep'
Assert-PublishLayout -Publish $publish
foreach ($folder in @($skill, $sweepSkill)) {
    if (-not (Test-Path -LiteralPath (Join-Path $folder 'SKILL.md') -PathType Leaf)) {
        Exit-Package "package: skills/$(Split-Path -Leaf $folder)/SKILL.md is missing."
    }
}
$version = Get-PublishVersion -Publish $publish

if (Test-Path -LiteralPath $OutputDir) {
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}
$stage = Join-Path $OutputDir 'stage'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse
Copy-Item -LiteralPath $skill -Destination (Join-Path $stage 'skill') -Recurse
Copy-Item -LiteralPath $sweepSkill -Destination (Join-Path $stage 'agent-sweep-skill') -Recurse
# One line, LF, no BOM (WriteAllText's default encoding is UTF-8 without one), as tools/install.ps1 writes it.
[System.IO.File]::WriteAllText((Join-Path $stage 'VERSION'), "$version`n")

$zip = Join-Path $OutputDir "godot-mcp-$(($version -split '\+')[0])-win-x64.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $stage -Recurse -Force
$installer = Join-Path $OutputDir 'install.ps1'
Write-Installer -Destination $installer
Write-Host "package: $installer"
Write-Host "package: $zip (version $version)"
exit 0
