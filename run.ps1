#!/usr/bin/env pwsh

<#
.SYNOPSIS
Builds, tests, formats and publishes godot-mcp.

.DESCRIPTION
Every command runs under tools/gate.ps1: its whole output goes to .tmp/<command>.log, the last lines are printed, and it
exits with the command's own status, or 124 when it outlived its ceiling and was killed with its children.

  build    dotnet build GodotMcp.slnx, warnings as errors; ceiling 300 s
  test     the unit tests (tests/GodotMcp.Tests); ceiling 180 s, and the runner's own --timeout 3m
  itest    the integration tests against the real Godot (GODOT_PATH, else F:\Godot\Godot_console.exe), in the class
           groups of the table at the top of this script (lifecycle, input, reads). It first checks that every
           `public sealed class <Name>Tests` in tests/GodotMcp.IntegrationTests is in exactly one group and every listed
           class exists, and stops with status 1 before running anything when not. It then builds the project once
           (.tmp/itest-build.log, ceiling 300 s) and runs each group as its own gate (.tmp/itest-<group>.log, ceiling
           300 s, the runner's own --timeout 4m). Every group runs even when an earlier one fails; a summary line per
           group follows, and the exit status is the first non-zero group's. On Windows every test run (a group's or
           a -Filter one) goes through tools/hidden-desktop.ps1, on a desktop of its own, so no Godot window shows.
  format   dotnet format style (info severity), then CSharpier, on the whole solution; ceiling 180 s each
  dotnet   the C# helper into bin/dotnet: the NativeAOT shim godot_mcp_dotnet.dll (win-x64, no pdb) and
           dotnet/godot_mcp_dotnet.gdextension at the top, loader/ (GodotMcp.Dotnet.Loader.dll and its
           runtimeconfig.json) and helper/ (GodotMcp.Dotnet.dll). Each project publishes into .tmp/dotnet-publish/<name>
           under its own gate (.tmp/dotnet-<name>.log, ceiling 300 s); bin/dotnet is then rebuilt from those files.
           The shim's link needs the MSVC linker (VS Build Tools' VC tools), which ILCompiler finds through vswhere under
           ProgramFiles(x86); when that variable is empty (as it can be from Git Bash) it is set to C:\Program Files (x86).
  publish  a framework-dependent win-x64 server at bin/publish/godot-mcp.exe, with bridge/ beside it, then the dotnet
           command (as above), whose bin/dotnet is copied to bin/publish/dotnet; ceiling 300 s
  install  publish (as above), then mirror bin/publish into $env:LOCALAPPDATA\godot-mcp (robocopy /MIR, no retries), and
           link ~/.claude/skills/godot-mcp to skills/godot-mcp as a directory junction; ceiling 300 s for the publish,
           60 s for the copy. The copy and the link are tools/install.ps1, logged to .tmp/install.log. A junction
           that points elsewhere is replaced; anything else at the link path is refused, never deleted.
           GODOT_MCP_INSTALL_DIR overrides the install folder, GODOT_MCP_SKILLS_DIR the folder the link is made in.
  gdtest   the bridge's GDScript unit tests (tests/bridge/test_*.gd) in headless Godot (GODOT_PATH, else
           F:\Godot\Godot_console.exe). It first imports tests/bridge (godot --headless --path tests/bridge --import,
           .tmp/gdtest-import.log, ceiling 120 s) when a test script is newer than the last import, so the project's
           global class list holds every script's class_name, then runs godot --headless --path tests/bridge --script
           res://run_tests.gd. Each failure prints as "FAIL <file>::<test>: <message>", then
           "gdtest: <passed> passed, <failed> failed"; ceiling 60 s

-Filter narrows test or itest to one test class, e.g. -Filter "*SessionLifecycleTests". A filtered itest skips the
groups: one gate, .tmp/itest.log, ceiling 300 s, --timeout 4m.

.EXAMPLE
pwsh run.ps1 itest -Filter "*McpServerSmokeTests"
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'A console build script: its lines are for the person running it.')]
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'itest', 'format', 'dotnet', 'publish', 'install', 'gdtest', 'help')]
    [string]$Command = 'help',

    [string]$Filter = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The integration test classes, one gate per group, run in this order. A new test class goes into one group; itest
