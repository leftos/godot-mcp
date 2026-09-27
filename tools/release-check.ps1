#requires -Version 7

<#
.SYNOPSIS
Checks a release tag against the version the build stamps, and writes that version's changelog section as the notes.

.DESCRIPTION
Run by the release workflow before anything is built. Stops with status 1 unless -Tag is vX.Y.Z with X.Y.Z equal to
VersionPrefix in Directory.Build.props, and unless CHANGELOG.md has a "## X.Y.Z - <date>" section with text in it.
Writes that text (the lines after the heading up to the next "## " heading, blank lines at either end dropped) to
-NotesPath, LF, no BOM.

.PARAMETER Tag
The pushed tag, e.g. v0.3.2.

.PARAMETER NotesPath
The file the release notes are written to; its folder is created.

.PARAMETER Root
The checkout holding Directory.Build.props and CHANGELOG.md; the folder above this script when omitted.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'A console build script: its lines are for the person running it.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$NotesPath,
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Exit-Check {
    param([Parameter(Mandatory)] [string]$Message)
    [Console]::Error.WriteLine($Message)
    exit 1
}

function Get-VersionPrefix {
    param([Parameter(Mandatory)] [string]$Checkout)
    $props = Join-Path $Checkout 'Directory.Build.props'
    $node = ([xml](Get-Content -LiteralPath $props -Raw)).SelectSingleNode('//VersionPrefix')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        Exit-Check "release-check: $props has no VersionPrefix."
    }
    return $node.InnerText.Trim()
}

# The lines under "## <Version> - " up to the next "## " heading, without the blank lines at either end; $null when the
# heading is missing.
function Get-ChangelogSection {
    param(
        [Parameter(Mandatory)] [string]$Checkout,
        [Parameter(Mandatory)] [string]$Version
    )
    $lines = @((Get-Content -LiteralPath (Join-Path $Checkout 'CHANGELOG.md') -Raw) -split "`r?`n")
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^## $([regex]::Escape($Version)) - ") {
            $start = $i + 1
            break
        }
    }
    if ($start -lt 0) {
        return $null
    }
    $end = $start
    while ($end -lt $lines.Count -and $lines[$end] -notmatch '^## ') {
        $end++
    }
    $section = if ($end -gt $start) { $lines[$start..($end - 1)] } else { @() }
    return (($section -join "`n").Trim())
}

$prefix = Get-VersionPrefix -Checkout $Root
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') {
    Exit-Check "release-check: the tag '$Tag' is not vX.Y.Z."
}
$version = $Matches[1]
if ($version -ne $prefix) {
    Exit-Check ("release-check: the tag '$Tag' does not match VersionPrefix $prefix in Directory.Build.props; " +
        "tag v$prefix, or bump VersionPrefix first.")
}
$notes = Get-ChangelogSection -Checkout $Root -Version $version
if ($null -eq $notes) {
    Exit-Check "release-check: CHANGELOG.md has no '## $version - <date>' section; add it before tagging."
}
if ($notes.Length -eq 0) {
    Exit-Check "release-check: CHANGELOG.md's '## $version' section is empty; write its notes before tagging."
}
$folder = Split-Path -Parent ([System.IO.Path]::GetFullPath($NotesPath))
New-Item -ItemType Directory -Force -Path $folder | Out-Null
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($NotesPath), "$notes`n")
Write-Host "release-check: $Tag matches VersionPrefix $prefix; its notes ($(@($notes -split "`n").Count) lines) are in $NotesPath"
exit 0
