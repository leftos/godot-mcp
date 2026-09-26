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
           group follows, and the exit status is the first non-zero group's.
  format   dotnet format style (info severity), then CSharpier, on the whole solution; ceiling 180 s each
  publish  a framework-dependent win-x64 server at bin/publish/godot-mcp.exe, with bridge/ beside it; ceiling 300 s
  gdtest   the bridge's GDScript unit tests (tests/bridge/test_*.gd) in headless Godot (GODOT_PATH, else
           F:\Godot\Godot_console.exe): godot --headless --path tests/bridge --script res://run_tests.gd. Each failure
           prints as "FAIL <file>::<test>: <message>", then "gdtest: <passed> passed, <failed> failed"; ceiling 60 s

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
    [ValidateSet('build', 'test', 'itest', 'format', 'publish', 'gdtest', 'help')]
    [string]$Command = 'help',

    [string]$Filter = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The integration test classes, one gate per group, run in this order. A new test class goes into one group; itest
# refuses to run while a class is in no group or a listed class no longer exists.
$itestGroups = [ordered]@{
    lifecycle = @('SessionLifecycleTests', 'AttachTests', 'QuietTests', 'WatchdogTests', 'McpServerSmokeTests', 'ProfileTests')
    input     = @('InputTests', 'GamepadTests')
    reads     = @('RuntimeReadTests', 'InspectionTests', 'BaselineTests')
    time      = @('TimeTests')
    prep      = @('PrepTests', 'RestartTests')
}
$itestNamespace = 'GodotMcp.IntegrationTests'
$itestProject = 'tests/GodotMcp.IntegrationTests/GodotMcp.IntegrationTests.csproj'

$root = $PSScriptRoot
$logDir = Join-Path $root '.tmp'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$solution = Join-Path $root 'GodotMcp.slnx'

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
        $results[$group] = Invoke-Logged -Name "itest-$group" -TimeoutSeconds 300 -Arguments $arguments
    }
    Write-ItestSummary -Results $results
    $failed = @($results.Values | Where-Object { $_ -ne 0 })
    if ($failed.Count -gt 0) {
        return $failed[0]
    }
    return 0
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
        exit (Invoke-Logged -Name 'itest' -TimeoutSeconds 300 -Arguments $arguments)
    }
    'format' {
        $status = Invoke-Logged -Name 'format-style' -TimeoutSeconds 180 -Arguments @('format', 'style', $solution, '--severity', 'info')
        if ($status -ne 0) {
            exit $status
        }
        exit (Invoke-Logged -Name 'format' -TimeoutSeconds 180 -Arguments @('csharpier', 'format', $root))
    }
    'publish' {
        $arguments = @(
            'publish', (Join-Path $root 'src/GodotMcp.Server/GodotMcp.Server.csproj'),
            '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $root 'bin/publish')
        )
        exit (Invoke-Logged -Name 'publish' -TimeoutSeconds 300 -Arguments $arguments)
    }
    'gdtest' {
        $arguments = @('--headless', '--path', (Join-Path $root 'tests/bridge'), '--script', 'res://run_tests.gd')
        exit (Invoke-Gated -Name 'gdtest' -TimeoutSeconds 60 -Program (Get-GodotPath) -Arguments $arguments)
    }
    default {
        Get-Help $PSCommandPath -Detailed
    }
}