# refuses to run while a class is in no group or a listed class no longer exists.
$itestGroups = [ordered]@{
    lifecycle = @('SessionLifecycleTests', 'AttachTests', 'QuietTests', 'WatchdogTests', 'McpServerSmokeTests', 'ProfileTests')
    input     = @('InputTests', 'GamepadTests', 'CaptureTests')
    reads     = @('RuntimeReadTests', 'InspectionTests', 'BaselineTests', 'PreviewTests')
    time      = @('TimeTests', 'BatchTests')
    prep      = @('PrepTests', 'RestartTests')
    recording = @('RecordingTests')
    headless  = @('HeadlessTests', 'HeadlessSceneTests', 'HeadlessPropertyTests', 'HeadlessSignalTests', 'HeadlessMeshTests', 'HeadlessBatchTests')
}
$itestNamespace = 'GodotMcp.IntegrationTests'
$itestProject = 'tests/GodotMcp.IntegrationTests/GodotMcp.IntegrationTests.csproj'

$root = $PSScriptRoot
$logDir = Join-Path $root '.tmp'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$solution = Join-Path $root 'GodotMcp.slnx'
$gdtestDir = Join-Path $root 'tests/bridge'
$gdtestStamp = Join-Path $gdtestDir '.godot/gdtest-import.stamp'
$dotnetOut = Join-Path $root 'bin/dotnet'
$dotnetStaging = Join-Path $logDir 'dotnet-publish'

$gate = Join-Path $root 'tools/gate.ps1'

# Runs a program under tools/gate.ps1: the whole output to .tmp/<Name>.log, the last 15 lines on the screen, and the
# process tree killed with status 124 when it outlives its ceiling (a run that reaches one has hung, not slowed).
function Invoke-Gated {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string]$Program,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    $log = Join-Path $logDir "$Name.log"
    Write-Host "$Program $($Arguments -join ' ')  (log: $log, ceiling: $TimeoutSeconds s)"
    & $gate -Log $log -TimeoutSeconds $TimeoutSeconds -Tail 15 -- $Program @Arguments | Out-Host
    return $LASTEXITCODE
}

# Runs dotnet under tools/gate.ps1, as Invoke-Gated does.
function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    return Invoke-Gated -Name $Name -TimeoutSeconds $TimeoutSeconds -Program 'dotnet' -Arguments $Arguments
}

# Runs an integration test gate as Invoke-Logged does, on Windows through tools/hidden-desktop.ps1, so the Godot windows
# the tests open appear on a desktop of their own and never on the user's screen.
function Invoke-ItestGated {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    if (-not $IsWindows) {
        return Invoke-Logged -Name $Name -TimeoutSeconds 300 -Arguments $Arguments
    }
    $hidden = @('-NoProfile', '-File', (Join-Path $root 'tools/hidden-desktop.ps1'), '--', 'dotnet') + $Arguments
    return Invoke-Gated -Name $Name -TimeoutSeconds 300 -Program 'pwsh' -Arguments $hidden
}

# The Godot executable as the server finds it (Installation.FindGodot): GODOT_PATH when set, else the default path;
# stops the script when the one it names does not exist.
function Get-GodotPath {
    $configured = $env:GODOT_PATH
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        if (-not (Test-Path -LiteralPath $configured -PathType Leaf)) {
            throw "GODOT_PATH is '$configured', which does not exist. Point it at the Godot 4.7 console executable."
        }
        return $configured
    }
    $default = 'F:\Godot\Godot_console.exe'
    if (-not (Test-Path -LiteralPath $default -PathType Leaf)) {
        throw "Godot was not found: GODOT_PATH is not set and $default does not exist. Set GODOT_PATH to the Godot 4.7 console executable."
    }
    return $default
}

