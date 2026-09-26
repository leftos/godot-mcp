#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds, tests, formats and publishes godot-mcp.

.DESCRIPTION
Every command runs under tools/gate.ps1: its whole output goes to .tmp/<command>.log, the last lines are printed, and it
exits with the command's own status, or 124 when it outlived its ceiling and was killed with its children.

  build    dotnet build GodotMcp.slnx, warnings as errors; ceiling 300 s
  test     the unit tests (tests/GodotMcp.Tests); ceiling 180 s, and the runner's own --timeout 3m
  itest    the integration tests against the real Godot (GODOT_PATH, else F:\Godot\Godot_console.exe); ceiling 300 s,
           and the runner's own --timeout 4m
  format   dotnet format style (info severity), then CSharpier, on the whole solution; ceiling 180 s each
  publish  a framework-dependent win-x64 server at bin/publish/godot-mcp.exe, with bridge/ beside it; ceiling 300 s

-Filter narrows test or itest to one test class, e.g. -Filter "*SessionLifecycleTests".

.EXAMPLE
pwsh run.ps1 itest -Filter "*McpServerSmokeTests"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'itest', 'format', 'publish', 'help')]
    [string]$Command = 'help',

    [string]$Filter = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$logDir = Join-Path $root '.tmp'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$solution = Join-Path $root 'GodotMcp.slnx'

$gate = Join-Path $root 'tools/gate.ps1'

# Runs dotnet under tools/gate.ps1: the whole output to .tmp/<Name>.log, the last 15 lines on the screen, and the
# process tree killed with status 124 when it outlives its ceiling (a run that reaches one has hung, not slowed).
function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    $log = Join-Path $logDir "$Name.log"
    Write-Host "dotnet $($Arguments -join ' ')  (log: $log, ceiling: $TimeoutSeconds s)"
    & $gate -Log $log -TimeoutSeconds $TimeoutSeconds -Tail 15 -- dotnet @Arguments | Out-Host
    return $LASTEXITCODE
}

function Get-TestArguments {
    param(
        [Parameter(Mandatory)] [string]$Project,
        [Parameter(Mandatory)] [string]$Timeout
    )
    $arguments = @('test', '--project', (Join-Path $root $Project), '--', '--timeout', $Timeout)
    if ($Filter) {
        $arguments += @('--filter-class', $Filter)
    }
    return $arguments
}

switch ($Command) {
    'build' {
        exit (Invoke-Logged -Name 'build' -TimeoutSeconds 300 -Arguments @('build', $solution, '-warnaserror'))
    }
    'test' {
        $arguments = Get-TestArguments -Project 'tests/GodotMcp.Tests/GodotMcp.Tests.csproj' -Timeout '3m'
        exit (Invoke-Logged -Name 'test' -TimeoutSeconds 180 -Arguments $arguments)
    }
    'itest' {
        $project = 'tests/GodotMcp.IntegrationTests/GodotMcp.IntegrationTests.csproj'
        exit (Invoke-Logged -Name 'itest' -TimeoutSeconds 300 -Arguments (Get-TestArguments -Project $project -Timeout '4m'))
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
    default {
        Get-Help $PSCommandPath -Detailed
    }
}
