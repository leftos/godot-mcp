#!/usr/bin/env pwsh

<#
.SYNOPSIS
Builds, tests, formats and publishes godot-mcp.

.DESCRIPTION
Every command but itest -InGate, itest-groups, itest-csharp and itest-capture (which run inside their caller's gate) runs under gate run,
called through the repo's wrapper tools/gate.ps1, which hands it to the installed gate.exe (on a machine without one the
wrapper runs the command directly, with no log, slot or watchdog): its whole output goes to .tmp/<command>.log, the last
lines are printed, and it exits with the command's own status, or 124 when the gate's watchdog killed it with every
process it started. The watchdog kills a run for the first of three reasons, each named by a kill line in the log that
this script prints with its reading: STALLED (no output and no CPU for 120 s: it hung), TIMED OUT (the ceiling ran out on
a clock that runs slower while other work keeps the machine busy: a busy loop or a ceiling set too tight) or BACKSTOP
(five times the ceiling in plain wall time: the machine was busy, so run it once more alone). The ceilings below are
load-adjusted. On GitHub Actions, where no gate is installed, a command runs ungated instead: its output still goes to
the same log, and the job's timeout-minutes bounds it in place of the ceiling and watchdog.

  build    dotnet build GodotMcp.slnx in Release, warnings as errors; ceiling 300 s
  test     the unit tests (tests/GodotMcp.Tests): the project built in Release first (.tmp/test-build.log, ceiling 300 s),
           then its dotnet test -c Release --no-build; ceiling 180 s
  itest    runs as one heavy gate run, which the gate routes to the builder: pwsh -NoProfile -File run.ps1 itest -InGate
           under gate run from the repo root, .tmp/itest-gate.log, ceiling 1800 s; the lane gates inside it run without
           slots. The builder's copy of the repo cannot see this repo's history, so with -Since <ref> the groups are
           selected here first, printing itest-groups' lines (below), and the inner run gets them as -Groups <a,b,...>
           in the groups table's order; with none selected it prints "itest: no group touched by the changes since
           <ref>" and exits 0 with no gate run. -Filter and -Groups pass through as given; a bare itest passes neither.
           The refusals, status 2, come before that run: -Filter with -Since, -Groups with either or naming no group,
           a -Filter selecting classes of both the visible-desktop group and a hidden-desktop one, and a -Since ref git
           cannot resolve. itest -InGate, the form the gate runs, does what follows in the process it is started in:
           the integration tests against the real Godot (GODOT_PATH, else a Godot*console*.exe on PATH), in the class
           groups of the table at the top of this script, which run in the three lanes of the table below it: timing
           (lifecycle, stopping, watchdog, input, time, recording, capture, reads, scratch, scratchpace, scratchfolder,
           scratchparallel, scratchuntil: the groups that assert wall-clock times), build (prep, scene, csharp) and
           untimed (sessions, attach, headless, nodes, scratchsteps, scratchbackground). It first checks that every
           `public sealed class <Name>Tests` in tests/GodotMcp.IntegrationTests is in exactly one group, every listed
           class exists, every group is in exactly one lane that names only groups, and the heavy groups and rules
           tables name only groups, and stops with status 1 before running anything when not.
           It then builds the project once in Release (.tmp/itest-build.log, ceiling 300 s) and runs each group as its own
           gate in a process of its own (.tmp/itest-<group>.log, ceiling 300 s, dotnet test -c Release --no-build; the
           csharp group's gate runs itest-csharp, below, with a ceiling of 420 s for its publishes, and the capture group's
           itest-capture, below, with a ceiling of 420 s for its publish): each lane's groups one at a time
           in the table's order, the three lanes at once. A group's console output, the gate's tail and verdict, goes to
           .tmp/itest-<group>.console (errors to .tmp/itest-<group>.console.err) and is printed under
           "== itest <group> (lane <lane>)" when the group ends. A failed group then prints, under "itest <group>: failed
           tests from <lane log>:", each failed test's block from its lane log (the "failed <Test>" line, its message and
           its "at" frames), at most 5 tests of at most 40 lines each, or says the log has no failed test.
           Every group runs even when an earlier one fails; a summary line per group follows in the groups table's order,
           then "itest: <n> groups in <m> lanes, wall <time>", and the exit status is the first non-zero group's in
           that order. Each gate takes one of the machine's gate slots, so the three lanes take three: a heavy slot for
           the groups of the heavy groups table (prep, headless, scene, nodes, csharp, scratchpace, whose tests build C#), a light
           one for the rest, whichever lane a group is in. On Windows every test run (a group's or a -Filter one) goes
           through tools/hidden-desktop.ps1, on a desktop of its own, so no Godot window shows, except a run of the
           visible groups table's groups (capture): Windows.Graphics.Capture cannot see a window on a hidden desktop,
           so those tests run on the user's desktop and their Godot windows show there.
           With -Since <ref> it runs only the groups itest-groups (below) selects, which leave the visible groups out
           unless a change touches recording or capture, in their lanes as above, after
           printing itest-groups' lines; with none selected it prints "itest: no group touched by the changes since
           <ref>" and exits 0. -Since with -Filter is refused with status 2. With -Groups <a,b,...> it runs exactly
           those groups, in their lanes as above, after the check above.
  itest-csharp  the command the csharp group's gate runs, and a -Filter itest's gate when the filter matches a class of
           the csharp group: the dotnet command's three publishes (as below) run with plain dotnet publish, no gate of
           their own, each to .tmp/dotnet-<name>.log with its time printed, then bin/dotnet laid out, then dotnet test
           -c Release --no-build of the csharp group's classes (-Filter's, when given), after a plain dotnet build -c
           Release -warnaserror of the test project to .tmp/itest-csharp-build.log. That build runs when itest-csharp
           runs on its own, and on the build box (an itest run's GMCP_ITEST_BUILT does not reach it); inside an itest
           run that built the project first it is skipped, printing "itest-csharp: the test project was built by this
           itest run; not building it again", because rebuilding the shared Release output while another lane's test
           host has it loaded fails with MSB3027. Everything runs in the one process the gate runs, so
           the group needs nothing laid out before it and runs wherever its gate runs it, the build box included. A
           failed publish or build prints its log's last lines and "itest-csharp: dotnet <step> failed (status <n>);
           its log is <path>" (<step> being "publish of <name>" or "build of the test project"), and exits with its
           status.
  itest-capture  the command the capture group's gate runs, and a -Filter itest's gate when the filter matches a class
           of the capture group: the window capture helper published into bin/capture with plain dotnet publish, no
           gate of its own, to .tmp/capture.log with its time printed, then dotnet test -c Release --no-build of the
           capture group's classes (-Filter's, when given) on the user's desktop, not through tools/hidden-desktop.ps1.
           It builds no test project: the itest build before it did, and its command never goes to the build box. A
           failed publish prints its log's last lines and "itest-capture: dotnet publish of capture failed (status
           <n>); its log is <path>", and exits with its status.
  itest-groups  names the itest groups the changes since -Since <ref> touch, running nothing and taking no gate: the
           changed files are the tracked files that differ from the ref (git diff --name-only <ref>, the working tree
           included) and the untracked files git does not ignore. Each file goes through the rules table at the top of
           this script, the first matching pattern deciding (anything under bridge/ selects every group but the visible
           ones, since godot_mcp_bridge.gd preloads every module), and a file no rule maps selects every group. A
           visible group (capture), whose windows show on the user's desktop, is selected only by a change to what it
           tests: the capture helper, the real-time recording, the window handle the hello reports
           (bridge/godot_mcp_window.gd and bridge/godot_mcp_bridge.gd), tools/hidden-desktop.ps1, Installation.cs or
           the integration tests' fixtures and support project, or by a file no rule maps or its own test classes. It
           prints
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
  publish  a framework-dependent win-x64 server at bin/publish/godot-mcp.exe, with bridge/ beside it and the agent skills
           as bin/publish/skill and bin/publish/agent-sweep-skill, copied from skills/ by tools/copy-skills.ps1 with
           docs/TOOLS.md shipped as bin/publish/skill/TOOLS.md (its links to sibling docs rewritten to their GitHub copy;
           refused when either SKILL.md or docs/TOOLS.md is missing, or a shipped file names an absolute path), then the
           dotnet command (as above), whose bin/dotnet is copied to bin/publish/dotnet, then the
           window capture helper (src/GodotMcp.Capture, framework-dependent win-x64) into an emptied bin/capture
           (.tmp/capture.log), copied to bin/publish/capture, then the launcher (src/GodotMcp.Launcher) published
           NativeAOT (win-x64) to bin/publish/launcher/godot-mcp.exe with its pdb (.tmp/launcher.log), which install
           places as the install folder's godot-mcp.exe and package zips as godot-mcp-launcher.exe; ceiling 300 s each
  install  publish (as above), then tools/install.ps1 (tools/InstallLayout.psm1's layout), logged to .tmp/install.log,
           into the install folder $env:LOCALAPPDATA\godot-mcp, holding its install.lock throughout (a second install
           waits up to GODOT_MCP_INSTALL_LOCK_WAIT_S seconds, 60 when unset, then refuses). An install from before the
           launcher (no current file) is migrated first: every godot-mcp.exe running as the folder's godot-mcp.exe is
           stopped, with one line naming the Claude session and project it served, and the flat files are removed.
           bin/publish, all but launcher/, is copied into a new version folder versions\<semver>-<sha> (.2, .3, ...
           when taken); its godot-mcp.exe --list-tools must exit 0, then the current file is pointed at it in one
           rename, its VERSION is copied beside the launcher, and bin/publish/launcher/godot-mcp.exe is placed as the
           folder's godot-mcp.exe (a different one, even running, is renamed aside to godot-mcp.exe.old-<guid>).
           Servers already running keep the version they started with: none is stopped. ~/.claude/skills/godot-mcp
           and ~/.claude/skills/godot-agent-sweep are linked to the new version's skill/ and agent-sweep-skill/ as
           directory junctions (one pointing elsewhere is replaced; anything else at a link path is refused, never
           deleted). One line then names each Claude session and project whose server still runs an older version, to
           reconnect there with /mcp; version folders other than the current and the previous one are removed when
           nothing runs from them. Last, the new version's godot-mcp.exe --sweep-agents runs, printing its lines as
           they come: a sweep that cannot start is reported and the install succeeds; one that exits non-zero makes the
           install exit 1, undoing nothing. Ceiling 300 s for the publish, then 300 s in a heavy slot for the rest,
           since the sweep's commits run the swept repositories' hooks (which can build). GODOT_MCP_INSTALL_DIR
           overrides the install folder, GODOT_MCP_SKILLS_DIR the folder the links are made in.
  package  the release download: bin/publish removed, publish (as above, so bin/publish already holds the skills as
           skill/ and agent-sweep-skill/), then tools/package.ps1 zips bin/publish, its launcher/godot-mcp.exe as
           godot-mcp-launcher.exe at the zip's root (without its pdb), and a VERSION file (the published product
           version) into .tmp/package/godot-mcp-<X.Y.Z>-win-x64.zip, writes .tmp/package/install.ps1
           (tools/install-release.ps1 with tools/InstalledServers.psm1 and tools/InstallLayout.psm1 inlined, the
           release's installer), and prints both paths; .tmp/package.log, ceiling 300 s for the publish, 60 s for the
           zip. The release workflow runs this command, so a local zip is built as CI builds it.
  gdtest   the bridge's GDScript unit tests (tests/bridge/test_*.gd) in headless Godot (GODOT_PATH, else a
           Godot*console*.exe on PATH). It first imports tests/bridge (godot --headless --path tests/bridge --import,
           .tmp/gdtest-import.log, ceiling 120 s) when a test script is newer than the last import, so the project's
           global class list holds every script's class_name, then runs godot --headless --path tests/bridge --script
           res://run_tests.gd. Each failure prints as "FAIL <file>::<test>: <message>", then
           "gdtest: <passed> passed, <failed> failed"; on a failure it then prints every FAIL line of the log after the
           tail, under a "gdtest: FAIL lines from .tmp/gdtest.log:" header, or says the log has none; ceiling 60 s
  pytest   the Python tools' tests (tests/tools) under pytest in parallel (-n auto, pytest-xdist), which uv supplies
           together with gdtoolkit; ceiling 120 s
  drive    drives this tree's own server with the tool calls of -Calls <file.json>, a JSON array of
           {"tool": "<name>", "arguments": {...}} objects (arguments optional). It first builds the server project
           (.tmp/drive-build.log, ceiling 300 s), then runs tools/drive.py (.tmp/drive.log, ceiling 300 s), which checks
           the file, starts src/GodotMcp.Server/bin/Debug/net10.0/godot-mcp.exe over stdio, runs the calls in order and
           prints each result under a "== <n> <tool>" header: text as it is, an image saved to .tmp/drive/<n>-<i>.png and
           printed as [image <path>]. The server's own log (its stderr) goes to .tmp/drive-server.log. The first call
           that fails is printed and stops the run with status 1, and a failed call or a server that stops answering
           ends the output with "server log: <path>"; a malformed file stops it with status 2 before the server
           starts, as does a missing -Calls. The gate runs with --no-markers: drive's exit status alone is the verdict,
           since a tool result can quote a game's error lines. The server is stopped at the end, killed if it has not exited 5 s
           after its input closed. It runs on the user's desktop, not the hidden one: a run is quiet unless a call asks
           for quiet: false, and then its window is meant to show.

