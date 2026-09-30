#!/usr/bin/env pwsh

<#
.SYNOPSIS
Builds, tests, formats and publishes godot-mcp.

.DESCRIPTION
Every command but itest-groups runs under tools/gate.ps1: its whole output goes to .tmp/<command>.log, the last lines are printed, and it
exits with the command's own status, or 124 when the gate's watchdog killed it with every process it started. The
watchdog kills a run for the first of three reasons, each named by a kill line in the log that this script prints with
its reading: STALLED (no output and no CPU for 120 s: it hung), TIMED OUT (the ceiling ran out on a clock that runs
slower while other work keeps the machine busy: a busy loop or a ceiling set too tight) or BACKSTOP (five times the
ceiling in plain wall time: the machine was busy, so run it once more alone). The ceilings below are load-adjusted.

  build    dotnet build GodotMcp.slnx in Release, warnings as errors; ceiling 300 s
  test     the unit tests (tests/GodotMcp.Tests): the project built in Release first (.tmp/test-build.log, ceiling 300 s),
           then its dotnet test -c Release --no-build; ceiling 180 s
  itest    the integration tests against the real Godot (GODOT_PATH, else a Godot*console*.exe on PATH), in the class
           groups of the table at the top of this script, which run in the three lanes of the table below it: timing
           (lifecycle, input, time, recording, reads: the groups that assert wall-clock times), build (prep, scene,
           csharp) and untimed (sessions, headless, nodes). It
           first checks that every `public sealed class <Name>Tests` in tests/GodotMcp.IntegrationTests is in exactly
           one group, every listed class exists, every group is in exactly one lane that names only groups, and the
           heavy groups and rules tables name only groups, and stops with status 1 before running anything when not.
           It then runs the dotnet command (as below) when the csharp group runs (a -Filter run: when the filter matches
           a class of the csharp group), builds the project once in Release
           (.tmp/itest-build.log, ceiling 300 s) and runs each group as its own gate in a process of its own
           (.tmp/itest-<group>.log, ceiling 300 s, dotnet test -c Release --no-build): each lane's groups one at a time
           in the table's order, the three lanes at once. A group's console output, the gate's tail and verdict, goes to
           .tmp/itest-<group>.console (errors to .tmp/itest-<group>.console.err) and is printed under
           "== itest <group> (lane <lane>)" when the group ends.
           Every group runs even when an earlier one fails; a summary line per group follows in the groups table's order,
           then "itest: <n> groups in <m> lanes, wall <time>", and the exit status is the first non-zero group's in
           that order. Each gate takes one of the machine's gate slots, so the three lanes take three: a heavy slot for
           the groups of the heavy groups table (prep, headless, scene, nodes, csharp, whose tests build C#), a light
           one for the rest, whichever lane a group is in. On Windows every test run (a group's or a -Filter one) goes
           through tools/hidden-desktop.ps1, on a desktop of its own, so no Godot window shows.
           With -Since <ref> it runs only the groups itest-groups (below) selects, in their lanes as above, after
           printing itest-groups' lines; with none selected it prints "itest: no group touched by the changes since
           <ref>" and exits 0. -Since with -Filter is refused with status 2.
  itest-groups  names the itest groups the changes since -Since <ref> touch, running nothing and taking no gate: the
           changed files are the tracked files that differ from the ref (git diff --name-only <ref>, the working tree
           included) and the untracked files git does not ignore. Each file goes through the rules table at the top of
           this script, the first matching pattern deciding (anything under bridge/ selects every group, since
           godot_mcp_bridge.gd preloads every module), and a file no rule maps selects every group. It prints
           "itest-groups: <file> is unmapped: no rule maps it, so it runs every group" for each such file,
           "itest-groups: <group> (from <file>)" for each selected group in the groups table's order with the first
           file that selected it, then "itest-groups: <n> of <m> groups from <k> changed files". -Since is required
           (status 2 without it); a ref git cannot resolve stops with status 2 and git's message; heavy groups or rules
           naming no group stop it with status 1. It reads only the tables and git, not the test project.
  format   dotnet format style (info severity), then CSharpier, on the whole solution; ceiling 180 s each
  format-check  dotnet csharpier check on the repo, changing nothing; ceiling 180 s
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
  install  publish (as above), then mirror bin/publish into $env:LOCALAPPDATA\godot-mcp (robocopy /MIR, no retries),
           link ~/.claude/skills/godot-mcp to skills/godot-mcp and ~/.claude/skills/godot-agent-sweep to
           skills/godot-agent-sweep as directory junctions, and run the installed godot-mcp.exe --sweep-agents, printing
           its lines as they come; ceiling 300 s for the publish, then 900 s in a heavy slot for the copy, the links and the
           sweep, whose commits run the swept repositories' hooks (which can build). They are tools/install.ps1, logged
           to .tmp/install.log. A junction that points
           elsewhere is replaced; anything else at a link path is refused, never deleted. A sweep that cannot start is
           reported and the install succeeds; one that exits non-zero makes the install exit 1, undoing nothing.
           GODOT_MCP_INSTALL_DIR overrides the install folder, GODOT_MCP_SKILLS_DIR the folder the links are made in.
           Before the mirror it stops every godot-mcp.exe running from the install folder, printing one line each
           that names the Claude session and project it served, to reconnect there with /mcp.
  package  the release download: bin/publish removed, publish (as above), then tools/package.ps1 zips bin/publish with
           skills/godot-mcp as skill/, skills/godot-agent-sweep as agent-sweep-skill/ and a VERSION file (the published
           product version) into
           .tmp/package/godot-mcp-<X.Y.Z>-win-x64.zip, writes .tmp/package/install.ps1 (tools/install-release.ps1
           with tools/InstalledServers.psm1 inlined, the release's installer), and prints both paths; .tmp/package.log, ceiling 300 s for the
           publish, 60 s for the zip. The release workflow runs this command, so a local zip is built as CI builds it.
  gdtest   the bridge's GDScript unit tests (tests/bridge/test_*.gd) in headless Godot (GODOT_PATH, else a
           Godot*console*.exe on PATH). It first imports tests/bridge (godot --headless --path tests/bridge --import,
           .tmp/gdtest-import.log, ceiling 120 s) when a test script is newer than the last import, so the project's
           global class list holds every script's class_name, then runs godot --headless --path tests/bridge --script
           res://run_tests.gd. Each failure prints as "FAIL <file>::<test>: <message>", then
           "gdtest: <passed> passed, <failed> failed"; ceiling 60 s
  pytest   the Python tools' tests (tests/tools) under pytest, which uv supplies together with gdtoolkit; ceiling 120 s
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
groups: the project built in Release first (.tmp/itest-build.log, ceiling 300 s), then one gate, ceiling 300 s, logged to
.tmp/itest-filter-<slug>.log, where <slug> is the filter with every run of characters outside A-Z, a-z and 0-9 turned
into one '-' and trimmed of '-' at both ends (.tmp/itest-filter-SessionLifecycleTests.log for the example), so filtered
runs of different classes in one tree at once keep their logs apart and never write a group's log; a filter that leaves
no slug logs to .tmp/itest.log.

Every command runs from the folder of this script, whatever folder it was started from, and gives the caller's location
back when it ends; a relative -Calls path is read against the caller's location.

.EXAMPLE
pwsh run.ps1 itest -Filter "*McpServerSmokeTests"

.EXAMPLE
pwsh run.ps1 itest -Since origin/main

.EXAMPLE
pwsh run.ps1 drive -Calls .tmp/calls.json
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = 'A console build script: its lines are for the person running it.')]
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet(
        'build', 'test', 'itest', 'itest-groups', 'format', 'format-check', 'dotnet', 'publish', 'install', 'package', 'gdtest', 'pytest',
        'drive', 'help'
    )]
    [string]$Command = 'help',

    [string]$Filter = '',

    [string]$Since = '',

    [string]$Calls = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The integration test classes, one gate per group, run in this order. A new test class goes into one group; itest
