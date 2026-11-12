"""Tests for the long-running line run.ps1 prints when a test run is killed, read from its diagnostic folder."""

from __future__ import annotations

import re
import subprocess
from pathlib import Path

RUN_PS1 = Path(__file__).resolve().parents[2] / "run.ps1"

# Loads run.ps1's long-running reader alone, without running the script, and prints what it finds under $LogDir.
DRIVER = """param([string]$RunPs1, [string]$LogDir)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
$function = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Show-ItestLongRunning'
}, $true)
if ($null -eq $function) {
    throw "run.ps1 has no function Show-ItestLongRunning"
}
. ([scriptblock]::Create($function.Extent.Text))
Show-ItestLongRunning -Folder (Join-Path $LogDir 'itest-lifecycle.diag') -Label 'itest itest-lifecycle'
"""

# Loads Get-ItestArgumentList alone with a stub Get-TestArgumentList, and prints what two runs got and what it cleared.
DIAG_DRIVER = """param([string]$RunPs1, [string]$LogDir)
$ErrorActionPreference = 'Stop'
$logDir = $LogDir
$stale = Join-Path $LogDir 'test.diag/stale'
New-Item -ItemType Directory -Force -Path $stale | Out-Null
Set-Content -LiteralPath (Join-Path $stale 'x.diag') -Value 'an earlier run'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
$function = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ItestArgumentList'
}, $true)
if ($null -eq $function) {
    throw "run.ps1 has no function Get-ItestArgumentList"
}
. ([scriptblock]::Create($function.Extent.Text))
function Get-TestArgumentList {
    param([string]$Project, [string[]]$Classes, [switch]$NoBuild)
    return @('test', '--project', $Project)
}
$first = Get-ItestArgumentList -Project 'tests/GodotMcp.Tests/GodotMcp.Tests.csproj' -Classes @() -Name 'test'
Write-Output "stale=$(Test-Path -LiteralPath $stale)"
$second = Get-ItestArgumentList -Project 'tests/GodotMcp.Tests/GodotMcp.Tests.csproj' -Classes @() -Name 'test'
$folder = [array]::IndexOf($first.Arguments, '--diagnostic-output-directory')
$project = [array]::IndexOf($first.Arguments, '--project')
Write-Output "first=$($first.Arguments[$folder + 1])"
Write-Output "folder=$($first.Folder)"
Write-Output "second=$($second.Arguments[$folder + 1])"
Write-Output "project=$($first.Arguments[$project + 1])"
"""

# Loads Complete-ItestGroup next to the reader, runs a process that exits with the given status, and prints what it wrote.
COMPLETE_DRIVER = """param([string]$RunPs1, [string]$LogDir, [int]$Status)
$ErrorActionPreference = 'Stop'
$logDir = $LogDir
$ast = [System.Management.Automation.Language.Parser]::ParseFile($RunPs1, [ref]$null, [ref]$null)
foreach ($name in 'Show-ItestLongRunning', 'Complete-ItestGroup') {
    $function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $function) {
        throw "run.ps1 has no function $name"
    }
    . ([scriptblock]::Create($function.Extent.Text))
}
function Get-GateKill { param([string]$Log) return 'gate: STALLED: it hung' }
function Show-ItestFailureBlock { param([string]$Label, [string]$Log) }
$folder = Join-Path $LogDir 'itest-nodes.diag/run1'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
Set-Content -LiteralPath (Join-Path $folder 'x.diag') -Value "LONG_LINE"
$process = Start-Process -FilePath ([Environment]::ProcessPath) -ArgumentList @('-NoProfile', '-Command', "exit $Status") `
    -PassThru -NoNewWindow
Write-Output "exit=$(Complete-ItestGroup -Group 'nodes' -Lane 'untimed' -Process $process -Folder (Split-Path -Parent $folder))"
"""