-Filter narrows test, itest, itest-csharp or itest-capture to one test class, e.g. -Filter "*SessionLifecycleTests". A filtered itest skips the
groups: the project built in Release first (.tmp/itest-build.log, ceiling 300 s), then one gate, ceiling 300 s (420 s,
through itest-csharp, when the filter selects a csharp class, or through itest-capture, when it selects a capture
class), logged to
.tmp/itest-filter-<slug>.log, where <slug> is the filter with every run of characters outside A-Z, a-z and 0-9 turned
into one '-' and trimmed of '-' at both ends (.tmp/itest-filter-SessionLifecycleTests.log for the example), so filtered
runs of different classes in one tree at once keep their logs apart and never write a group's log; a filter that leaves
no slug logs to .tmp/itest.log.

-Groups narrows itest to the named groups, comma-separated, e.g. -Groups time,csharp: the form a routed itest -Since
hands its inner run, since the builder cannot select groups from history it does not hold. A name the groups table does
not hold is refused with status 2, "itest -Groups names no group <name>; the groups are <list>.", as is -Groups with
-Filter or -Since: "itest takes one of -Filter, -Since or -Groups."

-InGate runs itest in the process it is started in, the inner form of the routed run that itest starts.

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
        'build', 'test', 'itest', 'itest-groups', 'itest-csharp', 'itest-capture', 'format', 'format-check', 'dotnet', 'publish', 'install', 'package',
        'gdtest', 'pytest', 'drive', 'help'
    )]
    [string]$Command = 'help',

    [string]$Filter = '',

    [string]$Since = '',

    [string]$Calls = '',

    [string]$Groups = '',

    [switch]$InGate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The integration test classes, one gate per group, run in this order. A new test class goes into one group; itest