# refuses to run while a class is in no group or a listed class no longer exists.
$itestGroups = [ordered]@{
    lifecycle = @('SessionLifecycleTests', 'WatchdogTests')
    sessions  = @('McpServerSmokeTests', 'AttachTests', 'ArmTests', 'QuietTests', 'ProfileTests')
    input     = @('InputTests', 'GamepadTests', 'CaptureTests', 'StressTests', 'WorldTargetTests', 'TextTargetTests', 'ItemTargetTests')
    reads     = @('RuntimeReadTests', 'InspectionTests', 'BaselineTests', 'PreviewTests', 'StateTests')
    time      = @('TimeTests', 'BatchTests')
    prep      = @('PrepTests', 'RestartTests')
    recording = @('RecordingTests')
    headless  = @('HeadlessTests', 'HeadlessMeshTests', 'WarmHeadlessTests')
    scene     = @('HeadlessSceneTests', 'HeadlessBatchTests')
    nodes     = @('HeadlessPropertyTests', 'HeadlessSignalTests')
    csharp    = @('CSharpToolTests', 'GameToolTests', 'CSharpStateTests')
}
# The lanes the groups run in: each lane's groups one at a time in this order, the lanes at once. The timing lane keeps
# the groups that assert wall-clock times apart from each other; the build and untimed lanes hold the rest, the C#
# builds and headless runs split between them beside the session tests. Every group is in exactly one lane; itest
# refuses to run while one is not.
$itestLanes = [ordered]@{
    timing  = @('lifecycle', 'input', 'time', 'recording', 'reads')
    build   = @('prep', 'scene', 'csharp')
    untimed = @('sessions', 'headless', 'nodes')
}
# The groups whose tests run dotnet builds of the CsProbe project on top of their Godot runs: each takes a heavy gate
# slot, in whichever lane it runs, and every other group a light one.
$itestHeavyGroups = @('prep', 'headless', 'scene', 'nodes', 'csharp')
# Which groups a changed file touches, for itest-groups and itest -Since: ordered pairs of a path pattern, '/'-separated
# from the repo root ('*' matches within one folder, '**' across folders), and what a match selects: 'all', 'none',
# 'class' (the group listing the class a test file is named for), or a list of groups. The first pattern a file matches
# decides; a file no pattern matches, or a test file whose class no group lists, selects every group and is named as
# unmapped. Anything under bridge/ selects every group, since godot_mcp_bridge.gd preloads every module.
$itestRuntimeGroups = @('lifecycle', 'sessions', 'input', 'reads', 'time', 'prep', 'recording', 'csharp')
$itestRules = @(
    @('bridge/**', 'all'),
    @('run.ps1', 'all'),
    @('tools/gate.ps1', 'all'),
    @('tools/hidden-desktop.ps1', 'all'),
    @('src/GodotMcp.Server/Session/**', 'all'),
    @('src/GodotMcp.Server/Wire/**', 'all'),
    @('src/GodotMcp.Server/*.cs', 'all'),
    @('src/GodotMcp.Server/*.csproj', 'all'),
    @('Directory.*.props', 'all'),
    @('tests/GodotMcp.TestSupport/**', 'all'),
    @('tests/GodotMcp.IntegrationTests/Fixtures/**', 'all'),
    @('tests/GodotMcp.IntegrationTests/GodotMcp.IntegrationTests.csproj', 'all'),
    @('tests/fixtures/**', 'all'),
    @('tests/GodotMcp.IntegrationTests/xunit.runner.json', 'all'),
    @('.gitignore', 'all'),
    @('.editorconfig', 'all'),
    @('global.json', 'all'),
    @('GodotMcp.slnx', 'all'),
    @('.config/**', 'all'),
    @('headless/**', @('headless', 'scene', 'nodes', 'reads')),
    @('src/GodotMcp.Server/Tools/HeadlessTools.Scene*.cs', 'scene'),
    @('src/GodotMcp.Server/Tools/HeadlessTools.Batch*.cs', 'scene'),
    @('src/GodotMcp.Server/Tools/HeadlessTools.Properties*.cs', 'nodes'),
    @('src/GodotMcp.Server/Tools/HeadlessTools.Signals*.cs', 'nodes'),
    @('src/GodotMcp.Server/Tools/HeadlessTools*.cs', 'headless'),
    @('src/GodotMcp.Server/Tools/InputTarget.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Input*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Gamepad*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Stress*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Capture*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Time*.cs', 'time'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Frames*.cs', 'time'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Batch*.cs', 'time'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Record*.cs', 'recording'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Inspect*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Snapshot*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Baseline*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Preview*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.State*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/State*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.CSharp*.cs', 'csharp'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.RunCSharp*.cs', 'csharp'),
    @('src/GodotMcp.Server/Tools/RuntimeTools*.cs', $itestRuntimeGroups),
    @('src/GodotMcp.Server/Tools/ProjectTools*.cs', @('lifecycle', 'sessions', 'prep')),
    @('src/GodotMcp.Server/Tools/**', 'all'),
    @('src/GodotMcp.Server/CSharp/**', 'csharp'),
    @('src/GodotMcp.Dotnet*/**', 'csharp'),
    @('dotnet/**', 'csharp'),
    @('src/GodotMcp.Server/Agents/**', 'none'),
    @('tests/GodotMcp.IntegrationTests/*.cs', 'class'),
    @('docs/**', 'none'),
    @('**/*.md', 'none'),
    @('skills/**', 'none'),
    @('tests/GodotMcp.Tests/**', 'none'),
    @('tests/bridge/**', 'none'),
    @('tests/tools/**', 'none'),
    @('tools/*.py', 'none'),
    @('.github/**', 'none'),
    @('CodeMetricsConfig.txt', 'none'),
    @('LICENSE', 'none'),
    @('.gitattributes', 'none'),
    @('.pre-commit-config.yaml', 'none'),
    @('ruff.toml', 'none'),
    @('.csharpierrc', 'none'),
    @('.csharpierignore', 'none'),
    @('tools/install.ps1', 'none'),
    @('tools/install-release.ps1', 'none'),
    @('tools/package.ps1', 'none'),
    @('tools/release-check.ps1', 'none'),
    @('tools/InstalledServers.psm1', 'none'),
    @('tools/gate.selftest.ps1', 'none'),
    @('tools/gdcomplexity-baseline.txt', 'none')
)
$itestNamespace = 'GodotMcp.IntegrationTests'
$itestProject = 'tests/GodotMcp.IntegrationTests/GodotMcp.IntegrationTests.csproj'
$unitTestProject = 'tests/GodotMcp.Tests/GodotMcp.Tests.csproj'
# The configuration every build of this script and every test run uses: a test run is preceded by the build of its own
# project here and passes -c Release --no-build, so it never builds a configuration of its own.
$configuration = 'Release'

$root = $PSScriptRoot
$logDir = Join-Path $root '.tmp'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$solution = Join-Path $root 'GodotMcp.slnx'
$gdtestDir = Join-Path $root 'tests/bridge'
$gdtestStamp = Join-Path $gdtestDir '.godot/gdtest-import.stamp'
$dotnetOut = Join-Path $root 'bin/dotnet'
$dotnetStaging = Join-Path $logDir 'dotnet-publish'

$gate = Join-Path $root 'tools/gate.ps1'

# The last kill line of the gate in a log (a match whose first group is STALLED, TIMED OUT or BACKSTOP), or $null.
function Get-GateKillLine {
    param([Parameter(Mandatory)] [string]$Log)
    return Select-String -LiteralPath $Log -Pattern '^gate: (STALLED|TIMED OUT|BACKSTOP)' -ErrorAction SilentlyContinue |
        Select-Object -Last 1
}

# The kind of the gate's kill in a log, STALLED, TIMED OUT or BACKSTOP; '' when the log has no kill line.
function Get-GateKillKind {
    param([Parameter(Mandatory)] [string]$Log)
    $line = Get-GateKillLine -Log $Log
    if (-not $line) {
        return ''
    }
    return $line.Matches[0].Groups[1].Value
}

# The gate's kill line in a log followed by what it means, or a note that the log has none.
function Get-GateKill {
    param([Parameter(Mandatory)] [string]$Log)
    $line = Get-GateKillLine -Log $Log
    if (-not $line) {
        return "no kill line in $Log"
    }
    $reading = switch ($line.Matches[0].Groups[1].Value) {
        'STALLED' { "It hung: read $Log for where it stopped." }
        'TIMED OUT' { "It kept working past its ceiling even allowing for load: a busy loop or a ceiling set too tight; read $Log before raising it." }
        default { 'The machine was busy: run it once more alone.' }
    }
    return "$($line.Line) $reading"
}

# Runs a program under tools/gate.ps1: the whole output to .tmp/<Name>.log, the last 15 lines on the screen, and the
# process tree killed with status 124 when the watchdog stops it (stalled, past its load-adjusted ceiling, or past five
# times it in wall time), with the log's kill line and its reading printed.
function Invoke-Gated {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string]$Program,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [ValidateSet('heavy', 'light')] [string]$Slot,
        [switch]$NoMarkers
    )
    $log = Join-Path $logDir "$Name.log"
    Write-Host "$Program $($Arguments -join ' ')  (log: $log, ceiling: $TimeoutSeconds s, slot: $Slot)"
    $gateOptions = @('-Log', $log, '-TimeoutSeconds', $TimeoutSeconds, '-Slot', $Slot, '-Tail', 15)
    if ($NoMarkers) {
        $gateOptions += '-NoMarkers'
    }
    & $gate @gateOptions -- $Program @Arguments | Out-Host
    $status = $LASTEXITCODE
    if ($status -eq 124) {
        Write-Host "$Name was killed by the gate: $(Get-GateKill -Log $log)" -ForegroundColor Yellow
    }
    return $status
}

