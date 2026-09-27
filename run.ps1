#!/usr/bin/env pwsh

<#
.SYNOPSIS
Builds, tests, formats and publishes godot-mcp.

.DESCRIPTION
Every command runs under tools/gate.ps1: its whole output goes to .tmp/<command>.log, the last lines are printed, and it
exits with the command's own status, or 124 when it outlived its ceiling and was killed with its children.

  build    dotnet build GodotMcp.slnx, warnings as errors; ceiling 300 s
  test     the unit tests (tests/GodotMcp.Tests); ceiling 180 s, and the runner's own --timeout 3m
  itest    the integration tests against the real Godot (GODOT_PATH, else a Godot*console*.exe on PATH), in the class
           groups of the table at the top of this script (lifecycle, input, reads). It first checks that every
           `public sealed class <Name>Tests` in tests/GodotMcp.IntegrationTests is in exactly one group and every listed
           class exists, and stops with status 1 before running anything when not. It then runs the dotnet command (as
           below; a -Filter run does so only when the filter matches a class of the csharp group), builds the project once
           (.tmp/itest-build.log, ceiling 300 s) and runs each group as its own gate (.tmp/itest-<group>.log, ceiling
           300 s, the runner's own --timeout 4m). Every group runs even when an earlier one fails; a summary line per
           group follows, and the exit status is the first non-zero group's. On Windows every test run (a group's or
           a -Filter one) goes through tools/hidden-desktop.ps1, on a desktop of its own, so no Godot window shows.
  format   dotnet format style (info severity), then CSharpier, on the whole solution; ceiling 180 s each
  dotnet   the C# helper into bin/dotnet: the NativeAOT shim godot_mcp_dotnet.dll (win-x64, no pdb) and
           dotnet/godot_mcp_dotnet.gdextension at the top, loader/ (GodotMcp.Dotnet.Loader.dll and its
           runtimeconfig.json) and helper/ (GodotMcp.Dotnet.dll and GodotMcp.Dotnet.Core.dll, which the loader
           resolves from the helper's folder). Each project publishes into .tmp/dotnet-publish/<name>
           under its own gate (.tmp/dotnet-<name>.log, ceiling 300 s); bin/dotnet is then rebuilt from those files.
           The shim's link needs the MSVC linker (VS Build Tools' VC tools), which ILCompiler finds through vswhere under
           ProgramFiles(x86); when that variable is empty (as it can be from Git Bash) it is set to C:\Program Files
           (x86), and vswhere's own folder is put on PATH, because a batch file the link runs afterwards calls vswhere.exe
           by bare name.
  publish  a framework-dependent win-x64 server at bin/publish/godot-mcp.exe, with bridge/ beside it, then the dotnet
           command (as above), whose bin/dotnet is copied to bin/publish/dotnet; ceiling 300 s
  install  publish (as above), then mirror bin/publish into $env:LOCALAPPDATA\godot-mcp (robocopy /MIR, no retries), and
           link ~/.claude/skills/godot-mcp to skills/godot-mcp as a directory junction; ceiling 300 s for the publish,
           60 s for the copy. The copy and the link are tools/install.ps1, logged to .tmp/install.log. A junction
           that points elsewhere is replaced; anything else at the link path is refused, never deleted.
           GODOT_MCP_INSTALL_DIR overrides the install folder, GODOT_MCP_SKILLS_DIR the folder the link is made in.
           Before the mirror it stops every godot-mcp.exe running from the install folder, printing one line each
           that names the Claude session and project it served, to reconnect there with /mcp.
  package  the release download: bin/publish removed, publish (as above), then tools/package.ps1 zips bin/publish with
           skills/godot-mcp as skill/ and a VERSION file (the published product version) into
           .tmp/package/godot-mcp-<X.Y.Z>-win-x64.zip, writes .tmp/package/install.ps1 (tools/install-release.ps1
           with tools/InstalledServers.psm1 inlined, the release's installer), and prints both paths; .tmp/package.log, ceiling 300 s for the
           publish, 60 s for the zip. The release workflow runs this command, so a local zip is built as CI builds it.
  gdtest   the bridge's GDScript unit tests (tests/bridge/test_*.gd) in headless Godot (GODOT_PATH, else a
           Godot*console*.exe on PATH). It first imports tests/bridge (godot --headless --path tests/bridge --import,
           .tmp/gdtest-import.log, ceiling 120 s) when a test script is newer than the last import, so the project's
           global class list holds every script's class_name, then runs godot --headless --path tests/bridge --script
           res://run_tests.gd. Each failure prints as "FAIL <file>::<test>: <message>", then
           "gdtest: <passed> passed, <failed> failed"; ceiling 60 s
  drive    drives this tree's own server with the tool calls of -Calls <file.json>, a JSON array of
           {"tool": "<name>", "arguments": {...}} objects (arguments optional). It first builds the server project
           (.tmp/drive-build.log, ceiling 300 s), then runs tools/drive.py (.tmp/drive.log, ceiling 300 s), which checks
           the file, starts src/GodotMcp.Server/bin/Debug/net10.0/godot-mcp.exe over stdio, runs the calls in order and
           prints each result under a "== <n> <tool>" header: text as it is, an image saved to .tmp/drive/<n>-<i>.png and
           printed as [image <path>]. The server's own log (its stderr) goes to .tmp/drive-server.log. The first call
           that fails is printed and stops the run with status 1, and a failed call or a server that stops answering
           ends the output with "server log: <path>"; a malformed file stops it with status 2 before the server
           starts, as does a missing -Calls. The gate runs with -NoMarkers: drive's exit status alone is the verdict,
           since a tool result can quote a game's error lines. The server is stopped at the end, killed if it has not exited 5 s
           after its input closed. It runs on the user's desktop, not the hidden one: a run is quiet unless a call asks
           for quiet: false, and then its window is meant to show.