STAMP = "2026-11-10T13:31:00.1234567-08:00"
FIRST = "[Long Running Test] 'GodotMcp.IntegrationTests.SessionLifecycleTests.ALaunchStarts', Elapsed: 00:00:30"
SECOND = "[Long Running Test] 'GodotMcp.IntegrationTests.SessionLifecycleTests.ALaunchStarts', Elapsed: 00:01:00"
RUN_ID = re.compile(r"^\.tmp/test\.diag/([0-9a-f]{32})$")


def _run(driver_source: str, tmp_path: Path, extra: dict[str, str] | None = None) -> list[str]:
    driver = tmp_path / "driver.ps1"
    driver.write_text(driver_source, encoding="utf-8", newline="\n")
    command = ["pwsh", "-NoProfile", "-File", str(driver), "-RunPs1", str(RUN_PS1), "-LogDir", str(tmp_path / ".tmp")]
    for flag, value in (extra or {}).items():
        command += [flag, value]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    assert result.stderr == ""
    return result.stdout.splitlines()


def _show(tmp_path: Path, diag: list[str] | None) -> list[str]:
    folder = tmp_path / ".tmp" / "itest-lifecycle.diag" / "run1"
    if diag is not None:
        folder.mkdir(parents=True)
        path = folder / "GodotMcp.IntegrationTests_20261110_131500.diag"
        path.write_text("\n".join(diag) + "\n", encoding="utf-8", newline="\n")
    return _run(DRIVER, tmp_path)


def test_the_last_long_running_line_is_printed_with_its_prefix(tmp_path: Path) -> None:
    printed = _show(tmp_path, [f"{STAMP} {FIRST}", f"{STAMP} {SECOND}", f"{STAMP} Test run finished"])

    assert printed == [f"itest itest-lifecycle: still running when it was killed: {STAMP} {SECOND}"]


def test_a_diag_folder_with_no_long_running_line_prints_nothing(tmp_path: Path) -> None:
    assert _show(tmp_path, [f"{STAMP} Test run finished"]) == []


def test_a_missing_diag_folder_prints_nothing(tmp_path: Path) -> None:
    assert _show(tmp_path, None) == []


def test_each_run_gets_its_own_diagnostic_folder(tmp_path: Path) -> None:
    printed = dict(line.split("=", 1) for line in _run(DIAG_DRIVER, tmp_path))

    match = RUN_ID.match(printed["first"])
    assert match, printed
    assert printed["second"] != printed["first"]
    assert printed["folder"] == str(tmp_path / ".tmp" / "test.diag" / match.group(1))


def test_the_run_gets_the_project_it_was_asked_for(tmp_path: Path) -> None:
    printed = dict(line.split("=", 1) for line in _run(DIAG_DRIVER, tmp_path))

    assert printed["project"] == "tests/GodotMcp.Tests/GodotMcp.Tests.csproj"


def test_the_name_folder_is_cleared_before_the_run(tmp_path: Path) -> None:
    printed = dict(line.split("=", 1) for line in _run(DIAG_DRIVER, tmp_path))

    assert printed["stale"] == "False", printed


def _complete(tmp_path: Path, status: int) -> list[str]:
    driver = tmp_path / "driver.ps1"
    driver.write_text(COMPLETE_DRIVER.replace("LONG_LINE", SECOND), encoding="utf-8", newline="\n")
    command = [
        "pwsh",
        "-NoProfile",
        "-File",
        str(driver),
        "-RunPs1",
        str(RUN_PS1),
        "-LogDir",
        str(tmp_path / ".tmp"),
        "-Status",
        str(status),
    ]
    result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)

    assert result.returncode == 0, result.stderr
    return result.stdout.splitlines()


def test_a_killed_group_names_its_hung_test(tmp_path: Path) -> None:
    printed = _complete(tmp_path, 124)

    assert f"itest itest-nodes: still running when it was killed: {SECOND}" in printed, printed


def test_a_failed_group_that_was_not_killed_prints_no_long_running_line(tmp_path: Path) -> None:
    printed = _complete(tmp_path, 1)

    assert not any("still running when it was killed" in line for line in printed), printed