# Runs dotnet under tools/gate.ps1, as Invoke-Gated does.
function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [int]$TimeoutSeconds,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [ValidateSet('heavy', 'light')] [string]$Slot
    )
    return Invoke-Gated -Name $Name -TimeoutSeconds $TimeoutSeconds -Program 'dotnet' -Arguments $Arguments -Slot $Slot
}

# Builds a test project in $configuration, warnings as errors, under its own gate, so the run after it has nothing to
# build and the runner loads that build. Returns the build's status.
function Invoke-TestBuild {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Project
    )
    $arguments = @('build', (Join-Path $root $Project), '-c', $configuration, '-warnaserror')
    return Invoke-Logged -Name $Name -TimeoutSeconds 300 -Arguments $arguments -Slot heavy
}

# The program and arguments of an integration test run: dotnet with the given arguments, on Windows through
# tools/hidden-desktop.ps1, so the Godot windows the tests open appear on a desktop of their own and never on the
# user's screen.
function Get-ItestCommand {
    param([Parameter(Mandatory)] [string[]]$Arguments)
    if (-not $IsWindows) {
        return @{ Program = 'dotnet'; Arguments = $Arguments }
    }
    $hidden = @('-NoProfile', '-File', (Join-Path $root 'tools/hidden-desktop.ps1'), '--', 'dotnet') + $Arguments
    return @{ Program = 'pwsh'; Arguments = $hidden }
}

