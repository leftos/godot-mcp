#requires -Version 7

<#
.SYNOPSIS
Zips a published godot-mcp into the download a release carries.

.DESCRIPTION
Run by `pwsh run.ps1 package` after its publish. Empties -OutputDir, then zips <Root>\bin\publish (the exe, bridge/,
headless/, dotnet/) with <Root>\skills\godot-mcp as skill/ and a VERSION file holding the published godot-mcp.dll's
product version (the value tools/install.ps1 writes beside an installed exe) into
<OutputDir>\godot-mcp-<X.Y.Z>-win-x64.zip, X.Y.Z being that version without its +<sha>. Prints the zip's path last.
Stops at the first failure with status 1.

.PARAMETER Root
The checkout whose bin/publish and skill are zipped.

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

$publish = Join-Path $Root 'bin/publish'
$skill = Join-Path $Root 'skills/godot-mcp'
Assert-PublishLayout -Publish $publish
if (-not (Test-Path -LiteralPath (Join-Path $skill 'SKILL.md') -PathType Leaf)) {
    Exit-Package 'package: skills/godot-mcp/SKILL.md is missing.'
}
$version = Get-PublishVersion -Publish $publish

if (Test-Path -LiteralPath $OutputDir) {
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}
$stage = Join-Path $OutputDir 'stage'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse
Copy-Item -LiteralPath $skill -Destination (Join-Path $stage 'skill') -Recurse
# One line, LF, no BOM (WriteAllText's default encoding is UTF-8 without one), as tools/install.ps1 writes it.
[System.IO.File]::WriteAllText((Join-Path $stage 'VERSION'), "$version`n")

$zip = Join-Path $OutputDir "godot-mcp-$(($version -split '\+')[0])-win-x64.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "package: $zip (version $version)"
exit 0