# refuses to run while a class is in no group or a listed class no longer exists.
$itestGroups = [ordered]@{
    lifecycle = @('SessionLifecycleTests', 'MultiSessionTests')
    stopping  = @('SessionStopTests')
    watchdog  = @('WatchdogTests')
    sessions  = @('McpServerSmokeTests', 'ProfileTests', 'ProcessSessionTests')
    attach    = @('AttachTests', 'ArmTests', 'QuietTests')
    input     = @('InputTests', 'GamepadTests', 'CaptureTests', 'StressTests', 'WorldTargetTests', 'TextTargetTests', 'ItemTargetTests', 'PopupTargetTests', 'FiredSignalsTests')
    reads     = @('RuntimeReadTests', 'InspectionTests', 'BaselineTests', 'PreviewTests', 'StateTests', 'HeadlessCaptureTests')
    time      = @('TimeTests', 'BatchTests', 'WatchTests', 'StepInputTests', 'WatchAudioTests')
    prep      = @('PrepTests', 'RestartTests')
    recording = @('RecordingTests')
    capture   = @('CaptureHelperTests', 'RealtimeRecordingTests')
    headless  = @('HeadlessTests', 'HeadlessMeshTests', 'WarmHeadlessTests')
    scene     = @('HeadlessSceneTests', 'HeadlessBatchTests')
    nodes     = @('HeadlessPropertyTests', 'HeadlessSignalTests')
    csharp    = @('CSharpToolTests', 'GameToolTests', 'CSharpStateTests', 'CSharpWatchTests', 'CSharpFiredSignalsTests', 'ScratchCSharpStateTests')
    scratch   = @('ScratchRunnerTests', 'ScratchLookRunTests')
    scratchsteps = @('ScratchStepsRunTests')
    scratchpace = @('ScratchPaceRunTests')
    scratchfolder = @('ScratchFolderTests', 'ScratchExpectRunTests')
    scratchparallel = @('ScratchFolderParallelTests')
    scratchuntil = @('ScratchUntilRunTests')
    scratchbackground = @('ScratchBackgroundTests')
}
# The lanes the groups run in: each lane's groups one at a time in this order, the lanes at once. The timing lane keeps
# the groups that assert wall-clock times apart from each other; the build and untimed lanes hold the rest, the C#
# builds and headless runs split between them beside the session tests. Every group is in exactly one lane; itest
# refuses to run while one is not.
$itestLanes = [ordered]@{
    timing  = @('lifecycle', 'stopping', 'watchdog', 'input', 'time', 'recording', 'capture', 'reads', 'scratch', 'scratchpace', 'scratchfolder', 'scratchparallel', 'scratchuntil')
    build   = @('prep', 'scene', 'csharp')
    untimed = @('sessions', 'attach', 'headless', 'nodes', 'scratchsteps', 'scratchbackground')
}
# The groups whose tests run dotnet builds of the CsProbe project on top of their Godot runs: each takes a heavy gate
# slot, in whichever lane it runs, and every other group a light one.
$itestHeavyGroups = @('prep', 'headless', 'scene', 'nodes', 'csharp', 'scratchpace')
# The groups whose tests capture a Godot window through Windows.Graphics.Capture, which sees no window on a hidden
# desktop: their runs skip tools/hidden-desktop.ps1, so their windows show on the user's desktop. Their gate takes a light
# slot, never a heavy one: a light gate always runs on this machine, and the visible desktop the group needs exists only
# here. No group is in both this table and the heavy groups one; itest refuses that, and a -Filter spanning them.
$itestVisibleGroups = @('capture')
# The ceiling of every itest gate, and of the csharp group's (and a filtered itest of its classes), whose gate also runs
# the C# helper's three publishes and the test project's build: the tests' 300 s plus 120 s. A
# cold itest-csharp of the whole group from a fresh clone (no bin or obj; the NuGet cache and compiler server warm) took
# 91 s wall, 35 s load-adjusted: the publishes 12 s (shim 8 s, loader 2 s, helper 2 s), the build 5 s, the tests 72 s;
# an earlier cold run of the publishes took 31 s (shim 17 s, loader 3 s, helper 11 s). The 120 s leaves room for a
# slower machine and a first NuGet restore of ILCompiler; the time on the build box is unmeasured.
$itestCeiling = 300
$itestCsharpCeiling = $itestCeiling + 120
# The ceiling of the capture group's gate (and a filtered itest of its classes), which also publishes the window capture
# helper: the tests' 300 s plus 120 s, as the csharp group's. The publish's time is unmeasured.
$itestCaptureCeiling = $itestCeiling + 120
# The ceiling of the one heavy gate run a whole itest goes out as, the gate repo's figure for the routed itest.
$itestRoutedCeiling = 1800
# Which groups a changed file touches, for itest-groups and itest -Since: ordered pairs of a path pattern, '/'-separated
# from the repo root ('*' matches within one folder, '**' across folders), and what a match selects: 'all' (every group
# but the visible groups table's), 'none', 'class' (the group listing the class a test file is named for), or a list of
# groups. The first pattern a file matches decides; a file no pattern matches, or a test file whose class no group lists,
# selects every group, the visible ones included, and is named as unmapped. A visible group runs on the user's desktop,
# so a rule selects it only for a change to what it tests: the capture helper, the real-time recording, the window
# handle the hello reports (godot_mcp_window.gd, sent by godot_mcp_bridge.gd), tools/hidden-desktop.ps1, which decides
# which desktop a group runs on, Installation.cs, which finds Godot and the capture helper, and the test harness and
# support the capture classes use; those rules name every group ($itestEveryGroup) or the capture group, ahead of the
# broader rules their paths also match. tests/fixtures/** stays 'all': a probe project's content seldom changes what
# the capture tests see, and the full itest covers it. Anything else under bridge/ selects 'all', since godot_mcp_bridge.gd
# preloads every module.
$itestRuntimeGroups = @('lifecycle', 'stopping', 'watchdog', 'sessions', 'attach', 'input', 'reads', 'time', 'prep', 'recording', 'csharp')
$itestEveryGroup = @($itestGroups.Keys)
# The groups that play scratch scenes, which a change to the scratch runner, the tools it drives or its shared tests selects.
$itestScratchGroups = @('scratch', 'scratchsteps', 'scratchpace', 'scratchfolder', 'scratchparallel', 'scratchuntil', 'scratchbackground')
$itestRules = @(
    @('bridge/godot_mcp_window.gd', $itestEveryGroup),
    @('bridge/godot_mcp_bridge.gd', $itestEveryGroup),
    @('bridge/**', 'all'),
    @('run.ps1', 'all'),
    @('tools/hidden-desktop.ps1', $itestEveryGroup),
    @('src/GodotMcp.Capture/**', @('capture')),
    @('src/GodotMcp.Server/Session/Realtime*.cs', @('capture')),
    @('src/GodotMcp.Server/Session/GodotSession.Realtime.cs', @('capture')),
    @('src/GodotMcp.Server/Session/Installation.cs', $itestEveryGroup),
    @('src/GodotMcp.Server/Session/Scratch*.cs', @($itestScratchGroups + 'csharp')),
    @('src/GodotMcp.Server/Session/**', 'all'),
    @('src/GodotMcp.Server/Wire/**', 'all'),
    @('src/GodotMcp.Server/*.cs', 'all'),
    @('src/GodotMcp.Server/*.csproj', 'all'),
    @('Directory.*.props', 'all'),
    @('tests/GodotMcp.TestSupport/**', $itestEveryGroup),
    @('tests/GodotMcp.IntegrationTests/Fixtures/**', $itestEveryGroup),
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
    @('src/GodotMcp.Server/Tools/HeadlessTools.cs', @(@('headless') + $itestScratchGroups)),
    @('src/GodotMcp.Server/Tools/HeadlessTools*.cs', 'headless'),
    @('src/GodotMcp.Server/Tools/InputTarget.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Input*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/FiredSignals.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Gamepad*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Stress*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Capture*.cs', 'input'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Watch*.cs', @('time', 'csharp')),
    @('src/GodotMcp.Server/Tools/Watch*.cs', @('time', 'csharp')),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Time*.cs', @(@('time') + $itestScratchGroups + 'csharp')),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Frames*.cs', 'time'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Batch*.cs', 'time'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Record*.cs', @('recording', 'capture')),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Inspect*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Snapshot*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Baseline*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.Preview*.cs', @(@('reads') + $itestScratchGroups)),
    @('src/GodotMcp.Server/Tools/RuntimeTools.State*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/State*.cs', 'reads'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.CSharp*.cs', 'csharp'),
    @('src/GodotMcp.Server/Tools/RuntimeTools.RunCSharp*.cs', 'csharp'),
    @('src/GodotMcp.Server/Tools/RuntimeTools*.cs', $itestRuntimeGroups),
    @('src/GodotMcp.Server/Tools/ProjectTools*.cs', @('lifecycle', 'stopping', 'watchdog', 'sessions', 'attach', 'prep')),
    @('src/GodotMcp.Server/Tools/ScratchTools*.cs', @($itestScratchGroups + 'csharp')),
    @('src/GodotMcp.Server/Tools/**', 'all'),
    @('src/GodotMcp.Server/CSharp/**', 'csharp'),
    @('src/GodotMcp.Dotnet*/**', 'csharp'),
    @('dotnet/**', 'csharp'),
    @('src/GodotMcp.Server/Agents/**', 'none'),
    @('src/GodotMcp.Launcher/**', 'none'),
    @('tools/InstallLayout.psm1', 'none'),
    @('tests/GodotMcp.IntegrationTests/ScratchRunnerTests.cs', $itestScratchGroups),
    @('tests/GodotMcp.IntegrationTests/ScratchRunBase.cs', @('scratch', 'scratchsteps', 'scratchpace')),
    @('tests/GodotMcp.IntegrationTests/ScratchFolderTests.cs', @('scratchfolder', 'scratchparallel')),
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
    @('tools/copy-skills.ps1', 'none'),
    @('tools/install.ps1', 'none'),
    @('tools/install-release.ps1', 'none'),
    @('tools/package.ps1', 'none'),
    @('tools/release-check.ps1', 'none'),
    @('tools/agent-mail-guard.ps1', 'none'),
    @('tools/InstalledServers.psm1', 'none'),
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
$captureOut = Join-Path $root 'bin/capture'
# Every gated call runs the repo's wrapper, tools/gate.ps1, in the running pwsh: the wrapper hands its words to the
# installed gate.exe, or runs the command directly on a machine without one.
$gateWrapper = Join-Path $root 'tools/gate.ps1'
$pwshPath = [Environment]::ProcessPath

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

# Runs a program with no gate: its whole output goes to the log in UTF-8 and not to the screen, the last 15 lines are
# printed, and its own exit status is returned. Only GitHub Actions reaches this, where the job's timeout-minutes bounds
# a run in place of the gate's ceiling and watchdog.
function Invoke-Ungated {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Program,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$Log
    )
    Write-Host "run.ps1: on GitHub Actions, running $Name ungated (no ceiling or watchdog; the job's timeout-minutes bounds it)"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Log) | Out-Null
    & $Program @Arguments *>&1 | Out-File -LiteralPath $Log -Encoding utf8
    $status = $LASTEXITCODE
    Get-Content -LiteralPath $Log -Tail 15 | Out-Host
    return $status
}