# Runs an integration test gate as Invoke-Gated does, with the program and arguments of Get-ItestCommand.
function Invoke-ItestGated {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [ValidateSet('heavy', 'light')] [string]$Slot
    )
    $command = Get-ItestCommand -Arguments $Arguments
    return Invoke-Gated -Name $Name -TimeoutSeconds 300 -Program $command.Program -Arguments $command.Arguments -Slot $Slot
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
        $found = @(
            Get-ChildItem -LiteralPath $folder.Trim('"') -Filter 'Godot*console*.exe' -File -ErrorAction SilentlyContinue
        )
        if ($found.Count -eq 0) {
            continue
        }
        $names = [string[]]@($found | ForEach-Object { $_.Name })
        [array]::Sort($names, [System.StringComparer]::OrdinalIgnoreCase)
        return ($found | Where-Object { $_.Name -ceq $names[-1] } | Select-Object -First 1).FullName
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
    $status = Invoke-Gated -Name 'gdtest-import' -TimeoutSeconds 120 -Program (Get-GodotPath) -Arguments $arguments -Slot light
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
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Classes,
        [switch]$NoBuild
    )
    $arguments = @('test', '--project', (Join-Path $root $Project), '-c', $configuration)
    if ($NoBuild) {
        $arguments += '--no-build'
    }
    if ($Classes.Count -gt 0) {
        $arguments += @('--', '--filter-class') + $Classes
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

# How the groups table has drifted from the project, one phrase per kind of drift: a declared class in no group, a
# listed class not declared, a class listed in more than one group. Empty when there is none.
function Get-ItestClassDrift {
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
    return $parts
}

# How the lanes table has drifted from the groups table, one phrase per kind of drift: a group in no lane, a group in
# more than one lane, a lane entry naming no group. Empty when there is none.
function Get-ItestLaneDrift {
    $groups = @($itestGroups.Keys)
    $laned = @($itestLanes.Values | ForEach-Object { $_ })
    $unlaned = @($groups | Where-Object { $laned -notcontains $_ })
    $repeated = @($laned | Group-Object | Where-Object Count -GT 1 | ForEach-Object Name)
    $unknown = @($laned | Where-Object { $groups -notcontains $_ } | Sort-Object -Unique)
    $parts = @()
    if ($unlaned.Count -gt 0) {
        $parts += "$($unlaned.Count) group(s) in no lane: $($unlaned -join ', ')"
    }
    if ($repeated.Count -gt 0) {
        $parts += "$($repeated.Count) group(s) in more than one lane: $($repeated -join ', ')"
    }
    if ($unknown.Count -gt 0) {
        $parts += "$($unknown.Count) lane entr(ies) naming no group: $($unknown -join ', ')"
    }
    return $parts
}

# The group names the heavy groups and rules tables give that name no group, as one phrase; empty when there is none.
function Get-ItestRuleDrift {
    $keywords = @('all', 'none', 'class')
    $groups = @($itestGroups.Keys)
    $named = @($itestRules | ForEach-Object { $_[1] } | Where-Object { $keywords -cnotcontains $_ }) + $itestHeavyGroups
    $unknown = @($named | Where-Object { $groups -cnotcontains $_ } | Sort-Object -Unique)
    if ($unknown.Count -eq 0) {
        return @()
    }
    return @("$($unknown.Count) name(s) in the heavy groups or rules naming no group: $($unknown -join ', ')")
}

# How the groups, lanes, heavy groups and rules tables have drifted from the project, as one message, or '' when every
# declared class is in exactly one group, every listed class is declared, every group is in exactly one lane that names
# only groups, and the heavy groups and rules name only groups.
function Get-ItestGroupDrift {
    $parts = @(Get-ItestClassDrift) + @(Get-ItestLaneDrift) + @(Get-ItestRuleDrift)
    if ($parts.Count -eq 0) {
        return ''
    }
    return "itest groups are out of date: $($parts -join '; '). Edit the groups, lanes, heavy groups and rules tables in run.ps1."
}

# A rule's path pattern as an anchored regex: '**/' matches any folders or none, a trailing '/**' anything below the
# folder, '*' anything within one folder name; every other character matches itself.
function ConvertTo-ItestRuleRegex {
    param([Parameter(Mandatory)] [string]$Pattern)
    $regex = [regex]::Escape($Pattern) -replace '\\\*\\\*/', '(?:.*/)?' -replace '/\\\*\\\*$', '/.*' -replace '\\\*', '[^/]*'
    return "^$regex$"
}

# The group listing a class, or '' when no group does.
function Get-ItestClassGroup {
    param([Parameter(Mandatory)] [string]$Class)
    foreach ($group in $itestGroups.Keys) {
        if ($itestGroups[$group] -ccontains $Class) {
            return $group
        }
    }
    return ''
}

# What a rule's selection gives a file: @{ Groups; Unmapped }, every group for 'all', none for 'none', the group listing
# the class the file is named for for 'class' (every group, unmapped, when no group lists it), else the rule's groups.
function Resolve-ItestRuleSelection {
    param(
        [Parameter(Mandatory)] [object]$Selects,
        [Parameter(Mandatory)] [string]$File
    )
    $every = @{ Groups = @($itestGroups.Keys); Unmapped = $false }
    if ($Selects -isnot [string]) {
        return @{ Groups = @($Selects); Unmapped = $false }
    }
    switch -CaseSensitive ($Selects) {
        'all' { return $every }
        'none' { return @{ Groups = @(); Unmapped = $false } }
        'class' {
            $group = Get-ItestClassGroup -Class ([IO.Path]::GetFileNameWithoutExtension($File))
            if ($group) {
                return @{ Groups = @($group); Unmapped = $false }
            }
            return @{ Groups = $every.Groups; Unmapped = $true }
        }
    }
    return @{ Groups = @($Selects); Unmapped = $false }
}

# What a changed file selects, @{ Groups; Unmapped }: the first rule its path matches decides, and a file no rule
# matches selects every group and is unmapped.
function Get-ItestFileGroup {
    param([Parameter(Mandatory)] [string]$File)
    foreach ($rule in $itestRules) {
        if ($File -cmatch (ConvertTo-ItestRuleRegex -Pattern $rule[0])) {
            return Resolve-ItestRuleSelection -Selects $rule[1] -File $File
        }
    }
    return @{ Groups = @($itestGroups.Keys); Unmapped = $true }
}

# The files changed since a ref, @{ Status; Files }: the tracked files that differ from it, the working tree included,
# and the untracked files git does not ignore, each once, '/'-separated from the repo root. Status 2, with git's message
# on stderr, when git cannot resolve the ref.
function Get-ChangedFile {
    param([Parameter(Mandatory)] [string]$Since)
    $tracked = @(& git -C $root -c core.quotepath=off diff --name-only $Since -- 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $tracked | ForEach-Object { [Console]::Error.WriteLine("$_") }
        return @{ Status = 2; Files = @() }
    }
    $untracked = @(& git -C $root -c core.quotepath=off ls-files --others --exclude-standard --full-name 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $untracked | ForEach-Object { [Console]::Error.WriteLine("$_") }
        return @{ Status = 2; Files = @() }
    }
    # git's warnings (a line ending it would convert, say) come back as error records beside the names.
    $files = @($tracked + $untracked | Where-Object { $_ -is [string] -and $_ } | Sort-Object -Unique -CaseSensitive)
    return @{ Status = 0; Files = $files }
}

# The groups some changed files touch, @{ Groups; Unmapped }: Groups ordered as the groups table, each with the first
# file (in path order) that selected it; Unmapped the files no rule maps.
function Get-ItestSelection {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Files)
    $firstFile = @{}
    $unmapped = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $Files) {
        $selection = Get-ItestFileGroup -File $file
        if ($selection.Unmapped) {
            $unmapped.Add($file)
        }
        foreach ($group in $selection.Groups) {
            if (-not $firstFile.ContainsKey($group)) {
                $firstFile[$group] = $file
            }
        }
    }
    $groups = [ordered]@{}
    foreach ($group in @($itestGroups.Keys | Where-Object { $firstFile.ContainsKey($_) })) {
        $groups[$group] = $firstFile[$group]
    }
    return @{ Groups = $groups; Unmapped = $unmapped.ToArray() }
}