# Whether tests/bridge needs importing: no stamp of an earlier import, or a test script newer than it. The stamp is our
# own file, not the class cache's mtime, because an import that changes no class need not rewrite the cache.
function Test-GdtestImportStale {
    if (-not (Test-Path -LiteralPath $gdtestStamp -PathType Leaf)) {
        return $true
    }
    $stamp = Get-Item -LiteralPath $gdtestStamp
    $scripts = Get-ChildItem -Path $gdtestDir -Filter '*.gd' -File
    $newer = @($scripts | Where-Object { $_.LastWriteTime -gt $stamp.LastWriteTime })
    return $newer.Count -gt 0
}

# Imports tests/bridge when a test script is newer than the last import, so ProjectSettings' global class list (read
# from .godot/global_script_class_cache.cfg) holds every test script's class_name and from_json can find a script class
# by its name. One line and no import when the list is up to date; the stamp is touched after a successful one. Returns
# 0 when the list is up to date or the import succeeded, else the import's own status.
function Invoke-GdtestImport {
    if (-not (Test-GdtestImportStale)) {
        Write-Host 'gdtest: import up to date'
        return 0
    }
    $arguments = @('--headless', '--path', $gdtestDir, '--import')
    $status = Invoke-Gated -Name 'gdtest-import' -TimeoutSeconds 120 -Program (Get-GodotPath) -Arguments $arguments
    if ($status -ne 0) {
        return $status
    }
    Set-Content -LiteralPath $gdtestStamp -Value '' -NoNewline
    return 0
}

# xUnit v3 under Microsoft Testing Platform takes several classes after one --filter-class and runs a test in any of
# them, as the native runner's repeated -class does ("Filters" footnote 1:
# https://xunit.net/docs/getting-started/v3/microsoft-testing-platform).
function Get-TestArgumentList {
    param(
        [Parameter(Mandatory)] [string]$Project,
        [Parameter(Mandatory)] [string]$Timeout,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Classes,
        [switch]$NoBuild
    )
    $arguments = @('test', '--project', (Join-Path $root $Project))
    if ($NoBuild) {
        $arguments += '--no-build'
    }
    $arguments += @('--', '--timeout', $Timeout)
    if ($Classes.Count -gt 0) {
        $arguments += @('--filter-class') + $Classes
    }
    return $arguments
}

# Every test class the integration test project declares: a `public sealed [partial] class <Name>Tests` in one of its
# top-level files (the Fixtures folder holds helpers, not tests).
function Get-ItestClass {
    $pattern = '^\s*public\s+sealed\s+(?:partial\s+)?class\s+(\w+Tests)\b'
    Get-ChildItem -Path (Join-Path $root (Split-Path -Parent $itestProject)) -Filter '*.cs' -File |
        Select-String -Pattern $pattern |
        ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Sort-Object -Unique
}

# How the groups table has drifted from the project, as one message, or '' when every declared class is in exactly one
# group and every listed class is declared.
function Get-ItestGroupDrift {
    $declared = @(Get-ItestClass)
    $listed = @($itestGroups.Values | ForEach-Object { $_ })
    $unlisted = @($declared | Where-Object { $listed -notcontains $_ })
    $missing = @($listed | Where-Object { $declared -notcontains $_ } | Sort-Object -Unique)
    $repeated = @($listed | Group-Object | Where-Object Count -GT 1 | ForEach-Object Name)
    $parts = @()
    if ($unlisted.Count -gt 0) {
        $parts += "$($unlisted.Count) class(es) in no group: $($unlisted -join ', ')"
    }
    if ($missing.Count -gt 0) {
        $parts += "$($missing.Count) listed but missing: $($missing -join ', ')"
    }
    if ($repeated.Count -gt 0) {
        $parts += "$($repeated.Count) listed in more than one group: $($repeated -join ', ')"
    }
    if ($parts.Count -eq 0) {
        return ''
    }
    return "itest groups are out of date: $($parts -join '; '). Edit the groups table in run.ps1."
}

function Write-ItestSummary {
    param([Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary]$Results)
    foreach ($group in $Results.Keys) {
        $verdict = switch ($Results[$group]) {
            0 { 'passed' }
            124 { 'TIMED OUT' }
            default { "FAILED (status $_)" }
        }
        Write-Host "itest ${group}: $verdict"
    }
}

