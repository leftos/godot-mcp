#!/usr/bin/env pwsh
<#
.SYNOPSIS
Builds, tests, formats and publishes godot-mcp.

.DESCRIPTION
Every command writes its whole output to .tmp/<command>.log, prints the last lines, and exits with the command's own
status.

  build    dotnet build GodotMcp.slnx, warnings as errors
  test     the unit tests (tests/GodotMcp.Tests); the runner stops a run after 3 minutes
  itest    the integration tests against the real Godot (GODOT_PATH, else F:\Godot\Godot_console.exe); 4 minutes
  format   dotnet format style (info severity), then CSharpier, on the whole solution
  publish  a framework-dependent win-x64 server at bin/publish/godot-mcp.exe, with bridge/ beside it

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

function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    $log = Join-Path $logDir "$Name.log"
    Write-Host "dotnet $($Arguments -join ' ')  (log: $log)"
    & dotnet @Arguments *> $log
    $status = $LASTEXITCODE
    Get-Content -Path $log -Tail 15 | ForEach-Object { Write-Host $_ }
    return $status
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
        exit (Invoke-Logged -Name 'build' -Arguments @('build', $solution, '-warnaserror'))
    }
    'test' {
        exit (Invoke-Logged -Name 'test' -Arguments (Get-TestArguments -Project 'tests/GodotMcp.Tests/GodotMcp.Tests.csproj' -Timeout '3m'))
    }
    'itest' {
        $project = 'tests/GodotMcp.IntegrationTests/GodotMcp.IntegrationTests.csproj'
        exit (Invoke-Logged -Name 'itest' -Arguments (Get-TestArguments -Project $project -Timeout '4m'))
    }
    'format' {
        $status = Invoke-Logged -Name 'format-style' -Arguments @('format', 'style', $solution, '--severity', 'info')
        if ($status -ne 0) {
            exit $status
        }
        exit (Invoke-Logged -Name 'format' -Arguments @('csharpier', 'format', $root))
    }
    'publish' {
        $arguments = @(
            'publish', (Join-Path $root 'src/GodotMcp.Server/GodotMcp.Server.csproj'),
            '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $root 'bin/publish')
        )
        exit (Invoke-Logged -Name 'publish' -Arguments $arguments)
    }
    default {
        Get-Help $PSCommandPath -Detailed
    }
}