# Chooses and prints the groups the changes since a ref touch: a line per unmapped file, a line per selected group with
# the file that first selected it, then the count. Returns @{ Status; Groups }: 2 when -Since is missing or git cannot
# resolve the ref, 1 when the heavy groups or rules name no group, else 0 with the groups in table order.
function Invoke-ItestGroupSelection {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Since)
    if ([string]::IsNullOrWhiteSpace($Since)) {
        [Console]::Error.WriteLine('itest-groups needs -Since <ref>: the git ref whose changes, the working tree and untracked files included, choose the groups.')
        return @{ Status = 2; Groups = @() }
    }
    $drift = @(Get-ItestRuleDrift)
    if ($drift.Count -gt 0) {
        [Console]::Error.WriteLine("itest rules are out of date: $($drift -join '; '). Edit the heavy groups and rules tables in run.ps1.")
        return @{ Status = 1; Groups = @() }
    }
    $changed = Get-ChangedFile -Since $Since
    if ($changed.Status -ne 0) {
        return @{ Status = $changed.Status; Groups = @() }
    }
    $selection = Get-ItestSelection -Files $changed.Files
    foreach ($file in $selection.Unmapped) {
        Write-Host "itest-groups: $file is unmapped: no rule maps it, so it runs every group"
    }
    foreach ($group in $selection.Groups.Keys) {
        Write-Host "itest-groups: $group (from $($selection.Groups[$group]))"
    }
    Write-Host "itest-groups: $($selection.Groups.Count) of $($itestGroups.Count) groups from $($changed.Files.Count) changed files"
    return @{ Status = 0; Groups = @($selection.Groups.Keys) }
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