-Filter narrows test or itest to one test class, e.g. -Filter "*SessionLifecycleTests". A filtered itest skips the
groups: one gate, .tmp/itest.log, ceiling 300 s, --timeout 4m.

.EXAMPLE
pwsh run.ps1 itest -Filter "*McpServerSmokeTests"

.EXAMPLE
pwsh run.ps1 drive -Calls .tmp/calls.json
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'A console build script: its lines are for the person running it.')]
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'itest', 'format', 'dotnet', 'publish', 'install', 'package', 'gdtest', 'drive', 'help')]
    [string]$Command = 'help',

    [string]$Filter = '',

    [string]$Calls = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The integration test classes, one gate per group, run in this order. A new test class goes into one group; itest
# refuses to run while a class is in no group or a listed class no longer exists.
$itestGroups = [ordered]@{
    lifecycle = @('SessionLifecycleTests', 'AttachTests', 'QuietTests', 'WatchdogTests', 'McpServerSmokeTests', 'ProfileTests')
    input     = @('InputTests', 'GamepadTests', 'CaptureTests', 'StressTests')
    reads     = @('RuntimeReadTests', 'InspectionTests', 'BaselineTests', 'PreviewTests')
    time      = @('TimeTests', 'BatchTests')
    prep      = @('PrepTests', 'RestartTests')
    recording = @('RecordingTests')
    headless  = @('HeadlessTests', 'HeadlessMeshTests')
    scene     = @('HeadlessSceneTests', 'HeadlessBatchTests')
    nodes     = @('HeadlessPropertyTests', 'HeadlessSignalTests')
    csharp    = @('CSharpToolTests')
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
        [Parameter(Mandatory)] [string[]]$Arguments,
        [switch]$NoMarkers
    )
    $log = Join-Path $logDir "$Name.log"
    Write-Host "$Program $($Arguments -join ' ')  (log: $log, ceiling: $TimeoutSeconds s)"
    $gateOptions = @('-Log', $log, '-TimeoutSeconds', $TimeoutSeconds, '-Tail', 15)
    if ($NoMarkers) {
        $gateOptions += '-NoMarkers'
    }
    & $gate @gateOptions -- $Program @Arguments | Out-Host
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