# The whole integration suite: the groups table checked, the project built once, then every group under its own gate,
# each run whatever the one before it did. Returns the first non-zero status, else 0.
function Invoke-ItestByGroup {
    $drift = Get-ItestGroupDrift
    if ($drift) {
        [Console]::Error.WriteLine($drift)
        return 1
    }
    $build = Invoke-Logged -Name 'itest-build' -TimeoutSeconds 300 -Arguments @('build', (Join-Path $root $itestProject), '-warnaserror')
    if ($build -ne 0) {
        return $build
    }
    $results = [ordered]@{}
    foreach ($group in $itestGroups.Keys) {
        $classes = @($itestGroups[$group] | ForEach-Object { "$itestNamespace.$_" })
        $arguments = Get-TestArgumentList -Project $itestProject -Timeout '4m' -Classes $classes -NoBuild
        $results[$group] = Invoke-ItestGated -Name "itest-$group" -Arguments $arguments
    }
    Write-ItestSummary -Results $results
    $failed = @($results.Values | Where-Object { $_ -ne 0 })
    if ($failed.Count -gt 0) {
        return $failed[0]
    }
    return 0
}

# The dotnet arguments of the publish, shared by publish and install.
function Get-PublishArgumentList {
    return @(
        'publish', (Join-Path $root 'src/GodotMcp.Server/GodotMcp.Server.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $root 'bin/publish')
    )
}

# The folder a variable names when it is set, else the default.
function Get-ConfiguredDirectory {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [AllowNull()] [string]$Configured,
        [Parameter(Mandatory)] [string]$Default
    )
    if ([string]::IsNullOrWhiteSpace($Configured)) {
        return $Default
    }
    return $Configured
}

# ILCompiler's findvcvarsall.bat runs "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" to find the
# MSVC linker and never searches PATH, so that variable is set to its default when empty (as it can be under Git Bash).
function Set-ProgramFilesX86 {
    ${env:ProgramFiles(x86)} = Get-ConfiguredDirectory -Configured ${env:ProgramFiles(x86)} -Default 'C:\Program Files (x86)'
}

# The C# helper's projects, each published into .tmp/dotnet-publish/<name>, and the files bin/dotnet takes from it:
# name = project, extra publish arguments, files, and the folder of bin/dotnet they go in ('' for the top).
$dotnetProjects = [ordered]@{
    shim   = @{
        Project = 'src/GodotMcp.Dotnet.Shim/GodotMcp.Dotnet.Shim.csproj'; Extra = @('-r', 'win-x64')
        Files = @('godot_mcp_dotnet.dll'); Folder = ''
    }
    loader = @{
        Project = 'src/GodotMcp.Dotnet.Loader/GodotMcp.Dotnet.Loader.csproj'; Extra = @()
        Files = @('GodotMcp.Dotnet.Loader.dll', 'GodotMcp.Dotnet.Loader.runtimeconfig.json'); Folder = 'loader'
    }
    helper = @{
        Project = 'src/GodotMcp.Dotnet/GodotMcp.Dotnet.csproj'; Extra = @()
        Files = @('GodotMcp.Dotnet.dll'); Folder = 'helper'
    }
}

# Replaces a folder with a copy of another.
function Copy-Folder {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
}

# Rebuilds bin/dotnet from the staged publishes and the tracked .gdextension.
function Copy-DotnetLayout {
    if (Test-Path -LiteralPath $dotnetOut) {
        Remove-Item -LiteralPath $dotnetOut -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $dotnetOut | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'dotnet/godot_mcp_dotnet.gdextension') -Destination $dotnetOut
    foreach ($name in $dotnetProjects.Keys) {
        $project = $dotnetProjects[$name]
        $folder = Join-Path $dotnetOut $project.Folder
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        foreach ($file in $project.Files) {
            Copy-Item -LiteralPath (Join-Path $dotnetStaging $name $file) -Destination $folder
        }
    }
}