# The gate name, and so the .tmp/<name>.log, of a filtered itest: itest-filter-<slug>, never a group's name, the slug being the filter with every run
# of characters outside [A-Za-z0-9] turned into one '-' and trimmed of '-' at both ends; plain itest when nothing is left.
function Get-ItestFilterLogName {
    param([Parameter(Mandatory)] [string]$Filter)
    $slug = ($Filter -replace '[^A-Za-z0-9]+', '-').Trim('-')
    if (-not $slug) {
        return 'itest'
    }
    return "itest-filter-$slug"
}

# A killed group's summary verdict: the kind of kill its log names, STALLED, TIMED OUT or BACKSTOP.
function Get-ItestKillVerdict {
    param([Parameter(Mandatory)] [string]$Group)
    $kind = Get-GateKillKind -Log (Join-Path $logDir "itest-$Group.log")
    if ($kind) {
        return $kind
    }
    return 'KILLED (status 124, no kill line in its log)'
}

function Write-ItestSummary {
    param([Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary]$Results)
    foreach ($group in $Results.Keys) {
        $verdict = switch ($Results[$group]) {
            0 { 'passed' }
            124 { Get-ItestKillVerdict -Group $group }
            default { "FAILED (status $_)" }
        }
        Write-Host "itest ${group}: $verdict"
    }
}

# One command-line word for Start-Process, which joins its -ArgumentList with single spaces and quotes nothing
# (https://learn.microsoft.com/powershell/module/microsoft.powershell.management/start-process, -ArgumentList): a word
# that is empty or holds a space or a quote is wrapped in quotes, its own quotes escaped.
function ConvertTo-CommandLineWord {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Word)
    if ($Word -eq '' -or $Word -match '[\s"]') {
        return '"' + ($Word -replace '"', '\"') + '"'
    }
    return $Word
}

# The gate slot kind of an itest group: heavy for the heavy groups table's, whose tests run dotnet builds of the CsProbe
# project on top of their Godot runs; light for the rest, which run Godot and at most one small fixture build. A group's
# lane never changes its slot.
function Get-ItestGroupSlot {
    param([Parameter(Mandatory)] [string]$Group)
    if ($itestHeavyGroups -contains $Group) {
        return 'heavy'
    }
    return 'light'
}

# The gate slot kind of a filtered itest: heavy when a filter selects a class of a heavy group, as Get-ItestGroupSlot
# gives that group in a full run; light otherwise. Each filter is matched against the class's full name, as the
# runner's --filter-class matches it.
function Get-ItestFilterSlot {
    param([Parameter(Mandatory)] [string[]]$Filters)
    $classes = @($itestHeavyGroups | ForEach-Object { $itestGroups[$_] } | ForEach-Object { "$itestNamespace.$_" })
    foreach ($filter in $Filters) {
        if (@($classes | Where-Object { $_ -like $filter }).Count -gt 0) {
            return 'heavy'
        }
    }
    return 'light'
}

