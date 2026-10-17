#requires -Version 7

<#
.SYNOPSIS
Copies the agent skills and docs/TOOLS.md into a published godot-mcp.

.DESCRIPTION
Run by `pwsh run.ps1 publish` after the server's publish. For each skill, replaces <Destination>\skill (godot-mcp) or
<Destination>\agent-sweep-skill (godot-agent-sweep) with a copy of its folder under <Root>\skills, so a released build
carries the same skill a from-source install links. Then writes <Root>\docs\TOOLS.md to <Destination>\skill\TOOLS.md,
where the skill beside it names it relatively, with each link to a sibling doc (](./<Name>.md, an anchor kept) rewritten
to its copy on GitHub, since no docs folder travels with the skill.

Refuses, with a one-line message on stderr and status 1, before copying anything: a skill folder without SKILL.md, a
missing docs/TOOLS.md, or any Markdown file it would ship that holds an absolute Windows drive path (X:\ or C:/), which
names a folder on the machine that built it rather than on the machine that runs it. Every shipped file is checked, and
the first such path in each is reported. Files are written UTF-8 without a BOM and with LF line endings.

.PARAMETER Root
The checkout whose skills/ and docs/TOOLS.md are copied.

.PARAMETER Destination
The publish folder, normally <Root>\bin\publish, that takes the copies as skill/ and agent-sweep-skill/.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Root,
    [Parameter(Mandatory)] [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The skills that ship, in the order they are copied: each one's folder under skills/ and the name it takes under
# -Destination, where the install folder's mirror and the release zip both take it from.
$skills = [ordered]@{
    'godot-mcp'         = 'skill'
    'godot-agent-sweep' = 'agent-sweep-skill'
}

# An absolute Windows drive path: a letter, a colon and a slash not preceded by a letter or digit, so an http:// URL and
# a bare a:b are not hits. The characters after a hit that its refusal quotes, enough to tell one path from another.
$absolutePathPattern = '(?i)(?<![A-Za-z0-9])[A-Z]:[\\/]'
$quotedTail = 30

function Exit-CopySkills {
    param([Parameter(Mandatory)] [string]$Message)
    [Console]::Error.WriteLine($Message)
    exit 1
}

# TOOLS.md's links to its sibling docs (](./<Name>.md, an optional #anchor kept) rewritten to their GitHub copy.
function ConvertTo-ShippedTools {
    param([Parameter(Mandatory)] [string]$Text)
    return $Text -replace '\]\(\./([\w.-]+\.md)(#[^)]*)?\)', '](https://github.com/leftos/godot-mcp/blob/main/docs/$1$2)'
}

# The Markdown a publish ships, each with the path its refusal names and the text it ships: every .md under a skill
# folder, then docs/TOOLS.md as rewritten.
function Get-ShippedMarkdown {
    param(
        [Parameter(Mandatory)] [string]$Checkout,
        [Parameter(Mandatory)] [string]$ToolsText
    )
    $files = @()
    foreach ($name in $skills.Keys) {
        $folder = Join-Path $Checkout "skills/$name"
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.md' -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($folder, $file.FullName).Replace('\', '/')
            $files += [pscustomobject]@{ Path = "skills/$name/$relative"; Text = [IO.File]::ReadAllText($file.FullName) }
        }
    }
    $files += [pscustomobject]@{ Path = 'docs/TOOLS.md'; Text = $ToolsText }
    return $files
}

# One refusal line for the first absolute drive path in each file that holds one, or nothing when every file is clean.
function Get-AbsolutePathRefusals {
    param([Parameter(Mandatory)] [object[]]$Files)
    $refusals = @()
    foreach ($file in $Files) {
        $lines = $file.Text -split "`r?`n"
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $match = [regex]::Match($lines[$index], $absolutePathPattern)
            if (-not $match.Success) {
                continue
            }
            $length = [Math]::Min($match.Length + $quotedTail, $lines[$index].Length - $match.Index)
            $found = $lines[$index].Substring($match.Index, $length)
            $refusals += "copy-skills: $($file.Path):$($index + 1): an absolute path ($found) ships with the skill; name it relatively or by URL."
            break
        }
    }
    return $refusals
}

foreach ($name in $skills.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root "skills/$name/SKILL.md") -PathType Leaf)) {
        Exit-CopySkills "copy-skills: skills/$name/SKILL.md is missing."
    }
}
$toolsSource = Join-Path $Root 'docs/TOOLS.md'
if (-not (Test-Path -LiteralPath $toolsSource -PathType Leaf)) {
    Exit-CopySkills 'copy-skills: docs/TOOLS.md is missing.'
}

$toolsText = ConvertTo-ShippedTools -Text ([IO.File]::ReadAllText($toolsSource))
$files = @(Get-ShippedMarkdown -Checkout $Root -ToolsText $toolsText)
$refusals = @(Get-AbsolutePathRefusals -Files $files)
if ($refusals.Count -gt 0) {
    foreach ($refusal in $refusals) {
        [Console]::Error.WriteLine($refusal)
    }
    exit 1
}

foreach ($name in $skills.Keys) {
    $target = Join-Path $Destination $skills[$name]
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root "skills/$name") -Destination $target -Recurse
}

$shipped = $toolsText.Replace("`r`n", "`n")
[IO.File]::WriteAllText((Join-Path $Destination 'skill/TOOLS.md'), $shipped, [Text.UTF8Encoding]::new($false))