# The Godot executable as the server finds it (Installation.FindGodot): GODOT_PATH when set and a file, else the first folder
# of PATH holding a Godot*console*.exe, the last of that folder's names under OrdinalIgnoreCase; stops the script when
# neither has one.
function Get-GodotPath {
    $configured = $env:GODOT_PATH
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        if (-not (Test-Path -LiteralPath $configured -PathType Leaf)) {
            throw "GODOT_PATH is '$configured', which does not exist. Point it at the Godot 4.7 console executable."
        }
        return $configured
    }
    foreach ($folder in ($env:PATH -split [IO.Path]::PathSeparator)) {
        if ([string]::IsNullOrWhiteSpace($folder)) {
            continue
        }
        $matches = @(
            Get-ChildItem -LiteralPath $folder.Trim('"') -Filter 'Godot*console*.exe' -File -ErrorAction SilentlyContinue
        )
        if ($matches.Count -eq 0) {
            continue
        }
        $names = [string[]]@($matches | ForEach-Object { $_.Name })
        [array]::Sort($names, [System.StringComparer]::OrdinalIgnoreCase)
        return ($matches | Where-Object { $_.Name -ceq $names[-1] } | Select-Object -First 1).FullName
    }
    $exe = "$env:LOCALAPPDATA\godot-mcp\godot-mcp.exe"
    throw "Godot was not found: GODOT_PATH is not set and no Godot*console*.exe is on PATH. Set GODOT_PATH to the Godot 4.7 console executable; with Claude Code, register this server with: claude mcp add godot -s local -e GODOT_PATH=<path to the Godot console exe> -- `"$exe`""
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

# Whether a -Filter selects a class of the csharp group, whose tests need bin/dotnet: each filter is matched against the
# class's full name, as the runner's --filter-class matches it.
function Test-ItestFilterNeedsDotnet {
    param([Parameter(Mandatory)] [string[]]$Filters)
    $classes = @($itestGroups['csharp'] | ForEach-Object { "$itestNamespace.$_" })
    foreach ($filter in $Filters) {
        if (@($classes | Where-Object { $_ -like $filter }).Count -gt 0) {
            return $true
        }
    }
    return $false
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
    $dotnet = Invoke-DotnetPublish
    if ($dotnet -ne 0) {
        return $dotnet
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

# The shim's link needs both halves of this. ILCompiler's findvcvarsall.bat runs
# "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" to find the MSVC linker and never searches PATH, so
# that variable is set to its default when empty (as it can be under Git Bash); a batch file the link runs afterwards
# calls vswhere.exe by bare name, so vswhere's folder is put on PATH too.
function Set-VswhereEnvironment {
    ${env:ProgramFiles(x86)} = Get-ConfiguredDirectory -Configured ${env:ProgramFiles(x86)} -Default 'C:\Program Files (x86)'
    $installerFolder = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    $entries = @([string]$env:PATH -split [IO.Path]::PathSeparator)
    if ($entries -notcontains $installerFolder) {
        $env:PATH = (@($installerFolder) + $entries) -join [IO.Path]::PathSeparator
    }
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
        Files = @('GodotMcp.Dotnet.dll', 'GodotMcp.Dotnet.Core.dll'); Folder = 'helper'
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
    Set-VswhereEnvironment
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

# Publishes into an emptied bin/publish, so no file of an earlier publish reaches the zip, then runs tools/package.ps1
# under its own gate.
function Invoke-Package {
    $publish = Join-Path $root 'bin/publish'
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    $status = Invoke-Publish
    if ($status -ne 0) {
        return $status
    }
    $arguments = @(
        '-NoProfile', '-File', (Join-Path $root 'tools/package.ps1'),
        '-Root', $root, '-OutputDir', (Join-Path $logDir 'package')
    )
    return Invoke-Gated -Name 'package' -TimeoutSeconds 60 -Program 'pwsh' -Arguments $arguments
}

# Builds the server project, then runs tools/drive.py under its own gate: the calls of a file sent in order to the
# server it built, over stdio. Returns 2 without building when no calls file is given.
function Invoke-Drive {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$CallsFile)
    if ([string]::IsNullOrWhiteSpace($CallsFile)) {
        [Console]::Error.WriteLine('drive needs -Calls <file.json>: a JSON array of {"tool": "<name>", "arguments": {...}} objects.')
        return 2
    }
    $project = Join-Path $root 'src/GodotMcp.Server/GodotMcp.Server.csproj'
    $build = Invoke-Logged -Name 'drive-build' -TimeoutSeconds 300 -Arguments @('build', $project, '-warnaserror')
    if ($build -ne 0) {
        return $build
    }
    $exe = if ($IsWindows) { 'godot-mcp.exe' } else { 'godot-mcp' }
    $server = Join-Path $root "src/GodotMcp.Server/bin/Debug/net10.0/$exe"
    $arguments = @(
        'run', '--quiet', 'python', (Join-Path $root 'tools/drive.py'),
        '--calls', $CallsFile, '--images', (Join-Path $logDir 'drive'), '--server-log', (Join-Path $logDir 'drive-server.log'), $server
    )
    # A tool result can quote a game's error lines, which the gate's failure markers would read as drive's own failure.
    return Invoke-Gated -Name 'drive' -TimeoutSeconds 300 -Program 'uv' -Arguments $arguments -NoMarkers
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
        if (Test-ItestFilterNeedsDotnet -Filters $filterClasses) {
            $dotnet = Invoke-DotnetPublish
            if ($dotnet -ne 0) {
                exit $dotnet
            }
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
    'package' {
        exit (Invoke-Package)
    }
    'gdtest' {
        $import = Invoke-GdtestImport
        if ($import -ne 0) {
            exit $import
        }
        $arguments = @('--headless', '--path', $gdtestDir, '--script', 'res://run_tests.gd')
        exit (Invoke-Gated -Name 'gdtest' -TimeoutSeconds 60 -Program (Get-GodotPath) -Arguments $arguments)
    }
    'drive' {
        exit (Invoke-Drive -CallsFile $Calls)
    }
    default {
        Get-Help $PSCommandPath -Detailed
    }
}