# Starts a group's gate as a process of its own, so the lanes' gates run at once: its console output (the gate's tail
# and verdict) goes to .tmp/itest-<group>.console, its errors to .tmp/itest-<group>.console.err. Returns the process.
function Invoke-ItestGroupProcess {
    param(
        [Parameter(Mandatory)] [string]$Group,
        [Parameter(Mandatory)] [string]$Lane
    )
    $classes = @($itestGroups[$Group] | ForEach-Object { "$itestNamespace.$_" })
    $command = Get-ItestCommand -Arguments (Get-TestArgumentList -Project $itestProject -Classes $classes -NoBuild)
    $log = Join-Path $logDir "itest-$Group.log"
    $slot = Get-ItestGroupSlot -Group $Group
    $gateOptions = @('-Log', $log, '-TimeoutSeconds', '300', '-Slot', $slot, '-Tail', '15')
    $words = @('-NoProfile', '-File', $gate) + $gateOptions + @('--', $command.Program) + $command.Arguments
    Write-Host "itest ${Group}: started in lane $Lane (log: $log, ceiling: 300 s, slot: $slot)"
    $console = Join-Path $logDir "itest-$Group.console"
    $process = Start-Process -FilePath 'pwsh' -ArgumentList @($words | ForEach-Object { ConvertTo-CommandLineWord -Word $_ }) `
        -NoNewWindow -PassThru -RedirectStandardOutput $console -RedirectStandardError "$console.err"
    # Without a handle taken while it runs, the process object of Start-Process has no ExitCode once it has exited.
    $null = $process.Handle
    return $process
}

# Prints a finished group's console output under a header, with the gate's kill line and its reading when it was
# killed. Returns the group's exit status.
function Complete-ItestGroup {
    param(
        [Parameter(Mandatory)] [string]$Group,
        [Parameter(Mandatory)] [string]$Lane,
        [Parameter(Mandatory)] [System.Diagnostics.Process]$Process
    )
    $Process.WaitForExit()
    $status = $Process.ExitCode
    $console = Join-Path $logDir "itest-$Group.console"
    Write-Host "== itest $Group (lane $Lane)"
    Get-Content -LiteralPath $console, "$console.err" -ErrorAction SilentlyContinue | Out-Host
    if ($status -eq 124) {
        Write-Host "itest-$Group was killed by the gate: $(Get-GateKill -Log (Join-Path $logDir "itest-$Group.log"))" -ForegroundColor Yellow
    }
    return $status
}

# Starts the next group of a lane's queue when it has one, recording it as the lane's running group.
function Invoke-ItestLaneNext {
    param(
        [Parameter(Mandatory)] [string]$Lane,
        [Parameter(Mandatory)] [hashtable]$Queues,
        [Parameter(Mandatory)] [hashtable]$Running
    )
    if ($Queues[$Lane].Count -eq 0) {
        return
    }
    $group = $Queues[$Lane].Dequeue()
    $Running[$Lane] = @{ Group = $group; Process = (Invoke-ItestGroupProcess -Group $group -Lane $Lane) }
}

# Stops the gates of the groups still running (an interrupted run), each with its whole process tree.
function Invoke-ItestStopTree {
    param([Parameter(Mandatory)] [hashtable]$Running)
    foreach ($entry in $Running.Values) {
        if (-not $entry.Process.HasExited) {
            & $gate -StopTree $entry.Process.Id | Out-Host
        }
    }
}

# Runs the given groups in their lanes: each lane's groups one at a time in order, the lanes at once, every group
# whatever the one before it did. Returns each group's exit status by group name.
function Invoke-ItestLane {
    param([Parameter(Mandatory)] [string[]]$Groups)
    $queues = @{}
    $running = @{}
    $statuses = @{}
    foreach ($lane in $itestLanes.Keys) {
        $laneGroups = [string[]]@($itestLanes[$lane] | Where-Object { $Groups -contains $_ })
        $queues[$lane] = [System.Collections.Generic.Queue[string]]::new($laneGroups)
    }
    try {
        foreach ($lane in $itestLanes.Keys) {
            Invoke-ItestLaneNext -Lane $lane -Queues $queues -Running $running
        }
        while ($running.Count -gt 0) {
            foreach ($lane in @($running.Keys)) {
                $entry = $running[$lane]
                if (-not $entry.Process.WaitForExit(500)) {
                    continue
                }
                $statuses[$entry.Group] = Complete-ItestGroup -Group $entry.Group -Lane $lane -Process $entry.Process
                $running.Remove($lane)
                Invoke-ItestLaneNext -Lane $lane -Queues $queues -Running $running
            }
        }
    }
    finally {
        Invoke-ItestStopTree -Running $running
    }
    return $statuses
}

# A wall time as minutes and seconds, e.g. 5m 12s.
function Format-WallTime {
    param([Parameter(Mandatory)] [TimeSpan]$Elapsed)
    return '{0}m {1:D2}s' -f [int][Math]::Floor($Elapsed.TotalMinutes), $Elapsed.Seconds
}

# How many lanes hold at least one of the given groups.
function Get-ItestLaneCount {
    param([Parameter(Mandatory)] [string[]]$Groups)
    $lanes = @($itestLanes.Keys | Where-Object { @($itestLanes[$_] | Where-Object { $Groups -contains $_ }).Count -gt 0 })
    return $lanes.Count
}

# The given groups of the integration suite: the C# helper published when the csharp group is among them, the project
# built once, then each group under its own gate in its lane, each run whatever the one before it did, and a summary
# line per group in table order with the run's wall time. Returns the first non-zero status in table order, else 0.
function Invoke-ItestByGroup {
    param([Parameter(Mandatory)] [string[]]$Groups)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    if ($Groups -contains 'csharp') {
        $dotnet = Invoke-DotnetPublish
        if ($dotnet -ne 0) {
            return $dotnet
        }
    }
    $build = Invoke-TestBuild -Name 'itest-build' -Project $itestProject
    if ($build -ne 0) {
        return $build
    }
    $statuses = Invoke-ItestLane -Groups $Groups
    $results = [ordered]@{}
    foreach ($group in @($itestGroups.Keys | Where-Object { $Groups -contains $_ })) {
        $results[$group] = $statuses[$group]
    }
    Write-ItestSummary -Results $results
    Write-Host "itest: $($results.Count) groups in $(Get-ItestLaneCount -Groups $Groups) lanes, wall $(Format-WallTime -Elapsed $clock.Elapsed)"
    $failed = @($results.Values | Where-Object { $_ -ne 0 })
    if ($failed.Count -gt 0) {
        return $failed[0]
    }
    return 0
}

# The groups an unfiltered itest runs: every group, or with -Since only those the changes since that ref touch. Returns
# @{ Status; Groups }: 1 when the tables have drifted from the project, 2 when git cannot resolve the ref, else 0.
function Get-ItestRunGroup {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Since)
    $drift = Get-ItestGroupDrift
    if ($drift) {
        [Console]::Error.WriteLine($drift)
        return @{ Status = 1; Groups = @() }
    }
    if (-not $Since) {
        return @{ Status = 0; Groups = @($itestGroups.Keys) }
    }
    return Invoke-ItestGroupSelection -Since $Since
}

# One integration test class or more, by -Filter: the C# helper published when a filter selects a csharp class, the
# project built, then one gate.
function Invoke-ItestFiltered {
    param([Parameter(Mandatory)] [string[]]$Filters)
    if (Test-ItestFilterNeedsDotnet -Filters $Filters) {
        $dotnet = Invoke-DotnetPublish
        if ($dotnet -ne 0) {
            return $dotnet
        }
    }
    $build = Invoke-TestBuild -Name 'itest-build' -Project $itestProject
    if ($build -ne 0) {
        return $build
    }
    $arguments = Get-TestArgumentList -Project $itestProject -Classes $Filters -NoBuild
    $slot = Get-ItestFilterSlot -Filters $Filters
    return Invoke-ItestGated -Name (Get-ItestFilterLogName -Filter $Filters[0]) -Arguments $arguments -Slot $slot
}

# The itest command: -Filter's classes, or the groups (all of them, or those -Since's changes touch) in their lanes.
# -Filter with -Since is refused with status 2; no group touched by the changes is status 0 with nothing run.
function Invoke-Itest {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Filters,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Since
    )
    if ($Filters.Count -gt 0 -and $Since) {
        [Console]::Error.WriteLine('itest takes -Filter or -Since, not both: -Filter runs the classes it names, -Since the groups a diff touches.')
        return 2
    }
    if ($Filters.Count -gt 0) {
        return Invoke-ItestFiltered -Filters $Filters
    }
    $run = Get-ItestRunGroup -Since $Since
    if ($run.Status -ne 0) {
        return $run.Status
    }
    if ($run.Groups.Count -eq 0) {
        Write-Host "itest: no group touched by the changes since $Since"
        return 0
    }
    return Invoke-ItestByGroup -Groups $run.Groups
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
function Initialize-VswhereEnvironment {
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
            Copy-Item -LiteralPath (Join-Path -Path $dotnetStaging -ChildPath $name -AdditionalChildPath $file) -Destination $folder
        }
    }
}

# Publishes the shim, the loader and the helper, each under its own gate, then lays out bin/dotnet. Returns the first
# failing publish's status, else 0.
function Invoke-DotnetPublish {
    Initialize-VswhereEnvironment
    foreach ($name in $dotnetProjects.Keys) {
        $project = $dotnetProjects[$name]
        $arguments = @('publish', (Join-Path $root $project.Project), '-c', 'Release', '-o', (Join-Path $dotnetStaging $name)) + $project.Extra
        $status = Invoke-Logged -Name "dotnet-$name" -TimeoutSeconds 300 -Arguments $arguments -Slot heavy
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
    $status = Invoke-Logged -Name 'publish' -TimeoutSeconds 300 -Arguments (Get-PublishArgumentList) -Slot heavy
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

# Publishes, then runs tools/install.ps1 under its own gate: the mirror into the install folder, the skill junctions and
# the agent sweep, whose commits run each swept repository's hooks.
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
    return Invoke-Gated -Name 'install' -TimeoutSeconds 900 -Program 'pwsh' -Arguments $arguments -Slot heavy
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
    return Invoke-Gated -Name 'package' -TimeoutSeconds 60 -Program 'pwsh' -Arguments $arguments -Slot light
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
    $build = Invoke-Logged -Name 'drive-build' -TimeoutSeconds 300 -Arguments @('build', $project, '-warnaserror') -Slot heavy
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
    return Invoke-Gated -Name 'drive' -TimeoutSeconds 300 -Program 'uv' -Arguments $arguments -Slot light -NoMarkers
}

[string[]]$filterClasses = @($Filter | Where-Object { $_ })

# A relative -Calls path names a file from where the caller stands, so it is read before the location moves.
if (-not [string]::IsNullOrWhiteSpace($Calls)) {
    $Calls = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Calls)
}

# Every command runs from the repo root, so dotnet and the gate find the solution and the tools from any folder; the
# finally gives the caller its location back after an exit, a failure or a gate kill alike.
Push-Location -LiteralPath $root
try {
    switch ($Command) {
        'build' {
            exit (Invoke-Logged -Name 'build' -TimeoutSeconds 300 -Arguments @('build', $solution, '-c', $configuration, '-warnaserror') -Slot heavy)
        }
        'test' {
            $build = Invoke-TestBuild -Name 'test-build' -Project $unitTestProject
            if ($build -ne 0) {
                exit $build
            }
            $arguments = Get-TestArgumentList -Project $unitTestProject -Classes $filterClasses -NoBuild
            # The whole unit suite runs its classes in parallel across the machine; a filtered run is a class or two.
            $slot = if ($filterClasses.Count -gt 0) { 'light' } else { 'heavy' }
            exit (Invoke-Logged -Name 'test' -TimeoutSeconds 180 -Arguments $arguments -Slot $slot)
        }
        'itest' {
            exit (Invoke-Itest -Filters $filterClasses -Since $Since)
        }
        'itest-groups' {
            exit (Invoke-ItestGroupSelection -Since $Since).Status
        }
        'format' {
            $status = Invoke-Logged -Name 'format-style' -TimeoutSeconds 180 -Arguments @('format', 'style', $solution, '--severity', 'info') -Slot heavy
            if ($status -ne 0) {
                exit $status
            }
            exit (Invoke-Logged -Name 'format' -TimeoutSeconds 180 -Arguments @('csharpier', 'format', $root) -Slot heavy)
        }
        'format-check' {
            exit (Invoke-Logged -Name 'format-check' -TimeoutSeconds 180 -Arguments @('csharpier', 'check', $root) -Slot heavy)
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
            exit (Invoke-Gated -Name 'gdtest' -TimeoutSeconds 60 -Program (Get-GodotPath) -Arguments $arguments -Slot light)
        }
        'pytest' {
            $arguments = @('run', '--quiet', '--with', 'pytest', '--with', 'gdtoolkit>=4,<5', 'pytest', '-q', 'tests/tools')
            exit (Invoke-Gated -Name 'pytest' -TimeoutSeconds 120 -Program 'uv' -Arguments $arguments -Slot light)
        }
        'drive' {
            exit (Invoke-Drive -CallsFile $Calls)
        }
        default {
            Get-Help $PSCommandPath -Detailed
        }
    }
}
finally {
    Pop-Location
}