# Publishes the shim, the loader and the helper, each under its own gate, then lays out bin/dotnet. Returns the first
# failing publish's status, else 0.
function Invoke-DotnetPublish {
    Set-ProgramFilesX86
    foreach ($name in $dotnetProjects.Keys) {
        $project = $dotnetProjects[$name]
        $arguments = @('publish', (Join-Path $root $project.Project), '-c', 'Release', '-o', (Join-Path $dotnetStaging $name)) + $project.Extra
        $status = Invoke-Logged -Name "dotnet-$name" -TimeoutSeconds 300 -Arguments $arguments
        if ($status -ne 0) {
            return $status
        }
    }
    Copy-DotnetLayout
    Write-Host "dotnet: the helper is in $dotnetOut"
    return 0
}

# Publishes the server into bin/publish, then the C# helper, copying bin/dotnet to bin/publish/dotnet.
function Invoke-Publish {
    $status = Invoke-Logged -Name 'publish' -TimeoutSeconds 300 -Arguments (Get-PublishArgumentList)
    if ($status -ne 0) {
        return $status
    }
    $status = Invoke-DotnetPublish
    if ($status -ne 0) {
        return $status
    }
    Copy-Folder -Source $dotnetOut -Destination (Join-Path $root 'bin/publish/dotnet')
    return 0
}

# Publishes, then runs tools/install.ps1 under its own gate: the mirror into the install folder and the skill junction.
function Invoke-Install {
    $status = Invoke-Publish
    if ($status -ne 0) {
        return $status
    }
    $installDir = Get-ConfiguredDirectory -Configured $env:GODOT_MCP_INSTALL_DIR -Default (Join-Path $env:LOCALAPPDATA 'godot-mcp')
    $skillsDir = Get-ConfiguredDirectory -Configured $env:GODOT_MCP_SKILLS_DIR -Default (Join-Path $HOME '.claude/skills')
    $arguments = @(
        '-NoProfile', '-File', (Join-Path $root 'tools/install.ps1'),
        '-Root', $root, '-InstallDir', $installDir, '-SkillsDir', $skillsDir
    )
    return Invoke-Gated -Name 'install' -TimeoutSeconds 60 -Program 'pwsh' -Arguments $arguments
}

[string[]]$filterClasses = @($Filter | Where-Object { $_ })

switch ($Command) {
    'build' {
        exit (Invoke-Logged -Name 'build' -TimeoutSeconds 300 -Arguments @('build', $solution, '-warnaserror'))
    }
    'test' {
        $arguments = Get-TestArgumentList -Project 'tests/GodotMcp.Tests/GodotMcp.Tests.csproj' -Timeout '3m' -Classes $filterClasses
        exit (Invoke-Logged -Name 'test' -TimeoutSeconds 180 -Arguments $arguments)
    }
    'itest' {
        if ($filterClasses.Count -eq 0) {
            exit (Invoke-ItestByGroup)
        }
        $arguments = Get-TestArgumentList -Project $itestProject -Timeout '4m' -Classes $filterClasses
        exit (Invoke-ItestGated -Name 'itest' -Arguments $arguments)
    }
    'format' {
        $status = Invoke-Logged -Name 'format-style' -TimeoutSeconds 180 -Arguments @('format', 'style', $solution, '--severity', 'info')
        if ($status -ne 0) {
            exit $status
        }
        exit (Invoke-Logged -Name 'format' -TimeoutSeconds 180 -Arguments @('csharpier', 'format', $root))
    }
    'dotnet' {
        exit (Invoke-DotnetPublish)
    }
    'publish' {
        exit (Invoke-Publish)
    }
    'install' {
        exit (Invoke-Install)
    }
    'gdtest' {
        $import = Invoke-GdtestImport
        if ($import -ne 0) {
            exit $import
        }
        $arguments = @('--headless', '--path', $gdtestDir, '--script', 'res://run_tests.gd')
        exit (Invoke-Gated -Name 'gdtest' -TimeoutSeconds 60 -Program (Get-GodotPath) -Arguments $arguments)
    }
    default {
        Get-Help $PSCommandPath -Detailed
    }
}