# Runs a program under gate run, through the wrapper tools/gate.ps1: the whole output to .tmp/<Name>.log, the last 15
# lines on the screen, and the process tree killed with status 124 when the watchdog stops it (stalled, past its
# load-adjusted ceiling, or past five times it in wall time), with the log's kill line and its reading printed. On
# GitHub Actions it runs the program ungated instead.
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
    if ($env:GITHUB_ACTIONS -eq 'true') {
        return Invoke-Ungated -Name $Name -Program $Program -Arguments $Arguments -Log $log
    }
    $gateOptions = @('run', '--log', $log, '--timeout', $TimeoutSeconds, '--slot', $Slot, '--tail', 15)
    if ($NoMarkers) {
        $gateOptions += '--no-markers'
    }
    & $pwshPath -NoProfile -File $gateWrapper @gateOptions -- $Program @Arguments | Out-Host
    $status = $LASTEXITCODE
    if ($status -eq 124) {
        Write-Host "$Name was killed by the gate: $(Get-GateKill -Log $log)" -ForegroundColor Yellow
    }
    return $status
}

# Runs dotnet under gate run, as Invoke-Gated does.
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
# user's screen. -Visible runs dotnet on the user's desktop, for the visible groups.
function Get-ItestCommand {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [switch]$Visible
    )
    if ($Visible -or -not $IsWindows) {
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
    return Invoke-Gated -Name $Name -TimeoutSeconds $itestCeiling -Program $command.Program -Arguments $command.Arguments -Slot $Slot
}

# The command a gate runs for the csharp group, or for a filtered itest of its classes: run.ps1 itest-csharp, which
# publishes the C# helper and lays out bin/dotnet before its tests, all inside that one gate.
function Get-ItestCsharpCommand {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Filter)
    $arguments = @('-NoProfile', '-File', (Join-Path $root 'run.ps1'), 'itest-csharp')
    if ($Filter) {
        $arguments += @('-Filter', $Filter)
    }
    return @{ Program = 'pwsh'; Arguments = $arguments }
}

# The command a gate runs for the capture group, or for a filtered itest of its classes: run.ps1 itest-capture, which
# publishes the window capture helper before its tests, all inside that one gate.
function Get-ItestCaptureCommand {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Filter)
    $arguments = @('-NoProfile', '-File', (Join-Path $root 'run.ps1'), 'itest-capture')
    if ($Filter) {
        $arguments += @('-Filter', $Filter)
    }
    return @{ Program = 'pwsh'; Arguments = $arguments }
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

# Prints every FAIL line of gdtest's log after a failed run, under a header. The tail above is the log's last 15 lines,
# which end with the watch test's expected "ERROR: shout" backtrace on every run, and stdout and stderr are not kept in
# time order, so a failure's FAIL lines sit further up. A log with no FAIL line (a crash, a gate kill) says so, and so
# does a missing log, never throwing.
function Show-GdtestFailures {
    param([Parameter(Mandatory)] [string]$Log)
    $failures = @()
    if (Test-Path -LiteralPath $Log -PathType Leaf) {
        $failures = @(Get-Content -LiteralPath $Log | Where-Object { $_ -match '^FAIL ' })
    }
    if ($failures.Count -eq 0) {
        Write-Host 'gdtest: no FAIL line in .tmp/gdtest.log; read the log for the cause'
        return
    }
    Write-Host 'gdtest: FAIL lines from .tmp/gdtest.log:'
    $failures | ForEach-Object { Write-Host $_ }
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

# How the heavy groups, visible groups and rules tables have drifted: a name that is no group, and a group listed in both
# the heavy and the visible groups, as one phrase each; empty when there is none.
function Get-ItestRuleDrift {
    $keywords = @('all', 'none', 'class')
    $groups = @($itestGroups.Keys)
    $named = @($itestRules | ForEach-Object { $_[1] } | Where-Object { $keywords -cnotcontains $_ }) + $itestHeavyGroups + $itestVisibleGroups
    $unknown = @($named | Where-Object { $groups -cnotcontains $_ } | Sort-Object -Unique)
    $both = @($itestHeavyGroups | Where-Object { $itestVisibleGroups -contains $_ } | Sort-Object -Unique)
    $parts = @()
    if ($unknown.Count -gt 0) {
        $parts += "$($unknown.Count) name(s) in the heavy groups, visible groups or rules naming no group: $($unknown -join ', ')"
    }
    if ($both.Count -gt 0) {
        $parts += "$($both.Count) group(s) in both the heavy groups and the visible groups: $($both -join ', ')"
    }
    return $parts
}

# How the groups, lanes, heavy and visible groups and rules tables have drifted from the project, as one message, or ''
# when every declared class is in exactly one group, every listed class is declared, every group is in exactly one lane
# that names only groups, every heavy, visible or rule name is a group, and no group is in both the heavy and the visible
# ones.
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

# What a rule's selection gives a file: @{ Groups; Unmapped }, every group but the visible ones for 'all', none for
# 'none', the group listing the class the file is named for for 'class' (every group, the visible ones included,
# unmapped, when no group lists it), else the rule's groups.
function Resolve-ItestRuleSelection {
    param(
        [Parameter(Mandatory)] [object]$Selects,
        [Parameter(Mandatory)] [string]$File
    )
    if ($Selects -isnot [string]) {
        return @{ Groups = @($Selects); Unmapped = $false }
    }
    switch -CaseSensitive ($Selects) {
        'all' { return @{ Groups = @($itestGroups.Keys | Where-Object { $itestVisibleGroups -notcontains $_ }); Unmapped = $false } }
        'none' { return @{ Groups = @(); Unmapped = $false } }
        'class' {
            $group = Get-ItestClassGroup -Class ([IO.Path]::GetFileNameWithoutExtension($File))
            if ($group) {
                return @{ Groups = @($group); Unmapped = $false }
            }
            return @{ Groups = @($itestGroups.Keys); Unmapped = $true }
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

# Whether a -Filter selects a class of a visible group, whose tests need the user's desktop and the capture helper: each
# filter is matched against the class's full name, as the runner's --filter-class matches it.
function Test-ItestFilterIsVisible {
    param([Parameter(Mandatory)] [string[]]$Filters)
    $classes = @($itestVisibleGroups | ForEach-Object { $itestGroups[$_] } | ForEach-Object { "$itestNamespace.$_" })
    foreach ($filter in $Filters) {
        if (@($classes | Where-Object { $_ -like $filter }).Count -gt 0) {
            return $true
        }
    }
    return $false
}

# The visible-desktop groups whose class names a -Filter selects, in table order; empty when it selects none.
function Get-ItestFilterVisibleGroups {
    param([Parameter(Mandatory)] [string]$Filter)
    return @($itestVisibleGroups | Where-Object {
            @($itestGroups[$_] | ForEach-Object { "$itestNamespace.$_" } | Where-Object { $_ -like $Filter }).Count -gt 0
        })
}

# Whether a -Filter selects a class of a group that is not a visible-desktop one.
function Test-ItestFilterIsHidden {
    param([Parameter(Mandatory)] [string]$Filter)
    $hidden = @($itestGroups.Keys | Where-Object { $itestVisibleGroups -notcontains $_ })
    $classes = @($hidden | ForEach-Object { $itestGroups[$_] } | ForEach-Object { "$itestNamespace.$_" })
    return @($classes | Where-Object { $_ -like $Filter }).Count -gt 0
}

# The refusal a -Filter earns for spanning the two, or '' when none does: one command cannot run both, since the visible
# group's windows exist only on the user's desktop, which a hidden-desktop run never shows.
function Get-ItestFilterSpanRefusal {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Filters)
    foreach ($filter in $Filters) {
        $visible = @(Get-ItestFilterVisibleGroups -Filter $filter)
        if ($visible.Count -gt 0 -and (Test-ItestFilterIsHidden -Filter $filter)) {
            return "itest -Filter '$filter' selects classes of the visible-desktop group $($visible -join ', ') and of hidden-desktop groups; filter them apart."
        }
    }
    return ''
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
    if ($Group -eq 'csharp') {
        $command = Get-ItestCsharpCommand -Filter ''
        $ceiling = $itestCsharpCeiling
    }
    elseif ($itestVisibleGroups -contains $Group) {
        $command = Get-ItestCaptureCommand -Filter ''
        $ceiling = $itestCaptureCeiling
    }
    else {
        $classes = @($itestGroups[$Group] | ForEach-Object { "$itestNamespace.$_" })
        $command = Get-ItestCommand -Arguments (Get-TestArgumentList -Project $itestProject -Classes $classes -NoBuild)
        $ceiling = $itestCeiling
    }
    $log = Join-Path $logDir "itest-$Group.log"
    $slot = Get-ItestGroupSlot -Group $Group
    $words = @('-NoProfile', '-File', $gateWrapper, 'run', '--log', $log, '--timeout', $ceiling, '--slot', $slot, '--tail', '15', '--',
        $command.Program) + $command.Arguments
    Write-Host "itest ${Group}: started in lane $Lane (log: $log, ceiling: $ceiling s, slot: $slot)"
    $console = Join-Path $logDir "itest-$Group.console"
    $process = Start-Process -FilePath $pwshPath -ArgumentList @($words | ForEach-Object { ConvertTo-CommandLineWord -Word $_ }) `
        -NoNewWindow -PassThru -RedirectStandardOutput $console -RedirectStandardError "$console.err"
    # Without a handle taken while it runs, the process object of Start-Process has no ExitCode once it has exited.
    $null = $process.Handle
    return $process
}

# The failure blocks of a test log, each an array of lines: a "failed <Test> (<time>)" line as Microsoft Testing Platform
# writes it, then the indented lines after it (where it ran, the message, the "at" frames), up to an empty line or the
# next unindented one (the next test's line, the assembly's summary). The file is streamed. A gate log's own lines (its
# "gat" stream: process spawns and exits, its verdict) are dropped, as they interleave with the test's output, and the
# stamp and stream of the rest ("<time> out ") taken off; a line without a stamp is read as it is.
function Get-ItestFailureBlock {
    param([Parameter(Mandatory)] [string]$Path)
    $gateLine = '^\d{4}-\d{2}-\d{2}T[\d:.]+[+-]\d{2}:\d{2} gat( |$)'
    $stamp = '^\d{4}-\d{2}-\d{2}T[\d:.]+[+-]\d{2}:\d{2} (out|err)( |$)'
    $blocks = [System.Collections.Generic.List[object]]::new()
    $block = $null
    foreach ($stamped in [System.IO.File]::ReadLines($Path)) {
        if ($stamped -match $gateLine) {
            continue
        }
        $line = $stamped -replace $stamp, ''
        if ($line -match '^failed ') {
            $block = [System.Collections.Generic.List[string]]::new()
            $block.Add($line)
            $blocks.Add($block)
        }
        elseif ($null -ne $block -and $line -match '^\s') {
            $block.Add($line)
        }
        else {
            $block = $null
        }
    }
    return $blocks.ToArray()
}

# Prints, after a failed group's console output, the failure block of each failed test in its lane log, so the message
# reaches the outer itest log, which a rerun on the build box cannot overwrite: at most 5 tests, each at most 40 lines,
# the last of a cut block saying how many lines the log has beyond it. A log with no failed test (a build error, a gate
# kill) says so, and a log that cannot be read (missing, locked) says why; it never throws.
function Show-ItestFailureBlock {
    param(
        [Parameter(Mandatory)] [string]$Group,
        [Parameter(Mandatory)] [string]$Log
    )
    try {
        $blocks = @(Get-ItestFailureBlock -Path $Log)
    }
    catch {
        Write-Host "itest ${Group}: could not read ${Log}: $($_.Exception.GetBaseException().Message)"
        return
    }
    if ($blocks.Count -eq 0) {
        Write-Host "itest ${Group}: no failed test in $Log; read the log for the cause"
        return
    }
    Write-Host "itest ${Group}: failed tests from ${Log}:"
    foreach ($block in @($blocks | Select-Object -First 5)) {
        if ($block.Count -le 40) {
            $block | ForEach-Object { Write-Host $_ }
            continue
        }
        $block[0..38] | ForEach-Object { Write-Host $_ }
        Write-Host "  ... $($block.Count - 39) more lines in the log"
    }
    if ($blocks.Count -gt 5) {
        Write-Host "itest ${Group}: $($blocks.Count - 5) more failed tests in the log"
    }
}

# Prints a finished group's console output under a header, with the gate's kill line and its reading when it was
# killed, and the failure blocks of its failed tests when it failed. Returns the group's exit status.
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
    if ($status -ne 0) {
        Show-ItestFailureBlock -Group $Group -Log (Join-Path $logDir "itest-$Group.log")
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

# Stops the gates of the groups still running (an interrupted run), each with its whole process tree: the wrapper's
# pwsh and the gate.exe under it.
function Invoke-ItestStopTree {
    param([Parameter(Mandatory)] [hashtable]$Running)
    foreach ($entry in $Running.Values) {
        if (-not $entry.Process.HasExited) {
            & $pwshPath -NoProfile -File $gateWrapper stop-tree $entry.Process.Id | Out-Host
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

# The given groups of the integration suite: the project built once, then each group under its own gate in its lane
# (the csharp group's publishing the C# helper inside its gate), each run whatever the one before it did, and a summary
# line per group in table order with the run's wall time. Returns the first non-zero status in table order, else 0.
function Invoke-ItestByGroup {
    param([Parameter(Mandatory)] [string[]]$Groups)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $build = Invoke-TestBuild -Name 'itest-build' -Project $itestProject
    if ($build -ne 0) {
        return $build
    }
    $builtBefore = $env:GMCP_ITEST_BUILT
    $env:GMCP_ITEST_BUILT = '1'
    try {
        $statuses = Invoke-ItestLane -Groups $Groups
    }
    finally {
        $env:GMCP_ITEST_BUILT = $builtBefore
    }
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

# The groups an unfiltered itest runs: -Groups' names when given, else every group, or with -Since only those the
# changes since that ref touch. Returns @{ Status; Groups }: 1 when the tables have drifted from the project, 2 when git
# cannot resolve the ref, else 0.
function Get-ItestRunGroup {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Since,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$GroupNames
    )
    $drift = Get-ItestGroupDrift
    if ($drift) {
        [Console]::Error.WriteLine($drift)
        return @{ Status = 1; Groups = @() }
    }
    if ($GroupNames.Count -gt 0) {
        return @{ Status = 0; Groups = $GroupNames }
    }
    if (-not $Since) {
        return @{ Status = 0; Groups = @($itestGroups.Keys) }
    }
    return Invoke-ItestGroupSelection -Since $Since
}

# One integration test class or more, by -Filter: the project built, then one gate, which runs itest-csharp (the C#
# helper published inside it) when a filter selects a csharp class.
function Invoke-ItestFiltered {
    param([Parameter(Mandatory)] [string[]]$Filters)
    $build = Invoke-TestBuild -Name 'itest-build' -Project $itestProject
    if ($build -ne 0) {
        return $build
    }
    $name = Get-ItestFilterLogName -Filter $Filters[0]
    $slot = Get-ItestFilterSlot -Filters $Filters
    if (Test-ItestFilterNeedsDotnet -Filters $Filters) {
        $command = Get-ItestCsharpCommand -Filter $Filters[0]
        return Invoke-Gated -Name $name -TimeoutSeconds $itestCsharpCeiling -Program $command.Program -Arguments $command.Arguments -Slot $slot
    }
    if (Test-ItestFilterIsVisible -Filters $Filters) {
        $command = Get-ItestCaptureCommand -Filter $Filters[0]
        return Invoke-Gated -Name $name -TimeoutSeconds $itestCaptureCeiling -Program $command.Program -Arguments $command.Arguments -Slot $slot
    }
    $arguments = Get-TestArgumentList -Project $itestProject -Classes $Filters -NoBuild
    return Invoke-ItestGated -Name $name -Arguments $arguments -Slot $slot
}

# The integration test project's Release build for itest-csharp, whose dotnet test runs with --no-build: warnings as
# errors, with no gate of its own. itest-csharp runs it when it runs on its own, and on the build box, which an itest
# run's GMCP_ITEST_BUILT does not reach; inside an itest run that built the project first it is skipped, because
# rebuilding the shared Release output while another lane's test host has it loaded fails with MSB3027. Returns the
# build's status.
function Invoke-ItestCsharpBuild {
    $arguments = @('build', (Join-Path $root $itestProject), '-c', $configuration, '-warnaserror')
    return Invoke-DotnetInProcess -Caller 'itest-csharp' -Name 'itest-csharp-build' -Step 'build of the test project' -Arguments $arguments
}

# The csharp group's own run, the command its gate runs (itest-csharp): the C# helper published and laid out in
# bin/dotnet and the test project built, all with no gate of its own, then dotnet test --no-build of -Filter's class,
# else the group's. A gate routed to another machine runs this whole command there, where the build before it may not
# have run. Returns the first non-zero status, else 0.
function Invoke-ItestCsharp {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Filter)
    $status = Invoke-DotnetPublishInProcess
    if ($status -ne 0) {
        return $status
    }
    if ($env:GMCP_ITEST_BUILT -eq '1') {
        Write-Host 'itest-csharp: the test project was built by this itest run; not building it again'
    }
    else {
        $status = Invoke-ItestCsharpBuild
        if ($status -ne 0) {
            return $status
        }
    }
    $classes = @($Filter | Where-Object { $_ })
    if ($classes.Count -eq 0) {
        $classes = @($itestGroups['csharp'] | ForEach-Object { "$itestNamespace.$_" })
    }
    $command = Get-ItestCommand -Arguments (Get-TestArgumentList -Project $itestProject -Classes $classes -NoBuild)
    & $command.Program @($command.Arguments) | Out-Host
    return $LASTEXITCODE
}

# The capture group's own run, the command its gate runs (itest-capture): the window capture helper published into
# bin/capture with no gate of its own, then dotnet test --no-build of -Filter's class, else the group's, on the user's
# desktop. The test project is not built here: the itest build before the gate did, and this command never leaves the
# machine. Returns the first non-zero status, else 0.
function Invoke-ItestCapture {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string]$Filter)
    Clear-CaptureOutput
    $status = Invoke-DotnetInProcess -Caller 'itest-capture' -Name 'capture' -Step 'publish of capture' -Arguments (Get-CapturePublishArgumentList)
    if ($status -ne 0) {
        return $status
    }
    $classes = @($Filter | Where-Object { $_ })
    if ($classes.Count -eq 0) {
        $classes = @($itestVisibleGroups | ForEach-Object { $itestGroups[$_] } | ForEach-Object { "$itestNamespace.$_" })
    }
    $command = Get-ItestCommand -Arguments (Get-TestArgumentList -Project $itestProject -Classes $classes -NoBuild) -Visible
    & $command.Program @($command.Arguments) | Out-Host
    return $LASTEXITCODE
}

# Why -Groups is refused: with -Filter or -Since, or naming a group the groups table does not hold. Empty when it is not.
function Get-ItestGroupsRefusal {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Filters,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Since,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$GroupNames
    )
    if ($GroupNames.Count -eq 0) {
        return ''
    }
    if ($Filters.Count -gt 0 -or $Since) {
        return 'itest takes one of -Filter, -Since or -Groups.'
    }
    $unknown = @($GroupNames | Where-Object { @($itestGroups.Keys) -cnotcontains $_ })
    if ($unknown.Count -gt 0) {
        return "itest -Groups names no group $($unknown -join ', '); the groups are $(@($itestGroups.Keys) -join ', ')."
    }
    return ''
}

# Why an itest's arguments are refused, needing no build: -Filter with -Since, -Groups with either or naming no group,
# or a -Filter spanning the visible-desktop group and a hidden-desktop one. Empty when they are not.
function Get-ItestRefusal {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Filters,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Since,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$GroupNames
    )
    $groupsRefusal = Get-ItestGroupsRefusal -Filters $Filters -Since $Since -GroupNames $GroupNames
    if ($groupsRefusal) {
        return $groupsRefusal
    }
    if ($Filters.Count -gt 0 -and $Since) {
        return 'itest takes -Filter or -Since, not both: -Filter runs the classes it names, -Since the groups a diff touches.'
    }
    if ($Filters.Count -gt 0) {
        return Get-ItestFilterSpanRefusal -Filters $Filters
    }
    return ''
}

# The itest command as one heavy gate run, which the gate routes to the builder: pwsh -NoProfile -File run.ps1 itest
# -InGate under gate run from the repo root (.tmp/itest-gate.log, ceiling 1800 s), the lanes' gates inside it running
# without slots. The builder's copy of the repo cannot see this repo's history, so -Since's groups are selected here,
# itest-groups' lines printed, and the inner run gets them as -Groups in table order; with none selected it prints that
# no group was touched and returns 0 with no gate run. -Filter and -Groups pass through; a bare itest passes nothing.
# Arguments refused with no build are refused here first, status 2. Returns the gate's status.
function Invoke-ItestRouted {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Filters,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Since,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$GroupNames
    )
    $refusal = Get-ItestRefusal -Filters $Filters -Since $Since -GroupNames $GroupNames
    if ($refusal) {
        [Console]::Error.WriteLine($refusal)
        return 2
    }
    $routedGroups = $GroupNames
    if ($Since) {
        $selection = Invoke-ItestGroupSelection -Since $Since
        if ($selection.Status -ne 0) {
            return $selection.Status
        }
        if ($selection.Groups.Count -eq 0) {
            Write-Host "itest: no group touched by the changes since $Since"
            return 0
        }
        $routedGroups = $selection.Groups
    }
    $inner = @('pwsh', '-NoProfile', '-File', 'run.ps1', 'itest', '-InGate')
    if ($Filters.Count -gt 0) {
        $inner += @('-Filter', $Filters[0])
    }
    if ($routedGroups.Count -gt 0) {
        $inner += @('-Groups', ($routedGroups -join ','))
    }
    $log = Join-Path $logDir 'itest-gate.log'
    Write-Host "$($inner -join ' ')  (log: $log, ceiling: $itestRoutedCeiling s, slot: heavy)"
    $gateOptions = @('run', '--cd', $root, '--log', $log, '--timeout', $itestRoutedCeiling, '--slot', 'heavy', '--tail', 15)
    & $pwshPath -NoProfile -File $gateWrapper @gateOptions -- @inner | Out-Host
    $status = $LASTEXITCODE
    if ($status -eq 124) {
        Write-Host "itest was killed by the gate: $(Get-GateKill -Log $log)" -ForegroundColor Yellow
    }
    return $status
}

# The itest command in the process it is started in (itest -InGate): -Filter's classes, or the groups (all of them, -Groups'
# names, or those -Since's changes touch) in their lanes. The refusals of Get-ItestRefusal are status 2; no group touched
# by the changes is status 0 with nothing run.
function Invoke-Itest {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Filters,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]$Since,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$GroupNames
    )
    $refusal = Get-ItestRefusal -Filters $Filters -Since $Since -GroupNames $GroupNames
    if ($refusal) {
        [Console]::Error.WriteLine($refusal)
        return 2
    }
    if ($Filters.Count -gt 0) {
        return Invoke-ItestFiltered -Filters $Filters
    }
    $run = Get-ItestRunGroup -Since $Since -GroupNames $GroupNames
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

# Copies the skills and docs/TOOLS.md into bin/publish through tools/copy-skills.ps1, so a released build carries the
# same skill a from-source install links, with TOOLS.md beside it. A missing SKILL.md or docs/TOOLS.md, or a shipped file
# naming an absolute path, stops the publish here with that script's status, before a build without them is installed or
# zipped.
function Copy-SkillFolders {
    $arguments = @(
        '-NoProfile', '-File', (Join-Path $root 'tools/copy-skills.ps1'),
        '-Root', $root, '-Destination', (Join-Path $root 'bin/publish')
    )
    & pwsh @arguments
    return $LASTEXITCODE
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

# The dotnet arguments that publish one of the C# helper's projects into its .tmp/dotnet-publish/<name>.
function Get-DotnetPublishArgumentList {
    param([Parameter(Mandatory)] [string]$Name)
    $project = $dotnetProjects[$Name]
    return @('publish', (Join-Path $root $project.Project), '-c', 'Release', '-o', (Join-Path $dotnetStaging $Name)) + $project.Extra
}

# Publishes the shim, the loader and the helper, each under its own gate, then lays out bin/dotnet. Returns the first
# failing publish's status, else 0.
function Invoke-DotnetPublish {
    Initialize-VswhereEnvironment
    foreach ($name in $dotnetProjects.Keys) {
        $status = Invoke-Logged -Name "dotnet-$name" -TimeoutSeconds 300 -Arguments (Get-DotnetPublishArgumentList -Name $name) -Slot heavy
        if ($status -ne 0) {
            return $status
        }
    }
    Copy-DotnetLayout
    Write-Host "dotnet: the helper is in $dotnetOut"
    return 0
}

# Runs dotnet in this process for itest-csharp, under the gate that runs itest-csharp rather than one of its own, its
# whole output in .tmp/<Name>.log, and prints the time it took. A failure prints the log's last lines, which a run on
# another machine brings back only in the gate's own log, and a line naming the step and the log. Returns dotnet's
# status.
function Invoke-DotnetInProcess {
    param(
        [Parameter(Mandatory)] [string]$Caller,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Step,
        [Parameter(Mandatory)] [string[]]$Arguments
    )
    $log = Join-Path $logDir "$Name.log"
    Write-Host "${Caller}: dotnet $Step (log: $log)"
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments *> $log
    $status = $LASTEXITCODE
    if ($status -ne 0) {
        Get-Content -LiteralPath $log -Tail 15 | Out-Host
        Write-Host "${Caller}: dotnet $Step failed (status $status); its log is $log"
        return $status
    }
    Write-Host "${Caller}: dotnet $Step done in $([int]$clock.Elapsed.TotalSeconds) s"
    return 0
}

# Publishes the shim, the loader and the helper in this process with plain dotnet publish, each to
# .tmp/dotnet-<name>.log, then lays out bin/dotnet. Returns the first failing publish's status, else 0.
function Invoke-DotnetPublishInProcess {
    Initialize-VswhereEnvironment
    foreach ($name in $dotnetProjects.Keys) {
        $arguments = Get-DotnetPublishArgumentList -Name $name
        $status = Invoke-DotnetInProcess -Caller 'itest-csharp' -Name "dotnet-$name" -Step "publish of $name" -Arguments $arguments
        if ($status -ne 0) {
            return $status
        }
    }
    Copy-DotnetLayout
    Write-Host "dotnet: the helper is in $dotnetOut"
    return 0
}

# The dotnet arguments that publish the window capture helper, framework-dependent like the server, into bin/capture.
function Get-CapturePublishArgumentList {
    return @(
        'publish', (Join-Path $root 'src/GodotMcp.Capture/GodotMcp.Capture.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $captureOut
    )
}

# Empties bin/capture, so no file of an earlier publish of the capture helper stays beside the new one.
function Clear-CaptureOutput {
    if (Test-Path -LiteralPath $captureOut) {
        Remove-Item -LiteralPath $captureOut -Recurse -Force
    }
}

# Publishes the window capture helper into an emptied bin/capture under its own gate. Returns the publish's status.
function Invoke-CapturePublish {
    Clear-CaptureOutput
    return Invoke-Logged -Name 'capture' -TimeoutSeconds 300 -Arguments (Get-CapturePublishArgumentList) -Slot heavy
}

# Publishes the server into bin/publish, copies the agent skills in beside it, then the C# helper, copying bin/dotnet to
# bin/publish/dotnet, then the window capture helper, copying bin/capture to bin/publish/capture, then the launcher into
# bin/publish/launcher.
function Invoke-Publish {
    $status = Invoke-Logged -Name 'publish' -TimeoutSeconds 300 -Arguments (Get-PublishArgumentList) -Slot heavy
    if ($status -ne 0) {
        return $status
    }
    $status = Copy-SkillFolders
    if ($status -ne 0) {
        return $status
    }
    $status = Invoke-DotnetPublish
    if ($status -ne 0) {
        return $status
    }
    Copy-Folder -Source $dotnetOut -Destination (Join-Path $root 'bin/publish/dotnet')
    $status = Invoke-CapturePublish
    if ($status -ne 0) {
        return $status
    }
    Copy-Folder -Source $captureOut -Destination (Join-Path $root 'bin/publish/capture')
    return Invoke-Logged -Name 'launcher' -TimeoutSeconds 300 -Arguments (Get-LauncherPublishArgumentList) -Slot heavy
}

# The dotnet arguments of the launcher's NativeAOT publish: one self-contained godot-mcp.exe in bin/publish/launcher.
function Get-LauncherPublishArgumentList {
    return @(
        'publish', (Join-Path $root 'src/GodotMcp.Launcher/GodotMcp.Launcher.csproj'),
        '-c', 'Release', '-r', 'win-x64', '-o', (Join-Path $root 'bin/publish/launcher')
    )
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
    return Invoke-Gated -Name 'install' -TimeoutSeconds 300 -Program 'pwsh' -Arguments $arguments -Slot heavy
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
[string[]]$groupNames = @($Groups -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

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
            if ($InGate) {
                exit (Invoke-Itest -Filters $filterClasses -Since $Since -GroupNames $groupNames)
            }
            exit (Invoke-ItestRouted -Filters $filterClasses -Since $Since -GroupNames $groupNames)
        }
        'itest-groups' {
            exit (Invoke-ItestGroupSelection -Since $Since).Status
        }
        'itest-csharp' {
            exit (Invoke-ItestCsharp -Filter $Filter)
        }
        'itest-capture' {
            exit (Invoke-ItestCapture -Filter $Filter)
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
            $status = Invoke-Gated -Name 'gdtest' -TimeoutSeconds 60 -Program (Get-GodotPath) -Arguments $arguments -Slot light
            if ($status -ne 0) {
                Show-GdtestFailures -Log (Join-Path $logDir 'gdtest.log')
            }
            exit $status
        }
        'pytest' {
            $arguments = @(
                'run', '--quiet', '--with', 'pytest', '--with', 'pytest-xdist', '--with', 'gdtoolkit>=4,<5',
                'pytest', '-q', '-n', 'auto', 'tests/tools'
            )
            exit (Invoke-Gated -Name 'pytest' -TimeoutSeconds 120 -Program 'uv' -Arguments $arguments -Slot heavy)
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
